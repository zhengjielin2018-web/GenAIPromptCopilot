import { describe, it, expect } from 'vitest'
import { pendingConfirmTurn, confirmDisplay, ACCEPT_TEXT } from '../lib/confirm'
import type { Entry } from '../lib/reducer'

const confirm = (t: number, choices: string[] = []): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'confirm', message: 'm', choices } })
const ask = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'ask', preamble: 'p', asks: [] } })
const fin = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: 't', intentSummary: 'i' } })
const msg = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'message', message: 'm' } })
const consent = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'save_consent_requested' } })
const user: Entry = { kind: 'user', text: 'x' }
const failure: Entry = { kind: 'failure', source: 'error', code: 'turn_failed', message: 'm', originalText: '' }

describe('pendingConfirmTurn', () => {
  it('is the latest confirm card', () => {
    expect(pendingConfirmTurn([user, confirm(1)])).toBe(1)
    expect(pendingConfirmTurn([confirm(1), user, confirm(2)])).toBe(2)
  })

  // 後端同一條規則（設計 §3.4）：討論、存檔確認、失敗都不清掉待確認
  it('survives a discussion, a save consent card and a failed turn', () => {
    expect(pendingConfirmTurn([confirm(1), user, msg(2)])).toBe(1)
    expect(pendingConfirmTurn([confirm(1), user, consent(2)])).toBe(1)
    expect(pendingConfirmTurn([confirm(1), user, failure])).toBe(1)
  })

  it('is gone once an ask or finalized card follows (the turn acted)', () => {
    expect(pendingConfirmTurn([confirm(1), user, ask(2)])).toBeNull()
    expect(pendingConfirmTurn([confirm(1), user, fin(2)])).toBeNull()
    expect(pendingConfirmTurn([fin(1)])).toBeNull()
    expect(pendingConfirmTurn([])).toBeNull()
  })

  it('treats a confirm card the server already answered 409 as not pressable', () => {
    expect(pendingConfirmTurn([confirm(1)], [1])).toBeNull()
    expect(pendingConfirmTurn([confirm(1), user, confirm(2)], [1])).toBe(2)       // 舊卡過期不影響新卡
    expect(pendingConfirmTurn([confirm(1), user, msg(2)], [1])).toBeNull()        // 討論不會讓過期的卡復活
    expect(pendingConfirmTurn([confirm(1)], [])).toBe(1)
  })
})

describe('confirmDisplay', () => {
  it('is the accept sentence without choices and the picked choice otherwise', () => {
    expect(ACCEPT_TEXT).toBe('對，就這樣')
    expect(confirmDisplay({ kind: 'confirm', message: 'm', choices: [] }, null)).toBe(ACCEPT_TEXT)
    expect(confirmDisplay({ kind: 'confirm', message: 'm', choices: ['換掉飲料，改拿雨傘', '換掉相機，改拿雨傘'] }, 1)).toBe('換掉相機，改拿雨傘')
  })
})
