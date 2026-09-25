import { TERMINAL_TOOLS, type AgentEvent, type FacetState, type FinalData, type FinalizedData, type PresetRef, type Recommendations, type RetrievalMode, type SessionSnapshotDto, type SessionStatus, type ToolDetail } from '../types/api'

export interface UserEntry { kind: 'user'; text: string }
/** detail 是 2026-09-25 加的：之前存進 sessionStorage 的條目沒有這個鍵。 */
export interface ToolEntry { kind: 'tool'; callId: string; name: string; argsSummary: string; summary: string | null; presets: PresetRef[]; detail?: ToolDetail | null; done: boolean }
/** recommendations 是 2026-09-25 加的：舊條目沒有這個鍵。 */
export interface FinalEntry { kind: 'final'; turnIndex: number; data: FinalData; recommendations?: Recommendations | null }
export interface FailureEntry { kind: 'failure'; source: 'error' | 'blocked' | 'stream_ended' | 'http'; code: string; message: string; originalText: string }
export type Entry = UserEntry | ToolEntry | FinalEntry | FailureEntry

export interface ChatState {
  sessionId: string | null
  turnIndex: number
  status: SessionStatus
  profile: string | null
  facetStates: Record<string, FacetState>
  /** 模型給每個已涵蓋 facet 的英文 tag；儀表板 chip 的 title 顯示 */
  facetTags: Record<string, string>
  askCount: number
  askLimit: number
  /** 這段對話建立時定下的知識庫開關；對話中途不會變 */
  retrieval: RetrievalMode
  transcript: Entry[]
  lastFinal: FinalizedData | null
  /** ask 之後高亮的維度，下一個 session 事件清掉 */
  highlighted: string[]
  pending: { text: string; settled: boolean; snapshot: ChatState } | null
}

export function initialState(): ChatState {
  return {
    sessionId: null, turnIndex: 0, status: 'Collecting', profile: null,
    facetStates: {}, facetTags: {}, askCount: 0, askLimit: 2, retrieval: 'on',
    transcript: [], lastFinal: null, highlighted: [], pending: null,
  }
}

/** 重載重建：權威欄位從 GET /api/sessions/{id}，對話流從 sessionStorage。
 *  送出當下會先存一次對話流，所以輪次中重載時它停在一個沒有下文的 user 條目：那一輪後端不是回滾了、就是已經定稿（由下面補的卡涵蓋），
 *  原文經 draft 回到輸入框，這裡把它拿掉。後端有 LastFinal、對話流卻沒有任何定稿卡時補一張，卡片才畫得出來、也才存得了。 */
export function hydrate(state: ChatState, dto: SessionSnapshotDto, transcript: Entry[]): ChatState {
  const lastFinal: FinalizedData | null = dto.lastFinal ? { kind: 'finalized', ...dto.lastFinal } : null
  const entries = transcript.at(-1)?.kind === 'user' ? transcript.slice(0, -1) : [...transcript]
  if (lastFinal && latestFinalizedTurn(entries) === null) entries.push({ kind: 'final', turnIndex: dto.turnIndex, data: { ...lastFinal } })
  return {
    ...state,
    sessionId: dto.sessionId, status: dto.status, profile: dto.profile, turnIndex: dto.turnIndex,
    askCount: dto.askCount, askLimit: dto.askLimit, facetStates: { ...dto.facetStates },
    // 舊後端沒有 facetTags
    facetTags: { ...(dto.facetTags ?? {}) },
    // 舊後端的回應沒有 retrieval：那時還沒有開關，一律是 on
    retrieval: dto.retrieval ?? 'on',
    lastFinal, transcript: entries, highlighted: [], pending: null,
  }
}

/** 送出當下：先快照（不含 pending），再推 user 條目。斷線可能發生在第一個事件之前，所以不能等 session 事件才快照。
 *  用 JSON 複製而不是 structuredClone：store 傳進來的是 Vue 的 reactive proxy，structuredClone 會丟 DataCloneError；
 *  狀態全是純資料（沒有 undefined／Date／函式），JSON 來回不失真。 */
export function beginTurn(state: ChatState, text: string): ChatState {
  const snapshot: ChatState = JSON.parse(JSON.stringify({ ...state, pending: null }))
  return { ...state, transcript: [...state.transcript, { kind: 'user', text }], pending: { text, settled: false, snapshot } }
}

export function applyEvent(state: ChatState, ev: AgentEvent): ChatState {
  switch (ev.type) {
    case 'session': {
      const next = { ...state, sessionId: ev.sessionId, turnIndex: ev.turnIndex, status: ev.status, highlighted: [] }
      // 採用輪：泡泡先放暫代字，這裡換成伺服器組的那句（只換這一輪還在等的那則）
      if (!ev.text || !state.pending) return next
      const idx = findLastIndex(state.transcript, e => e.kind === 'user')
      if (idx < 0) return next
      const transcript = state.transcript.slice()
      transcript[idx] = { kind: 'user', text: ev.text }
      return { ...next, transcript }
    }

    case 'tool_call': {
      if (TERMINAL_TOOLS.has(ev.name)) return state
      const entry: ToolEntry = { kind: 'tool', callId: ev.callId, name: ev.name, argsSummary: ev.argsSummary, summary: null, presets: [], detail: null, done: false }
      return { ...state, transcript: [...state.transcript, entry] }
    }

    case 'tool_result': {
      const idx = state.transcript.findIndex(e => e.kind === 'tool' && e.callId === ev.callId)
      const presets = ev.presets ?? []
      // 沒有 detail 的工具（或舊後端）線上不帶這個鍵；補回 null，state 裡才不會出現 undefined
      const detail = ev.detail ?? null
      if (idx < 0) {
        const orphan: ToolEntry = { kind: 'tool', callId: ev.callId, name: ev.name, argsSummary: '', summary: ev.summary, presets, detail, done: true }
        return { ...state, transcript: [...state.transcript, orphan] }
      }
      const transcript = state.transcript.slice()
      transcript[idx] = { ...(transcript[idx] as ToolEntry), summary: ev.summary, presets, detail, done: true }
      return { ...state, transcript }
    }

    case 'dimensions':
      // 線上省略 null（WhenWritingNull），缺鍵要補回 null，否則 state 裡會出現 undefined
      return { ...state, profile: ev.profile ?? null, facetStates: { ...ev.facetStates }, facetTags: { ...(ev.facetTags ?? {}) } }

    case 'token': {
      const idx = findLastIndex(state.transcript, e => e.kind === 'final' && e.data.kind === 'message')
      if (idx < 0) return state
      const transcript = state.transcript.slice()
      const entry = transcript[idx] as FinalEntry
      if (entry.data.kind !== 'message') return state
      transcript[idx] = { ...entry, data: { ...entry.data, message: entry.data.message + ev.text } }
      return { ...state, transcript }
    }

    case 'final': {
      // 拿掉 type 欄位；用 ev.kind 收窄（rest 物件會失去 discriminated union）
      const { type: _t, ...rest } = ev
      const data = rest as FinalData
      const entry: FinalEntry = { kind: 'final', turnIndex: state.turnIndex, data }
      let next: ChatState = { ...state, transcript: [...state.transcript, entry], pending: settle(state.pending) }
      if (ev.kind === 'ask') {
        next = { ...next, askCount: state.askCount + 1, highlighted: ev.asks.map(a => a.dimension) }
      } else if (ev.kind === 'finalized') {
        next = { ...next, lastFinal: data as FinalizedData, status: 'Finalized' }
      }
      return next
    }

    case 'recommendations': {
      const idx = findLastIndex(state.transcript, e => e.kind === 'final' && e.turnIndex === ev.turnIndex)
      if (idx < 0) return state
      const { type: _t, ...recs } = ev
      const transcript = state.transcript.slice()
      transcript[idx] = { ...(transcript[idx] as FinalEntry), recommendations: recs as Recommendations }
      return { ...state, transcript }
    }

    case 'error':
      return fail(state, 'error', ev.code, ev.message)

    case 'blocked':
      return fail(state, 'blocked', ev.reason, ev.message)

    default:
      return state
  }
}

/** 串流關閉。沒收到終止事件就當 stream_ended（後端 RequestAborted 會回滾，這裡對稱）；所有 tool 卡片收尾。 */
export function endTurn(state: ChatState): ChatState {
  if (!state.pending) return state
  const s = state.pending.settled ? state : fail(state, 'stream_ended', 'stream_ended', '連線在這一輪結束前中斷了。已還原到送出前的狀態，可以直接再送一次。')
  return {
    ...s,
    pending: null,
    transcript: s.transcript.map(e => (e.kind === 'tool' && !e.done ? { ...e, done: true } : e)),
  }
}

/** 對話流裡最新一張定稿卡的輪次。save-to-shared 永遠存後端的 LastFinal，所以只有這一張可以存。 */
export function latestFinalizedTurn(transcript: Entry[]): number | null {
  for (let i = transcript.length - 1; i >= 0; i--) {
    const e = transcript[i]
    if (e.kind === 'final' && e.data.kind === 'finalized') return e.turnIndex
  }
  return null
}

/** 非 200 回應（404／409／5xx）：還原快照並記一筆失敗，這一輪就此結束。 */
export function failHttp(state: ChatState, code: string, message: string): ChatState {
  return { ...fail(state, 'http', code, message), pending: null }
}

function fail(state: ChatState, source: FailureEntry['source'], code: string, message: string): ChatState {
  const p = state.pending
  const base = p ? p.snapshot : state
  const failure: FailureEntry = { kind: 'failure', source, code, message, originalText: p?.text ?? '' }
  return { ...base, transcript: [...base.transcript, failure], pending: p ? { ...p, settled: true } : null }
}

function settle(p: ChatState['pending']): ChatState['pending'] { return p ? { ...p, settled: true } : null }

function findLastIndex<T>(xs: T[], pred: (x: T) => boolean): number {
  for (let i = xs.length - 1; i >= 0; i--) if (pred(xs[i])) return i
  return -1
}
