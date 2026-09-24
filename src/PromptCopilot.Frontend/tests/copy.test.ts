import { describe, it, expect } from 'vitest'
import { sourceName } from '../lib/copy'

describe('sourceName', () => {
  it('把 source_ref 前綴翻成給人看的來源名稱', () => {
    expect(sourceName('civitai:12345:0')).toBe('Civitai')
    expect(sourceName('kisegae:1741156656403')).toBe('Kisegaeningyou')
  })

  it('沒有來源或不認識的前綴回 null，抽屜就退回原本那句話', () => {
    expect(sourceName(null)).toBeNull()
    expect(sourceName('')).toBeNull()
    expect(sourceName('danbooru:1')).toBeNull()
  })
})
