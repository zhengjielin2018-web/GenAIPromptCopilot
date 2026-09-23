import type { OptionItem } from '../types/api'

/** 選項對應的 preset id；沒有就回 null。後端序列化會省略 null 欄位，所以 presetId 可能是 undefined，不能用 !== null 判斷。 */
export function presetIdOf(o: OptionItem): number | null {
  return o.presetId ?? null
}
