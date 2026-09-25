import type { TurnBody } from '../composables/useApi'

/** 測試用的審查開關：後端開放（GET /api/config/safety）而且使用者關掉了，才帶 safety: off；
 *  其餘情況送出的 body 跟沒有開關時一模一樣。後端沒開放還帶 off 會被 403。 */
export function messageBody(body: TurnBody, safety: { canDisable: boolean; off: boolean }): TurnBody & { safety?: 'off' } {
  return safety.canDisable && safety.off ? { ...body, safety: 'off' } : body
}
