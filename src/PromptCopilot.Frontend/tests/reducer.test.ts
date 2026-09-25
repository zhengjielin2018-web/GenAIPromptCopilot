import { describe, it, expect } from 'vitest'
import { initialState, beginTurn, applyEvent, endTurn, failHttp, hydrate, latestFinalizedTurn, type ChatState, type Entry, type ToolEntry } from '../lib/reducer'
import type { AgentEvent, SessionSnapshotDto, TagSource } from '../types/api'

const session = (turnIndex = 1): AgentEvent => ({ type: 'session', sessionId: 's1', turnIndex, status: 'Collecting' })
const call = (id: string, name = 'SearchPresets'): AgentEvent => ({ type: 'tool_call', callId: id, name, argsSummary: 'dimension: style' })
const result = (id: string): AgentEvent => ({ type: 'tool_result', callId: id, name: 'SearchPresets', summary: '風格 池 4455 → 3', presets: [{ id: 7, title: 't', imageUrl: null }] })
const dims: AgentEvent = { type: 'dimensions', profile: 'portrait', facetStates: { 'style.genre': 'covered', 'scene.location': 'missing' } }
const ask: AgentEvent = { type: 'final', kind: 'ask', preamble: 'p', asks: [{ dimension: 'style', question: 'q', missingFacetIds: ['style.genre'], options: [{ label: 'a', tags: 't', presetId: null }] }] }
const finalized: AgentEvent = { type: 'final', kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' }
const recs: AgentEvent = { type: 'recommendations', turnIndex: 1, dimensions: [{ dimension: 'style', label: '風格', anchored: false, anchorTags: [], sets: [
  { presetId: 7, title: '油畫', imageUrl: 'https://img', sourceRef: 'civitai:1:0', dist: 0.2, facets: [{ facetId: 'style.genre', label: '藝術流派', state: 'missing', tags: ['oil painting'] }] },
] }] }
// 線上照 WhenWritingNull：llm／base 的 presetTitle 是 null，整個鍵不會出現
const POS: TagSource[] = [{ tag: 'neon lights', origin: 'rag', presetIds: [9, 3], presetTitle: '霓虹雨夜' }, { tag: '1girl', origin: 'llm', presetIds: [] }]
const NEG: TagSource[] = [{ tag: 'lowres', origin: 'base', presetIds: [] }]

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

  // 縮圖角落的來源標籤靠 sourceRef；reducer 整包帶過去，不能在這裡被挑掉
  it('tool_result keeps the sourceRef on each preset', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, { type: 'tool_result', callId: 'c1', name: 'SearchPresets', summary: 's',
      presets: [{ id: 7, title: 't', imageUrl: 'https://img', sourceRef: 'civitai:12345:0' }, { id: 8, title: 'u' }] })
    expect((s.transcript.at(-1) as any).presets).toEqual([
      { id: 7, title: 't', imageUrl: 'https://img', sourceRef: 'civitai:12345:0' },
      { id: 8, title: 'u' },
    ])
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

  it('final finalized carries the tag sources into lastFinal and the card', () => {
    const s = applyEvent(started(), { ...finalized, positiveSources: POS, negativeSources: NEG } as AgentEvent)
    expect(s.lastFinal?.positiveSources).toEqual(POS)
    expect(s.lastFinal?.negativeSources).toEqual(NEG)
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'final', data: { kind: 'finalized', positiveSources: POS, negativeSources: NEG } })
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

  // 設計 §7.1：推薦掛在該輪的追問卡／定稿卡上，重載後跟著 transcript 一起回來
  it('recommendations attaches to the final entry of its turn', () => {
    let s = applyEvent(started(), ask)
    s = applyEvent(s, recs)
    const entry = s.transcript.at(-1) as any
    expect(entry.kind).toBe('final')
    expect(entry.recommendations.dimensions[0].sets[0].presetId).toBe(7)
  })

  it('recommendations for a turn without a final entry is ignored', () => {
    const s = applyEvent(started(), { ...recs, turnIndex: 9 } as AgentEvent)
    expect(s.transcript.some(e => e.kind === 'final')).toBe(false)
  })

  // 採用輪：送出當下泡泡是暫代字，session 事件帶伺服器組的那句
  it('session with text replaces the pending user entry text', () => {
    let s = beginTurn({ ...initialState(), sessionId: 's1' }, '採用〈油畫〉…')
    s = applyEvent(s, { type: 'session', sessionId: 's1', turnIndex: 2, status: 'Finalized', text: '採用〈油畫〉（知識庫 #7）：藝術流派照它的（oil painting）。' })
    expect(s.transcript.at(-1)).toEqual({ kind: 'user', text: '採用〈油畫〉（知識庫 #7）：藝術流派照它的（oil painting）。' })
    // pending.text 也要跟著換：這一輪後面失敗時，失敗條目與重試帶的是伺服器那句，不是暫代字
    expect(s.pending?.text).toBe('採用〈油畫〉（知識庫 #7）：藝術流派照它的（oil painting）。')
    const failed = applyEvent(s, { type: 'error', code: 'turn_failed', message: 'x' })
    expect(failed.transcript.at(-1)).toMatchObject({ kind: 'failure', originalText: '採用〈油畫〉（知識庫 #7）：藝術流派照它的（oil painting）。' })
  })

  // 只換這一輪還在等的那則：前幾輪的泡泡不動；沒有進行中的輪次時整個對話流不動
  it('session with text changes only the pending turn bubble, and nothing when idle', () => {
    let s = beginTurn({ ...initialState(), sessionId: 's1' }, 'a')
    s = endTurn(applyEvent(applyEvent(s, session(1)), ask))
    s = beginTurn(s, 'b')
    s = applyEvent(s, { type: 'session', sessionId: 's1', turnIndex: 2, status: 'Collecting', text: 'B' })
    expect(s.transcript.map(e => e.kind)).toEqual(['user', 'final', 'user'])
    expect(s.transcript[0]).toEqual({ kind: 'user', text: 'a' })
    expect(s.transcript[2]).toEqual({ kind: 'user', text: 'B' })
    const idle = endTurn(applyEvent(s, ask))
    expect(idle.pending).toBeNull()
    const after = applyEvent(idle, { type: 'session', sessionId: 's1', turnIndex: 3, status: 'Collecting', text: 'C' })
    expect(after.transcript).toEqual(idle.transcript)
  })

  it('session without text leaves the user entry alone', () => {
    const s = started()
    expect(s.transcript.at(-1)).toEqual({ kind: 'user', text: '一個女生' })
  })

  it('dimensions carries facetTags; missing key resets to {}', () => {
    let s = applyEvent(started(), { ...dims, facetTags: { 'style.genre': 'oil painting' } } as AgentEvent)
    expect(s.facetTags).toEqual({ 'style.genre': 'oil painting' })
    s = applyEvent(s, dims)
    expect(s.facetTags).toEqual({})
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
  const LAST = { positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' }
  const dto = (lastFinal: SessionSnapshotDto['lastFinal'] = LAST): SessionSnapshotDto => ({
    sessionId: 's1', status: lastFinal ? 'Finalized' : 'Collecting', profile: 'landscape', turnIndex: 4, askCount: 2, askLimit: 2,
    facetStates: { 'scene.location': 'covered' }, lastFinal,
  })
  const user = (text: string): Entry => ({ kind: 'user', text })
  const fin = (turnIndex: number): Entry => ({ kind: 'final', turnIndex, data: { kind: 'finalized', ...LAST } })
  const msg = (turnIndex: number): Entry => ({ kind: 'final', turnIndex, data: { kind: 'message', message: 'm' } })

  it('takes authoritative fields from the dto and the transcript from storage', () => {
    const s = hydrate(initialState(), dto(), [user('x'), fin(4)])
    expect(s).toMatchObject({ sessionId: 's1', status: 'Finalized', profile: 'landscape', turnIndex: 4, askCount: 2, askLimit: 2 })
    expect(s.lastFinal).toEqual({ kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' })
    expect(s.transcript).toEqual([user('x'), fin(4)])
    expect(s.pending).toBeNull()
  })

  // 後端有 LastFinal、對話流卻沒有定稿卡（定稿那一輪沒來得及存進 sessionStorage）：補一張，卡片才畫得出來、也才存得了
  it('appends a synthetic finalized card when the dto has lastFinal and the transcript has none', () => {
    const s = hydrate(initialState(), dto(), [user('x'), msg(3)])
    expect(s.transcript).toHaveLength(3)
    expect(s.transcript.at(-1)).toEqual({ kind: 'final', turnIndex: 4, data: { kind: 'finalized', ...LAST } })
    expect(latestFinalizedTurn(s.transcript)).toBe(4)
  })

  it('does not append a card when the transcript already has a finalized entry', () => {
    const s = hydrate(initialState(), dto(), [user('x'), fin(2), user('y'), msg(3)])
    expect(s.transcript).toEqual([user('x'), fin(2), user('y'), msg(3)])
    expect(latestFinalizedTurn(s.transcript)).toBe(2)
  })

  // 送出當下存的對話流停在 user 條目：那一輪後端已回滾（或已定稿，由補上的卡涵蓋），原文經 draft 回到輸入框
  it('drops a trailing user entry that no entry follows, but keeps one that a final entry follows', () => {
    expect(hydrate(initialState(), dto(null), [user('a'), msg(3), user('b')]).transcript).toEqual([user('a'), msg(3)])
    expect(hydrate(initialState(), dto(null), [user('a'), msg(3)]).transcript).toEqual([user('a'), msg(3)])
  })

  it('keeps the tag sources when rebuilding lastFinal and the synthetic card', () => {
    const s = hydrate(initialState(), dto({ ...LAST, positiveSources: POS, negativeSources: NEG }), [user('x')])
    expect(s.lastFinal).toEqual({ kind: 'finalized', ...LAST, positiveSources: POS, negativeSources: NEG })
    expect(s.transcript.at(-1)).toEqual({ kind: 'final', turnIndex: 4, data: { kind: 'finalized', ...LAST, positiveSources: POS, negativeSources: NEG } })
  })

  it('drops the dangling user entry before appending the synthetic card', () => {
    expect(hydrate(initialState(), dto(), [user('a'), msg(3), user('b')]).transcript)
      .toEqual([user('a'), msg(3), { kind: 'final', turnIndex: 4, data: { kind: 'finalized', ...LAST } }])
  })

  it('takes facetTags from the dto and defaults to {} for an old backend; old final entries without recommendations survive', () => {
    const dto = { sessionId: 's1', status: 'Collecting', profile: 'portrait', turnIndex: 1, askCount: 0, askLimit: 2, facetStates: {}, lastFinal: null } as SessionSnapshotDto
    const old: Entry[] = [{ kind: 'user', text: 'x' }, { kind: 'final', turnIndex: 1, data: { kind: 'ask', preamble: 'p', asks: [] } }]
    expect(hydrate(initialState(), dto, old).facetTags).toEqual({})
    expect((hydrate(initialState(), dto, old).transcript[1] as any).recommendations).toBeUndefined()
    expect(hydrate(initialState(), { ...dto, facetTags: { 'style.genre': 'anime' } }, old).facetTags).toEqual({ 'style.genre': 'anime' })
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

describe('retrieval and detail (2026-09-25)', () => {
  const detail = {
    items: [{ dimension: 'style', facetId: null, label: '風格', query: '寫實攝影', grounded: false, poolSize: 4455, k: 3, hits: [
      { id: 7, title: 't', band: '高', dist: 0.2, usable: false, facets: { 'style.genre': 'missing' } },
    ] }],
  }

  it('tool_result with detail stores it on the entry', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, { type: 'tool_result', callId: 'c1', name: 'SearchPresets', summary: '風格 池 4455 → 1', presets: [], detail })
    expect((s.transcript.at(-1) as ToolEntry).detail).toEqual(detail)
  })

  it('tool_result without detail leaves detail null', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, result('c1'))
    expect((s.transcript.at(-1) as ToolEntry).detail).toBeNull()
  })

  it('initial state is retrieval on; hydrate takes retrieval from the dto and defaults to on when absent', () => {
    expect(initialState().retrieval).toBe('on')
    const dto: SessionSnapshotDto = { sessionId: 's9', status: 'Collecting', profile: null, turnIndex: 0, askCount: 0, askLimit: 2, facetStates: {}, lastFinal: null, retrieval: 'off' }
    expect(hydrate(initialState(), dto, []).retrieval).toBe('off')
    const { retrieval: _drop, ...older } = dto
    expect(hydrate(initialState(), older as SessionSnapshotDto, []).retrieval).toBe('on')
  })
})
