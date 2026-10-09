/** 測試用的審查開關：後端開放（GET /api/config/safety）而且使用者關掉了，才帶 safety: off；
 *  其餘情況送出的 body 跟沒有開關時一模一樣。後端沒開放還帶 off 會被 403。對話輪與生成預覽共用（預覽設計 §8）。 */
export function messageBody<T extends object>(body: T, safety: { canDisable: boolean; off: boolean }): T & { safety?: 'off' } {
  return safety.canDisable && safety.off ? { ...body, safety: 'off' } : body
}
