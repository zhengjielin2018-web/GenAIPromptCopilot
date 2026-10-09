import type { RenderGetResult } from '../composables/useApi'
import type { RenderView } from '../types/api'
import { isFinished } from './render'

export interface PollDeps {
  get: () => Promise<RenderGetResult>
  onView: (v: RenderView) => void
  /** 404：預覽或 session 已過期（後端重啟、session 過期） */
  onGone: () => void
  /** failures：連續失敗 maxFailures 次；too_long：輪詢超過 maxDurationMs 還沒結束 */
  onGiveUp: (reason: 'failures' | 'too_long') => void
  /** 換了對話或那張卡已經換成新的 renderId：停，而且不回報（不能把舊結果寫進新對話） */
  isCurrent: () => boolean
  setTimer: (fn: () => void, ms: number) => unknown
  intervalMs?: number
  maxFailures?: number
  /** 總上限。後端最壞約 5 分鐘（預估等待 60 秒＋RunPod 180 秒＋審圖／自評 60 秒），這裡給兩倍：後端卡住時不要永遠輪詢下去 */
  maxDurationMs?: number
  now?: () => number
}

/** 輪詢一張預覽到結束（預覽設計 §8）。網路錯誤與非 404 的失敗先忍著，連續太多次才放棄；總時間超過上限也放棄。 */
export function pollRender(d: PollDeps): { stop(): void } {
  const interval = d.intervalMs ?? 1500
  const maxFailures = d.maxFailures ?? 5
  const maxDuration = d.maxDurationMs ?? 10 * 60_000
  const now = d.now ?? Date.now
  const started = now()
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
      d.onGiveUp('failures')
      return
    }
    if (now() - started >= maxDuration) {
      d.onGiveUp('too_long')
      return
    }
    d.setTimer(() => { void tick() }, interval)
  }
  void tick()
  return { stop() { stopped = true } }
}
