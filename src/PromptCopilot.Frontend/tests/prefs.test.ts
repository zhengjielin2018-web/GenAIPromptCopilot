import { describe, it, expect } from 'vitest'
import { loadPrefs, savePrefs, DEFAULT_PREFS } from '../lib/prefs'
import type { StorageLike } from '../lib/persist'

function memStorage(): StorageLike & { map: Map<string, string> } {
  const map = new Map<string, string>()
  return { map, getItem: k => map.get(k) ?? null, setItem: (k, v) => { map.set(k, v) }, removeItem: k => { map.delete(k) } }
}
const throwing: StorageLike = {
  getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('denied') }, removeItem: () => { throw new Error('denied') },
}

describe('prefs', () => {
  it('defaults to retrieval on and trace hidden', () => {
    expect(loadPrefs(memStorage())).toEqual({ v: 1, retrieval: 'on', showTrace: false })
    expect(loadPrefs(null)).toEqual(DEFAULT_PREFS)
  })

  it('round-trips both switches under pc.prefs', () => {
    const s = memStorage()
    savePrefs({ v: 1, retrieval: 'off', showTrace: true }, s)
    expect(s.map.has('pc.prefs')).toBe(true)
    expect(loadPrefs(s)).toEqual({ v: 1, retrieval: 'off', showTrace: true })
  })

  it('falls back to defaults on bad json, wrong version or unknown values', () => {
    const s = memStorage()
    s.setItem('pc.prefs', '{not json')
    expect(loadPrefs(s)).toEqual(DEFAULT_PREFS)
    s.setItem('pc.prefs', JSON.stringify({ v: 2, retrieval: 'off', showTrace: true }))
    expect(loadPrefs(s)).toEqual(DEFAULT_PREFS)
    s.setItem('pc.prefs', JSON.stringify({ v: 1, retrieval: 'maybe', showTrace: 'yes' }))
    expect(loadPrefs(s)).toEqual(DEFAULT_PREFS)
  })

  it('swallows storage errors on both read and write', () => {
    expect(loadPrefs(throwing)).toEqual(DEFAULT_PREFS)
    expect(() => savePrefs({ v: 1, retrieval: 'off', showTrace: true }, throwing)).not.toThrow()
  })
})
