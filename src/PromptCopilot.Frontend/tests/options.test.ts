import { describe, it, expect } from 'vitest'
import { presetIdOf } from '../lib/options'
import type { OptionItem } from '../types/api'

describe('presetIdOf', () => {
  // 後端 SseWriter 用 WhenWritingNull：presetId 為 null 的選項在線上根本沒有這個鍵，不是 presetId: null
  it('treats an omitted presetId (the real wire shape) as no preset', () => {
    const wire = JSON.parse('{"label":"厚塗油畫","tags":"impasto"}') as OptionItem
    expect(presetIdOf(wire)).toBeNull()
  })

  it('treats an explicit null as no preset', () => {
    expect(presetIdOf({ label: 'a', tags: 't', presetId: null })).toBeNull()
  })

  it('returns the id when present, including 0', () => {
    expect(presetIdOf({ label: 'a', tags: 't', presetId: 42 })).toBe(42)
    expect(presetIdOf({ label: 'a', tags: 't', presetId: 0 })).toBe(0)
  })
})
