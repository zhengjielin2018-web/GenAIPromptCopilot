# 已知問題與待修清單

已查明但還沒修的問題。修掉一項就把它移到文末「已修正」並註明 commit。
子專案 2 的效果調整（prompt、檢索、預算）刻意延後到子專案 4 之後一起做，所以大部分項目屬於那一輪。

| # | 問題 | 類型 | 優先 |
| :--- | :--- | :--- | :--- |
| 3 | 純文字補救的重試請求被 Gemini 回 400 | bug | 中 |
| 4 | 無害描述被上游 SAFETY 連續誤擋 | 行為 | 中 |
| 5 | eval #5、#18 行為不符預期；§14 端到端要在新 HEAD 重跑 | 調整 | 低 |
| 10 | 整套組合推薦的已知限制：錨靠模型翻譯、同義詞抓不到、SQL 的錨比對比 C# 粗、換了內容沒重給 `tags` 時舊錨留著；採用輪失敗後重試是純文字，HTTP 層就失敗時填回的是佔位字 | 限制 | 低 |
| 11 | `AskUser`／`Discuss` 的 call args 壓縮對 Gemini 不生效 | 成本 | 低 |
| 6 | 子專案 4 全分支審查留下的小項目 | 整理 | 低 |

---

## 3. 純文字補救的重試請求被 Gemini 回 400

**現象**（2026-09-24，子專案 3 瀏覽器走查）：「一個女生」→ audit `Protocol_Violation {"attempt":1}` 後緊接 `Turn_Failed {"stage":"loop","message":"...400 (Bad Request)","errorClass":"HttpOperationException"}`。前端正確回滾並給重試鈕，同一句再送通常會過。

**推測**：補救時送給 Gemini 的 history 形狀不合法，例如多一則 system 訊息或空的 model 訊息。尚未查證。

**修正方向**：先用 fake `IChatCompletionService` 讓第一次回純文字，攔下第二次請求的 `ChatHistory` 檢查形狀；再對照 Gemini 的 role 規則（`GeminiRoleFixHandler` 已處理過一次同類問題）。

**2026-09-29 再現**：瀏覽器實測時又發生一次（session `36063629…`），同樣是 `Protocol_Violation` 後接 `Turn_Failed` 400。當時的 audit 仍只有 connector 的例外訊息；#7 修好後，下一次發生時 Gemini 回的錯誤本文會在 `Turn_Failed` payload 的 `upstream.body`。

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

- **eval #5**「一個女生，其他隨便」：仍追問風格，沒有直接定稿。
- **eval #18**：追問偏少，一次只問 1–2 個維度，回答後就定稿，服裝整組留空。#1 修好後要重測，預算可能是原因之一。→ 追問政策已在 `fix/ask-all-missing` 反轉，重測。
- **§14 端到端驗收**是在 `fc449f7` 跑的，之後改過輪次流程、重試分類、輸出安全，要在新 HEAD 重跑。
- **eval #21／#22** 的故障注入方式（改 `Llm:Model`）測不到：404 不重試，session 在記憶體裡、重啟就沒了。要換一種注入方式，例如可設定的 fake 失敗次數。
- **facet 檢索「過濾準、排序不準」在穿著上看得到**（2026-09-29 瀏覽器驗收 R2 觀察）：「一個銀髮少女穿涼鞋站在雨夜街頭」的 `SearchPresets`，鞋履項目「涼鞋」池 396 → 5，前 5 名的鞋履 tag 依序是 `sandals`、`cowboy boots`、`sandals`、`wearing Nike sneakers`、`sneakers`（前 3 名 2 筆對），而知識庫鞋履 facet 含 sandal 的有 18 筆。同一次的髮型髮色「銀髮少女」5/5、地點類型「雨夜街頭」5/5 都對。穿著片段涵蓋頭到腳，整套向量被上下身主導，鞋履這種小 facet 排序就不準；髮型、地點的片段本身就以那個 facet 為主，不受影響。這支持 facet 向量子表案（`preset_facet_embeddings`）。

## 10. 整套組合推薦的已知限制（2026-09-25）

（原本也編成 9，跟文末已修正的 #9 撞號，2026-09-25 改為 10。）
（2026-09-29：「照它的」取代 covered facet 時舊 tag 沒被拿掉的那一條已修正，移到文末「已修正」的 #10。）

- **錨靠模型翻譯**：追問階段的錨是模型在 `SetFacetStates` 給的英文 `tags`，翻錯或沒給就退回無錨（「最接近你描述的組合」），不報錯。定稿後多了 positive 的 tag 當錨，會好一些。
- **同義詞抓不到**：錨比對是整段相等或空白為界的字尾（`platform sandals` ↔ `sandals`），`slippers` 對 `sandals` 不會命中。後續的 facet 向量案（子表 `preset_facet_embeddings`）用「該 facet 向量最近的」補這個缺口，排在本案之後。
- **採用那一輪失敗後的重試是純文字**：失敗條目的「重試」把伺服器組的採用句填回輸入框，重送時走一般訊息，模型仍會照第 6 條處理（還在收集且有 missing 的維度就追問，否則定稿），但 `Adoption` 沒記帳、tag 不會標 `adopted`。要重新採用請再按一次卡片上的「採用」。
- **錨的 SQL 比對只做小寫與底線換空白**：`RecommendAsync` 對資料庫的 tag 只做 `replace(lower(tag), '_', ' ')`，沒有 `TagAttribution.Normalize` 剝 `:數字` 權重、外層括號、連續空白那幾步，比 C# 端的比對粗。`facet_tags` 保留原始 SD 語法（如 `(sandals:1.2)`）時有兩個後果：一是錨會漏配，漏到不足 2 筆就退回無錨，卡片照樣有推薦，所以不容易被發現；二是被別的錨撈進來的候選，回報的 `anchorTags` 由 C# 以完整的 `Normalize` 算，可能多列一個 SQL 沒真正比中的錨。
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

## 已修正

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

**驗收**：`HistoryTrimmerGeminiTests` 用真的 connector 跑一輪 auto-invoke，壓縮後再送下一輪，檢查那次請求的 `functionResponse`：role 是 `user`、`name` 是 `Knowledge_SearchPresets`、內容只剩 `{dimension, facetId, poolSize, hits: [{id, title}]}` 與錯誤項目，本文裡沒有片段文字，`functionCall` 的 `thoughtSignature` 還在；平行呼叫（`SearchPresets` + `SetProfile`）時仍是同一則、兩個 part，只有 `SearchPresets` 被壓。另一條測試釘住 connector 的形狀（`CalledToolResults`、`Items` 沒有 `FunctionResultContent`），connector 換版時會先紅。**實測待做**：用這個分支建的 api 跑兩輪以上，看第二輪送出的 `functionResponse` 已是壓縮形狀。
