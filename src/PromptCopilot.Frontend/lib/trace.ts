import type { Entry, FinalEntry, ToolEntry } from './reducer'
import { isPresetsDetail, type TagSource } from '../types/api'

export interface Contribution { presetId: number; title: string; sourceRef: string | null; tags: string[] }
export interface ContributionSummary { counts: { rag: number; llm: number; base: number }; byPreset: Contribution[] }

/** 定稿卡的「檢索貢獻」：只靠伺服器標的 tag 來源。counts 只算正向（與 audit 的 tagOrigins 一致）；
 *  負向 tag 加 - 前綴列在同一組。
 *  一個 tag 只歸給 presetIds[0]：後端把整段相等的片段排在前面，presetTitle／sourceRef 也是它的，chip 點開的也是它。
 *  後面的 presetIds 是字尾相符的其他片段，知識庫裡同名片段常有好幾筆，全列會把同一個標題重複很多次、讀不出誰貢獻了什麼。 */
export function contributions(positive: TagSource[], negative: TagSource[]): ContributionSummary {
  const counts = { rag: 0, llm: 0, base: 0 }
  for (const t of positive) if (t.origin !== 'adopted') counts[t.origin] += 1  // counts 還沒有 adopted 這格
  const groups = new Map<number, Contribution>()
  const add = (t: TagSource, label: string) => {
    if (t.origin !== 'rag' || t.presetIds.length === 0) return
    const id = t.presetIds[0]
    const g = groups.get(id) ?? { presetId: id, title: t.presetTitle ?? String(id), sourceRef: t.sourceRef ?? null, tags: [] }
    g.tags.push(label)
    groups.set(id, g)
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
      // 同維度後面的項目蓋掉前面的，label 也跟著換成該 item 的：標籤要跟數字來自同一個 item，
      // 否則同維度兩個 facet 項目（例如 年齡與性別 → 髮型）會把前一個的名字配上後一個的候選池
      pools.set(it.dimension, { dimension: it.dimension, label: it.label, poolSize: it.poolSize })
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
