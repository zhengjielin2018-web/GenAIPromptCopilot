import type { Entry } from './reducer'
import type { FinalData } from '../types/api'

/** 沒有選項的確認卡按下去時，伺服器當使用者訊息的那一句（後端 ConfirmValidator.AcceptText）。 */
export const ACCEPT_TEXT = '對，就這樣'

/** 可以按的確認卡（先確認再動手設計 §7）：從尾端往回找，先碰到確認卡就是它；先碰到追問卡或定稿卡代表已經動過手，沒有可按的。
 *  討論泡泡、存檔確認、失敗條目、使用者泡泡、工具卡都不改變待確認——跟後端 §3.4 同一條規則；不一致時伺服器的 409 會說明。
 *  staleTurns：伺服器已對那張卡回過 409（沒有待確認的內容／只有最新一張可以按），再按也不會成功，視同沒有可按的卡。 */
export function pendingConfirmTurn(transcript: Entry[], staleTurns: readonly number[] = []): number | null {
  for (let i = transcript.length - 1; i >= 0; i--) {
    const e = transcript[i]
    if (e.kind !== 'final') continue
    if (e.data.kind === 'confirm') return staleTurns.includes(e.turnIndex) ? null : e.turnIndex
    if (e.data.kind === 'ask' || e.data.kind === 'finalized') return null
  }
  return null
}

/** 按下按鈕時泡泡先顯示的字；伺服器的 session 事件帶回同一句。 */
export function confirmDisplay(data: Extract<FinalData, { kind: 'confirm' }>, choice: number | null): string {
  return choice === null ? ACCEPT_TEXT : data.choices[choice] ?? ACCEPT_TEXT
}
