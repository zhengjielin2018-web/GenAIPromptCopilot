import { describe, it, expect } from 'vitest'
import { isFinished, showsImage, isRealistic, statusText, renderButton, verdictMark, toastText } from '../lib/render'
import type { RenderView } from '../types/api'

const view = (over: Partial<RenderView>): RenderView => ({
  renderId: 'r1', turnIndex: 3, status: 'queued', position: null, safety: 'on', message: null,
  selfCheck: { status: 'pending', items: [] },
  timings: { queueMs: null, delayMs: null, executionMs: null, reviewMs: null, selfCheckMs: null },
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
    expect(statusText(view({ status: 'self_checking' }))).toBe('自評中')
    expect(statusText(view({ status: 'done' }))).toBe('')
    expect(statusText(view({ status: 'blocked', message: '預覽圖被判定為不當內容，沒有顯示' }))).toBe('預覽圖被判定為不當內容，沒有顯示')
    expect(statusText(view({ status: 'failed', message: null }))).toBe('預覽沒有完成')
  })

  it('marks verdicts and picks the toast text', () => {
    expect([verdictMark('present'), verdictMark('absent'), verdictMark('unclear')]).toEqual(['✓', '✗', '？'])
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
