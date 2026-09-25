import { describe, it, expect } from 'vitest'
import { adoptRows, setChoice, takeAll, adoptPayload, latestRecommendableTurn, adoptPlaceholder, mineLabel } from '../lib/adopt'
import type { Entry } from '../lib/reducer'
import type { RecommendedSet } from '../types/api'

const SET: RecommendedSet = { presetId: 41720, title: '和風女僕', dist: 0.2, facets: [
  { facetId: 'clothing.head', label: '頭部配件', state: 'missing', tags: ['maid headdress'] },
  { facetId: 'clothing.upper', label: '上半身', state: 'covered', tags: ['purple kimono'] },
  { facetId: 'clothing.lower', label: '下半身', state: 'missing', tags: [] },
  { facetId: 'clothing.footwear', label: '鞋履', state: 'waived', tags: ['sandals'] },
  { facetId: 'clothing.material', label: '材質', state: 'notApplicable', tags: ['silk'] },
] }

describe('adoptRows', () => {
  // 設計 §7.2：missing→照它的、covered／waived→留我的、這套沒有的停用、notApplicable 不列
  it('defaults by state, disables rows the set has nothing for, hides notApplicable', () => {
    expect(adoptRows(SET)).toEqual([
      { facetId: 'clothing.head', label: '頭部配件', state: 'missing', setTags: ['maid headdress'], available: true, choice: 'set' },
      { facetId: 'clothing.upper', label: '上半身', state: 'covered', setTags: ['purple kimono'], available: true, choice: 'mine' },
      { facetId: 'clothing.lower', label: '下半身', state: 'missing', setTags: [], available: false, choice: 'mine' },
      { facetId: 'clothing.footwear', label: '鞋履', state: 'waived', setTags: ['sandals'], available: true, choice: 'mine' },
    ])
  })

  // 推薦事件的 state 停在出卡那一輪；之後討論輪改了狀態，對照表要照目前的
  it('uses the current facet state over the one on the card', () => {
    const rows = adoptRows(SET, { 'clothing.head': 'covered', 'clothing.upper': 'covered' })
    expect(rows[0]).toMatchObject({ facetId: 'clothing.head', state: 'covered', choice: 'mine' })
    expect(rows.map(r => r.facetId)).toEqual(['clothing.head', 'clothing.upper', 'clothing.lower', 'clothing.footwear'])   // 沒給目前狀態的列照卡上的
    expect(adoptRows(SET, {})).toEqual(adoptRows(SET))
  })

  it('setChoice only changes available rows; takeAll switches every available row', () => {
    const rows = adoptRows(SET)
    expect(setChoice(rows, 'clothing.upper', 'set')[1].choice).toBe('set')
    expect(setChoice(rows, 'clothing.lower', 'set')[2].choice).toBe('mine')
    expect(takeAll(rows).map(r => r.choice)).toEqual(['set', 'set', 'mine', 'set'])
  })

  it('adoptPayload lists the set rows and is null when nothing is taken', () => {
    expect(adoptPayload(41720, 'clothing', adoptRows(SET))).toEqual({ presetId: 41720, dimension: 'clothing', take: ['clothing.head'] })
    expect(adoptPayload(41720, 'clothing', adoptRows(SET).map(r => ({ ...r, choice: 'mine' as const })))).toBeNull()
  })

  it('mineLabel and adoptPlaceholder', () => {
    expect([mineLabel('covered'), mineLabel('missing'), mineLabel('waived'), mineLabel('notApplicable')]).toEqual(['保留你講的', '空白', '不指定', '不適用'])
    expect(adoptPlaceholder('和風女僕')).toBe('採用〈和風女僕〉…')
  })
})

describe('latestRecommendableTurn', () => {
  const ask = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'ask', preamble: 'p', asks: [] } })
  const fin = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: 't', intentSummary: 'i' } })
  const msg = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'message', message: 'm' } })

  // 只有最新一張追問卡或定稿卡可以採用；中間的討論氣泡不算新結果
  it('returns the latest ask or finalized turn, skipping message entries', () => {
    expect(latestRecommendableTurn([ask(1), fin(2), msg(3)])).toBe(2)
    expect(latestRecommendableTurn([fin(2), ask(3)])).toBe(3)
    expect(latestRecommendableTurn([{ kind: 'user', text: 'x' }, msg(1)])).toBeNull()
  })
})
