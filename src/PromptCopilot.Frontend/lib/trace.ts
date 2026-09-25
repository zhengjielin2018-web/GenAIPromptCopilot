import type { Entry, FinalEntry, ToolEntry } from './reducer'
import { isPresetsDetail, type TagSource } from '../types/api'

export interface Contribution { presetId: number; title: string; sourceRef: string | null; tags: string[] }
export interface ContributionSummary { counts: { rag: number; llm: number; base: number }; byPreset: Contribution[] }

/** 定稿卡的「檢索貢獻」：只靠伺服器標的 tag 來源。counts 只算正向（與 audit 的 tagOrigins 一致）；
 *  一個 tag 對到多個 preset 時每個都列；負向 tag 加 - 前綴列在同一組。
 *  TagSource 的 presetTitle／sourceRef 是 presetIds[0] 那筆片段的（後端「取第一個」），所以只有 i === 0 的提及才可信：
 *  它設定 title、補上還沒有的 sourceRef；其他提及只在建組時借標題當備用，sourceRef 留 null。 */
export function contributions(positive: TagSource[], negative: TagSource[]): ContributionSummary {
  const counts = { rag: 0, llm: 0, base: 0 }
  for (const t of positive) counts[t.origin] += 1
  const groups = new Map<number, Contribution>()
  const add = (t: TagSource, label: string) => {
    if (t.origin !== 'rag') return
    t.presetIds.forEach((id, i) => {
      const g = groups.get(id) ?? { presetId: id, title: t.presetTitle ?? String(id), sourceRef: null, tags: [] }
      if (i === 0) {
        if (t.presetTitle) g.title = t.presetTitle
        g.sourceRef = g.sourceRef ?? t.sourceRef ?? null
      }
      g.tags.push(label)
      groups.set(id, g)
    })
  }
  for (const t of positive) add(t, t.tag)
  for (const t of negative) add(t, `-${t.tag}`)
  const byPreset = [...groups.values()].sort((a, b) => b.tags.length - a.tags.length || a.presetId - b.presetId)
  return { counts, byPreset }
}

export interface RetrievalSummary {
  /** 完成的 SearchPresets 次數 */
  searches: number
  /** 每個維度最近一次查詢的候選池；facet 項目歸到所屬維度，同維度取最後一個項目 */
  pools: { dimension: string; label: string; poolSize: number }[]
  /** 命中過的不同 preset 數 */
  seen: number
  /** 最新一次定稿裡 rag 來源（正負向）引用的不同 preset 數 */
  borrowed: number
}

/** 儀表板的「本次對話檢索摘要」。transcript 沒有任何帶 detail 的 SearchPresets 卡時 searches 為 0，畫面整區隱藏。 */
export function retrievalSummary(transcript: Entry[]): RetrievalSummary {
  const pools = new Map<string, { dimension: string; label: string; poolSize: number }>()
  const seen = new Set<number>()
  let searches = 0
  let latestFinal: FinalEntry | null = null
  for (const e of transcript) {
    if (e.kind === 'final') { if (e.data.kind === 'finalized') latestFinal = e; continue }
    if (e.kind !== 'tool' || !e.done) continue
    const t = e as ToolEntry
    if (!isPresetsDetail(t.name, t.detail)) continue
    searches += 1
    for (const it of t.detail.items) {
      if (it.error) continue
      // 同維度後面的項目蓋掉前面的，但 label 用維度的：facet 項目的 label 是 facet 名，維度列要維度名
      const prev = pools.get(it.dimension)
      pools.set(it.dimension, { dimension: it.dimension, label: it.facetId ? (prev?.label ?? it.label) : it.label, poolSize: it.poolSize })
      for (const h of it.hits) seen.add(h.id)
    }
  }
  const borrowed = new Set<number>()
  if (latestFinal && latestFinal.data.kind === 'finalized') {
    for (const s of [...(latestFinal.data.positiveSources ?? []), ...(latestFinal.data.negativeSources ?? [])])
      if (s.origin === 'rag') s.presetIds.forEach(id => borrowed.add(id))
  }
  return { searches, pools: [...pools.values()], seen: seen.size, borrowed: borrowed.size }
}
