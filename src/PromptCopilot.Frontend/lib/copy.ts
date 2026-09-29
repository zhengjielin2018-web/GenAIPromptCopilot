import type { FailureEntry } from './reducer'

const TITLES: Record<string, string> = {
  Blocked_NSFW: '輸入被安全規則攔下',
  Blocked_Celebrity: '輸入涉及真實人物，被攔下',
  Blocked_Output: '模型的輸出被安全規則攔下',
  Blocked_Upstream: '上游模型拒絕生成這段內容',
  timeout: '這一輪逾時',
  protocol_violation: '模型沒有依規定結束這一輪',
  turn_failed: '這一輪失敗',
  stream_ended: '連線中斷',
  http_404: '上次的對話已過期',
  http_409: '這個對話還有一輪在跑',
}

export function failureTitle(source: FailureEntry['source'], code: string): string {
  return TITLES[code] ?? (source === 'blocked' ? '被攔下' : '發生錯誤')
}

/** source_ref 前綴 → 來源名稱與來源說明。URL 由後端算（sourceUrl），這裡只管顯示的字。 */
const SOURCES: Record<string, { name: string; notice: string }> = {
  civitai: {
    name: 'Civitai',
    notice: '圖片與提示詞片段來自 Civitai 使用者上傳的公開內容，著作權屬原作者；本服務只連結、不轉存。',
  },
  kisegae: {
    name: 'Kisegaeningyou',
    notice: '來自 GitHub 專案 Kisegaeningyou，上游未標示授權，著作權屬原作者；本服務只連結、不轉存。',
  },
}
const GENERIC_NOTICE = '圖片來自來源網站，著作權屬原作者；本服務不轉存。'

function sourceOf(sourceRef: string | null | undefined) {
  if (!sourceRef) return null
  return SOURCES[sourceRef.split(':')[0]] ?? null
}

export function sourceName(sourceRef: string | null | undefined): string | null {
  return sourceOf(sourceRef)?.name ?? null
}

/** 圖片與片段的來源說明。顯示在圖片旁，讓使用者知道內容屬於原作者。 */
export function sourceNotice(sourceRef: string | null | undefined): { name: string | null; text: string } {
  const s = sourceOf(sourceRef)
  return { name: s?.name ?? null, text: s?.notice ?? GENERIC_NOTICE }
}

/** 推薦區塊每個維度的說明（facet 向量設計 §6.5）。 */
export function recommendationLead(d: { anchored: boolean; similar?: boolean; anchorTags: string[] }): string {
  if (d.anchored) return `含你講的 ${d.anchorTags.join(', ')}`
  if (d.similar) return `接近你講的 ${d.anchorTags.join(', ')}`
  return '最接近你描述的組合'
}

/** 工具卡的查詢句：facet 項目帶英文時顯示「原話（英文）」，跟送去 embedding 的字串同形。 */
export function queryLabel(it: { query: string; tags?: string | null }): string {
  return it.tags ? `${it.query}（${it.tags}）` : it.query
}

/** 只有 facet 項目標示走哪種向量；維度項目本來就只有整套向量，不標。 */
export function methodLabel(it: { facetId?: string | null; method?: 'facet' | 'preset' }): string | null {
  if (!it.facetId) return null
  return it.method === 'facet' ? 'facet 向量' : '整套向量'
}
