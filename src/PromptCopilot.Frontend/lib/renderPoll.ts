import type { RenderGetResult } from '../composables/useApi'
import type { RenderView } from '../types/api'
import { isFinished } from './render'

export interface PollDeps {
  get: () => Promise<RenderGetResult>
  onView: (v: RenderView) => void
  /** 404：預覽或 session 已過期（後端重啟、session 過期） */
  onGone: () => void
  /** 連續失敗 maxFailures 次 */
  onGiveUp: () => void
  /** 換了對話或那張卡已經換成新的 renderId：停，而且不回報（不能把舊結果寫進新對話） */
  isCurrent: () => boolean
  setTimer: (fn: () => void, ms: number) => unknown
  intervalMs?: number
  maxFailures?: number
}

/** 輪詢一張預覽到結束（預覽設計 §8）。網路錯誤與非 404 的失敗先忍著，連續太多次才放棄。 */
export function pollRender(d: PollDeps): { stop(): void } {
  const interval = d.intervalMs ?? 1500
  const maxFailures = d.maxFailures ?? 5
  let stopped = false
  let failures = 0
  async function tick() {
    if (stopped || !d.isCurrent()) return
    let r: RenderGetResult | null
    try { r = await d.get() } catch { r = null }
    if (stopped || !d.isCurrent()) return
    if (r?.ok) {
      failures = 0
      d.onView(r.view)
      if (isFinished(r.view.status)) return
    } else if (r && r.status === 404) {
      d.onGone()
      return
    } else if (++failures >= maxFailures) {
      d.onGiveUp()
      return
    }
    d.setTimer(() => { void tick() }, interval)
  }
  void tick()
  return { stop() { stopped = true } }
}
