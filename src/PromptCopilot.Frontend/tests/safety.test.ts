import { describe, it, expect } from 'vitest'
import { messageBody } from '../lib/safety'

const adopt = { presetId: 1, dimension: 'scene', take: ['scene.location'] }

describe('messageBody', () => {
  it('sends the body unchanged while review is on', () => {
    expect(messageBody({ text: 'hi' }, { canDisable: true, off: false })).toEqual({ text: 'hi' })
    expect(messageBody({ adopt }, { canDisable: true, off: false })).toEqual({ adopt })
  })

  it('adds safety off to text and adopt bodies when review is switched off', () => {
    expect(messageBody({ text: 'hi' }, { canDisable: true, off: true })).toEqual({ text: 'hi', safety: 'off' })
    expect(messageBody({ adopt }, { canDisable: true, off: true })).toEqual({ adopt, safety: 'off' })
  })

  it('never sends safety off when the backend does not allow it', () => {
    expect(messageBody({ text: 'hi' }, { canDisable: false, off: true })).toEqual({ text: 'hi' })
  })
})
