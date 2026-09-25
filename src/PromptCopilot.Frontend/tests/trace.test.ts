import { describe, it, expect } from 'vitest'
import { contributions, retrievalSummary } from '../lib/trace'
import type { Entry, ToolEntry } from '../lib/reducer'
import type { TagSource } from '../types/api'

const POS: TagSource[] = [
  { tag: 'neon lights', origin: 'rag', presetIds: [9, 3], presetTitle: '霓虹雨夜', sourceRef: 'civitai:1:0' },
  { tag: 'sandals', origin: 'rag', presetIds: [3], presetTitle: '夏日涼鞋' },
  { tag: '1girl', origin: 'llm', presetIds: [] },
  { tag: 'masterpiece', origin: 'base', presetIds: [] },
]
const NEG: TagSource[] = [
  { tag: 'lowres', origin: 'base', presetIds: [] },
  { tag: 'blurry', origin: 'rag', presetIds: [9], presetTitle: '霓虹雨夜' },
]

describe('contributions', () => {
  it('counts positive origins only and groups each tag under its first preset, most tags first', () => {
    const c = contributions(POS, NEG)
    expect(c.counts).toEqual({ rag: 2, llm: 1, base: 1 })
    expect(c.byPreset).toEqual([
      { presetId: 9, title: '霓虹雨夜', sourceRef: 'civitai:1:0', tags: ['neon lights', '-blurry'] },
      { presetId: 3, title: '夏日涼鞋', sourceRef: null, tags: ['sandals'] },
    ])
  })

  // 知識庫裡同一組來源常有好幾筆同名片段，一個 tag 會對到全部；只歸給第一筆（chip 點開的那筆），否則同一標題會重複列很多次
  it('ignores presetIds after the first, so same-title presets do not repeat', () => {
    const c = contributions([
      { tag: 'a', origin: 'rag', presetIds: [5, 6, 7], presetTitle: 'five', sourceRef: 'civitai:5:0' },
      { tag: 'b', origin: 'rag', presetIds: [5, 8], presetTitle: 'five' },
    ], [])
    expect(c.byPreset).toEqual([{ presetId: 5, title: 'five', sourceRef: 'civitai:5:0', tags: ['a', 'b'] }])
  })

  it('handles empty sources', () => {
    expect(contributions([], [])).toEqual({ counts: { rag: 0, llm: 0, base: 0 }, byPreset: [] })
  })
})

const tool = (callId: string, detail: unknown, name = 'SearchPresets', done = true): ToolEntry =>
  ({ kind: 'tool', callId, name, argsSummary: '', summary: 's', presets: [], detail: detail as ToolEntry['detail'], done })
const item = (dimension: string, label: string, poolSize: number, hitIds: number[], facetId: string | null = null) => ({
  dimension, facetId, label, query: 'q', grounded: true, poolSize, k: 5, error: null,
  hits: hitIds.map(id => ({ id, title: `t${id}`, band: '高', dist: 0.2, usable: true, facets: {} })),
})

describe('retrievalSummary', () => {
  it('returns searches 0 for a transcript without detail', () => {
    const t: Entry[] = [{ kind: 'user', text: 'x' }, tool('c1', null), tool('c2', undefined)]
    expect(retrievalSummary(t)).toEqual({ searches: 0, pools: [], seen: 0, borrowed: 0 })
  })

  it('counts searches, latest pool per dimension, distinct presets seen and presets borrowed in the latest final', () => {
    const t: Entry[] = [
      tool('c1', { items: [item('style', '風格', 4455, [1, 2]), item('clothing', '鞋履', 300, [3], 'clothing.footwear')] }),
      { kind: 'final', turnIndex: 1, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: '', intentSummary: '',
        positiveSources: [{ tag: 'a', origin: 'rag', presetIds: [1] }] } },
      tool('c2', { items: [item('style', '風格', 4460, [2, 4]), item('clothing', '鞋履', 19, [], 'clothing.footwear')] }),
      tool('c3', { hits: [] }, 'SearchSimilarPrompts'),
      tool('c4', { items: [item('style', '風格', 1, [])] }, 'SearchPresets', false),   // 進行中不算
      { kind: 'final', turnIndex: 2, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: '', intentSummary: '',
        positiveSources: [{ tag: 'a', origin: 'rag', presetIds: [2, 4] }, { tag: 'b', origin: 'llm', presetIds: [] }],
        negativeSources: [{ tag: 'c', origin: 'rag', presetIds: [4] }] } },
    ]
    expect(retrievalSummary(t)).toEqual({
      searches: 2,
      pools: [{ dimension: 'style', label: '風格', poolSize: 4460 }, { dimension: 'clothing', label: '鞋履', poolSize: 19 }],
      seen: 4,
      borrowed: 2,
    })
  })

  it('skips items with error when collecting pools', () => {
    const t: Entry[] = [tool('c1', { items: [{ ...item('style', '風格', 0, []), error: 'x' }] })]
    expect(retrievalSummary(t).pools).toEqual([])
  })

  it('labels each dimension pool with the item that supplied the number', () => {
    const t: Entry[] = [tool('c1', { items: [
      item('appearance', '年齡與性別', 120, [1], 'appearance.age_gender'),
      item('appearance', '髮型', 640, [2], 'appearance.hair'),
    ] })]
    expect(retrievalSummary(t).pools).toEqual([{ dimension: 'appearance', label: '髮型', poolSize: 640 }])
  })
})
