import type { Entry } from './reducer'

export interface Persisted { v: 1; sessionId: string; transcript: Entry[] }
export interface StorageLike { getItem(k: string): string | null; setItem(k: string, v: string): void; removeItem(k: string): void }

const KEY = 'pc.session'

function defaultStorage(): StorageLike | null {
  try { return typeof sessionStorage === 'undefined' ? null : sessionStorage } catch { return null }
}

/** 只存顯示用的 transcript 與 sessionId。任何錯誤（隱私模式、被停用、壞資料）都當作沒有。 */
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
