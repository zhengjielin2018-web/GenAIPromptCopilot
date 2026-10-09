export type FacetState = 'covered' | 'missing' | 'waived' | 'notApplicable'
export type SessionStatus = 'Collecting' | 'Finalized'

/** 後端 SseWriter 用 WhenWritingNull：值為 null 的欄位會整個省略，所以可為 null 的欄位在線上也可能不存在。 */
export interface OptionItem { label: string; tags: string; presetId?: number | null }
export interface AskItem { dimension: string; question: string; missingFacetIds: string[]; options: OptionItem[] }
/** sourceRef：資料來源識別（`civitai:12345:0`），縮圖依前綴標來源名。2026-09-25 加的，之前存進 sessionStorage 的沒有。 */
export interface PresetRef { id: number; title: string; imageUrl?: string | null; sourceRef?: string | null }

export type RetrievalMode = 'on' | 'off'

/** tool_result.detail（2026-09-25 起）：SearchPresets 與 SearchSimilarPrompts 各一種，用事件的 name 分辨。不含 snippet 本文。 */
export interface SearchPresetsHit { id: number; title: string; band: string; dist: number; usable: boolean; facets: Record<string, string> }
export interface SearchPresetsItem {
  dimension: string; facetId?: string | null; label: string; query: string
  grounded: boolean; poolSize: number; k: number; error?: string | null; hits: SearchPresetsHit[]
  /** facet 項目這次用的英文 tag（2026-09-29）；沒有、或舊資料沒這欄就當沒有 */
  tags?: string | null
  /** facet：facet 向量子表；preset：整套向量（維度項目，或子表沒資料的退路）。舊資料沒這欄當 preset */
  method?: 'facet' | 'preset'
}
export interface SearchPresetsDetail { items: SearchPresetsItem[] }
export interface SearchSimilarHit { intent: string; profile: string; dist: number }
export interface SearchSimilarDetail { hits: SearchSimilarHit[] }
export type ToolDetail = SearchPresetsDetail | SearchSimilarDetail
export function isPresetsDetail(name: string, d: ToolDetail | null | undefined): d is SearchPresetsDetail { return name === 'SearchPresets' && !!d && 'items' in d }
export function isSimilarDetail(name: string, d: ToolDetail | null | undefined): d is SearchSimilarDetail { return name === 'SearchSimilarPrompts' && !!d && 'hits' in d }

/** 定稿 tag 的來源，伺服器定稿時比對 ledger 算的。rag：知識庫片段（presetIds 依片段被撈到的先後，presetTitle 與 sourceRef 取第一個）；
 *  llm：模型生成；base：基礎畫質詞／負向詞。presetTitle、sourceRef 為 null 時線上省略。
 *  adopted（2026-09-25）：使用者採用推薦組合帶進來的，presetIds 是那套的 id。 */
export interface TagSource { tag: string; origin: 'rag' | 'llm' | 'base' | 'adopted'; presetIds: number[]; presetTitle?: string | null; sourceRef?: string | null }

/** positiveSources／negativeSources 是 2026-09-25 加的：之前存進 sessionStorage 的定稿卡沒有，畫面退回純文字。 */
export interface FinalizedData {
  kind: 'finalized'; positive: string; negative: string; tips: string; intentSummary: string
  positiveSources?: TagSource[]; negativeSources?: TagSource[]
}
/** confirm（2026-10-05，先確認再動手設計 §4）：確認卡；choices 沒有歧義時是空陣列。 */
export type FinalData =
  | { kind: 'ask'; preamble: string; asks: AskItem[] }
  | { kind: 'message'; message: string; options?: OptionItem[] }
  | FinalizedData
  | { kind: 'save_consent_requested' }
  | { kind: 'confirm'; message: string; choices: string[] }

/** 推薦組法（2026-09-30）：定稿卡每套的理由；追問卡沒有 */
export type SetReason = 'anchored' | 'similar' | 'query' | 'explore'

/** 整套組合推薦（2026-09-25，設計 §5.4）：跟在 final 之後，掛在該輪的追問卡／定稿卡上。facets 列該維度全部 facet，state 是本輪結束時的四態。
 *  reason／anchorTags／rank／prob（2026-09-30）：只有定稿卡有。batch 是前端蓋上去的：這套來自第幾批（換一批往右接） */
export interface RecommendedFacet { facetId: string; label: string; state: FacetState; tags: string[] }
export interface RecommendedSet { presetId: number; title: string; imageUrl?: string | null; sourceRef?: string | null; dist: number; facets: RecommendedFacet[]
  reason?: SetReason | null; anchorTags?: string[] | null; rank?: number | null; prob?: number | null; batch?: number | null }
/** similar（2026-09-29）：字面錨抓不到、改用 facet 向量離錨夠近的組合；與 anchored 不會同時為 true。舊資料沒這欄當 false。
 *  batch（2026-09-30）：定稿卡目前最新是第幾批；追問卡沒有 */
export interface RecommendedDimension { dimension: string; label: string; anchored: boolean; similar?: boolean; anchorTags: string[]; sets: RecommendedSet[]; batch?: number | null }
export interface Recommendations { turnIndex: number; dimensions: RecommendedDimension[] }

/** POST /api/sessions/{id}/messages 的 adopt：照它的 facet 清單，其餘該維度 facet 保留使用者原本的。
 *  batch（2026-09-30）：這套來自定稿卡第幾批，只給量測用 */
export interface AdoptRequest { presetId: number; dimension: string; take: string[]; batch?: number }

/** POST /api/sessions/{id}/messages 的 confirm（先確認再動手設計 §3.5）：按下確認卡。沒有選項的卡 choice 是 null。 */
export interface ConfirmRequest { turnIndex: number; choice: number | null }

export type AgentEvent =
  | { type: 'session'; sessionId: string; turnIndex: number; status: SessionStatus; text?: string | null }
  | { type: 'tool_call'; callId: string; name: string; argsSummary: string }
  | { type: 'tool_result'; callId: string; name: string; summary: string; presets?: PresetRef[]; detail?: ToolDetail }
  | { type: 'dimensions'; profile?: string | null; facetStates: Record<string, FacetState>; facetTags?: Record<string, string> }
  | { type: 'token'; text: string }
  | ({ type: 'final' } & FinalData)
  | ({ type: 'recommendations' } & Recommendations)
  | { type: 'blocked'; reason: string; message: string }
  | { type: 'error'; code: string; message: string }

export type AgentEventType = AgentEvent['type']
export const AGENT_EVENT_TYPES = ['session', 'tool_call', 'tool_result', 'dimensions', 'token', 'final', 'recommendations', 'blocked', 'error'] as const satisfies readonly AgentEventType[]

/** POST /api/sessions */
export interface SessionCreated { sessionId: string; retrieval: RetrievalMode }

/** GET /api/sessions/{id} */
export interface SessionSnapshotDto {
  sessionId: string
  status: SessionStatus
  profile: string | null
  turnIndex: number
  askCount: number
  askLimit: number
  facetStates: Record<string, FacetState>
  /** turnIndex：哪一輪定的稿（2026-10-09，生成預覽要帶它）；舊後端沒有 */
  lastFinal: (Omit<FinalizedData, 'kind'> & { turnIndex?: number }) | null
  /** 舊後端沒有這個欄位，讀的一方當成 on */
  retrieval?: RetrievalMode
  /** 模型給每個已涵蓋 facet 的英文 tag（2026-09-25）；舊後端沒有 */
  facetTags?: Record<string, string>
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
  /** tag → facet 拆分（2026-09-25）；尚未回填時是 null（後端照樣輸出這個欄位，不省略）；舊後端沒有這個欄位 */
  facetTags?: Record<string, string[]> | null
}

/** 終止型 tool 與純狀態 tool 不發 tool_result；卡片在輪次結束時收尾。終止型的內容由 final 條目呈現，不另畫卡片。 */
export const TERMINAL_TOOLS: ReadonlySet<string> = new Set(['AskUser', 'Discuss', 'FinalizePrompt', 'RequestSaveConsent', 'Confirm'])

/** 定稿後生成預覽（2026-10-09，預覽設計 §5.2） */
export type RenderStatus = 'queued' | 'generating' | 'reviewing' | 'self_checking' | 'done' | 'failed' | 'blocked'
export type SelfCheckVerdictKind = 'present' | 'absent' | 'unclear'
export interface SelfCheckItemView { facetId: string; label: string; tag: string; verdict: SelfCheckVerdictKind; reason: string }
export interface RenderView {
  renderId: string; turnIndex: number; status: RenderStatus; position: number | null; safety: 'on' | 'off'; message: string | null
  selfCheck: { status: 'pending' | 'ok' | 'unavailable'; items: SelfCheckItemView[] }
  timings: { queueMs: number | null; delayMs: number | null; executionMs: number | null; reviewMs: number | null; selfCheckMs: number | null }
}
