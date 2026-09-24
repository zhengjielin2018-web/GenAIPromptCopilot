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
