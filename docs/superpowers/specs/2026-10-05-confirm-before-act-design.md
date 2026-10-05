# 先確認再動手：設計

日期：2026-10-05
來源：2026-10-05 brainstorm。使用者回報「對話感不太重」：提出要求後模型直接動手，例如角色已經拿著相機與飲料、使用者再要雨傘，模型不先問就改。
附帶一案：追問卡不再推薦整套組合（§8），推翻 [2026-09-25 整套組合推薦](2026-09-25-set-recommendations-design.md)「每次追問也推薦」的定案。

---

## 1. 問題與目標

**問題：模型被結構逼著直接動手。** 定稿後只要 facet 有變就必須 `FinalizePrompt`（system.md 流程第 3 條）；`AskUser` 只在 `Collecting` 才在清單上（`ToolSetBuilder`）；`Discuss` 定稿後帶狀態變更會被拒。「先問一句再改」在現在的協定裡沒有合法出口，不只是 prompt 寫得不夠。追問階段也一樣：模型把使用者的話直接寫進 facet，使用者看到的是結果，不是理解。

**目標**：

- 使用者說的任何會改動畫面的話，模型都先確認，使用者明確同意後才動手。範圍包含第一次描述、每一批追問的回答、定稿後的每個修改（brainstorm 選項 C，「盡量謹慎」）。
- 「先確認」由程式保證：沒確認就拿不到會改狀態的工具。不靠 system prompt 拜託模型。
- 要求跟現有內容衝突或有多種解讀時，確認卡列出 2–4 個解讀讓使用者選（雨傘：換掉相機／換掉飲料／三樣都拿）。
- 確認之前，facet 狀態、儀表板、定稿都不變。

**不是目標**：縮短等待。每個要求變成「確認輪＋動手輪」，等待約為兩倍；這是選「盡量謹慎」時接受的取捨（§2）。

## 2. 決定紀錄

| 題目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 什麼要確認 | **任何會改畫面的使用者文字**：第一次描述、追問的回答、定稿後的修改、「隨便／直接給我」 | 使用者選 C 並要求「盡量謹慎」；追問階段也選「每批回答都確認」而不是「定稿前總確認」 |
| 什麼不用確認 | 對確認卡本身的回應（按鈕）；採用推薦組合（採用彈窗本身就是確認）；純提問與討論（不改畫面）；要求存進共享庫（`RequestSaveConsent` 本身就是確認卡） | 再確認會無限循環或重複確認 |
| 做法 | **兩段式、程式把關**（確認輪只有 `Confirm`／`Discuss`，按鈕後的動手輪才有改狀態的工具） | 跟定稿閘門、追問額度同一套原則：違規的選項不給選。只改 prompt 的做法不採用：定稿閘門當初就是因為 system.md 的追問政策不被遵守才改成程式把關（`DialogPlugin.UnaskedMissing`） |
| 預先算好、按「對」立即套用 | **不做** | 有歧義時沒辦法預先算每個解讀；藏起來的結果要跨輪存在 session、history 要特別處理；摘要可能跟實際改動對不上。之後嫌慢，可以只對「定稿前總確認」補預先計算（那裡沒有多解讀） |
| 什麼算「明確確認」 | **只認按鈕**（按「對」或點一個解讀）；在輸入框打「好」「OK」一律當新的意見，再出一張確認卡 | 程式保證、最嚴格。讓模型判斷「打的字是同意還是修正」會讓「好，不過雨傘要紅色」這種半同意半修改直接動手，又變成靠 prompt |
| 確認卡失效 | 只有最新一筆待確認可以按；中間夾 `Discuss` 問答仍有效；出了新的確認卡或已經動手（含採用）就失效 | 狀態沒變的期間，卡片內容仍然成立 |
| 確認時要不要看英文 tag | **不要**，tag 留到定稿卡 | 確認的是意圖；使用者在 brainstorm 沒要求 |
| 確認前儀表板預覽 | **不做** | YAGNI；之後真的需要再加 |
| 動手輪的輸入分類器 | **不跑** | 動手輪的內容是上一輪模型的確認文字，已過輸出審查；原話在確認輪已過輸入審查。省一次 LLM 呼叫 |
| history 保留輪數 | `HistoryTurns` **10 → 20** | 每個要求多一則「對，就這樣」使用者訊息，維持原本記得的要求數；確認輪只有一次 `Confirm`、沒有檢索結果，增加的 token 有限 |
| 追問卡推薦 | **拿掉**，只有定稿卡推薦；採用改成定稿後才收 | 使用者 2026-10-05 決定（§8） |

## 3. 回合模型

### 3.1 兩種輪

| 輸入 | 輪 | 工具清單 |
| :--- | :--- | :--- |
| 使用者文字（`{"text": …}`） | **確認輪** | `Confirm`、`Discuss`、`SearchPresets`、`SearchSimilarPrompts`；`Finalized` 時加 `RequestSaveConsent` |
| 按確認卡（`{"confirm": …}`） | **動手輪** | `SetProfile`、`SetFacetStates`、`FinalizePrompt`、`SearchPresets`、`SearchSimilarPrompts`；`Collecting` 且 `AskCount < MaxAskCount` 且不是「隨便」時加 `AskUser` |
| 採用（`{"adopt": …}`） | **動手輪** | 同上（採用只在定稿後，見 §8，所以實際上沒有 `AskUser`） |

沿用的既有規則：

- 知識庫關掉（`retrieval: off`）時兩種輪都拿掉兩個檢索工具。
- 確認輪的 `Discuss` 照現在的規則出現：`Finalized`，或 `DiscussStreak < MaxDiscussStreak`；「隨便」那一輪拿掉。所以「隨便」的確認輪只剩 `Confirm`（加檢索）。
- 追問額度 `MaxAskCount = 2` 不變；確認輪不算追問、不碰 `AskCount`。

`ToolSetBuilder.Build` 多一個參數 `TurnKind`（`Propose`／`Act`）。`ToolNames.Always` 拆成兩組；`ToolNames.Terminal` 加 `Confirm`。

### 3.2 `Confirm`（新的終止型工具，在 `DialogPlugin`）

```
Confirm(
  message: string,          // 繁中卡片正文
  choices: string[]? = null // 0 個或 2–4 個解讀，陳述句
)
```

- `choices` 要有預設值：SK 只看「有沒有預設值」決定必填（`Discuss.options` 踩過一次）。
- 清洗：`message` 去空白後不可為空；`choices` 去空白、去空字串、去重複後只能是 0 個或 2–4 個，每個 40 字以內（prompt 要求 20 字，伺服器放寬到 40，模型稍微超過不必白燒一次重叫）。不合格回錯誤字串讓模型重叫（同 `AskUser` 清洗的做法），被拒的理由記進 `turn.Rejections`。
- **不改任何 facet 狀態**，沒有 `facetStates` 參數。
- 成功時在 session 記一筆待確認（§3.4），`turn.Outcome = new ConfirmOutcome(message, choices)`。
- 輸出審查：`OutputSafetyFilter.Guarded` 加 `Confirm`；`OutputTextFor` 取 `message` 與每個 `choices`。

### 3.3 `Discuss` 任何時候都不能改 facet

現在只有 `Finalized` 時擋「帶著改過的狀態」。改成不分狀態都擋（`Profile` 已設定時才比對，比對基準照舊是 `turn.TurnStartFacetStates`），否則 `Discuss` 是繞過確認的後門。錯誤字串改成：「錯誤：Discuss 不能改 facet 狀態；使用者要改畫面時，請用 Confirm 跟他確認」。

`Discuss` 的 `facetStates` 參數保留（儀表板同步仍用它；值必須等於現值）。

### 3.4 待確認（`PendingConfirmation`）

```
record PendingConfirmation(int TurnIndex, string Message, IReadOnlyList<string> Choices, bool AutoComplete);
```

- 放在 `Session.PendingConfirmation`，進 `Snapshot`／`Restore`：確認輪被攔或失敗時不會留下半張卡；動手輪失敗時回滾回來，確認卡仍可再按。
- `AutoComplete`：確認輪的輸入分類器判定「隨便／直接給我」時為 true。**確認輪不改 `session.AutoFill`**，按下確認後才設。
- 寫入：`Confirm` 成功時覆蓋舊的。
- 清除：動手輪（含採用輪）在取快照之後、呼叫模型之前清掉。`Discuss`、`RequestSaveConsent`、被攔、失敗都不清。

### 3.5 按確認（`{"confirm": {"turnIndex": n, "choice": k | null}}`）

端點拿到 session 鎖之後驗證：

| 情況 | 回應 |
| :--- | :--- |
| 沒有待確認 | `409`「沒有待確認的內容」 |
| `turnIndex` 不等於待確認的輪次 | `409`「只有最新一張確認卡可以按」 |
| 待確認有選項，`choice` 是 null 或超出範圍 | `400` |
| 待確認沒有選項，`choice` 不是 null | `400` |

通過後組 `TurnInput`：

- `Text`（使用者泡泡與 history 裡的使用者訊息）：沒有選項 → 「對，就這樣」；有選項 → 那個選項的原文。`session` 事件的 `text` 帶回這句（同採用）。
- `Confirmed = (pending, choice)`。

`AgenticOrchestrator` 遇到 `Confirmed`：不跑 `guard.CheckAsync`，改用 `GuardResult.Ok(pending.AutoComplete)`；取快照後清掉待確認；`AutoComplete` 時設 `session.AutoFill = true`；工具清單用 `Act`。

`confirm` 與 `adopt` 同時出現回 `400`；有 `confirm` 時 `text` 忽略。`safety` 照舊適用：`off` 時動手輪的輸出不檢。

## 4. 事件與 API

- `final` 多一種 `kind: "confirm"`：`{ message, choices: string[] }`（沒有選項時是空陣列）。
- 確認輪沒有 `recommendations`（`TryRecommendAsync` 只對定稿，見 §8）。
- `dimensions` 照舊在成功的一輪最後送；確認輪的狀態不變，送的是現值。
- `messages` 端點說明補上 `confirm` body、`final.kind = confirm`、§3.5 的 409／400，以及定稿前 `adopt` 的 409（§8）。
- `GET /api/sessions/{id}` 不變。前端從對話流推算哪張卡可按（§7）；跟伺服器不一致時，伺服器的 409 會說明。

## 5. System prompt

`system.md` 的「## 流程」段換成 placeholder `{{FLOW}}`，內容依這一輪的種類二選一，放在兩個新檔 `Prompts/flow-propose.md`、`Prompts/flow-act.md`（csproj 跟 `system.md` 一樣 `CopyToOutputDirectory`）。模型只看得到這一輪適用的規則。`SystemPromptBuilder.Build` 多收 `TurnKind` 與確認內容；版本 hash 照舊由組好的整份 prompt 算。

### 5.1 確認輪

| 使用者說的 | 怎麼確認 |
| :--- | :--- |
| 第一次描述題材 | 用使用者的說法複述理解的畫面：主體，以及各面向講到的東西；一句話帶過還沒講的面向。不補沒講的。不預告確認後是追問還是定稿 |
| 回答追問 | 「我會把風格設成寫實攝影、鏡頭設成低角度」。不預告確認後是追問還是定稿 |
| 定稿後要改 | 對照「目前的定稿」，講清楚改哪裡、從什麼改成什麼 |
| 隨便／你決定／直接給我 | 對每個還有 missing facet 的維度逐一寫出補成什麼（「風格補寫實攝影、鏡頭補半身平視、背景補雨夜街景」），說明確認後直接定稿。只列維度名稱不算 |
| 純提問、討論 | 用 `Discuss` 回答，不確認。問題夾著修改就走 `Confirm`，正文順便回答 |
| 要存進共享庫 | `RequestSaveConsent` |

**什麼時候給 `choices`**：要求跟現有內容衝突，或有多種解讀。

- 搶同一個位置：雙手已滿再拿東西、地點換地點、時間或光源互斥（正午與霓虹夜景）。
- 前後矛盾：晴天又下雨、拿掉雨卻還撐傘。
- 太模糊：「更有氣質」「換個感覺」→ 2–4 個具體方向。

選項寫成陳述句、20 字以內、彼此互斥；做得到的「全都要」也列，註明代價（「三樣都拿（可能不自然）」）；不列「算了不改」。

**正文**：繁中 1–3 句，用使用者的說法，不寫英文 tag。單一提案時寫成陳述，按鈕由前端加。

### 5.2 動手輪

開頭是一段伺服器組的區塊：

```
## 使用者已確認

<確認卡正文>
使用者選的是：<選項原文>          ← 有選項時才有

這一輪照上面的內容動手，不要加入確認以外的改動。
```

流程同現在的第 1、3、5 條：第一次確認後 `SetProfile` → `SetFacetStates` → 檢索 → 還有 missing 而且工具清單有 `AskUser` 就 `AskUser`，否則 `FinalizePrompt`；定稿後的修改 `FinalizePrompt`。現在的第 6 條（採用）搬到這裡，並簡化成採用後一律 `FinalizePrompt`（§8）。

### 5.3 其他

- 純文字補救提示（`CallWithReminderAsync`）改成列出這一輪實際有的終止型工具，不再寫死四個名字。
- `intentSummary` 範例句不動。

> 2026-10-05 驗收後的修正（`docs/eval-cases.md` 該節的 C2、C8 與補充觀察），現行做法：
> - **確認卡不預告下一步。** 原本 §5.1「回答追問」要卡片講「接著問 X、Y」或「直接定稿」，驗收時常講錯：卡片說直接定稿，動手輪照 §5.2 還是追問了。確認輪猜不到一段自由回答會讓哪些 facet 變 covered；即使伺服器先照規則算好「確認之後的下一步」給它，回答第一次追問那張仍只有 2/6 講對。所以卡片只講要設什麼，下一張卡自然呈現接下來是追問還是定稿。例外是「隨便」：動手輪一律直接定稿，卡片照舊說確認後直接定稿。
> - **「隨便」補齊每一個 missing facet。** 確認卡要逐維度寫出補成什麼。Session 事實多一行「還有 missing facet 的維度」，算法同動手輪：waived 與有委託 note 的不算。§5.2 區塊在「隨便」那張卡（待確認的 `AutoComplete`）的最後一句改成「使用者把沒講的交給你決定：補齊每一個 missing 的 facet 就是他確認的內容…」：原句「不要加入確認以外的改動」會讓定稿幾乎不補（`tagOrigins.llm` 0–1）。`flow-act.md` 第 4 條同樣寫明補齊不算「確認以外的改動」。
> - Session 事實的追問改成「追問已用：N／上限 M」。
> - **兩個流程段寫明工具的完整名稱**（`Dialog_Confirm`、`Dialog_FinalizePrompt` 等）。模型有時只寫 `Confirm`，SK 回「function that wasn't defined」，模型就一直重送，直到整輪 120 秒逾時。改過流程段文字之後特別常見：沒改過的 prompt 12/12 正常，改寫後的規則文字 3/20。寫明完整名稱後 12/12 正常。程式端還沒有防護，列為後續工作。

## 6. 邊界情況

1. **工具預算用完**：確認輪 → 強制 `Confirm`（只掛 `Confirm`，提示「tool 呼叫預算已用盡。請立即以目前的理解呼叫 Confirm 跟使用者確認」）；動手輪 → 照舊強制 `FinalizePrompt`。`ForcedConfirmAsync` 與 `ForcedFinalizeAsync` 共用同一個骨架。
2. **模型只回純文字**：確認輪照舊包成 `Discuss`（`Discuss` 不能改狀態，包了也安全）；動手輪沒有 `Discuss`，重試仍無結果就 `ProtocolViolationException`，整輪回滾，待確認回來，卡片可再按。
3. **「隨便」**：確認輪偵測到時工具只剩 `Confirm`（加檢索），`AutoComplete` 記在待確認；按下後才 `AutoFill = true`、動手輪不給 `AskUser`。
4. **確認卡沒按時做了別的**：問問題（`Discuss`）→ 卡片仍有效；採用 → 動手，卡片失效；換一批推薦、存進共享庫 → 不影響。
5. **重載**：確認卡在對話流（sessionStorage）裡，伺服器記著待確認，重載後照樣能按。session 過期照舊開新對話。
6. **兩個分頁**：伺服器只認最新一筆待確認，舊分頁按到過期的卡回 409。

## 7. 前端

- **型別**：`FinalData` 加 `{ kind: 'confirm'; message: string; choices: string[] }`；`TurnBody` 加 `{ confirm: { turnIndex: number; choice: number | null } }`。
- **`lib/confirm.ts`**：
  - `pendingConfirmTurn(transcript)`：從尾端往回找，先碰到 `confirm` 就回它的輪次；先碰到 `ask`／`finalized` 回 null；`message`、`save_consent_requested`、失敗條目、使用者泡泡、工具卡跳過。規則與 §3.4 一致。
  - `confirmDisplay(data, choice)`：泡泡暫代字（「對，就這樣」或選項原文）。
- **`ConfirmCard.vue`**：正文；沒有選項時一顆「對，就這樣」，有選項時每個選項一顆；底下一行「不對的話，直接在下面打字修正。」。顏色與追問卡（黃）、失敗（洋紅）區分。按鈕只在 `turnIndex === pendingConfirmTurn` 且不在跑時可按；過期的停用，title「已經有新的進展，這張卡不能再按」。
- **store**：`confirm(turnIndex, choice)` → `runTurn(confirmDisplay(…), { confirm: … })`。非 200 時顯示伺服器的理由（同 `adopt`）。
- **失敗**：按確認那一輪的失敗條目不帶原文（不出「重試：把原文放回輸入框」，放回去送出會變成新意見），訊息後補一句「確認卡還在，可以再按一次」。
- **輸入框**：有可按的確認卡時，placeholder 改成「按上面的按鈕套用；在這裡打字會當成修正」。
- **追問卡**：選項照舊填進輸入框，送出走確認輪。推薦條拿掉（§8）。

`manual-tests/chat.py`：`/ok` 送 `choice: null`，`/1`～`/4` 送對應選項；顯示確認卡的正文與編號選項。

## 8. 附帶變更：追問卡不再推薦

使用者 2026-10-05 決定：未定稿前不用推薦。

- `AgenticOrchestrator.TryRecommendAsync` 只對 `FinalizedOutcome`。
- `RecommendationService.BuildAsync` 拿掉 `AskOutcome` 分支與 `AskRowAsync`（變成用不到的程式），`outcome` 參數型別改成 `FinalizedOutcome`。相關測試改寫或刪除。
- 採用端點：`s.Status != Finalized` 回 `409`「定稿後才能採用組合」，取代「尚未判定題材」那條（定稿必然已有題材）。
- 前端：`AskCard` 拿掉 `RecommendationStrip`；可採用的卡只認最新一張定稿卡，改用現有的 `latestFinalizedTurn`，刪掉 `lib/adopt.ts` 的 `latestRecommendableTurn`。
- system.md 採用規則簡化（§5.2）。
- `scripts/adoption_report.py` 不改：舊資料照樣讀，新資料的追問卡那欄是 0。

計畫裡排第一個任務、獨立 commit，跟確認流程互不依賴。

## 9. 稽核

- `Turn_Completed` 的 `outcome` 多 `ConfirmOutcome`，payload 加 `confirmChoices`（選項數）。
- 動手輪 payload 加 `confirmed: { turnIndex, choice }`。
- 動手輪的 `RawInput` 是組好的那句（「對，就這樣」或選項原文）。

## 10. 測試

### 10.1 後端單元（xUnit，`FakeChatCompletion`）

- `ToolSetBuilder`：三種輸入各自的清單；「隨便」確認輪只剩 `Confirm`＋檢索；`retrieval: off` 兩種輪都沒有檢索；確認輪 `Finalized` 有 `RequestSaveConsent`、動手輪沒有。
- `DialogPlugin.Confirm`：0 個與 2–4 個選項通過；1 個、5 個、超過 40 字、空 `message` 回錯誤；去重後剩 1 個也回錯誤；呼叫後 facet 狀態不變；待確認的輪次與 `AutoComplete` 正確。
- `DialogPlugin.Discuss`：`Collecting` 帶改過的狀態被拒。
- `Session`：待確認進快照、`Restore` 回來。
- `AgenticOrchestrator`：
  - 動手輪不呼叫輸入分類器（fake 計數 0）；`session` 事件 `text` 是組好的句子。
  - 動手輪 system prompt 含「使用者已確認」區塊與選項；確認輪不含。
  - 動手輪失敗（fake 丟例外）後待確認還在。
  - 確認輪預算用完 → 強制 `Confirm`，只掛 `Confirm`。
  - 動手輪兩次純文字 → `protocol_violation`。
  - `AutoFill` 只在按下確認後為 true。
  - 採用輪清掉待確認。
- 端點：§3.5 四種錯誤；`confirm`＋`adopt` 同時出現 400；定稿前 `adopt` 409。
- `GeminiToolDeclarationTests`：`Confirm` 的 `choices` 不在 required。
- `SystemPromptBuilder`：兩種輪換進不同流程段。
- 推薦：追問輪不產生 `recommendations`；定稿輪照舊。
- `OutputSafetyFilter`：`Confirm` 的 `message` 與 `choices` 會送進分類器。

### 10.2 前端（Vitest）

- reducer 收 `final` `confirm`。
- `pendingConfirmTurn`：只有確認卡 → 它；確認卡後接 `Discuss` → 仍是它；後接追問卡或定稿卡 → null；後接失敗條目 → 仍是它；兩張確認卡 → 後一張。
- `confirmDisplay`。
- 確認輪失敗條目不帶原文。
- 可採用的卡只認最新一張定稿卡。

### 10.3 瀏覽器驗收（Claude 用 Playwright 跑，寫進 `docs/eval-cases.md` 新的一節）

| # | 操作 | 預期 |
| :--- | :--- | :--- |
| C1 | 送「一位金色短髮的中年女士拿著相機和飲料站在雨夜的霓虹街頭」 | 確認卡複述畫面；按下前儀表板不變；按「對」後出追問卡，沒有推薦條 |
| C2 | 用選項回答追問 | 先出確認卡（「我會把風格設成…」），按「對」後才追問或定稿 |
| C3 | 定稿後送「讓她拿雨傘」，跑 3 次 | 3 次都是有選項的確認卡；選「換掉飲料」後定稿有雨傘與相機、沒有飲料 |
| C4 | 定稿後送「鞋子換成靴子」 | 單一提案卡；按「對」後重新定稿 |
| C5 | 確認卡沒按時問「寫實跟動漫差在哪」 | `Discuss` 回答；之後舊確認卡仍可按且有效 |
| C6 | 確認卡出現後在輸入框打「好」 | 出新的確認卡，不動手 |
| C7 | 動手後看舊確認卡；curl 帶舊輪次送確認 | 舊卡停用；curl 409 |
| C8 | 送「直接給我」 | 確認卡列出打算補的內容；按「對」後定稿 |
| C9 | 確認卡沒按時重新整理 | 卡片回來，照樣能按 |
| C10 | 從定稿卡採用一套；curl 在追問階段送採用 | 採用直接動手、不出確認卡；curl 409 |

驗收時從 log 的 `Turn …` 摘要行記確認輪與動手輪的耗時中位數，寫進驗收紀錄。

C3 若 3 次中有沒給選項的，先調 `flow-propose.md` 的衝突說明再重跑，不放寬標準。

## 11. 文件同步

- 主規格 [2026-09-21-genai-prompt-copilot-design.md](2026-09-21-genai-prompt-copilot-design.md)：§4.2 工具表加 `Confirm`、§4.3 清單規則改成兩種輪、§4.4 狀態機補待確認、§4.6 確認輪的預算用盡、§4.9 system prompt 的 `{{FLOW}}`、§10.1／10.2 `confirm` body 與 `final.kind`、§11 確認卡、§12 測試。各處註明「2026-10-05，見先確認再動手設計」。
- `SessionEndpoints` 的 `messages` 說明（§4）。
- README：第 5 行「每次追問與定稿另外推薦」改成只有定稿；第 45 行一輪對話的描述補上確認輪／動手輪。
- [2026-09-25 整套組合推薦](2026-09-25-set-recommendations-design.md)與 [2026-09-30 推薦組法](2026-09-30-recommendation-slate-design.md)：講到追問卡推薦的地方標註「2026-10-05 起只在定稿卡」。
- `docs/eval-cases.md`：S1、S7、G5 標註已改；新增 C1–C10 一節。
- `docs/單輪流程說明.md` 描述的是 `scripts/demo.py`，主體不動；只有 §8 的 C# 端說明（推薦與採用）在最後審查時改成現行做法。

## 12. 不做

- 確認前在儀表板預覽改動。
- 預先計算、按「對」立即套用（§2）。
- 確認時顯示英文 tag。
- 讓打字的「好」算確認。
- 改 `MaxAskCount`、`MaxDiscussStreak`。
- 改 `scripts/demo.py` 單輪流程。
