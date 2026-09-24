# 已知問題與待修清單

已查明但還沒修的問題。修掉一項就把它移到文末「已修正」並註明 commit。
子專案 2 的效果調整（prompt、檢索、預算）刻意延後到子專案 4 之後一起做，所以大部分項目屬於那一輪。

| # | 問題 | 類型 | 優先 |
| :--- | :--- | :--- | :--- |
| 1 | 人像題材第一輪常被強制定稿，整個 session 不再追問 | bug | 高 |
| 2 | `SearchPresets` 在候選池有幾千筆時回 0 筆 | bug | 高（#1 的成因之一） |
| 3 | 純文字補救的重試請求被 Gemini 回 400 | bug | 中 |
| 4 | 無害描述被上游 SAFETY 連續誤擋 | 行為 | 中 |
| 7 | log 不足：看不出被擋的原因，一輪的經過只在資料庫裡 | 可觀測性 | 中（#4 要靠它確認） |
| 5 | eval #5、#18 行為不符預期；§14 端到端要在新 HEAD 重跑 | 調整 | 低 |
| 6 | 子專案 4 全分支審查留下的小項目 | 整理 | 低 |

---

## 1. 人像題材第一輪常被強制定稿，整個 session 不再追問

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

**修正方向**（擇一或併用，建議兩者都做）：

1. 把 `MaxToolCallsPerTurn` 調到能容納最壞情況：1 + 6×2 + `SetFacetStates` 1 + `SearchSimilarPrompts` 1 + 終止 1 = 16。改 `appsettings.json`，主規格 §4.5 `ToolBudgetFilter` 那列的「預設 8」一起改。
2. 讓一次 `SearchPresets` 接受多組 `(dimension, query)`，一輪只花一兩次呼叫。主規格 §9 本來就寫「多個維度的查詢語句合併為單次 `embed_batch` 呼叫」，目前的工具簽名做不到。這是工具契約變更，要同步 system.md、`OutputSafetyFilter` 取材、前端 `tool_call` 卡片的摘要。
3. 另外考慮：強制定稿前先補一次 `SetFacetStates`，或讓強制定稿的 prompt 明確要求依使用者原話標 covered。

**驗收**：用上表兩句重跑，應該看到追問卡；audit 沒有 `Tool_Budget_Exhausted`；`Turn_Completed.toolCalls` 在預算內。另外補一條 eval 案例：「有細節但缺風格與鏡頭的人像描述」→ `final.kind = ask`。

## 2. `SearchPresets` 在候選池有幾千筆時回 0 筆

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

**修正方向**：pgvector 已是 0.8.6，支援 iterative scan。在過濾搜尋的交易內 `SET LOCAL hnsw.iterative_scan = strict_order`（結果嚴格依距離排序）；或在資料庫層級 `ALTER DATABASE prompt_copilot SET hnsw.iterative_scan = strict_order`，但這要同步寫進 `db/init/001_schema.sql` 才會跟著 fresh clone 走。候選池最大只有 6,752 筆，改成精確掃描也可以接受，但會失去 HNSW 的展示意義。

**驗收**：加一條整合測試（`[IntegrationFact]`）：拿某個 scene preset 的向量對 style facet 查，筆數必須等於 k。Python 端在 `tests/test_retrieval.py` 加對應的 integration 測試。

**備註**：這一項之前被記成「模型要自己換成『寫實攝影』重搜」的 prompt 調整問題，實際上是檢索 bug。

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

## 5. 效果調整（非 bug）

- **eval #5**「一個女生，其他隨便」：仍追問風格，沒有直接定稿。
- **eval #18**：追問偏少，一次只問 1–2 個維度，回答後就定稿，服裝整組留空。#1 修好後要重測，預算可能是原因之一。
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

（尚無）
