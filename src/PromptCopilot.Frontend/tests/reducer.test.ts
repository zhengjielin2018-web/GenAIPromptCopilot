import { describe, it, expect } from 'vitest'
import { initialState, beginTurn, applyEvent, endTurn, failHttp, hydrate, latestFinalizedTurn, type ChatState, type Entry } from '../lib/reducer'
import type { AgentEvent } from '../types/api'

const session = (turnIndex = 1): AgentEvent => ({ type: 'session', sessionId: 's1', turnIndex, status: 'Collecting' })
const call = (id: string, name = 'SearchPresets'): AgentEvent => ({ type: 'tool_call', callId: id, name, argsSummary: 'dimension: style' })
const result = (id: string): AgentEvent => ({ type: 'tool_result', callId: id, name: 'SearchPresets', summary: '風格 池 4455 → 3', presets: [{ id: 7, title: 't', imageUrl: null }] })
const dims: AgentEvent = { type: 'dimensions', profile: 'portrait', facetStates: { 'style.genre': 'covered', 'scene.location': 'missing' } }
const ask: AgentEvent = { type: 'final', kind: 'ask', preamble: 'p', asks: [{ dimension: 'style', question: 'q', missingFacetIds: ['style.genre'], options: [{ label: 'a', tags: 't', presetId: null }] }] }
const finalized: AgentEvent = { type: 'final', kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' }

function started(): ChatState {
  let s = initialState()
  s = { ...s, sessionId: 's1' }
  s = beginTurn(s, '一個女生')
  return applyEvent(s, session())
}

describe('beginTurn', () => {
  it('snapshots before pushing the user entry and marks pending', () => {
    const s = beginTurn({ ...initialState(), sessionId: 's1' }, 'hi')
    expect(s.transcript).toEqual([{ kind: 'user', text: 'hi' }])
    expect(s.pending?.text).toBe('hi')
    expect(s.pending?.settled).toBe(false)
    expect(s.pending?.snapshot.transcript).toEqual([])
    expect(s.pending?.snapshot.pending).toBeNull()
  })
})

describe('applyEvent', () => {
  it('session sets turnIndex/status and clears highlights', () => {
    const s = applyEvent({ ...started(), highlighted: ['style'] }, session(2))
    expect(s.turnIndex).toBe(2)
    expect(s.status).toBe('Collecting')
    expect(s.highlighted).toEqual([])
  })

  it('tool_call appends a pending tool entry; tool_result fills it by callId', () => {
    let s = applyEvent(started(), call('c1'))
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'tool', callId: 'c1', done: false, summary: null })
    s = applyEvent(s, result('c1'))
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'tool', callId: 'c1', done: true, summary: '風格 池 4455 → 3' })
    expect((s.transcript.at(-1) as any).presets[0].id).toBe(7)
  })

  it('terminal tool calls are not turned into tool entries', () => {
    const s = applyEvent(started(), call('c9', 'FinalizePrompt'))
    expect(s.transcript.some(e => e.kind === 'tool')).toBe(false)
  })

  it('an orphan tool_result becomes a standalone done entry', () => {
    const s = applyEvent(started(), result('nope'))
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'tool', callId: 'nope', done: true, summary: '風格 池 4455 → 3' })
  })

  it('dimensions overwrite profile and facetStates', () => {
    const s = applyEvent(started(), dims)
    expect(s.profile).toBe('portrait')
    expect(s.facetStates).toEqual({ 'style.genre': 'covered', 'scene.location': 'missing' })
  })

  // SseWriter 用 WhenWritingNull：profile 為 null 時線上根本沒有這個鍵，要跟 store 一樣從原始 JSON 組事件
  it('dimensions without a profile key on the wire sets profile to null, not undefined', () => {
    const data = JSON.parse('{"facetStates":{}}')
    const s = applyEvent({ ...started(), profile: 'portrait' }, { ...data, type: 'dimensions' } as AgentEvent)
    expect(s.profile).toBeNull()
    expect(s.facetStates).toEqual({})
  })

  it('final ask pushes entry, bumps askCount, highlights dimensions, settles', () => {
    const s = applyEvent(started(), ask)
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'final', turnIndex: 1, data: { kind: 'ask' } })
    expect(s.askCount).toBe(1)
    expect(s.highlighted).toEqual(['style'])
    expect(s.pending?.settled).toBe(true)
  })

  it('final finalized sets lastFinal and status', () => {
    const s = applyEvent(started(), finalized)
    expect(s.lastFinal).toEqual({ kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' })
    expect(s.status).toBe('Finalized')
    expect(s.askCount).toBe(0)
  })

  it('error restores the snapshot and appends a failure with the original text', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, dims)
    s = applyEvent(s, { type: 'error', code: 'timeout', message: '逾時' })
    expect(s.profile).toBeNull()
    expect(s.transcript.filter(e => e.kind === 'tool')).toHaveLength(0)
    expect(s.transcript.filter(e => e.kind === 'user')).toHaveLength(0)
    expect(s.transcript.at(-1)).toEqual({ kind: 'failure', source: 'error', code: 'timeout', message: '逾時', originalText: '一個女生' })
    expect(s.pending?.settled).toBe(true)
  })

  it('blocked behaves like error with source blocked', () => {
    const s = applyEvent(started(), { type: 'blocked', reason: 'Blocked_NSFW', message: '被攔' })
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'failure', source: 'blocked', code: 'Blocked_NSFW' })
  })

  it('token appends text to the latest message entry and is a no-op otherwise', () => {
    let s = applyEvent(started(), { type: 'final', kind: 'message', message: 'ab' })
    s = applyEvent(s, { type: 'token', text: 'c' })
    expect((s.transcript.at(-1) as any).data.message).toBe('abc')
    const before = started()
    expect(applyEvent(before, { type: 'token', text: 'x' })).toEqual(before)
  })

  it('unknown events leave state untouched', () => {
    const before = started()
    expect(applyEvent(before, { type: 'whatever' } as any)).toEqual(before)
  })
})

describe('endTurn', () => {
  it('clears pending and marks all tool entries done when settled', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, ask)
    s = endTurn(s)
    expect(s.pending).toBeNull()
    expect(s.transcript.find(e => e.kind === 'tool')).toMatchObject({ done: true })
  })

  it('treats an unsettled end as stream_ended failure with rollback', () => {
    let s = applyEvent(started(), dims)
    s = endTurn(s)
    expect(s.pending).toBeNull()
    expect(s.profile).toBeNull()
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'failure', source: 'stream_ended', originalText: '一個女生' })
  })

  it('is a no-op when nothing is pending', () => {
    const s = initialState()
    expect(endTurn(s)).toEqual(s)
  })
})

describe('failHttp', () => {
  it('rolls back and records an http failure', () => {
    const s = failHttp(beginTurn({ ...initialState(), sessionId: 's1' }, 'hi'), 'http_409', '這個對話還有一輪在跑')
    expect(s.pending).toBeNull()
    expect(s.transcript).toEqual([{ kind: 'failure', source: 'http', code: 'http_409', message: '這個對話還有一輪在跑', originalText: 'hi' }])
  })
})

describe('hydrate', () => {
  it('takes authoritative fields from the dto and the transcript from storage', () => {
    const s = hydrate(initialState(), {
      sessionId: 's1', status: 'Finalized', profile: 'landscape', turnIndex: 4, askCount: 2, askLimit: 2,
      facetStates: { 'scene.location': 'covered' },
      lastFinal: { positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' },
    }, [{ kind: 'user', text: 'x' }])
    expect(s).toMatchObject({ sessionId: 's1', status: 'Finalized', profile: 'landscape', turnIndex: 4, askCount: 2, askLimit: 2 })
    expect(s.lastFinal).toEqual({ kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' })
    expect(s.transcript).toEqual([{ kind: 'user', text: 'x' }])
    expect(s.pending).toBeNull()
  })
})

describe('latestFinalizedTurn', () => {
  const fin = (turnIndex: number): Entry => ({ kind: 'final', turnIndex, data: { kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' } })
  const msg = (turnIndex: number): Entry => ({ kind: 'final', turnIndex, data: { kind: 'message', message: 'm' } })

  // save-to-shared 永遠存後端的 LastFinal（最新一次定稿）。只有最新那張卡可以存，
  // 否則舊卡的描述會配上新的提示詞寫進共享庫。
  it('is the turn of the last finalized entry, ignoring later non-finalized entries', () => {
    expect(latestFinalizedTurn([fin(3), { kind: 'user', text: 'x' }, fin(5), msg(6)])).toBe(5)
  })

  it('is null when nothing was finalized', () => {
    expect(latestFinalizedTurn([{ kind: 'user', text: 'x' }, msg(1)])).toBeNull()
  })
})
