import type { RenderView } from '../types/api'
import { isFinished } from './render'

/** 一張定稿卡最新一次的預覽（預覽設計 §8）。store 只做 I/O，狀態怎麼變都在這裡（可測）。
 *  error：輪詢放棄時的訊息（圖照樣留著）；requestError：按鈕送出被拒絕或斷線的訊息（顯示在按鈕下方，不取代已經好的那張）。
 *  fresh：這個頁面上按出來的（重新整理後接回的不算）——只有 fresh 的、或看過它還沒結束的，結束時才跳提示。
 *  fixSent：這張的「請助理修改」已經按過（修正建議設計 §8），換成新的一張就重設。 */
export interface RenderSlot {
  renderId: string | null
  view: RenderView | null
  error: string | null
  requestError: string | null
  expired: boolean
  requesting: boolean
  fresh: boolean
  fixSent: boolean
}

const EMPTY: RenderSlot = { renderId: null, view: null, error: null, requestError: null, expired: false, requesting: false, fresh: false, fixSent: false }

/** 按下「生成預覽」：拿到 202 之前，卡上原本那張照樣留著。 */
export function beginRequest(prev: RenderSlot | undefined): RenderSlot {
  return { ...(prev ?? EMPTY), requesting: true, requestError: null }
}

/** 被拒絕（409／429／503）或斷線：舊的那張不動，訊息放在按鈕下方。 */
export function requestFailed(prev: RenderSlot, error: string): RenderSlot {
  return { ...prev, requesting: false, requestError: error }
}

/** 收下了（202）：換成新的這張。 */
export function requestAccepted(renderId: string): RenderSlot {
  return { ...EMPTY, renderId, fresh: true }
}

/** 重新整理後從 sessionStorage 接回。 */
export function resumed(renderId: string): RenderSlot {
  return { ...EMPTY, renderId }
}

/** 輪詢連續失敗太多次：已經顯示的圖留著，另外寫一行。 */
export function gaveUp(prev: RenderSlot, error: string): RenderSlot {
  return { ...prev, error }
}

/** 輪詢拿到一個狀態。toast：這次剛結束、之前看過它在跑（或是這頁按的），而且卡不在畫面內。 */
export function applyView(prev: RenderSlot, v: RenderView, cardVisible: boolean): { slot: RenderSlot; toast: boolean } {
  const wasRunning = prev.fresh || (!!prev.view && !isFinished(prev.view.status))
  const finished = isFinished(v.status)
  return { slot: { ...prev, view: v, fresh: finished ? false : prev.fresh }, toast: finished && wasRunning && !cardVisible }
}

/** 這個 session 有一張預覽還沒結束（不管在哪張卡上）：所有按鈕停用（預覽設計 §8）。 */
export function isInFlight(slots: Record<number, RenderSlot>): boolean {
  return Object.values(slots).some(r =>
    r.requesting || (!!r.renderId && !r.expired && !r.error && (!r.view || !isFinished(r.view.status))))
}

/** 按了「請助理修改」：同一張的建議不再送第二次。 */
export function markFixSent(prev: RenderSlot): RenderSlot {
  return { ...prev, fixSent: true }
}
