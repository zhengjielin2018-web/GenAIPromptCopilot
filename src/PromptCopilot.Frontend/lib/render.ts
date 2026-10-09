import type { RenderStatus, RenderView, SelfCheckVerdictKind } from '../types/api'

const FINISHED: ReadonlySet<RenderStatus> = new Set(['done', 'failed', 'blocked'])
export function isFinished(status: RenderStatus): boolean { return FINISHED.has(status) }

/** 審查開著時，self_checking 表示審查已經過了，圖可以拿（預覽設計 §5.2）。 */
export function showsImage(status: RenderStatus): boolean { return status === 'self_checking' || status === 'done' }

const REALISTIC = ['photorealistic', 'realistic', 'photo', 'photograph', 'raw photo']

/** 寫實風（預覽設計 §4.3）：style.genre 有 tag 就看它，沒有就看正向詞；整詞比對、不分大小寫。判錯只是多或少一行提醒。 */
export function isRealistic(facetTags: Record<string, string>, positive: string): boolean {
  const text = (facetTags['style.genre'] ?? positive).toLowerCase()
  return REALISTIC.some(w => new RegExp(`(^|[^a-z])${w.replace(' ', '\s+')}($|[^a-z])`).test(text))
}

export function statusText(v: RenderView): string {
  switch (v.status) {
    case 'queued': return v.position ? `排第 ${v.position} 位` : '排隊中'
    case 'generating': return '生圖中（閒置後第一張可能要半分鐘）'
    case 'reviewing': return '審查圖片中'
    case 'self_checking': return '自評中'
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

export function verdictMark(v: SelfCheckVerdictKind): string { return v === 'present' ? '✓' : v === 'absent' ? '✗' : '？' }

export function toastText(status: RenderStatus): string { return status === 'done' ? '預覽好了' : '預覽沒有完成' }
