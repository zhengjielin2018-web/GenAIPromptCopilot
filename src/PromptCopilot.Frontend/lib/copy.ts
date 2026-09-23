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
