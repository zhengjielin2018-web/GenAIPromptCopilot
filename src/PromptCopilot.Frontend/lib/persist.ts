import type { Entry } from './reducer'

/** savedTurns：已存進共享庫的定稿輪次；draft：輪次進行中送出的原文。兩者是後加的，舊資料沒有，讀的一方當成 [] 與 ''。 */
export interface Persisted { v: 1; sessionId: string; transcript: Entry[]; savedTurns?: number[]; draft?: string }
export interface StorageLike { getItem(k: string): string | null; setItem(k: string, v: string): void; removeItem(k: string): void }

const KEY = 'pc.session'

function defaultStorage(): StorageLike | null {
  try { return typeof sessionStorage === 'undefined' ? null : sessionStorage } catch { return null }
}

/** 只存顯示用的 transcript、sessionId 與少量前端狀態。任何錯誤（隱私模式、被停用、壞資料）都當作沒有。 */
export function loadPersisted(storage: StorageLike | null = defaultStorage()): Persisted | null {
  try {
    const raw = storage?.getItem(KEY)
    if (!raw) return null
    const p = JSON.parse(raw)
    if (!p || p.v !== 1 || typeof p.sessionId !== 'string' || !Array.isArray(p.transcript)) return null
    return p as Persisted
  } catch { return null }
}

export function savePersisted(p: Omit<Persisted, 'v'>, storage: StorageLike | null = defaultStorage()): void {
  try { storage?.setItem(KEY, JSON.stringify({ v: 1, ...p })) } catch { /* 存不進去就算了，重載時會開新對話 */ }
}

export function clearPersisted(storage: StorageLike | null = defaultStorage()): void {
  try { storage?.removeItem(KEY) } catch { /* 同上 */ }
}
