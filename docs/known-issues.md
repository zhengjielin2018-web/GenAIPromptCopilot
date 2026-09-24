# 已知問題與待修清單

已查明但還沒修的問題。修掉一項就把它移到文末「已修正」並註明 commit。
子專案 2 的效果調整（prompt、檢索、預算）刻意延後到子專案 4 之後一起做，所以大部分項目屬於那一輪。

| # | 問題 | 類型 | 優先 |
| :--- | :--- | :--- | :--- |
| 3 | 純文字補救的重試請求被 Gemini 回 400 | bug | 中 |
| 4 | 無害描述被上游 SAFETY 連續誤擋 | 行為 | 中 |
| 7 | log 不足：看不出被擋的原因，一輪的經過只在資料庫裡 | 可觀測性 | 中（#4 要靠它確認） |
| 8 | `HistoryTrimmer` 對 Gemini 的工具結果從未生效 | 可觀測性／成本 | 中 |
| 5 | eval #5、#18 行為不符預期；§14 端到端要在新 HEAD 重跑 | 調整 | 低 |
| 6 | 子專案 4 全分支審查留下的小項目 | 整理 | 低 |

---

## 3. 純文字補救的重試請求被 Gemini 回 400

**現象**（2026-09-24，子專案 3 瀏覽器走查）：「一個女生」→ audit `Protocol_Violation {"attempt":1}` 後緊接 `Turn_Failed {"stage":"loop","message":"...400 (Bad Request)","errorClass":"HttpOperationException"}`。前端正確回滾並給重試鈕，同一句再送通常會過。

**推測**：補救時送給 Gemini 的 history 形狀不合法，例如多一則 system 訊息或空的 model 訊息。尚未查證。

**修正方向**：先用 fake `IChatCompletionService` 讓第一次回純文字，攔下第二次請求的 `ChatHistory` 檢查形狀；再對照 Gemini 的 role 規則（`GeminiRoleFixHandler` 已處理過一次同類問題）。

## 4. 無害描述被上游 SAFETY 連續誤擋

**現象**（2026-09-24）：「中年阿姨在廚房夾菜，穿著圍裙」連送 3 次、改寫兩次，共 5 次 `Blocked_Upstream {"stage":"loop","reason":"SAFETY","attempts":2}`。改成「中年女士在廚房，正把青菜放入便當內，她穿著圍裙與長褲」才通過。

**已知背景**：主規格 §15 把上游內容攔截的內部重試設為 1 次，理由是同一份 SFW 內容常被誤擋、重送一次常會過。這次重試過仍被擋。

**已查到的線索**（2026-09-24）：

- 5 次都發生在**第一次**模型呼叫：被擋的輪次沒有任何 `Tool_Invoked`，送出去的只有 system prompt、工具定義與使用者那句話。輸入端的 `SafetyGuard` 分類器同樣呼叫 Gemini，卻每次都放行。
- 主迴圈與分類器都沒設 `safetySettings`，走 Gemini 預設門檻。
- 被擋的 5 句，衣著都只有「穿著圍裙」；把「阿姨」換成「女士」照樣被擋；加上「與長褲」才通過。**假設**：在「產生生圖提示詞」的語境下，「女性＋只穿圍裙＋廚房」貼近 SD 語料常見的「裸體圍裙」題材，被上游分類器判定。這只是從字面規律推的，目前的紀錄無法證實（見 #7）。

**修正方向**：先做 #7，把被擋的類別與分數記下來，確認假設。可以考慮的處理：調整 Gemini 的 `safetySettings` 門檻（要評估對 NSFW 防線的影響）、或在 `blocked` 的前端文案提示「換個說法」。這一項跟 NSFW 過濾範圍無關，過濾範圍已定案。

## 7. log 不足：看不出被擋的原因，一輪的經過只在資料庫裡

**現況**：

- **`audit_logs`（資料庫）** 是唯一完整的紀錄：每輪的輸入原文、每次工具呼叫的參數與結果、結局、攔截、存檔。重啟不會消失。
- **API 容器 log**（`docker compose logs api`）幾乎沒有對話的資訊：程式只在寫 audit 失敗、串流中途出錯、缺 API key 時寫 log。其餘全是 `System.Net.Http` 每次 embedding 請求的 Information 行（一輪可達數十行）。容器重建後這份 log 就沒了。

**缺的東西**：

1. `Blocked_Upstream` 只記 `reason`，而且這個值不可靠：`LlmFailureClassifier.BlockReasonOf` 在例外訊息含 "blocked" 或 "safety" 時一律回 `SAFETY`。「輸入被拒」（`promptFeedback.blockReason`）與「輸出被截」（`finishReason`）在 audit 裡長得一樣，也沒有 `safetyRatings` 的類別與機率。#4 因此無法確認。
2. `Turn_Failed` 只記例外訊息的前段，沒有 Gemini 回應本文（#3 的 400 需要它）。
3. 容器 log 沒有每輪一行的摘要，看 demo 時沒辦法從終端機判斷發生什麼事。

**修正方向**：

- 攔截時把 `GeminiMetadata` 的 `PromptFeedbackBlockReason`、`FinishReason`、`PromptFeedbackSafetyRatings`／候選的 `SafetyRatings` 整包寫進 `Blocked_Upstream` 的 payload，並分開記「輸入被拒」與「輸出被截」。例外路徑拿不到 metadata 時，至少記下完整例外訊息。
- `AgenticOrchestrator` 在每輪結束時寫一行 Information：session、turn、結局、工具呼叫數、耗時。
- `appsettings.json` 把 `System.Net.Http` 調到 Warning。

**驗收**：重送「中年阿姨在廚房夾菜，穿著圍裙」，`Blocked_Upstream` 的 payload 看得到是哪一種攔截、哪個類別、什麼機率；`docker compose logs api` 每輪有一行摘要，沒有 embedding 請求的雜訊。

## 8. `HistoryTrimmer` 對 Gemini 的工具結果從未生效（可觀測性／成本，中）

- 現象：Google connector 把工具結果放在 `GeminiChatMessageContent.CalledToolResult`，tool 訊息的 `Items` 只有一個空的 `TextContent`，所以 `HistoryTrimmer.CompressTurn` 的 `Items.OfType<FunctionResultContent>()` 找不到東西，多輪 §6.3 的壓縮實際上一次都沒跑；下一輪請求仍帶完整片段本文。Reviewer 用 connector 真實產生的 history 實測確認（2026-09-24）。
- 影響：只在合成測試裡有效；正式路徑沒有壓縮，token 成本比設計高。舊形狀的 history 不受影響（session 只在記憶體，重啟即清）。
- 修正方向：把 tool 訊息正規化成 `FunctionResultContent`（或直接重建訊息），並用 connector 產生的 history 寫測試。

## 5. 效果調整（非 bug）

- **eval #5**「一個女生，其他隨便」：仍追問風格，沒有直接定稿。
- **eval #18**：追問偏少，一次只問 1–2 個維度，回答後就定稿，服裝整組留空。#1 修好後要重測，預算可能是原因之一。→ 追問政策已在 `fix/ask-all-missing` 反轉，重測。
- **§14 端到端驗收**是在 `fc449f7` 跑的，之後改過輪次流程、重試分類、輸出安全，要在新 HEAD 重跑。
- **eval #21／#22** 的故障注入方式（改 `Llm:Model`）測不到：404 不重試，session 在記憶體裡、重啟就沒了。要換一種注入方式，例如可設定的 fake 失敗次數。

## 6. 子專案 4 全分支審查留下的小項目

都不影響功能，順手時再做。

- `scripts/export_seed.py` 的 `run()` 收下 stderr，失敗時只看得到指令，看不到 PostgreSQL 的錯誤原文。
- `export_seed.pipe()` 先檢查 pg_dump 的結束碼再檢查 pg_restore，還原失敗可能被報成來源端的 broken pipe。
- `docker/nginx.conf` 轉送 `Host $host` 會丟掉埠號，`$http_host` 可保留 `localhost:8080`。
- `docker/Dockerfile.api` 執行階段是 root，aspnet image 內建 `app` 使用者。
- `.gitattributes` 只把 `*.sh` 固定成 LF；`nginx.conf` 與 `001_schema.sql` 在 Windows checkout 是 CRLF，目前靠兩者容忍 CRLF 才沒事。
- CI 的 docker job 沒跑 `docker compose config --quiet`。
- README 缺「重置知識庫：`docker compose down -v`」與「沒填 key 會是什麼樣子」。
- 四個服務都寫死 `container_name`，兩套 stack 不能並存（fresh clone 測試要先 `docker compose down` 開發用的那套）。
- `PresetDrawer.vue` 在模板裡算了兩次 `sourceName()`。
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

**修正**（分支 `fix/ask-all-missing`，commit hash 待 merge 後補）：system.md 第 1 條改為 `SetProfile` → `SetFacetStates` → `SearchPresets`；同時把追問政策反轉為問滿 missing 維度：該維度底下只要還有任何 facet 是 missing（waived 與有委託 note 的不算）就要問，只講一部分的維度也問剩下的 facet，使用者接受完整描述也可能先被追問（eval #2）。起因是使用者 2026-09-25 實測回饋「描述缺很多面向，但追問很少」，見 #5 的 eval #18。`SetFacetStates` 與 `AskUser` 的工具描述、主規格 §4.2 與 §15、批次設計 §3.4 同步。主規格 §9「grounded 由伺服器算」原則不變，只是讓伺服器有資料可算。

**驗收**：重跑 eval #1、#18、#24，看第一輪 `SearchPresets` 對使用者講過的維度是否 `grounded: true`、追問是否把 missing 維度問滿。
