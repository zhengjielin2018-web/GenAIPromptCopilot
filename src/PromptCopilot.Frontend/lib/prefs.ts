import type { StorageLike } from './persist'

/** 跨對話的偏好：用不用知識庫（只影響新開的對話）、要不要顯示檢索細節（純顯示）。存 localStorage，與 pc.session（sessionStorage）分開。 */
export interface Prefs { v: 1; retrieval: 'on' | 'off'; showTrace: boolean }

export const DEFAULT_PREFS: Prefs = { v: 1, retrieval: 'on', showTrace: false }
const KEY = 'pc.prefs'

function defaultStorage(): StorageLike | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage } catch { return null }
}

/** 任何錯誤（隱私模式、被停用、壞資料、不認得的值）都回預設。 */
export function loadPrefs(storage: StorageLike | null = defaultStorage()): Prefs {
  try {
    const raw = storage?.getItem(KEY)
    if (!raw) return { ...DEFAULT_PREFS }
    const p = JSON.parse(raw)
    if (!p || p.v !== 1) return { ...DEFAULT_PREFS }
    if (p.retrieval !== 'on' && p.retrieval !== 'off') return { ...DEFAULT_PREFS }
    if (typeof p.showTrace !== 'boolean') return { ...DEFAULT_PREFS }
    return { v: 1, retrieval: p.retrieval, showTrace: p.showTrace }
  } catch { return { ...DEFAULT_PREFS } }
}

export function savePrefs(p: Prefs, storage: StorageLike | null = defaultStorage()): void {
  try { storage?.setItem(KEY, JSON.stringify(p)) } catch { /* 存不進去就算了，下次開頁回預設 */ }
}
