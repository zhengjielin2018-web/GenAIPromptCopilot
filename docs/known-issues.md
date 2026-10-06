# 已知問題與待修清單

已查明但還沒修的問題。修掉一項就把它移到文末「已修正」並註明 commit。
子專案 2 的效果調整（prompt、檢索、預算）刻意延後到子專案 4 之後一起做，所以大部分項目屬於那一輪。

| # | 問題 | 類型 | 優先 |
| :--- | :--- | :--- | :--- |
| 4 | 無害描述被上游 SAFETY 連續誤擋 | 行為 | 中 |
| 5 | eval #5、#18 行為不符預期；§14 端到端要在新 HEAD 重跑 | 調整 | 低 |
| 10 | 整套組合推薦的已知限制：錨靠模型翻譯、SQL 的錨比對比 C# 粗、換了內容沒重給 `tags` 時舊錨留著；採用輪失敗後重試是純文字，HTTP 層就失敗時填回的是佔位字 | 限制 | 低 |
| 11 | `AskUser`／`Discuss` 的 call args 壓縮對 Gemini 不生效 | 成本 | 低 |
| 6 | 子專案 4 全分支審查留下的小項目 | 整理 | 低 |

---

## 4. 無害描述被上游 SAFETY 連續誤擋

**現象**（2026-09-24）：「中年阿姨在廚房夾菜，穿著圍裙」連送 3 次、改寫兩次，共 5 次 `Blocked_Upstream {"stage":"loop","reason":"SAFETY","attempts":2}`。改成「中年女士在廚房，正把青菜放入便當內，她穿著圍裙與長褲」才通過。

**已知背景**：主規格 §15 把上游內容攔截的內部重試設為 1 次，理由是同一份 SFW 內容常被誤擋、重送一次常會過。這次重試過仍被擋。

**已查到的線索**（2026-09-24）：

- 5 次都發生在**第一次**模型呼叫：被擋的輪次沒有任何 `Tool_Invoked`，送出去的只有 system prompt、工具定義與使用者那句話。輸入端的 `SafetyGuard` 分類器同樣呼叫 Gemini，卻每次都放行。
- 主迴圈與分類器都沒設 `safetySettings`，走 Gemini 預設門檻。
- 被擋的 5 句，衣著都只有「穿著圍裙」；把「阿姨」換成「女士」照樣被擋；加上「與長褲」才通過。**假設**：在「產生生圖提示詞」的語境下，「女性＋只穿圍裙＋廚房」貼近 SD 語料常見的「裸體圍裙」題材，被上游分類器判定。這只是從字面規律推的，當時的紀錄無法證實（#7 已補上紀錄）。

**修正方向**：#7 已完成，`Blocked_Upstream` 現在記得到攔截種類與 `safetyRatings`。下一步是重送「中年阿姨在廚房夾菜，穿著圍裙」，讀 payload 的 `upstream.kind` 與 `upstream.safetyRatings`，確認上面的假設。可以考慮的處理：調整 Gemini 的 `safetySettings` 門檻（要評估對 NSFW 防線的影響）、或在 `blocked` 的前端文案提示「換個說法」。這一項跟 NSFW 過濾範圍無關，過濾範圍已定案。

**#7 的實測結果**（2026-09-29）：「中年阿姨在廚房夾菜，穿著圍裙」連送三次都是**輸入被拒**（`promptFeedback.blockReason = PROHIBITED_CONTENT`），`safetyRatings` 是空的；同一輪先跑的輸入分類器呼叫（同一個模型、同一句話）是 `STOP` 放行，被拒的是帶 system prompt 與工具定義的主迴圈第一次呼叫。`PROHIBITED_CONTENT` 不屬於 `safetySettings` 可調門檻的四個 harm 類別（那些會附 `safetyRatings`），所以「調 `safetySettings` 門檻」這條路走不通。上面「女性＋只穿圍裙＋廚房」的假設仍未證實也未排除；觸發點看起來是「生圖提示詞的語境＋這句話」的組合，而不是這句話本身。

## 5. 效果調整（非 bug）

- **eval #5**「一個女生，其他隨便」：先確認再動手之前仍追問風格，沒有直接定稿。2026-10-05 起「隨便」先出確認卡列出要補的內容，按下後的動手輪不給 `AskUser`、直接補齊定稿（驗收 C8 的「直接給我」與 C3 前置都這樣走）；eval #5 原句還沒重跑。
- **eval #18**：追問偏少，一次只問 1–2 個維度，回答後就定稿，服裝整組留空。#1 修好後要重測，預算可能是原因之一。→ 追問政策已在 `fix/ask-all-missing` 反轉，重測。
- **§14 端到端驗收**是在 `fc449f7` 跑的，之後改過輪次流程、重試分類、輸出安全，要在新 HEAD 重跑。
- **eval #21／#22** 的故障注入方式（改 `Llm:Model`）測不到：404 不重試，session 在記憶體裡、重啟就沒了。要換一種注入方式，例如可設定的 fake 失敗次數。

## 10. 整套組合推薦的已知限制（2026-09-25）

（原本也編成 9，跟文末已修正的 #9 撞號，2026-09-25 改為 10。）
（2026-09-29：「照它的」取代 covered facet 時舊 tag 沒被拿掉的那一條已修正，移到文末「已修正」的 #10。）
（2026-09-29：同義詞抓不到已由 facet 向量的近似錨處理，見已修正 #12。）

- **錨靠模型翻譯**：錨是模型在 `SetFacetStates` 給的英文 `tags`，加上定稿 positive 的 tag。翻錯或沒給時，字面錨那一層撈不到東西，相關位改由近似錨或純向量補（理由標「最接近你描述的」），不報錯。（2026-10-05 起只有定稿卡推薦，所以一定有 positive 的 tag 可當錨。）
- **採用那一輪失敗後的重試是純文字**：失敗條目的「重試」把伺服器組的採用句填回輸入框，重送時走一般訊息（2026-10-05 起打字會先過確認輪，模型出確認卡，使用者按下後的動手輪才照 `flow-act.md` 第 5 條處理採用句並重新定稿），但 `Adoption` 沒記帳、tag 不會標 `adopted`。要重新採用請再按一次卡片上的「採用」。
- **錨的 SQL 比對只做小寫與底線換空白**：`RecommendAsync` 對資料庫的 tag 只做 `replace(lower(tag), '_', ' ')`，沒有 `TagAttribution.Normalize` 剝 `:數字` 權重、外層括號、連續空白那幾步，比 C# 端的比對粗。`facet_tags` 保留原始 SD 語法（如 `(sandals:1.2)`）時有兩個後果：一是錨會漏配，字面錨那一層少撈幾筆，相關位改由近似錨或純向量補，卡片照樣有推薦，所以不容易被發現；二是被別的錨撈進來的候選，回報的 `anchorTags` 由 C# 以完整的 `Normalize` 算，可能多列一個 SQL 沒真正比中的錨。
- **covered facet 換了內容、模型沒重給 `tags` 時舊錨留著**：`Session.ApplyFacetStates` 只在 `tags` 非空時覆寫、狀態改成非 covered 時才移除。使用者把涼鞋改成靴子，模型維持 covered 卻沒附新的 `tags`，推薦仍以 `sandals` 當錨。
- **採用在 HTTP 層就失敗時，重試填回的是佔位字**：上一條「重試」指的是 `session` 事件之後才失敗（泡泡已換成伺服器組的整句）。若採用在 HTTP 層就被擋（`400`／`409`，或在 `session` 事件之前斷線），失敗條目的原文還是前端的佔位字「採用〈標題〉…」，按「重試」會把這串佔位字填回輸入框；照送只是一般訊息，而且沒有任何 tag。要重新採用請再按一次卡片上的「採用」。

## 11. `AskUser`／`Discuss` 的 call args 壓縮對 Gemini 不生效（成本，低）

- 現象（2026-09-29，修 #8 時用真的 connector 查到）：`HistoryTrimmer.CompressTurn` 第二條（call args 去掉 `tags`，主規格 §4.7）改的是 model 訊息 `Items` 裡的 `FunctionCallContent.Arguments`，但 Google connector 送出時讀的是 `GeminiChatMessageContent.ToolCalls`（`GeminiFunctionToolCall.Arguments`），兩份不是同一個物件。實測改了 `FunctionCallContent.Arguments` 後，下一次請求的 `functionCall.args` 原封不動。
- 影響：選項的 `tags` 每輪留在 history 裡，量比 #8 的片段本文小很多。
- 修正方向：跟 #8 一樣重建 model 訊息，但 `GeminiFunctionToolCall` 與帶 tool call 的 `GeminiChatMessageContent` 建構子都是 internal，而且要保住 `thoughtSignature`（Gemini 3 要求帶回）。可以考慮直接改請求本文（像 `GeminiRoleFixHandler` 那樣在 HTTP 層處理）。

## 6. 子專案 4 全分支審查留下的小項目

都不影響功能，順手時再做。
（2026-09-25 清掉兩項：`PresetDrawer.vue` 的 `sourceName()` 在圖片來源說明改版時已改成 `sourceNotice()` 只算一次；README 的「重置知識庫」與「沒填 key」已補。）

- `scripts/export_seed.py` 的 `run()` 收下 stderr，失敗時只看得到指令，看不到 PostgreSQL 的錯誤原文。
- `export_seed.pipe()` 先檢查 pg_dump 的結束碼再檢查 pg_restore，還原失敗可能被報成來源端的 broken pipe。
- `docker/nginx.conf` 轉送 `Host $host` 會丟掉埠號，`$http_host` 可保留 `localhost:8080`。
- `docker/Dockerfile.api` 執行階段是 root，aspnet image 內建 `app` 使用者。
- `.gitattributes` 只把 `*.sh` 固定成 LF；`nginx.conf` 與 `001_schema.sql` 在 Windows checkout 是 CRLF，目前靠兩者容忍 CRLF 才沒事。
- CI 的 docker job 沒跑 `docker compose config --quiet`。
- 四個服務都寫死 `container_name`，兩套 stack 不能並存（fresh clone 測試要先 `docker compose down` 開發用的那套）。
- `PresetRepository` 為了測試 fake 拿掉了 `sealed`（沿用 `FakeHistories` 的既有做法）。

---

## 15. 檢索時機只靠流程說明，模型偶爾不查（效果，低）

**現象**（2026-10-06 檢索時機實驗，[實驗紀錄](experiments/2026-10-06-retrieval-timing.md) §5、§6）：動手輪檢索率（沿用前一個確認輪的檢索也算）兩輪分別 97%、87.5%，同一份流程說明下有變異；第二輪 Q1 第 1 次連續 4 個動手輪沒查。追問的回答裡夾著單項委託（「[風格] 寫實攝影／衣服你幫我設計」）時，確認輪兩輪共 0/6 先檢索，確認卡上的寫法是模型自己想的（動手輪 6/6 有查）。

**可能的方向**：動手輪的程式把關——知識庫開啟時，動手輪（採用除外）呼叫 `FinalizePrompt`／`AskUser` 前若這輪與前一個確認輪都沒查就退回（[檢索時機設計](superpowers/specs/2026-10-06-retrieval-timing-design.md) §6.4 的做法 2）。代價是忘記查的那一輪多一次來回（約 3–4 秒）。單項委託夾在回答裡的情況程式分不出來，只能靠流程說明。

## 16. 「家居服」這類概念有查但命中分級低，模型不借（效果，低）

**現象**（2026-10-06 實驗紀錄 §6.4）：動手輪查「家居服上衣」「家居褲」時，facet 向量的命中多是「低」分級、內容混在其他衣物裡（`cozy oversized sweater, holding a steaming cup`、`flower print pajama pants, cotton pajama long sleeve top`、`black tank top, gray sweatpants, clothes`）。模型照「只借相符的詞」沒有借，自己寫 `comfortable loungewear top`、`loungewear pants`；知識庫裡其實有 `pajama pants`、`sweatpants` 這類單品。

**可能的方向**：檢索品質問題，跟檢索時機無關。可以從查詢端（「家居服」翻成具體單品再查）、片段端（facet tag 拆得更細）或借用規則（允許從混雜片段挑出單一單品）著手，另開一案。

## 已修正

### 14. 檢索幾乎只在第一輪發生，RAG 對定稿的貢獻接近 0（效果，中）

**現象**（2026-10-05，session `ddaf2115…`）：15 輪、定稿 5 次，`SearchPresets` 只在第 2 輪叫 1 次。之後「衣服你幫我設計」「家居感的衣褲」「推薦一些場景」「深夜咖啡廳前」全由模型自己寫，例如 `comfortable lounge wear top`（知識庫 0 筆）；知識庫其實有 70 筆睡衣類穿著、15 筆咖啡廳場景。最後定稿標 rag 的 4 個 tag 全是使用者原話直譯、碰巧對上片段。

**原因**：流程說明只在 `flow-act.md` 第 1 條要求檢索（「用一次」）；回答追問、定稿後修改、隨便與確認輪都沒提。2026-09-24 改成批次檢索之後，每個 session 平均約 1 次。

**修正**（分支 `feat/retrieval-timing`，設計見 [檢索時機設計](superpowers/specs/2026-10-06-retrieval-timing-design.md)）：每個寫入新內容的動手輪都檢索；要寫出使用者沒講內容的確認輪先檢索；只寫維度名稱的查詢擋掉；確認輪檢索結果多留一輪；量測分「借來／碰巧對上」。結果見 [實驗紀錄](experiments/2026-10-06-retrieval-timing.md)。

**結果**（2026-10-06）：rag 佔非基礎詞 25% → 80%、每次定稿模型自己寫的 tag 13.9 → 3.7 個；動手輪檢索率 87%（目標 90%，5 個沒查的有 4 個沿用前一個確認輪的檢索結果）；要寫出使用者沒講內容的確認輪 6/12（目標 80%：隨便、推薦有查，單項委託、模糊要求沒查）；確認輪協定違規 1 → 4 次、1 輪失敗。第二輪（補確認輪例子與收尾規則）：協定違規回到 1 次、0 失敗；確認輪 8/12；動手輪（含沿用確認輪的檢索）87.5%，兩輪間有變異；rag 佔非基礎詞 72%。使用者 2026-10-06 決定照現狀收：結果指標（rag 佔比、模型自己寫的 tag）兩輪都遠高於基準；沒達標的檢索率與檢索品質另記 #15、#16。

### 1. 人像題材第一輪常被強制定稿，整個 session 不再追問

**現象**（2026-09-24，`docker compose up` 手動試用）：輸入一段有細節的人像描述，第一輪直接出定稿卡，沒有追問卡、沒有 chip。之後怎麼補充都只會重新定稿。儀表板上使用者講過的維度也顯示為 missing。

**證據**：兩個 session 的第一輪都一樣。

| session | 第一輪輸入 | audit |
| :--- | :--- | :--- |
| `5df96a64…` | 一個老爺爺在稻田裡面喝茶，遠處是房子，太陽很大，老爺爺有著白色捲髮，穿著白色短衣 | `SetProfile` 1 次、`SearchPresets` 7 次，第 9 次呼叫 → `Tool_Budget_Exhausted {"ToolCalls": 9}` → 強制 `FinalizePrompt`，facetStates 全部 missing |
| `543fa33a…` | 中年女士在廚房，正把青菜放入便當內，她穿著圍裙與長褲 | 同上 |

prompt_version 都是 `8c10dcfe1f16`，跟子專案 3 驗收時能正常追問的版本相同。組態也相同（user-secrets 只有 `Llm:ApiKey`）。**與 docker 無關**，用 `start_api.py` 跑一樣會發生。在這之前 33 輪一次都沒發生，差別在這次模型照 system prompt 的指示把維度搜滿了。

**根因**：預算跟 system prompt 的指示在算術上不相容。

- `ToolBudgetFilter` 每次工具呼叫都計數，終止型工具也算；`Orchestrator:MaxToolCallsPerTurn` = 8。
- `Prompts/system.md` 第 1 條要求：先 `SetProfile`，再對每個適用維度呼叫 `SearchPresets`，使用者沒講的維度分兩次給對比方向。
- 人像有 6 個維度，所以一輪需要：

  ```text
  SetProfile 1 + 六個維度各 1 + 每個沒講的維度再 1 + AskUser 1 = 8 + 沒講的維度數
  ```

  只要有一個維度沒講，照做就超過 8。#2 的 0 筆結果會讓模型換說法重搜，再多吃掉幾次。
- 預算用盡 → 強制定稿 → `Status = Finalized` → `ToolSetBuilder` 在 Finalized 狀態不給 `AskUser`（主規格 §4.3）→ 這個 session 永遠不再追問。
- 強制定稿時模型還沒呼叫 `SetFacetStates`，所以使用者講過的維度也是 missing，tips 會把它們全列成「未指定」。

**修正**（分支 `fix/batch-search-presets`，merge commit `abf8a6e`；設計見 [批次 SearchPresets 設計](superpowers/specs/2026-09-24-batch-search-presets-design.md)）：採原本列的方向 2 + 3，方向 1 當保險。

- `SearchPresets` 改收 `queries: {dimension, query}[]`，一輪的檢索只花一次工具呼叫，embedding 走一次 batch；`system.md` 第 1 條與工具描述同步。
- `MaxToolCallsPerTurn` 8 → 16。批次後第一輪預期 4–5 次呼叫，16 只是模型仍拆開呼叫時的餘裕。
- 強制定稿提示要求 `facetStates` 依使用者原話標 covered；`FinalizePrompt` 本來就收 `facetStates`，不需要多一次 `SetFacetStates`。

**驗收**：待 merge 後依設計 §7 跑，結果記到 `docs/eval-cases.md`。

**備註**：若 §7 驗收發現模型仍逐維度呼叫，16 沒有餘裕（1 + 12 + 1 + 1 + 1 = 16），任何一次重搜就會再觸發強制定稿；屆時考慮再放寬或在 Description 加強批次指示。

### 2. `SearchPresets` 在候選池有幾千筆時回 0 筆

**現象**：`"style 老爺爺在稻田裡面喝茶"` 回 `{"poolSize":4455,"hits":[]}`；`"camera 動漫插畫視角"` 回 `{"poolSize":2147,"hits":[]}`。

**根因**：`prompt_knowledge_presets.preset_embedding` 上是 HNSW 近似索引。帶 `WHERE facet_ids && …` 的查詢，pgvector 先從全表取最近的 `hnsw.ef_search`（預設 40）筆，**之後**才套過濾。查詢句的最近鄰若都落在別的維度，過濾完就一筆不剩。風格池只佔全表 23%，鏡頭池只佔 11%，用整句畫面描述去搜時特別容易發生。

**實測**（2026-09-24，唯讀查詢）：以「日本稻田風景」(id 20920) 的向量代替整句查詢，對風格池取前 3 筆。

| 查法 | 結果 |
| :--- | :--- |
| 現行（HNSW 索引） | 0 筆 |
| `SET LOCAL enable_indexscan = off`（精確掃描） | 3 筆，最近距離 0.153（高） |
| `SET LOCAL hnsw.iterative_scan = relaxed_order` | 3 筆 |

以「日系動漫風格」(id 9474) 的向量搜鏡頭池，HNSW 同樣回 0 筆。

**影響範圍**：

- `src/PromptCopilot.Api/Data/PresetRepository.cs` 的 `SearchSql`
- `scripts/pipeline/retrieval.py` 的同一條 SQL（`scripts/demo.py` 走它）
- `HistoryRepository` 的 `SearchSimilarPrompts` 也是 HNSW 加 `subject_profile` 過濾，理論上有同樣風險，修的時候一起確認

**修正**（分支 `fix/hnsw-iterative-scan`，merge commit `deaf9ae`）：`db/init/001_schema.sql` 在 `CREATE EXTENSION vector` 之後把資料庫層級設成 `hnsw.iterative_scan = strict_order`，用 `current_database()` 組 `ALTER DATABASE`，資料庫名跟著 `POSTGRES_DB` 走。過濾後不足 k 筆時 HNSW 會繼續往外搜，結果仍嚴格依距離排序。SQL 不用改，`PresetRepository`、`HistoryRepository` 與 `retrieval.py` 一起生效。沒改成精確掃描：候選池最大 6,752 筆，精確也可行，但會失去 HNSW 的展示意義。完整說明見[檢索設計 §12.1](superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md#121-hnsw-是先搜再過濾2026-09-24)。

**驗收**（2026-09-24，開發機資料庫）：

| 測試 | 修正前 | 修正後 |
| :--- | :--- | :--- |
| C# `Database_turns_on_hnsw_iterative_scan_in_strict_order` | `off` | `strict_order` |
| C# `Preset_search_fills_k_from_the_style_pool_when_the_query_sits_among_scene_presets`（id 最小、帶 scene 不帶 style facet 的片段向量對風格池取 3 筆） | 0 筆 | 3 筆，與精確掃描同 id 同順序 |
| Python `test_retrieve_presets_fills_k_from_the_style_pool_for_a_query_among_scene_presets` | 0 筆 | 3 筆 |
| Python 既有的 `test_retrieve_presets_pool_is_empty_for_a_dimension_the_profile_lacks`（單位向量對 clothing 池） | 0 筆，同一個原因失敗 | 通過 |

`HistoryRepository` 的 `SearchSimilarPrompts` 確認有同樣風險：拿 200 筆 portrait 紀錄的向量對其他 profile 各取 3 筆，關掉 iterative scan 時 155–198 筆不足 3，開了之後 0 筆。同一個設定一起修掉。

**備註**：

- 已經建好的資料庫不會重跑 `db/init`，要手動執行一次（開發機的 `prompt_copilot` 已在 2026-09-24 執行過）：

  ```bash
  docker compose exec db psql -U postgres -d prompt_copilot -c "ALTER DATABASE prompt_copilot SET hnsw.iterative_scan = strict_order"
  ```

  只對之後建立的連線生效。API 連線池裡的舊連線不會變，重啟 API（`docker compose restart api`）才保證全部生效。
- 種子還原（`docker/seed.sh`）是 `pg_restore --data-only` 灌進既有的資料庫，不會重建資料庫，設定不會丟。`scripts/export_seed.py` 的丟棄容器套同一份 schema，也會有這個設定，但 dump 是 data-only，不帶資料庫設定。
- 這一項之前被記成「模型要自己換成『寫實攝影』重搜」的 prompt 調整問題，實際上是檢索 bug。

### 9. 第一輪 `grounded` 永遠是空的

**現象**：`SetProfile` 把所有 facet 重設為 missing；system.md 第 1 條原本要模型緊接著 `SearchPresets`，`SetFacetStates` 在之後才（可能）呼叫。所以第一輪的每個維度都是 `grounded = false`：k 一律 3、片段一律「僅供建議」，即使使用者已描述該維度。批次化之後這件事必然發生（第一輪只有一次檢索、緊跟在 `SetProfile` 之後）。

**影響**：第一輪就定稿（描述完整或「都你決定」）時，模型被告知不可借用使用者已描述維度的知識庫詞。

**修正**（分支 `fix/ask-all-missing`，merge commit `b9290e4`；定稿閘門在 `ee3887f`）：system.md 第 1 條改為 `SetProfile` → `SetFacetStates` → `SearchPresets`；同時把追問政策反轉為問滿 missing 維度：該維度底下只要還有任何 facet 是 missing（waived 與有委託 note 的不算）就要問，只講一部分的維度也問剩下的 facet，使用者接受完整描述也可能先被追問（eval #2）。起因是使用者 2026-09-25 實測回饋「描述缺很多面向，但追問很少」，見 #5 的 eval #18。`SetFacetStates` 與 `AskUser` 的工具描述、主規格 §4.2 與 §15、批次設計 §3.4 同步。主規格 §9「grounded 由伺服器算」原則不變，只是讓伺服器有資料可算。

**驗收**：重跑 eval #1、#18、#24，看第一輪 `SearchPresets` 對使用者講過的維度是否 `grounded: true`、追問是否把 missing 維度問滿。

### 7. log 不足：看不出被擋的原因，一輪的經過只在資料庫裡

**現況**：

- **`audit_logs`（資料庫）** 是唯一完整的紀錄：每輪的輸入原文、每次工具呼叫的參數與結果、結局、攔截、存檔。重啟不會消失。
- **API 容器 log**（`docker compose logs api`）幾乎沒有對話的資訊：程式只在寫 audit 失敗、串流中途出錯、缺 API key 時寫 log。其餘全是 `System.Net.Http` 每次 embedding 請求的 Information 行（一輪可達數十行）。容器重建後這份 log 就沒了。

**缺的東西**：

1. `Blocked_Upstream` 只記 `reason`，而且這個值不可靠：`LlmFailureClassifier.BlockReasonOf` 在例外訊息含 "blocked" 或 "safety" 時一律回 `SAFETY`。「輸入被拒」（`promptFeedback.blockReason`）與「輸出被截」（`finishReason`）在 audit 裡長得一樣，也沒有 `safetyRatings` 的類別與機率。#4 因此無法確認。
2. `Turn_Failed` 只記例外訊息的前段，沒有 Gemini 回應本文（#3 的 400 需要它）。
3. 容器 log 沒有每輪一行的摘要，看 demo 時沒辦法從終端機判斷發生什麼事。
4. `Tool_Invoked` 的 args 與 result 截到 200 字，查 2026-09-25 的服裝 tag 問題時無法從 audit 看出 clothing 查了什麼、撈到什麼，只能重跑推斷；至少 `SearchPresets` 與 `FinalizePrompt` 要存完整。

**修正方向**：

- 攔截時把 `GeminiMetadata` 的 `PromptFeedbackBlockReason`、`FinishReason`、`PromptFeedbackSafetyRatings`／候選的 `SafetyRatings` 整包寫進 `Blocked_Upstream` 的 payload，並分開記「輸入被拒」與「輸出被截」。例外路徑拿不到 metadata 時，至少記下完整例外訊息。
- `AgenticOrchestrator` 在每輪結束時寫一行 Information：session、turn、結局、工具呼叫數、耗時。
- `appsettings.json` 把 `System.Net.Http` 調到 Warning。

**驗收**：重送「中年阿姨在廚房夾菜，穿著圍裙」，`Blocked_Upstream` 的 payload 看得到是哪一種攔截、哪個類別、什麼機率；`docker compose logs api` 每輪有一行摘要，沒有 embedding 請求的雜訊。

**修正**（分支 `fix/upstream-diagnostics`，commit `f455999`、`b3e117b`、`72bfa7e`）：沒走上面第一條的 `GeminiMetadata`——connector 碰到輸入被拒時直接丟例外，根本拿不到 metadata。改在 HTTP 層讀回應本文。

- `GeminiDiagnosticsHandler`（包在 `GeminiRoleFixHandler` 外面）讀每一次 `:generateContent` 的回應，記進這一輪的 `UpstreamDiagnostics`（`AsyncLocal`，`AgenticOrchestrator` 每輪開頭放新的；輸入／輸出分類器走同一個 client，也算在內）：`promptFeedback.blockReason` → `input_blocked`，`finishReason` 屬內容攔截 → `output_blocked`，連同 `safetyRatings`；非 2xx 記狀態碼與本文（截到 2 KB）；另記呼叫次數與在途的那一次。每次呼叫寫一行 `Gemini <狀態碼> <毫秒> ms finish=… block=…`。
- `Blocked_Upstream` 多 `upstream: {kind, reason, safetyRatings}`，有它時 `reason` 與給使用者的訊息改用它的 reason。`Turn_Failed` 在最近一次是非 2xx 時多 `upstream: {status, body}`，逾時時多 `upstream: {calls, pendingMs?}`。
- 每輪結束寫一行 `Turn <session>#<turn> <事件> <細節> tools=<n> gemini=<n> <毫秒> ms`，任何結局都寫。
- `appsettings.json` 的 `System.Net.Http` 調到 Warning。
- `Tool_Invoked` 的 `SearchPresets`、`FinalizePrompt` 存完整的 args 與 result，其他 tool 照舊截 200 字。
- 主規格 §4.6、§6.2、§7 的 audit 欄位同步。

**驗收**：單元測試（`GeminiDiagnosticsHandlerTests`、`AgenticOrchestratorTests`、`FiltersTests`）。2026-09-29 實測（`fix/upstream-diagnostics` 建的 api）：圍裙那句連送三次，三次都是 `Blocked_Upstream`，payload `upstream: {"kind":"input_blocked","reason":"PROHIBITED_CONTENT","safetyRatings":[]}`、`attempts: 2`；對照句「一隻橘貓睡在窗台上」正常追問。`docker compose logs api` 每次呼叫一行（例：`Gemini 200 696 ms finish=- block=PROHIBITED_CONTENT`）、每輪一行（`Turn 0c1c0ac7…#1 Blocked_Upstream PROHIBITED_CONTENT tools=0 gemini=3 2343 ms`），沒有 embedding 請求的 `System.Net.Http` 行。結果寫回 #4。

### 10. 「照它的」取代 covered facet 時，舊 tag 沒被拿掉

（原本是 §10 整套組合推薦已知限制的其中一條，修正後移來這裡。）

**現象**（2026-09-29 瀏覽器驗收 T5）：使用者先講了「紫色短版連帽外套、發光比基尼、透視乳膠材質」，採用 #41745 按「全部照它的」，伺服器正確記 `adoption.replaced=["clothing.upper","clothing.material"]`，但定稿同時留著 `purple cropped hoodie`、`cyberpunk glowing bikini`、`translucent latex material`、`glossy latex` 與這套的 `purple hoodie`、`cropped hoodie`、`glowing bikini`、`see-through`、`latex clothes`，互相重複。另外 `purple cropped hoodie` 被標成 `adopted`，實際上是使用者原本的詞。

**根因**：system prompt 第 6 條只說「照它的 facet 寫入括號內的 tag」，沒說要拿掉該 facet 原本的 tag，模型就把新 tag 加在舊的旁邊。`adopted` 的比對沿用 rag 的字尾規則，`purple cropped hoodie` 以空白為界結尾是 `cropped hoodie`，所以算成採用帶進來的。

**修正**（分支 `fix/adopt-replace`，commit `8bbc964`、`6a10255`）：

- 伺服器組句時，換掉的 facet（`replaced`）若 `session.FacetTags` 有模型先前給的英文 tag，括號裡接「，取代原本的 …」點名要拿掉的舊 tag（`AdoptionComposer`）。
- system.md 第 6 條改成「照它的」是**取代**：定稿時該 facet 只留括號內的 tag，原本的 tag 全部拿掉——從使用者先前描述翻的、上一版定稿裡屬於這個 facet 的、「取代原本的」後面列的都算。
- `adopted` 只認正規化後整段相等，不再用字尾規則（`TagAttribution.AdoptedBy`）；沒命中的照常走 rag → llm。
- 設計 `2026-09-25-set-recommendations-design.md` §6.2、§6.4、§6.5 與 `docs/單輪流程說明.md` 同步。

**驗收**：單元測試（`AdoptionComposerTests`、`SystemPromptBuilderTests`、`TagAttributionTests`）；瀏覽器重跑 T5，看定稿裡上半身／材質只剩這套的 tag、`adopted` chip 只標這套的原字。

2026-09-29 實測（`fix/adopt-replace` 建的 compose，前端操作）：不用 T5 的內容（開著審查會被 `Blocked_Output`），改用「一個少女穿紫色連帽外套和牛仔短褲、白色運動鞋」→ 定稿 `purple hoodie`、`denim shorts`、`white sneakers` → 採用 #41618「率性秋季日常裝」全部照它的。組句是「上半身照它的（crop top, white shirt, brown overcoat, open coat, long sleeves，取代原本的 purple hoodie）、下半身照它的（denim shorts, blue shorts，取代原本的 denim shorts）…；鞋履、配件飾品保留我的」；新定稿沒有 `purple hoodie`，上半身、下半身、頭部配件、材質只剩這套的 9 個 tag（全是 adopted），留我的 `white sneakers` 仍是 rag。儀表板「借用 3 筆」與定稿卡「檢索貢獻」的 3 列一致。小瑕疵：新舊 tag 相同時仍會寫「取代原本的 denim shorts」。

**備註**：同一輪也把儀表板「借用」改成跟定稿卡「檢索貢獻」同一個歸屬（每個 rag tag 只算 `presetIds[0]`，commit `2d82451`）：驗收 R2 儀表板寫「借用 9 筆」、定稿卡只列 5 筆。

### 8. `HistoryTrimmer` 對 Gemini 的工具結果從未生效

**現象**（2026-09-24，reviewer 用 connector 真實產生的 history 確認）：Google connector 把工具結果放在 `GeminiChatMessageContent.CalledToolResult(s)`，tool 訊息的 `Items` 只有一個空的 `TextContent`，所以 `HistoryTrimmer.CompressTurn` 的 `Items.OfType<FunctionResultContent>()` 找不到東西，主規格 §4.7 的 tool result 壓縮實際上一次都沒跑；下一輪請求仍帶完整片段本文（`positive`／`negative`），token 成本比設計高。原本的單元測試手工組 `FunctionResultContent`，不是 connector 實際存的形狀，所以一直是綠的。

**查證**（真的 `GoogleAIGeminiChatCompletionService`，HTTP 用假 handler）：

- connector 1.80.1-alpha 的 tool 訊息是 `GeminiChatMessageContent`，結果在 `CalledToolResults`（`GeminiFunctionToolResult` 包一個 `FunctionResult`），送出時序列化成 `functionResponse`，`name` 是 `Knowledge_SearchPresets`。模型一次發多個呼叫時，全部結果在**同一則** tool 訊息裡。
- 原本列的方向「正規化成 `FunctionResultContent`」走不通：connector 序列化時直接丟 `NotSupportedException: Unsupported content type. FunctionResultContent is not supported by Gemini.`。
- `GeminiFunctionToolResult`、`FunctionResult` 都沒有公開的 setter，只能整則重建。

**修正**（分支 `fix/history-trimmer-gemini`，commit `13a5a6d`）：採「重建 Gemini 訊息」。

- `CompressTurn` 碰到帶 `CalledToolResults` 的 `GeminiChatMessageContent`，逐一用原本的 `CompressResult` 規則壓；壓得動的換成 `new GeminiFunctionToolResult(call, new FunctionResult(原結果, 壓縮字串))`，其餘沿用原物件，再在同一個 index 換掉整則訊息。`call` 從前一則 model 訊息公開的 `ToolCalls` 按名稱找回來（結果只從它取 `FullyQualifiedName`）。
- 單一結果用公開的建構子，補回 `ModelId`、`Metadata`；多個結果的建構子是 internal，用反射呼叫（不能拆成多則：Gemini 要求 functionResponse 的 part 數跟 call 數一樣）。找不到建構子或重建出錯就不壓這則，不丟例外。
- 原本的 `FunctionResultContent` 路徑保留。
- 主規格 §4.7 同步。
- 同一次查到 call args 壓縮（§4.7 第二條）也有同類問題，另列 #11。

**驗收**：`HistoryTrimmerGeminiTests` 用真的 connector 跑一輪 auto-invoke，壓縮後再送下一輪，檢查那次請求的 `functionResponse`：role 是 `user`、`name` 是 `Knowledge_SearchPresets`、內容只剩 `{dimension, facetId, poolSize, hits: [{id, title}]}` 與錯誤項目，本文裡沒有片段文字，`functionCall` 的 `thoughtSignature` 還在；平行呼叫（`SearchPresets` + `SetProfile`）時仍是同一則、兩個 part，只有 `SearchPresets` 被壓。另一條測試釘住 connector 的形狀（`CalledToolResults`、`Items` 沒有 `FunctionResultContent`），connector 換版時會先紅。實測見下一段。

2026-09-29 實測（`fix/history-trimmer-gemini` 建的 api，直接打 SSE）：同一段對話連跑 5 輪（描述 → 回答追問 → 直接給我 → 背景改夕陽海邊 → 鞋子換白色運動鞋），第 1 輪呼叫 `SearchPresets`、之後幾輪的歷史都帶著壓縮過的工具結果；5 輪都 `Turn_Completed`（2 次追問、3 次定稿），`docker compose logs api` 裡 20 次 `Gemini 200`，沒有 400、沒有重試。省了多少 token 看不到：audit 的 `prompt_tokens` 從來沒寫過。

### 3. 純文字補救的重試請求被 Gemini 回 400

**現象**（2026-09-24，子專案 3 瀏覽器走查）：「一個女生」→ audit `Protocol_Violation {"attempt":1}` 後緊接 `Turn_Failed {"stage":"loop","message":"...400 (Bad Request)","errorClass":"HttpOperationException"}`。前端正確回滾並給重試鈕，同一句再送通常會過。2026-09-29 瀏覽器實測又發生一次（session `36063629…`），形狀相同。

**根因**（2026-09-29 查證）：原本推測的「多一則 system 訊息」在線上根本不在 `contents` 裡；問題是 `contents` 以 model 結尾。

- 第一次呼叫以純文字結束（沒有終止型工具）時，`CallAsync` 把那則 assistant 訊息加進 `ChatHistory`，補救再接一則 system 提示。回覆只有空白時不加，所以是間歇性的。
- Google connector 把 `ChatHistory` 裡**每一則** system 訊息（不管位置）都搬進 `systemInstruction.parts`，不留在 `contents`。用真的 `GoogleAIGeminiChatCompletionService` + `GeminiRoleFixHandler` 接罐頭 handler 擷取重試請求：`systemInstruction.parts` 是 `["SYS-PROMPT","RETRY-SYS"]`，`contents` 以 `{"role":"model","parts":[{"text":"好的，我來幫你整理。","thoughtSignature":"SIG-2"}]}` 結尾。
- 直接打 Gemini（`gemini-3.5-flash-lite`，v1beta `generateContent`）：`contents` 以 model 結尾，不論有沒有帶 tools，都回 `400 INVALID_ARGUMENT`「Requests ending with a model turn are not supported.」；後面補一則 user 就 200。
- 同一個機制的第二個問題（從擷取到的請求本文推得，沒有在線上觀察到）：補救提示與強制定稿提示（「tool 呼叫預算已用盡。請立即…呼叫 FinalizePrompt 定稿…」）在這一輪成功後仍留在 history，之後每一輪 connector 都把它們併進 `systemInstruction`，直到 `HistoryTrimmer.Truncate` 剪掉那一輪。也就是說，強制定稿過一次之後，後面好幾輪的模型都還看得到「預算已用盡，請立即定稿」。

**修正**（分支 `fix/retry-ends-with-model`，commit `b850d57`）：

- 重試前把第一次的純文字拿出 history（`CallAsync` 改成回傳它補上的那則），重試請求就以使用者訊息或工具結果結尾。重試有文字 → 照舊包成 `Discuss`；重試連文字都沒有 → 把第一次的放回原位再包，維持「兩次之中有一次有文字就包」；其餘照舊 `ProtocolViolationException`。
- 補救提示與強制定稿提示改由 `CallWithReminderAsync` 帶：加進 history、呼叫、在 `finally` 裡拿掉同一則（比對參考，不是把 index 0 以外的 system 都刪掉）。system 在線上跟位置無關，拿掉不影響其他訊息。刻意不改成 user 訊息：`Truncate` 以 user 訊息數輪次，多一則會從一輪的中間剪。
- 主規格 §4.6、`docs/SK架構說明.md` 第 3、5、6 節同步。`docs/單輪流程說明.md` 只描述 Python 的單輪 demo，沒有這段流程，不用改。

**驗收**：

- `AgenticOrchestratorTests`：重試請求（忽略 system）不以 assistant 結尾；先跑工具才回純文字時，重試請求以工具結果結尾；經過補救、經過強制定稿的一輪成功後，history 只剩 index 0 的 system；重試空白時，包裝退回用第一次的文字。
- `AgenticOrchestratorGeminiTests`：orchestrator 接真的 connector，假 handler 照 Gemini 的規則對以 model 結尾的請求回 400。連兩輪都走補救：重試請求的 `contents` 只有 `user`、補救提示在 `systemInstruction`；下一輪的 `systemInstruction` 只剩 system prompt。修正前第一輪就是 `turn_failed`。另一條釘住 connector 的形狀（history 中間的 system 訊息進 `systemInstruction`、不進 `contents`），connector 換版時會先紅。
- 還沒在 compose 或瀏覽器上實測：純文字回覆是模型自己決定的，沒辦法指定觸發，要靠之後走查時看 audit 的 `Protocol_Violation` 後面接的是 `Turn_Completed`。

2026-09-29 實測（臨時測試，沒 commit）：`AgenticOrchestratorTests.Harness` 接真的 connector，第一次請求由假 handler 回純文字「好的，我來幫你整理這個角色的設定。」模擬模型沒呼叫工具，之後的請求都打真的 `gemini-3.5-flash-lite`；輸入「一個女生」，orchestrator 各跑一次 master 版與修正版。master：補救請求 `contents` 是 `[user, model]`，Gemini 回 400「Requests ending with a model turn are not supported.」，audit 是 `Protocol_Violation` → `Turn_Failed {"stage":"loop","errorClass":"HttpOperationException",…400…}`，跟瀏覽器看到的一樣。修正版：補救請求 `contents` 是 `[user]`、`systemInstruction` 兩則（system prompt＋補救提示），Gemini 回 200 並接著呼叫 `SetProfile` → `SetFacetStates` → `AskUser`，audit 是 `Protocol_Violation` → `Turn_Completed`（`AskOutcome`），這一輪結束後 history 除 index 0 外沒有 system 訊息。限制：第一次的純文字是罐頭，不是模型自己產的；Harness 的 kernel 沒掛 filter 也沒有知識庫，所以 `AskUser` 後迴圈沒停、`SearchPresets` 找不到函式，這些跟 #3 無關。下一輪 `systemInstruction` 只剩一則沒有打真的 Gemini 驗，由 `AgenticOrchestratorGeminiTests` 涵蓋。

### 12. facet 檢索「過濾準、排序不準」與推薦的同義詞（facet 層級向量）

**現象**：R2 驗收「涼鞋」查 `clothing.footwear` 前 5 名只有 2 筆 sandals（知識庫有 18 筆）；推薦的錨 `slippers` 對不上 `sandals`，退回無錨。原本分別記在 §5 與 §10。

**根因**：facet 項目過濾後仍拿整套片段向量（`title。description。全部 tag`）排序，穿著片段被上下身主導；錨比對只做字面。

**修正**（分支 `feat/facet-vector-retrieval`，merge commit `d227284`；2026-09-30 驗收後的門檻與查詢調整見下方「驗收」）：子表 `preset_facet_embeddings` 存每個片段每個 facet 的向量（`scripts/embed_facet_tags.py` 從 `facet_tags` 算，seed-v3 帶著）；`SearchPresets` facet 項目改比 facet 向量、依 `tag_key` 去重、查詢句「原話（模型給的英文 tags）」、分級門檻 0.22／0.27；推薦字面錨不到 2 筆先試近似錨（facet 距離 ≤ 0.30，驗收後由 0.23 放寬），前端標「接近你講的 …」。不建 HNSW（設計 §7）。設計：`docs/superpowers/specs/2026-09-29-facet-vector-retrieval-design.md`；實驗：`docs/experiments/2026-09-29-facet-vector-text.md`。

**驗收**：單元測試（`KnowledgePluginTests`、`RecommendationServiceTests`、`test_embed_facet_tags.py`、`test_tags.py`）、整合測試 `RepositoryIntegrationTests`。離線重跑與線上驗收見實驗紀錄與 `docs/eval-cases.md` 2026-09-29 facet 向量一節。

2026-09-30 實測（master `d227284` 建的 compose，開發庫跑 `embed_facet_tags.py`：37,011 筆、0 批失敗、737 秒）：17 題走正式 SQL，前 5 名命中 75／85、相異組合 85／85（現行 67／76）；R2「涼鞋」前 5 名 5 種不同的涼鞋；模型 39／39 個 facet 項目都有帶 `tags`；子表清掉鞋履時照常退回整套向量。驗收時再改兩處（分支 `fix/facet-threshold-and-query`）：近似錨門檻 0.23 → 0.30（單一詞的同義詞落在 0.28–0.34，0.23 等於不會觸發，實測表見設計 §6.4）；`SearchFacetSql` 改成先取前 k 筆再 join，地點類型 69 → 33 ms（原寫法為了 join 把片段表全表掃一遍，執行計畫見設計 §7.1）。

### 13. 模型呼叫裸名工具，整輪跑到逾時

**現象**（2026-10-05，先確認再動手驗收）：log 先出一次 `Protocol_Violation`（attempt 1），之後 120 秒內呼叫 Gemini 幾十次（一輪 80–100 次，`9bbb5888…` 是 47 次）、工具呼叫幾乎是 0–3 次，最後 `Turn_Failed`（`Timeout`，約 120,013 ms）。回滾是正確的，待確認還在，同一個 session 再按一次就成功。

**根因**：kernel 宣告的工具名是 `<Plugin>_<Function>`（`Dialog_Confirm`），模型有時只寫 `Confirm`。這段在 connector 的 `GeminiChatCompletionClient`（不是 SK 核心，原本這裡寫錯了）：`FunctionChoiceBehavior.Auto()` 轉成 `EnabledFunctions(autoInvoke: true)`，名字對不上宣告（不分大小寫）就只回模型一句「Error: Function call request for a function that wasn't defined.」，再繼續 auto-invoke 迴圈。這條路徑不經過任何 `IAutoFunctionInvocationFilter`，`ToolBudgetFilter` 數不到、`TerminalToolFilter` 停不下來；一次呼叫的上限是 `DefaultMaximumAutoInvokeAttempts = 128`，模型多半原封不動重送，所以先撞到 120 秒逾時。

**證據**：驗收時在 Gemini 回應 log 暫時印出 functionCall 名稱查到（4 個並行的確認輪合計 182 次裸名 `Confirm`）；經過見 `docs/eval-cases.md`「2026-10-05 先確認再動手」一節的補充觀察。發生率跟 prompt 文字有關：沒改過的 prompt 12/12 正常、只加一個空格 4/6、兩個流程段寫明完整名稱之後 12/12。修正時用真的 connector 接罐頭 handler 離線重現：模型卡在同一個沒宣告的呼叫上，一輪打了 258 次 Gemini（兩次 SK 呼叫各 129 次）。

**修正**（分支 `fix/tool-name-guard`，commit `7845daf`；同分支 `62bdb93` 順帶拿掉一個觸發來源）：

- `GeminiToolNameHandler`（Gemini `HttpClient` 最外層的 `DelegatingHandler`）在 connector 解析回應之前，從同一個請求的 `tools[].functionDeclarations` 讀宣告清單。`candidates[].content.parts[].functionCall.name` 對不上宣告時，裸名只對上一個宣告（`_` 後面那段相同）就改成那個全名，呼叫照常進 plugin 與 filter，預算也數得到。
- 改名沒有破壞 `thoughtSignature`：2026-10-05 直接打 `gemini-3.5-flash-lite`，把呼叫改名（改成另一個宣告名、或改成裸名）後連簽章送回都是 200，對照組拿掉簽章是 400「Function call is missing a thought_signature」。Gemini 只驗有沒有帶，不綁函式名稱。
- 改不回來的（這一輪根本沒有的工具、對上兩個宣告的裸名）照原樣交給 connector，記進這一輪的 `UpstreamDiagnostics`；同一輪第 2 次就丟 `UndeclaredToolCallException`。重試層把它當致命錯誤不重試；`AgenticOrchestrator.CallAsync` 把它當成「這次呼叫沒有結果」，走原本的補提示重試，提示先點名「SetProfile 不在這一輪的工具清單裡」。還是不行就以 `protocol_violation` 收掉，離線重現從 258 次降到 3 次。
- audit：`Turn_Completed` 記 `toolNameRepairs`（有改名才寫），`Protocol_Violation` 與 `Turn_Failed` 記 `undeclared`。log 每次改名一行 `Gemini tool name repaired Confirm → Dialog_Confirm`，每次沒宣告的呼叫一行 warning。
- `62bdb93`：還沒題材的確認輪（使用者第一句話）不給 `SearchPresets`／`SearchSimilarPrompts`。沒題材時它們只會回「請先呼叫 SetProfile」，這一輪卻沒有 `SetProfile`，模型照錯誤去叫就是上面那條路。
- 沒採用「限制每輪 Gemini 呼叫數」：數字難定，而且只能讓失敗變快；改名能讓那一輪直接成功。流程段與補救、強制收尾的提示仍寫完整名稱，`{{TOOLS}}` 仍是短名，沒有重新量測：兩種寫法現在都會落到同一個工具。

**驗收**：單元測試 `GeminiToolNameHandlerTests`（8 個）、`ResilientChatCompletionTests.Undeclared_tool_abort_is_not_retried`、`ToolSetBuilderTests.Propose_turn_without_a_profile_has_no_search_tools`；`AgenticOrchestratorGeminiTests` 走真的 connector 驗三個情境（裸名、反覆叫沒宣告的、卡住），修正前三個都紅。全套 494 通過。

2026-10-05 線上實測（分支 build，`start_api.py --port 5077`，`gemini-3.5-flash-lite`）：

- 原本的 prompt：8 個 session 各三輪（第一句 → 按確認 → 問「寫實跟動漫差在哪」）加上第一句就提問，23 輪全部 `Turn_Completed`，Gemini 72 次全是 200。第一句的確認輪都只呼叫 `Confirm`，2.8–5.0 秒。這一批沒有出現裸名。
- 故意拿掉 `flow-propose.md` 第 5 條的完整名稱提示（重現當初的觸發條件，測完已還原）：16 個第一句的確認輪有 8 輪寫裸名 `Confirm`，全部改名成功，16 輪都是 `Turn_Completed`、每輪 3 次 Gemini、2.6–4.3 秒，audit 這 8 輪記 `toolNameRepairs: 1`。修正前這 8 輪會跑到 120 秒逾時。
