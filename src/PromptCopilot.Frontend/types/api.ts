export type FacetState = 'covered' | 'missing' | 'waived' | 'notApplicable'
export type SessionStatus = 'Collecting' | 'Finalized'

/** 後端 SseWriter 用 WhenWritingNull：值為 null 的欄位會整個省略，所以可為 null 的欄位在線上也可能不存在。 */
export interface OptionItem { label: string; tags: string; presetId?: number | null }
export interface AskItem { dimension: string; question: string; missingFacetIds: string[]; options: OptionItem[] }
/** sourceRef：資料來源識別（`civitai:12345:0`），縮圖依前綴標來源名。2026-09-25 加的，之前存進 sessionStorage 的沒有。 */
export interface PresetRef { id: number; title: string; imageUrl?: string | null; sourceRef?: string | null }

/** 定稿 tag 的來源，伺服器定稿時比對 ledger 算的。rag：知識庫片段（presetIds 依片段被撈到的先後，presetTitle 與 sourceRef 取第一個）；
 *  llm：模型生成；base：基礎畫質詞／負向詞。presetTitle、sourceRef 為 null 時線上省略。 */
export interface TagSource { tag: string; origin: 'rag' | 'llm' | 'base'; presetIds: number[]; presetTitle?: string | null; sourceRef?: string | null }

/** positiveSources／negativeSources 是 2026-09-25 加的：之前存進 sessionStorage 的定稿卡沒有，畫面退回純文字。 */
export interface FinalizedData {
  kind: 'finalized'; positive: string; negative: string; tips: string; intentSummary: string
  positiveSources?: TagSource[]; negativeSources?: TagSource[]
}
export type FinalData =
  | { kind: 'ask'; preamble: string; asks: AskItem[] }
  | { kind: 'message'; message: string; options?: OptionItem[] }
  | FinalizedData
  | { kind: 'save_consent_requested' }

export type AgentEvent =
  | { type: 'session'; sessionId: string; turnIndex: number; status: SessionStatus }
  | { type: 'tool_call'; callId: string; name: string; argsSummary: string }
  | { type: 'tool_result'; callId: string; name: string; summary: string; presets?: PresetRef[] }
  | { type: 'dimensions'; profile?: string | null; facetStates: Record<string, FacetState> }
  | { type: 'token'; text: string }
  | ({ type: 'final' } & FinalData)
  | { type: 'blocked'; reason: string; message: string }
  | { type: 'error'; code: string; message: string }

export type AgentEventType = AgentEvent['type']
export const AGENT_EVENT_TYPES = ['session', 'tool_call', 'tool_result', 'dimensions', 'token', 'final', 'blocked', 'error'] as const satisfies readonly AgentEventType[]

/** GET /api/sessions/{id} */
export interface SessionSnapshotDto {
  sessionId: string
  status: SessionStatus
  profile: string | null
  turnIndex: number
  askCount: number
  askLimit: number
  facetStates: Record<string, FacetState>
  lastFinal: Omit<FinalizedData, 'kind'> | null
}

/** GET /api/config/facets */
export interface FacetCatalog {
  dimensions: { key: string; label: string; facets: { id: string; label: string; hint: string }[] }[]
  profiles: Record<string, { labels: Record<string, string>; dimensions: Record<string, string[]> }>
}

/** GET /api/presets/{id} */
export interface PresetDetail {
  id: number
  title: string
  category: string
  description: string
  tags: string[]
  facetIds: string[]
  promptSnippet: string
  negativeSnippet: string | null
  imageUrl: string | null
  sourceRef: string | null
  sourceUrl: string | null
}

/** 終止型 tool 與純狀態 tool 不發 tool_result；卡片在輪次結束時收尾。終止型的內容由 final 條目呈現，不另畫卡片。 */
export const TERMINAL_TOOLS: ReadonlySet<string> = new Set(['AskUser', 'Discuss', 'FinalizePrompt', 'RequestSaveConsent'])
