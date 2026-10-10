import type { RenderStatus, RenderView, SelfCheckItemView, SelfCheckVerdictKind, SuggestionKind } from '../types/api'

const FINISHED: ReadonlySet<RenderStatus> = new Set(['done', 'failed', 'blocked'])
export function isFinished(status: RenderStatus): boolean { return FINISHED.has(status) }

/** 審查開著時，self_checking 表示審查已經過了，圖可以拿（預覽設計 §5.2）。 */
export function showsImage(status: RenderStatus): boolean { return status === 'self_checking' || status === 'done' }

/** 「raw photo」不另列：整詞比對下 photo 已經抓得到。 */
const REALISTIC = ['photorealistic', 'realistic', 'photo', 'photograph']

/** 寫實風（預覽設計 §4.3）：style.genre 有 tag 就看它，沒有就看正向詞；整詞比對、不分大小寫。判錯只是多或少一行提醒。 */
export function isRealistic(facetTags: Record<string, string>, positive: string): boolean {
  const text = (facetTags['style.genre'] ?? positive).toLowerCase()
  return REALISTIC.some(w => new RegExp(`(^|[^a-z])${w}($|[^a-z])`).test(text))
}

export function statusText(v: RenderView): string {
  switch (v.status) {
    case 'queued': return v.position ? `排第 ${v.position} 位` : '排隊中'
    case 'generating': return '生圖中（閒置後第一張可能要一兩分鐘）'
    case 'reviewing': return '審查圖片中'
    case 'self_checking': return '評分中'
    case 'done': return ''
    default: return v.message ?? '預覽沒有完成'
  }
}

export type RenderButton = { visible: false } | { visible: true; disabled: boolean; note: string | null }

/** 使用者按了也只會撞到 409 的情況，先在前端擋（預覽設計 §8）。有預覽沒結束的優先：它要寫字說明。 */
export function renderButton(o: { enabled: boolean; isLatest: boolean; busy: boolean; inFlight: boolean }): RenderButton {
  if (!o.enabled || !o.isLatest) return { visible: false }
  if (o.inFlight) return { visible: true, disabled: true, note: '上一張還在生成' }
  if (o.busy) return { visible: true, disabled: true, note: null }
  return { visible: true, disabled: false, note: null }
}

export function verdictMark(v: SelfCheckVerdictKind): string { return v === 'met' ? '✓' : v === 'unmet' ? '✗' : '？' }

/** 問題標籤（符合度設計 §9）：prompt 漏了要改 prompt；沒畫出來是這次生圖沒畫出 prompt 寫的東西。 */
export function issueLabel(i: SelfCheckItemView): { text: string; hint: string } | null {
  if (i.issue === 'prompt_missing') return { text: 'prompt 漏了', hint: '可以請助理補進 prompt' }
  if (i.issue === 'not_rendered') return { text: '沒畫出來', hint: 'prompt 有寫，但這次沒畫出來' }
  return null
}

/** tag 欄：負向詞後面標「（負向）」。prompt 沒寫卻畫出來了是剛好，換 seed 可能就不見。 */
export function tagText(i: SelfCheckItemView): string {
  const parts = [...i.tags, ...i.negativeTags.map(t => `${t}（負向）`)]
  if (parts.length > 0) return parts.join(', ')
  return i.verdict === 'met' ? 'prompt 沒寫，剛好畫出來' : 'prompt 沒寫'
}

/** 「你的要求」與「模型幫你挑的」分開；各自照清單原本的順序，不把有問題的排到前面。 */
export function splitItems(items: SelfCheckItemView[]): { user: SelfCheckItemView[]; delegated: SelfCheckItemView[] } {
  return { user: items.filter(i => i.source === 'user'), delegated: items.filter(i => i.source === 'delegated') }
}

export function toastText(status: RenderStatus): string { return status === 'done' ? '預覽好了' : '預覽沒有完成' }

export type SuggestionButton = { visible: false } | { visible: true; label: string; disabled: boolean; note: string | null }

/** 修正建議的按鈕（修正建議設計 §8）：只在最新的定稿卡；「請助理修改」按過一次就停用（只記在前端，重新整理後恢復）。 */
export function suggestionButton(o: { kind: SuggestionKind; isLatest: boolean; busy: boolean; inFlight: boolean; sent: boolean }): SuggestionButton {
  if (o.kind === 'none' || !o.isLatest) return { visible: false }
  const label = o.kind === 'reroll' ? '換 seed 重生' : '請助理修改'
  if (o.kind !== 'reroll' && o.sent) return { visible: true, label, disabled: true, note: '已送出修正' }
  if (o.inFlight) return { visible: true, label, disabled: true, note: '上一張還在生成' }
  if (o.busy) return { visible: true, label, disabled: true, note: null }
  return { visible: true, label, disabled: false, note: null }
}
