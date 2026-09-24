import { describe, it, expect } from 'vitest'
import { loadPersisted, savePersisted, clearPersisted, type StorageLike } from '../lib/persist'

function memStorage(): StorageLike & { map: Map<string, string> } {
  const map = new Map<string, string>()
  return { map, getItem: k => map.get(k) ?? null, setItem: (k, v) => { map.set(k, v) }, removeItem: k => { map.delete(k) } }
}
const throwing: StorageLike = {
  getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('denied') }, removeItem: () => { throw new Error('denied') },
}

describe('persist', () => {
  it('round-trips sessionId and transcript', () => {
    const s = memStorage()
    savePersisted({ sessionId: 'abc', transcript: [{ kind: 'user', text: '嗨' }] }, s)
    expect(loadPersisted(s)).toEqual({ v: 1, sessionId: 'abc', transcript: [{ kind: 'user', text: '嗨' }] })
    clearPersisted(s)
    expect(loadPersisted(s)).toBeNull()
  })

  // 已存進共享庫的輪次要跟著重載，否則同一張卡能再存一次；draft 讓輪次中重載時原文回到輸入框
  it('round-trips savedTurns and draft', () => {
    const s = memStorage()
    savePersisted({ sessionId: 'abc', transcript: [], savedTurns: [3], draft: 'x' }, s)
    expect(loadPersisted(s)).toEqual({ v: 1, sessionId: 'abc', transcript: [], savedTurns: [3], draft: 'x' })
  })

  it('still loads an entry stored before savedTurns and draft existed', () => {
    const s = memStorage()
    s.setItem('pc.session', JSON.stringify({ v: 1, sessionId: 'abc', transcript: [{ kind: 'user', text: '嗨' }] }))
    const p = loadPersisted(s)
    expect(p?.sessionId).toBe('abc')
    expect(p?.savedTurns).toBeUndefined()
    expect(p?.draft).toBeUndefined()
  })

  it('returns null for garbage or wrong version', () => {
    const s = memStorage()
    s.setItem('pc.session', '{not json')
    expect(loadPersisted(s)).toBeNull()
    s.setItem('pc.session', JSON.stringify({ v: 0, sessionId: 'x', transcript: [] }))
    expect(loadPersisted(s)).toBeNull()
  })

  it('never throws when storage is unavailable', () => {
    expect(loadPersisted(throwing)).toBeNull()
    expect(() => savePersisted({ sessionId: 'x', transcript: [] }, throwing)).not.toThrow()
    expect(() => clearPersisted(throwing)).not.toThrow()
  })
})
