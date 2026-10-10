import { describe, it, expect } from 'vitest'
import { pollRender, type PollDeps } from '../lib/renderPoll'
import type { RenderGetResult } from '../composables/useApi'
import type { RenderView } from '../types/api'

const view = (status: RenderView['status']): RenderView => ({
  renderId: 'r1', turnIndex: 3, status, position: null, safety: 'on', message: null,
  selfCheck: { status: 'pending', score: null, summary: null, items: [] }, timings: { queueMs: null, delayMs: null, executionMs: null, reviewMs: null, requirementsMs: null, selfCheckMs: null },
})
const flush = () => new Promise(r => setTimeout(r, 0))

function harness(results: (RenderGetResult | Error)[], over: Partial<PollDeps> = {}) {
  const timers: (() => void)[] = []
  const seen: string[] = []
  const calls = { gone: 0, giveUp: 0, gets: 0, reasons: [] as string[] }
  const deps: PollDeps = {
    get: async () => { calls.gets++; const r = results.shift()!; if (r instanceof Error) throw r; return r },
    onView: v => { seen.push(v.status) },
    onGone: () => { calls.gone++ },
    onGiveUp: reason => { calls.giveUp++; calls.reasons.push(reason) },
    isCurrent: () => true,
    setTimer: fn => { timers.push(fn) },
    maxFailures: 3,
    ...over,
  }
  const step = async () => { const t = timers.shift(); t?.(); await flush() }
  return { deps, timers, seen, calls, step }
}

describe('pollRender', () => {
  it('reports every view and stops after a final one', async () => {
    const h = harness([{ ok: true, view: view('generating') }, { ok: true, view: view('self_checking') }, { ok: true, view: view('done') }])
    pollRender(h.deps); await flush()
    await h.step(); await h.step()
    expect(h.seen).toEqual(['generating', 'self_checking', 'done'])
    expect(h.timers).toHaveLength(0)
  })

  it('stops and reports gone on 404', async () => {
    const h = harness([{ ok: false, status: 404 }])
    pollRender(h.deps); await flush()
    expect(h.calls.gone).toBe(1)
    expect(h.timers).toHaveLength(0)
  })

  it('keeps going through a few network errors and gives up after too many in a row', async () => {
    const h = harness([new Error('net'), { ok: true, view: view('generating') }, new Error('net'), { ok: false, status: 500 }, new Error('net')])
    pollRender(h.deps); await flush()
    for (let i = 0; i < 4; i++) await h.step()
    expect(h.seen).toEqual(['generating'])
    expect(h.calls.giveUp).toBe(1)
    expect(h.calls.reasons).toEqual(['failures'])
    expect(h.timers).toHaveLength(0)
  })

  it('gives up when it has been running too long without finishing', async () => {
    let t = 0
    const h = harness([{ ok: true, view: view('generating') }, { ok: true, view: view('reviewing') }, { ok: true, view: view('reviewing') }],
      { now: () => t, maxDurationMs: 1000 })
    pollRender(h.deps); await flush()
    t = 600; await h.step()
    expect(h.calls.giveUp).toBe(0)
    t = 1000; await h.step()
    expect(h.seen).toEqual(['generating', 'reviewing', 'reviewing'])
    expect(h.calls.reasons).toEqual(['too_long'])
    expect(h.timers).toHaveLength(0)
  })

  it('stops without reporting when the session changed', async () => {
    let current = true
    const h = harness([{ ok: true, view: view('generating') }, { ok: true, view: view('done') }], { isCurrent: () => current })
    pollRender(h.deps); await flush()
    current = false
    await h.step()
    expect(h.seen).toEqual(['generating'])
    expect(h.calls.gets).toBe(1)
  })

  it('does nothing more after stop()', async () => {
    const h = harness([{ ok: true, view: view('generating') }, { ok: true, view: view('done') }])
    const p = pollRender(h.deps); await flush()
    p.stop()
    await h.step()
    expect(h.seen).toEqual(['generating'])
  })
})
