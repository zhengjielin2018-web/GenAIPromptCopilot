import { TERMINAL_TOOLS, type AgentEvent, type FacetState, type FinalData, type FinalizedData, type PresetRef, type SessionSnapshotDto, type SessionStatus } from '../types/api'

export interface UserEntry { kind: 'user'; text: string }
export interface ToolEntry { kind: 'tool'; callId: string; name: string; argsSummary: string; summary: string | null; presets: PresetRef[]; done: boolean }
export interface FinalEntry { kind: 'final'; turnIndex: number; data: FinalData }
export interface FailureEntry { kind: 'failure'; source: 'error' | 'blocked' | 'stream_ended' | 'http'; code: string; message: string; originalText: string }
export type Entry = UserEntry | ToolEntry | FinalEntry | FailureEntry

export interface ChatState {
  sessionId: string | null
  turnIndex: number
  status: SessionStatus
  profile: string | null
  facetStates: Record<string, FacetState>
  askCount: number
  askLimit: number
  transcript: Entry[]
  lastFinal: FinalizedData | null
  /** ask 之後高亮的維度，下一個 session 事件清掉 */
  highlighted: string[]
  pending: { text: string; settled: boolean; snapshot: ChatState } | null
}

export function initialState(): ChatState {
  return {
    sessionId: null, turnIndex: 0, status: 'Collecting', profile: null,
    facetStates: {}, askCount: 0, askLimit: 2,
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
    case 'session':
      return { ...state, sessionId: ev.sessionId, turnIndex: ev.turnIndex, status: ev.status, highlighted: [] }

    case 'tool_call': {
      if (TERMINAL_TOOLS.has(ev.name)) return state
      const entry: ToolEntry = { kind: 'tool', callId: ev.callId, name: ev.name, argsSummary: ev.argsSummary, summary: null, presets: [], done: false }
      return { ...state, transcript: [...state.transcript, entry] }
    }

    case 'tool_result': {
      const idx = state.transcript.findIndex(e => e.kind === 'tool' && e.callId === ev.callId)
      const presets = ev.presets ?? []
      if (idx < 0) {
        const orphan: ToolEntry = { kind: 'tool', callId: ev.callId, name: ev.name, argsSummary: '', summary: ev.summary, presets, done: true }
        return { ...state, transcript: [...state.transcript, orphan] }
      }
      const transcript = state.transcript.slice()
      transcript[idx] = { ...(transcript[idx] as ToolEntry), summary: ev.summary, presets, done: true }
      return { ...state, transcript }
    }

    case 'dimensions':
      // 線上省略 null（WhenWritingNull），缺鍵要補回 null，否則 state 裡會出現 undefined
      return { ...state, profile: ev.profile ?? null, facetStates: { ...ev.facetStates } }

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
