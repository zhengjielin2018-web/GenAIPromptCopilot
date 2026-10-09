import { describe, it, expect } from 'vitest'
import { beginRequest, requestFailed, requestAccepted, resumed, gaveUp, applyView, isInFlight } from '../lib/renderSlot'
import type { RenderView } from '../types/api'

const view = (status: RenderView['status']): RenderView => ({
  renderId: 'r1', turnIndex: 3, status, position: null, safety: 'on', message: null,
  selfCheck: { status: 'pending', items: [] }, timings: { queueMs: null, delayMs: null, executionMs: null, reviewMs: null, selfCheckMs: null },
})
const done = () => applyView(requestAccepted('r1'), view('done'), true).slot

describe('render slot', () => {
  // 再按一次被 429／503／409 或斷線擋下：已經好的那張要留著，訊息只放在按鈕下方（預覽設計 §8）
  it('keeps the existing preview when a new request is refused', () => {
    const s = requestFailed(beginRequest(done()), '這段對話的預覽張數已達上限（10 張）')
    expect(s.renderId).toBe('r1')
    expect(s.view?.status).toBe('done')
    expect(s.requestError).toBe('這段對話的預覽張數已達上限（10 張）')
    expect(s.requesting).toBe(false)
  })

  it('replaces the preview only once the new one is accepted', () => {
    const pending = beginRequest(done())
    expect(pending.view?.status).toBe('done')
    expect(pending.requesting).toBe(true)
    const s = requestAccepted('r2')
    expect(s).toMatchObject({ renderId: 'r2', view: null, requestError: null, requesting: false, fresh: true })
  })

  it('keeps the image when polling gives up', () => {
    const shown = applyView(requestAccepted('r1'), view('self_checking'), true).slot
    const s = gaveUp(shown, '查不到預覽的狀態，重新整理頁面再看看')
    expect(s.view?.status).toBe('self_checking')
    expect(s.error).toBe('查不到預覽的狀態，重新整理頁面再看看')
  })

  it('counts as in flight across cards until the preview finishes, fails to poll or expires', () => {
    expect(isInFlight({})).toBe(false)
    expect(isInFlight({ 3: done(), 5: beginRequest(undefined) })).toBe(true)
    expect(isInFlight({ 3: done(), 5: requestAccepted('r2') })).toBe(true)
    expect(isInFlight({ 5: applyView(requestAccepted('r2'), view('generating'), true).slot })).toBe(true)
    expect(isInFlight({ 5: gaveUp(applyView(requestAccepted('r2'), view('generating'), true).slot, 'x') })).toBe(false)
    expect(isInFlight({ 5: { ...requestAccepted('r2'), expired: true } })).toBe(false)
    expect(isInFlight({ 3: requestFailed(beginRequest(done()), 'x') })).toBe(false)
  })

  it('toasts only when a preview finishes off-screen after it was seen running or requested here', () => {
    expect(applyView(requestAccepted('r1'), view('done'), false).toast).toBe(true)        // 這頁按的、在畫面外好了
    expect(applyView(requestAccepted('r1'), view('done'), true).toast).toBe(false)        // 卡在畫面內
    expect(applyView(resumed('r1'), view('done'), false).toast).toBe(false)               // 重新整理後接回一張早就好的
    const running = applyView(resumed('r1'), view('generating'), false).slot
    expect(applyView(running, view('blocked'), false).toast).toBe(true)                   // 接回時還在跑，之後在畫面外結束
    const finished = applyView(requestAccepted('r1'), view('done'), false).slot
    expect(applyView(finished, view('done'), false).toast).toBe(false)                    // 同一個結束狀態不重複跳
  })
})
