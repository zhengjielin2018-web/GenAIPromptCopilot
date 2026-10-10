import { describe, it, expect } from 'vitest'
import { isFinished, showsImage, isRealistic, statusText, renderButton, verdictMark, toastText, issueLabel, tagText, splitItems, suggestionButton } from '../lib/render'
import type { RenderView, SelfCheckItemView } from '../types/api'

const view = (over: Partial<RenderView>): RenderView => ({
  renderId: 'r1', turnIndex: 3, status: 'queued', position: null, safety: 'on', message: null,
  selfCheck: { status: 'pending', score: null, summary: null, items: [], suggestion: null },
  timings: { queueMs: null, delayMs: null, executionMs: null, reviewMs: null, requirementsMs: null, selfCheckMs: null },
  seed: 42,
  ...over,
})

describe('render status', () => {
  it('knows which states are final and which show the image', () => {
    expect(['done', 'failed', 'blocked'].every(s => isFinished(s as never))).toBe(true)
    expect(['queued', 'generating', 'reviewing', 'self_checking'].some(s => isFinished(s as never))).toBe(false)
    expect(showsImage('self_checking')).toBe(true)
    expect(showsImage('done')).toBe(true)
    expect(showsImage('reviewing')).toBe(false)
  })

  it('has its own words for every stage', () => {
    expect(statusText(view({ status: 'queued', position: 2 }))).toBe('排第 2 位')
    expect(statusText(view({ status: 'queued', position: null }))).toBe('排隊中')
    expect(statusText(view({ status: 'generating' }))).toBe('生圖中（閒置後第一張可能要一兩分鐘）')
    expect(statusText(view({ status: 'reviewing' }))).toBe('審查圖片中')
    expect(statusText(view({ status: 'self_checking' }))).toBe('評分中')
    expect(statusText(view({ status: 'done' }))).toBe('')
    expect(statusText(view({ status: 'blocked', message: '預覽圖被判定為不當內容，沒有顯示' }))).toBe('預覽圖被判定為不當內容，沒有顯示')
    expect(statusText(view({ status: 'failed', message: null }))).toBe('預覽沒有完成')
  })

  it('marks verdicts and picks the toast text', () => {
    expect([verdictMark('met'), verdictMark('unmet'), verdictMark('unclear')]).toEqual(['✓', '✗', '？'])
    expect(toastText('done')).toBe('預覽好了')
    expect(toastText('blocked')).toBe('預覽沒有完成')
    expect(toastText('failed')).toBe('預覽沒有完成')
  })
})

describe('isRealistic', () => {
  it('looks at style.genre first, whole words, any case', () => {
    expect(isRealistic({ 'style.genre': 'Photorealistic' }, 'anime')).toBe(true)
    expect(isRealistic({ 'style.genre': 'anime' }, 'photorealistic, 1girl')).toBe(false)
    expect(isRealistic({}, '1girl, raw photo, 85mm')).toBe(true)
    expect(isRealistic({}, '1girl, photobomb')).toBe(false)
    expect(isRealistic({}, 'masterpiece, 1girl')).toBe(false)
  })
})

describe('renderButton', () => {
  const base = { enabled: true, isLatest: true, busy: false, inFlight: false }
  it('is hidden when render is off or the card is not the latest final', () => {
    expect(renderButton({ ...base, enabled: false })).toEqual({ visible: false })
    expect(renderButton({ ...base, isLatest: false })).toEqual({ visible: false })
  })
  it('is disabled with a note while another preview is unfinished', () => {
    expect(renderButton({ ...base, inFlight: true, busy: true })).toEqual({ visible: true, disabled: true, note: '上一張還在生成' })
  })
  it('is disabled without a note while a turn streams', () => {
    expect(renderButton({ ...base, busy: true })).toEqual({ visible: true, disabled: true, note: null })
  })
  it('is ready otherwise', () => {
    expect(renderButton(base)).toEqual({ visible: true, disabled: false, note: null })
  })
})

const item = (over: Partial<SelfCheckItemView>): SelfCheckItemView => ({
  id: 'r1', text: '銀色雙馬尾', source: 'user', tags: ['silver hair'], negativeTags: [], verdict: 'met', issue: 'none', reason: '有', ...over,
})

describe('self-check items', () => {
  it('labels only the two actionable issues', () => {
    expect(issueLabel(item({ issue: 'prompt_missing' }))).toEqual({ text: 'prompt 漏了', hint: '可以請助理補進 prompt' })
    expect(issueLabel(item({ issue: 'not_rendered' }))).toEqual({ text: '沒畫出來', hint: 'prompt 有寫，但這次沒畫出來' })
    expect(issueLabel(item({ issue: 'none' }))).toBeNull()
    expect(issueLabel(item({ issue: 'unclear' }))).toBeNull()
  })

  it('shows tags, marks negatives, and explains when the prompt has none', () => {
    expect(tagText(item({ tags: ['silver hair', 'twin tails'] }))).toBe('silver hair, twin tails')
    expect(tagText(item({ tags: [], negativeTags: ['hat'] }))).toBe('hat（負向）')
    expect(tagText(item({ tags: [], verdict: 'unmet' }))).toBe('prompt 沒寫')
    expect(tagText(item({ tags: [], verdict: 'met' }))).toBe('prompt 沒寫，剛好畫出來')
  })

  it('splits user requirements from delegated ones, keeping order', () => {
    const items = [item({ id: 'r1' }), item({ id: 'r2', source: 'delegated' }), item({ id: 'r3' })]
    const { user, delegated } = splitItems(items)
    expect(user.map(i => i.id)).toEqual(['r1', 'r3'])
    expect(delegated.map(i => i.id)).toEqual(['r2'])
  })
})

describe('suggestionButton', () => {
  const base = { kind: 'fix_prompt' as const, isLatest: true, busy: false, inFlight: false, sent: false }

  it('labels by kind and hides for none or old cards', () => {
    expect(suggestionButton(base)).toEqual({ visible: true, label: '請助理修改', disabled: false, note: null })
    expect(suggestionButton({ ...base, kind: 'rewrite_tags' })).toEqual({ visible: true, label: '請助理修改', disabled: false, note: null })
    expect(suggestionButton({ ...base, kind: 'reroll' })).toEqual({ visible: true, label: '換 seed 重生', disabled: false, note: null })
    expect(suggestionButton({ ...base, kind: 'none' })).toEqual({ visible: false })
    expect(suggestionButton({ ...base, isLatest: false })).toEqual({ visible: false })
  })

  // Review Focus 5：按過一次、對話在跑、預覽還沒結束都不能再送
  it('disables while busy, while a preview runs, and after the fix was sent', () => {
    expect(suggestionButton({ ...base, sent: true })).toEqual({ visible: true, label: '請助理修改', disabled: true, note: '已送出修正' })
    expect(suggestionButton({ ...base, inFlight: true })).toEqual({ visible: true, label: '請助理修改', disabled: true, note: '上一張還在生成' })
    expect(suggestionButton({ ...base, busy: true })).toEqual({ visible: true, label: '請助理修改', disabled: true, note: null })
    expect(suggestionButton({ ...base, kind: 'reroll', sent: true })).toEqual({ visible: true, label: '換 seed 重生', disabled: false, note: null })
  })
})
