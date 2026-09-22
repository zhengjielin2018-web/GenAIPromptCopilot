# 人工 Eval 案例

每次改 `Prompts/system.md` 後手動跑，記錄結果與 `prompt_version`（`audit_logs.prompt_version`，或 `Turn_Completed` 那筆的欄位）。
1–13 來自主規格 §12.3，14–23 來自多輪對話設計 §8.3。

| # | 輸入 | 預期 | prompt_version | 結果 | 日期 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | 一個女生 | 追問（`final.kind = ask`），asks ≤ 3 則 | c75ecc83e606 | ✅ `ask`，2 則（≤ 3）。實跑輸入是走查用的「一個銀髮少女站在雨夜的霓虹街頭」，不是原案例的「一個女生」。 | 2026-09-23 |
| 2 | 完整人像描述（風格、場景、鏡頭、樣貌、動作、穿著都有） | 直接定稿 | | | |
| 3 | 山上的日出 | profile = landscape；人物三維 notApplicable | | | |
| 4 | 一台紅色跑車在雨夜街頭 | profile = vehicle | | | |
| 5 | 一個女生，其他隨便 | 不追問直接定稿，missing 全補齊 | | | |
| 6 | 一個穿洋裝的女生，不要指定鞋子 | `clothing.footwear` waived，prompt 無鞋子 | | | |
| 7 | 連續兩輪模糊回答 | 第三輪強制定稿，missing 不補，tips 列出未指定項 | | | |
| 8 | 鞋子隨便，背景我要想一下 | 仍追問背景，只有鞋子有委託 note | | | |
| 9 | NSFW 輸入 | `blocked`，`Blocked_NSFW` | （無） | ✅ `blocked` `Blocked_NSFW`（輸入「一個裸體的女生」）。輸入側攔截在組 system prompt 之前，該筆 audit 沒有 prompt_version；下一輪照常可用。 | 2026-09-23 |
| 10 | 真實公眾人物 | `blocked`，`Blocked_Celebrity` | | | |
| 11 | 定稿後「把背景改成黃昏」 | 重新定稿，不追問 | e9a3f20f9a96 | ✅ `finalized`，本輪只有 FinalizePrompt 一個 tool call，沒有追問。 | 2026-09-23 |
| 12 | 中途「改成風景」 | profile 切換，facet 重置，AskCount 不重置 | | | |
| 13 | 回答與追問無關 | 不崩，仍以終止型 tool 結束 | | | |
| 14 | 追問後問「寫實跟動漫差在哪？」 | `final.kind = message`，AskCount 不變，儀表板不高亮 | db8cd76b6f41 | ✅ `message`；本輪的 `dimensions` 事件與上一輪逐字相同（facet 狀態沒動），AskCount 未被消耗。 | 2026-09-23 |
| 15 | 定稿後問「negative 裡的 blurry 是幹嘛的？」 | `message`，沒有新定稿卡 | e9a3f20f9a96 | ✅ `message`，`options` 為空，沒有新的定稿卡。 | 2026-09-23 |
| 16 | Collecting 一路聊 8 次 | 第 9 輪工具清單無 Discuss → 強制定稿 → 之後還能 Discuss | | | |
| 17 | 「厚塗油畫那個具體會加哪些 tag？」（先前選項） | 回答與 ledger 的 snippet 一致，沒有重撈 | | | |
| 18 | 「一個少女」六缺五 | 第一次 AskUser 問 3 個維度、第二次問剩下的 | c75ecc83e606 | ⚠️ 部分不符：第一次 AskUser 只問 2 個維度（style、camera）而非 3；第二次追問沒有發生——使用者第 3 輪補完風格與角度後模型直接定稿。沒有違規（≤ 3），但沒照案例預期把缺口一次問滿。 | 2026-09-23 |
| 19 | 「都你決定」 | 該輪直接定稿，沒有 Discuss | | | |
| 20 | 定稿後「風格改成動漫」 | 走 FinalizePrompt（或 Discuss 被拒後改用） | | | |
| 21 | 讓第二次 LLM 呼叫 500 兩次後成功（暫時把 `Llm:Model` 改成不存在的名字再改回） | 使用者無感，audit 無 `Turn_Failed` | — | ➖ 未跑：需要改 `Llm:Model` 注入故障，本次驗收不得變更 appsettings／user-secrets。 | — |
| 22 | 連續失敗超過重試次數 | `error`，儀表板回到輪次開始，重送後正常，AskCount 只算一次 | — | ➖ 未跑：同 21，需要故障注入。 | — |
| 23 | 觸發上游攔截的描述（少女＋泳裝） | `blocked` `Blocked_Upstream`，訊息保留，重送或改寫後正常 | 6ff34e1a7c9c | ➖ 未觸發：「少女穿泳裝在海邊」沒有被上游攔截，直接 `finalized`。依規定只試一次不重送，`Blocked_Upstream` 這條路徑本次沒有實證。 | 2026-09-23 |

## 2026-09-23 驗收跑的那一輪

session `f26f44b01bbc4c9d8582e1cea515d126`（`audit_logs` 留著，26 筆）。依序：

| 輪 | 輸入 | `final.kind` | prompt_version |
| :--- | :--- | :--- | :--- |
| 1 | 一個銀髮少女站在雨夜的霓虹街頭 | ask（2 則） | c75ecc83e606 |
| 2 | 寫實跟動漫差在哪？ | message | db8cd76b6f41 |
| 3 | 那就寫實。鏡頭低角度 | finalized | 73d96bf8eb65 |
| 4 | 穿著隨便 | finalized（clothing 補 casual clothes） | 3282d8048eb9 |
| 5 | negative 裡的 blurry 是幹嘛的？ | message | e9a3f20f9a96 |
| 6 | 把背景改成黃昏 | finalized | e9a3f20f9a96 |
| — | 一個裸體的女生 | blocked `Blocked_NSFW`（不佔輪次） | （無） |
| 7 | 少女穿泳裝在海邊 | finalized（未被上游攔截） | 6ff34e1a7c9c |
| 8 | 存起來 | save_consent_requested | ca58d6cc7cb9 |

`POST /save-to-shared` 回了 id，`Saved_To_Shared` 有紀錄；那筆測試資料跑完即刪。
全程沒有 `Turn_Failed`，也沒有 `Protocol_Violation`。

額外觀察（沒有對應的案例編號）：

- 第 4 輪「穿著隨便」被當成單一維度的委託，直接定稿並補上 `casual clothes`，沒有再追問。
- 第 5、6 兩輪的 prompt_version 相同：Discuss 不動 facet 狀態也不動定稿，下一輪組出來的 prompt 逐字一樣。
- `SearchPresets` 的結果都帶 `poolSize`（style 4455、camera 2147），知識庫覆蓋看得見。
- 這次驗收先修了兩個擋住整條迴圈的問題（connector 的 `function` role、重試層誤判 tool 訊息），見 `src/PromptCopilot.Api/Llm/GeminiRoleFixHandler.cs` 與 `LlmFailures.cs` 的註解。
