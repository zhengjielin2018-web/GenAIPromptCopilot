import { describe, it, expect } from 'vitest'
import { sourceName, sourceNotice } from '../lib/copy'

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

describe('sourceNotice', () => {
  it('civitai：說明內容來自使用者上傳的公開內容，只連結不轉存', () => {
    expect(sourceNotice('civitai:12345:0')).toEqual({
      name: 'Civitai',
      text: '圖片與提示詞片段來自 Civitai 使用者上傳的公開內容，著作權屬原作者；本服務只連結、不轉存。',
    })
  })

  it('kisegae：點名 GitHub 專案並說明上游未標示授權', () => {
    expect(sourceNotice('kisegae:1741156656403')).toEqual({
      name: 'Kisegaeningyou',
      text: '來自 GitHub 專案 Kisegaeningyou，上游未標示授權，著作權屬原作者；本服務只連結、不轉存。',
    })
  })

  it('沒有來源或不認識的前綴：不點名，仍說明著作權屬原作者', () => {
    const generic = { name: null, text: '圖片來自來源網站，著作權屬原作者；本服務不轉存。' }
    expect(sourceNotice(null)).toEqual(generic)
    expect(sourceNotice(undefined)).toEqual(generic)
    expect(sourceNotice('')).toEqual(generic)
    expect(sourceNotice('danbooru:1')).toEqual(generic)
  })
})
