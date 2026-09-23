import { describe, it, expect } from 'vitest'
import { composeDraft, appendChip, chipKey } from '../lib/composer'

const L = { style: '風格', scene: '場景' }

describe('composeDraft', () => {
  it('groups chips by dimension with prefix and 、', () => {
    expect(composeDraft([
      { dimension: 'style', label: '寫實攝影' }, { dimension: 'scene', label: '雨夜街頭' }, { dimension: 'style', label: '柔光' },
    ], L)).toBe('[風格] 寫實攝影、柔光\n[場景] 雨夜街頭')
  })

  it('renders dimension-less chips without a prefix', () => {
    expect(composeDraft([{ dimension: null, label: '厚塗油畫' }, { dimension: null, label: '賽璐璐' }], L)).toBe('厚塗油畫、賽璐璐')
  })

  it('falls back to the raw dimension key when no label is known', () => {
    expect(composeDraft([{ dimension: 'pose', label: '回眸' }], L)).toBe('[pose] 回眸')
  })

  it('is empty for no chips', () => {
    expect(composeDraft([], L)).toBe('')
  })
})

describe('appendChip', () => {
  it('appends on a new line after manual text', () => {
    expect(appendChip('我要一個女生', { dimension: 'style', label: '寫實攝影' }, L)).toBe('我要一個女生\n[風格] 寫實攝影')
  })
  it('does not add a leading newline to an empty draft', () => {
    expect(appendChip('', { dimension: null, label: 'x' }, L)).toBe('x')
  })
})

describe('chipKey', () => {
  it('distinguishes same label across dimensions', () => {
    expect(chipKey({ dimension: 'style', label: 'a' })).not.toBe(chipKey({ dimension: 'scene', label: 'a' }))
  })
})
