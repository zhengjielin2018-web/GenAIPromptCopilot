# 檢索時機：設計

日期：2026-10-06
來源：2026-10-05～06 brainstorm。使用者看了最新一次 session，覺得「RAG 實際找到的東西不多，幾乎都靠模型自己想」。

---

## 1. 問題與目標

**現象**（session `ddaf2115…`，2026-10-05 15:39–15:47）：15 輪、定稿 5 次，`SearchPresets` 只在第 2 輪叫 1 次，`SearchSimilarPrompts` 0 次。

| 輪 | 使用者 | 檢索 | 結果 |
| :--- | :--- | :--- | :--- |
| 2 | 確認第一句描述 | 有（唯一一次） | 查的是使用者講過的東西（中年女士、金色短髮、街頭、霓虹、雨夜），加上只寫「風格」「鏡頭」兩個字的維度項目，撈回印象派、Liam Wong 這類不相干的片段；追問卡的風格選項 `presetId` 都是 null |
| 6 | 衣服你幫我設計 | 無 | 模型自己寫 `dark trench coat` |
| 8 | 家居感的衣褲 | 無 | 模型寫 `comfortable lounge wear top, relaxed lounge pants`，知識庫 0 筆；知識庫有 70 筆睡衣、運動褲、帽 T 類穿著片段 |
| 9 | 你推薦一些場景設計讓我參考 | 無 | 有檢索工具，直接用 `Discuss` 憑空列方向 |
| 11 | 深夜咖啡廳前 | 無 | 模型寫 `standing in front of a cozy cafe, warm window glow…`；知識庫有 15 筆咖啡廳片段，#4581「夜間咖啡廳 `night, cafe, neon lights, streetspace`」幾乎就是要的畫面 |
| 13、15 | 上班族、齊劉海 | 無 | 模型自己寫 |

最後一版定稿的 tag 來源是 llm 17、rag 4、base 3。那 4 個 rag（`city street`、`neon lights`、`rain`、`long black hair`）全是使用者原話直譯、剛好跟片段字面相同；`long black hair` 是靠字尾規則對上某個角色片段的 `black hair`。

**原因**：流程說明只在一個地方叫模型檢索——`flow-act.md` 第 1 條（第一次描述）的 `{{RETRIEVAL_STEP}}`，而且寫的是「用一次」。第 2 條（回答追問）、第 3 條（定稿後修改）、第 4 條（隨便）與確認輪都沒提。模型照字面執行：9/24 改成批次檢索（`dd170ed`）之後，每個 session 平均約 1 次檢索，最近十幾個 session 全是 1 次。這是 agentic 化之後才有的問題：Python `demo.py` 是固定管線，每次都由程式檢索每個維度。

**目標**：

- 定稿裡真正從片段借來的 tag 明顯變多。
- 模型自己編、不像 SD tag 的寫法變少（`comfortable lounge wear top`）。
- 交給模型決定的內容（「衣服你幫我設計」「隨便」「推薦一些場景」）從知識庫挑。
- 延遲增加在可接受範圍（§6.3）。

**不是目標**：改 tag 來源的分類（§2）；讓模型用共享庫（`SearchSimilarPrompts`，§9）。

## 2. 決定紀錄

| 題目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 哪些動手輪要檢索 | **每個寫入新內容的動手輪**：第一次描述、回答追問、定稿後修改、隨便；採用不查 | 使用者選 A。具體描述也能被片段校正成真的 SD tag（咖啡廳那例）；代價是每個動手輪多約 2–3 秒 |
| 確認輪要不要檢索 | **卡片要寫出使用者沒講的具體內容時要**（隨便、單項委託、模糊要求的解讀、Discuss 推薦） | 使用者選 A。卡片一寫出「深色風衣」，動手輪就只能照做，知識庫要在寫卡之前進來 |
| 確認輪的檢索結果 | **多留一輪不壓縮**（§4） | 現在每輪結束就把片段內容壓掉，只剩 id 與標題；不留的話動手輪拿不到確認輪挑的 tag |
| 直譯碰巧對上怎麼算 | **來源分類不改，只在報表另算「借來／碰巧對上」**（§5.1） | 使用者選 C：碰巧對上至少代表知識庫證實了模型的寫法是真 tag，不該從 rag 拿掉；前端不動 |
| 做法 | **只改流程說明＋量測**（brainstorm 做法 1） | 第一輪那條檢索步驟模型每次都照做，服從度高，問題出在沒寫到。程式把關（做法 2）與伺服器代查（做法 3）留作退路（§6.4） |
| 只寫維度名稱的查詢 | **程式擋掉該項** | 「風格」「鏡頭」撈回的是全維度最近的隨機片段，沒有用；擋住只影響該項，其他項照常回結果 |
| `SearchSimilarPrompts` | **不在這案** | 共享庫是另一個來源，用不用、怎麼用是另一題；等這案的實驗結果再談 |
| `demo.py` | **不動** | 固定管線，沒有這個問題 |

## 3. 流程說明

### 3.1 動手輪（`flow-act.md`）

| 條 | 情況 | 檢索 |
| :--- | :--- | :--- |
| 1 | 第一次描述 | 照舊（`{{RETRIEVAL_STEP}}`），兩處收緊：沒講的維度的兩個對比方向要寫具體方向，**不可只寫維度名稱**；追問選項照 §3.3 從結果挑 |
| 2 | 回答追問 | 寫入前先用一次 `SearchPresets`，這一輪要寫的每個 facet 一項（`facetId`＋確認內容裡那一項的說法＋`tags`）。使用者選的選項帶 presetId 的，直接用「你先前提供過的選項」裡那個片段，不用再查那一項 |
| 3 | 定稿後修改 | 同第 2 條，查要改的 facet |
| 4 | 隨便 | 卡片列的內容若是從上一輪檢索結果挑的，直接用那個片段；卡片沒列到的 missing facet 再查（配合目前畫面寫具體查詢，例「雨夜街頭的外套」） |
| 5 | 採用 | 不查：內容本來就是片段 |

第 2–4 條的規則放在一段新的 `{{RETRIEVAL_ACT}}`（流程清單之後的獨立段落，不編號，off 時整段消失不留空號）。草稿：

> **檢索**：第 2–4 條要寫入新內容前（`SetFacetStates` 或 `FinalizePrompt` 之前），先用一次 `SearchPresets` 查這一輪要寫的 facet：每個 facet 一項，`facetId` 加上確認內容裡那一項的說法，附上你翻的英文 `tags`。使用者選的選項帶 presetId（見「你先前提供過的選項」），或確認卡的內容是從上一輪檢索結果挑的，就直接用那個片段的 tag，不用再查那一項。隨便的確認卡沒列到的 missing facet，配合目前的畫面寫具體查詢。第 5 條（採用）不查。
> **借用**：結果裡標「可借入提示詞」、而且跟確認內容相符的片段，寫 tag 時優先用片段的寫法，只借相符的詞；都不相符才用你自己翻的。

### 3.2 確認輪（`flow-propose.md`）

判斷標準：**卡片或回答要寫出使用者沒講的具體內容時，先檢索再從片段挑。**使用者自己講清楚要改什麼時不查，留給動手輪。

新段 `{{RETRIEVAL_PROPOSE}}`（同樣是清單之後的獨立段落）。草稿：

> **檢索**：卡片或回答要寫出使用者沒講的具體內容時，先用一次 `SearchPresets`，再從結果挑：說隨便／你決定（每個 missing 維度要列出補什麼）、把單一項目交給你（「衣服你幫我設計」）、要求太模糊要給 2–4 個解讀、問你推薦或還有什麼方向（`Discuss` 的參考方向）。查詢配合目前的畫面寫具體方向（例：「雨夜街頭的外套」「寫實攝影」）：整個維度用 `dimension` 項目，單一 facet 用 `facetId` 項目。卡片正文用中文描述你挑的片段內容，不寫英文 tag；`Discuss` 的參考方向帶片段的 presetId。使用者自己講清楚要改什麼時，這一輪不查，動手輪會查。

還沒題材的確認輪本來就沒有檢索工具（`ToolSetBuilder`），不受影響；第一句就說「隨便」時，確認卡照舊由模型寫，動手輪第 1 條的檢索負責校正寫法。衝突型的解讀（雨傘那例）不用查，模糊型的才要；這由模型判斷，報表不硬算（§5.3）。

### 3.3 選項從片段挑（`system.md`）

`{{RETRIEVAL_RULE}}`（提示詞規則段）的 on 版本加一條：

> - `AskUser` 的選項與 `Discuss` 的參考方向優先從檢索到的片段挑：label 寫片段的內容、tags 用片段的寫法、帶 presetId；知識庫沒有合適的方向才自己提（presetId 留空）。

`Discuss` 用法那句「`options` 是參考方向，可以是知識庫沒有的方向（`presetId` 留空）」改成「`options` 是參考方向；知識庫沒有的方向 `presetId` 留空」，不再鼓勵空 presetId。

### 3.4 Placeholder 與知識庫關閉

`SystemPromptBuilder` 多兩組 on／off 常數：`RetrievalActOn`／`RetrievalActOff`、`RetrievalProposeOn`／`RetrievalProposeOff`，off 都是空字串。替換位置跟現有兩個 placeholder 一樣：`{{FLOW}}` 換進來之後、session 內容換進來之前。`RetrievalStepOn` 照 §3.1 收緊。對照組（`retrieval: off`）組出來的 prompt 不可出現 `SearchPresets`。

### 3.5 只寫維度名稱的維度項目

`KnowledgePlugin.SearchPresetsAsync` 驗證維度項目（沒有 `facetId` 的）時，`query` 去空白後等於下列任一個，該項回錯誤、不進 embedding：

- 維度代號（`style`）
- `catalog.DimensionLabels[dimension]`（`風格`）
- `catalog.DimensionLabel(dimension, profile)`（題材專屬的名稱）

錯誤字串：「維度 {dimension} 的 query 只寫了維度名稱，請寫具體方向（例：寫實攝影、日系動漫插畫）」。其他項目照常回結果，跟現有的逐項錯誤一致。facet 項目不檢查：它的 query 是使用者原話。

## 4. 確認輪的檢索結果多留一輪

現況：每輪收尾時 `HistoryTrimmer.CompressTurn(history, startIdx)` 把這一輪的 `SearchPresets` 結果壓成只剩 id、標題、池大小。

改成：

- `CompressTurn` 多一個參數 `keepSearchResults`。確認輪收尾傳 true：`SearchPresets` 結果保留原樣，其他照常壓（`SearchSimilarPrompts` 照壓，不在這案）。
- 新增 `HistoryTrimmer.CompressSearchResultsBefore(history, endIndex)`：把 `endIndex` 之前所有 `SearchPresets` 結果壓縮。壓縮是冪等的（已壓過的形狀再壓一次結果相同），所以每輪都掃整段也不會壞東西。
- 每輪收尾（不分種類）在 `CompressTurn` 旁邊呼叫 `CompressSearchResultsBefore(history, 本輪使用者訊息的位置)`。效果：確認輪的檢索結果留到下一輪結束才壓。下一輪是動手輪、是另一個確認輪、還是 `Discuss`，都一樣。
- 位置跟現在的修剪一樣，在「事件先算好再修剪」之後。從這裡開始這一輪不會再回滾，所以改到前一輪的訊息不會跟快照衝突（`Restore` 只截掉尾巴，不還原前面訊息的內容）。
- 代價：下一輪的上下文多一份完整檢索結果，這次 session 那份約 1 萬字元（3–4k token）。

## 5. 量測

### 5.1 借來／碰巧對上（只用在報表）

Session 多一本 `TagTimeline`：正規化後的 tag（`TagAttribution.Normalize`）→ 第一次出現的是**模型**還是**片段**。已經有紀錄的 key 不覆蓋。

| 記成「模型」 | 記成「片段」 |
| :--- | :--- |
| `SetFacetStates` 每項的 `tags` | `SearchPresets` 每筆命中的 positive 片段，在寫進 ledger 時逐 tag 記 |
| `SearchPresets` facet 項目的 `tags`（在處理命中之前記，因為那是模型送進來的輸入） | |
| `AskUser`／`Discuss` 清洗後 `presetId` 為 null 的選項 `tags` | |

帶 presetId 的選項不記：tag 來自片段，片段那邊已經記過。`TagTimeline` 進 `Snapshot`／`Restore`（同 ledger）。

定稿時，對 `PositiveSources` 裡 origin 是 `rag` 的每個 tag：

- **借來**：timeline 裡這個 tag（整段相等）第一次出現是「片段」。
- **碰巧對上**：其他情況。包括模型先寫的，以及 timeline 裡根本沒有這個 tag（只靠字尾規則對上片段）。

例：`long black hair` 只靠字尾對上片段的 `black hair` → 碰巧對上。`streetspace` 先出現在 #4581 的片段、模型後來才寫 → 借來。第一輪 `SetFacetStates` 先寫 `city street`、檢索才撈到含 `city street` 的片段 → 碰巧對上。

純函式 `RagSplit.Classify(IReadOnlyList<TagSource> sources, TagTimeline timeline)` 回 `(borrowed, echo)` 兩個 tag 清單。**前端與 SSE 不變**：定稿卡的 rag 標記照舊（§2）。

### 5.2 稽核

`Turn_Completed` 的 payload 加：

| 欄位 | 內容 |
| :--- | :--- |
| `kind` | `propose`／`act`／`adopt` |
| `searches` | 本輪 `SearchPresets` 呼叫次數 |
| `searchItems` | 本輪送出的項目總數 |
| `searchItemErrors` | 其中回錯誤的項目數（含 §3.5） |
| `autoComplete` | 確認輪的輸入分類器判定「隨便」時為 true；其他輪省略 |
| `options` | 結局是 `AskUser` 或 `Discuss` 時：`{total, withPreset}`（清洗後） |
| `ragSplit` | 定稿時：`{borrowed: [...], echo: [...]}`，tag 原文清單 |

計數放在 `TurnContext`，由 `KnowledgePlugin` 與 `DialogPlugin` 累加。

### 5.3 報表

`scripts/adoption_report.py` 加一節「檢索時機」，並新增參數 `--sessions`（逗號分隔的 session id），讓重播只算自己跑出來的 session：

- 動手輪（`kind=act`，不含 `adopt`）的檢索率
- 「隨便」確認輪（`autoComplete`）的檢索率；帶參考方向的 `Discuss` 輪（`options.total > 0`）的檢索率
- `AskUser`／`Discuss` 選項帶 presetId 的比例
- 每次定稿的平均：借來、碰巧對上、llm、base、adopted；rag（借來＋碰巧對上）佔非基礎詞的比例
- 延遲中位數：確認輪、動手輪，各自再分有沒有檢索

單項委託與模糊解讀從 audit 分不出來，由重播腳本依劇本標註判斷（§6.2）。

## 6. 驗收實驗

### 6.1 順序

1. 先做量測（§5），流程說明不動，用重播腳本跑**基準**。
2. 再改流程說明、選項規則、維度名稱檢查與歷史壓縮（§3、§4），同一批劇本跑**改後**。

這樣前後是同一把尺。API 用 Docker 的 `api` 容器重建後跑：本機 Debug 建置會被 Windows 應用程式控制擋。

### 6.2 重播腳本

新增 `manual-tests/replay.py`，沿用 `chat.py` 的 `Chat` 客戶端；劇本放在 `manual-tests/replay_scenarios.json`。

- 步驟有兩種：`say`（送一段文字）；`pick`（照前端的格式，用最近一張追問卡每個維度的第一個選項組回答，可附 `extra` 文字，例如「衣服你幫我設計」）。沒有待回答的追問卡時，`pick` 只送 `extra`；`extra` 也沒有就跳過。
- 每個確認輪結束在 `Confirm` 時自動按確認，有解讀選項就選第一個；結束在 `Discuss` 就繼續下一步。
- 每步標註預期（`expectSearch`：`propose`／`act`／`none`）。腳本從 SSE 的工具事件判斷那一輪實際有沒有叫 `SearchPresets`，印出每步一列：輪別、結局、有沒有檢索、預期、延遲。最後印 session id，給報表的 `--sessions` 用。

劇本（`eval-cases.md` 編號 Q1–Q3）：

| 編號 | 步驟 |
| :--- | :--- |
| Q1 | 這次 session 的原話：say「一位金色短髮的中年女士站在雨夜的霓虹街頭」→ pick → pick＋「衣服你幫我設計」→ say「家居感的衣褲」→ say「你推薦一些場景設計讓我參考」→ say「深夜咖啡廳前（帶溫暖燈光與招牌）」→ say「改成黑長直髮的上班族女士」→ say「穿回家居服，頭髮要齊劉海，年齡是年輕上班族」 |
| Q2 | 隨便：say「一隻在森林裡的狐狸」→ say「其他隨便，你決定」 |
| Q3 | 模糊修改：say「穿和服的少女在神社前」→ pick → pick → say「讓她更有氣質」 |

改前、改後各跑 3 次。

### 6.3 通過標準

| 指標 | 基準（session `ddaf2115`） | 目標 |
| :--- | :--- | :--- |
| 動手輪檢索率（採用除外） | 1/7 | ≥ 90% |
| 交給模型決定／推薦的確認輪檢索率（`autoComplete`、帶參考方向的 `Discuss`、劇本標 `propose` 的步驟） | 0/2 | ≥ 80% |
| 每次定稿「借來」的 tag | 約 0 | 平均 ≥ 2 |
| rag（借來＋碰巧對上）佔非基礎詞 | 19%（4/21） | ≥ 50% |
| 延遲中位數增加 | — | 確認輪、動手輪各 ≤ 3 秒 |

「借來」的目標訂得保守：動手輪檢索時模型會先寫自己的翻譯（查詢的 `tags`），那些字之後出現在定稿也只算碰巧對上。真正算借來的是片段裡多出來、模型原本沒寫的詞。

另外人工看 Q1 的定稿：`comfortable lounge wear top` 這類知識庫沒有、也不像 SD tag 的寫法是否消失。

### 6.4 結果不好時的退路

寫進實驗紀錄，下一步跟使用者討論再決定，不在這案自動加做：

| 結果 | 退路 |
| :--- | :--- |
| 檢索率不夠 | 做法 2：開啟檢索時，動手輪沒叫過 `SearchPresets` 就呼叫 `FinalizePrompt`／`AskUser` 的，伺服器拒絕並請模型先查 |
| 有查但借來的少 | 先分清是片段不相符（檢索品質），還是相符卻沒借（借用規則或回傳格式），再分別處理 |
| 延遲超標 | 做法 3：伺服器在 `SetFacetStates` 帶 tag 時代查，少一次模型來回 |

## 7. 測試

- `SystemPromptBuilderTests`：on 時動手輪 prompt 含 `RetrievalActOn`、確認輪含 `RetrievalProposeOn`、兩種輪都含選項規則；off 時兩種輪都不出現 `SearchPresets`；`RetrievalStepOn` 含「不可只寫維度名稱」。
- `KnowledgePlugin`：維度項目 query 等於代號、通用名稱、題材專屬名稱時該項回錯誤，同一次呼叫的其他項目照常有結果；facet 項目不受影響。
- `HistoryTrimmer`：`keepSearchResults` 為 true 時 `SearchPresets` 結果保留、`AskUser` 選項的 tags 照樣剝掉；`CompressSearchResultsBefore` 壓掉指定位置之前的結果、不碰之後的；對已壓過的結果冪等。Gemini 的 `CalledToolResults` 路徑也要測（known-issues #8）。
- `TagTimeline`／`RagSplit`：§5.1 的三個例子；模型先寫、片段後到 → 碰巧對上；帶 presetId 的選項不記；`Restore` 回滾 timeline。
- 稽核：確認輪、動手輪、採用輪的 `kind`；`searches`、`searchItems`、`searchItemErrors` 計數；定稿的 `ragSplit`。
- `scripts/tests/test_adoption_report.py`：「檢索時機」一節的各比例與 `--sessions` 篩選。

## 8. 文件

- `docs/單輪流程說明.md`：跟流程說明的修改放在同一個 commit。
- `docs/known-issues.md`：加一條新編號（現象、證據、修正），合併時移到已修正。
- `docs/eval-cases.md`：加「2026-10-06 檢索時機」一節，Q1–Q3 與跑法。
- `docs/experiments/2026-10-06-retrieval-timing.md`：基準與改後的數字、`SearchPresets` 耗時、延遲分布、Q1 定稿對照、結論與退路判斷。
- `manual-tests/README.md`：重播腳本的用法。

## 9. 不做

- 改 tag 來源分類、前端加新來源樣式（§2）。
- 程式把關（做法 2）、伺服器代查（做法 3）：留作退路（§6.4）。
- `SearchSimilarPrompts` 的使用時機。
- `demo.py`。

## 10. 第二輪修正（2026-10-06）

第一輪實驗（實驗紀錄 §4–§5）之後，使用者決定再改一輪：

- **確認輪段落**（§3.2）補兩個例子：單項委託跟追問的回答寫在同一句裡也算，只查交給模型的那一項；模糊要求（「更有氣質」「換個感覺」）從片段挑不同方向當 `choices`。再補收尾規則：查完照樣以 `Confirm` 結束（清單裡有 `Discuss` 才能用），`SetFacetStates`、`FinalizePrompt` 要等下一輪才有。第一輪確認輪協定違規 1 → 4 次，都是查完就想直接動手或叫清單裡沒有的 `Discuss`。
- **量測**（§5.3、§6.3）：動手輪檢索率多算一個「含沿用前一個確認輪的檢索」版本。§3.1 第 4 條本來就允許動手輪直接用確認輪挑好的片段；第一輪 5 個沒查的動手輪有 4 個是這種情況。通過標準的「動手輪檢索率 ≥ 90%」改看這個版本，原本的照列。重播腳本的判定多一個 `CARRY`。
