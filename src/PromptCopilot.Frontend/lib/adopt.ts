import type { AdoptRequest, FacetState, RecommendedSet } from '../types/api'
import type { Entry } from './reducer'

export type AdoptChoice = 'mine' | 'set'
export interface AdoptRow { facetId: string; label: string; state: FacetState; setTags: string[]; available: boolean; choice: AdoptChoice }

/** 對照表的列（設計 §7.2）：notApplicable 不列；預設 missing→照它的、covered／waived→留我的；這套沒有的列停用、固定留我的。
 *  facetStates 是目前的狀態：推薦事件的 state 停在出卡那一輪，之後的討論輪可能已經改過，有目前的就以目前的為準。 */
export function adoptRows(set: RecommendedSet, facetStates?: Record<string, FacetState>): AdoptRow[] {
  return set.facets.map(f => ({ f, state: facetStates?.[f.facetId] ?? f.state })).filter(x => x.state !== 'notApplicable').map(({ f, state }) => {
    const available = f.tags.length > 0
    return { facetId: f.facetId, label: f.label, state, setTags: f.tags, available, choice: available && state === 'missing' ? 'set' : 'mine' }
  })
}

export function setChoice(rows: AdoptRow[], facetId: string, choice: AdoptChoice): AdoptRow[] {
  return rows.map(r => (r.facetId === facetId && r.available ? { ...r, choice } : r))
}

/** 「整套照它的」：全部可用的列切到照它的。 */
export function takeAll(rows: AdoptRow[]): AdoptRow[] {
  return rows.map(r => (r.available ? { ...r, choice: 'set' } : r))
}

export function adoptPayload(presetId: number, dimension: string, rows: AdoptRow[]): AdoptRequest | null {
  const take = rows.filter(r => r.choice === 'set').map(r => r.facetId)
  return take.length ? { presetId, dimension, take } : null
}

/** 只有最新一張追問卡或定稿卡上的推薦可以採用：舊卡的狀態已失效。中間的討論氣泡不出新卡、不算新結果，
 *  但 Discuss 可能改了 facet 狀態，所以對照表讀的是目前的狀態（adoptRows 的 facetStates），不是卡上那份。 */
export function latestRecommendableTurn(transcript: Entry[]): number | null {
  for (let i = transcript.length - 1; i >= 0; i--) {
    const e = transcript[i]
    if (e.kind === 'final' && (e.data.kind === 'ask' || e.data.kind === 'finalized')) return e.turnIndex
  }
  return null
}

/** 送出當下的使用者泡泡文字；session 事件到了會換成伺服器組的那句。 */
export function adoptPlaceholder(title: string): string { return `採用〈${title}〉…` }

/** 對照表「你的」欄：只講狀態，不猜使用者的原話（伺服器沒有逐 facet 的原話）。 */
export function mineLabel(state: FacetState): string {
  return { covered: '保留你講的', missing: '空白', waived: '不指定', notApplicable: '不適用' }[state]
}
