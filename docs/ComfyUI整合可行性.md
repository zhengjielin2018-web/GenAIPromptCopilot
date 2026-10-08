# 線上 ComfyUI API 整合可行性評估

日期：2026-10-08
狀態：**評估，未定案**。對應 [Agent 化提案](Agent化提案.md) §6.1（P0 閉環）與 §9 第 2 項（生圖後端）。定案後照慣例另開 `superpowers/specs/` 設計文件與 `superpowers/plans/` 計畫。
對象：決定生圖後端的人。讀完應該知道：線上 ComfyUI API 能不能接、接在程式的哪裡、要付出什麼、哪些還沒驗證、建議怎麼開始。

外部服務的規格與價格是 2026-10-08 查到的，動工前要再確認一次（來源見文末）。

---

## 1. 結論

**可行，建議採用**，並取代提案 §6.1 原本分開列的「託管的 SDXL API」與「本機 ComfyUI」兩個選項。

| 理由 | 說明 |
| :--- | :--- |
| 方言相符 | ComfyUI 跑的是 SDXL／Illustrious／NoobAI 這類 checkpoint，吃的就是我們產出的 SD tag 語法。Gemini／Imagen 不吃 tag 語法，驗證的是替代品（提案 §6.1 已點出） |
| 介面穩定 | 官方的 Comfy API v2 是有版本號的 REST 介面（v2 內只做加法、破壞性變更要等 v3），先輪詢、送出可冪等。C# 直接用 `HttpClient` 打，不需要 SDK |
| 一份工作流、三種跑法 | 同一個 API 格式的 workflow JSON、同一組 v2 端點，只換 base URL 就能跑 Comfy Cloud、Comfy API 專屬部署、或本機 ComfyUI（透過官方的 comfy-api-proxy）。「託管為主、本機選配」從兩套程式變成一個 client |
| 架構接點都在 | 工具清單限權、預算 filter、輸出審查 filter、SSE 工具卡、整輪回滾都已存在（§4），不必改骨架 |

**動工前要先量的三件事**（不是擋路，但會改變設計）：

1. **延遲**：一張圖加上自評要幾秒還沒量過。現在動手輪中位數 6.1–7.9 秒，整輪逾時 120 秒（§5）。
2. **圖片安全**：SD 系模型（尤其動漫 finetune）就算提示詞乾淨也可能生出不當內容。看圖審查必須做，不是選配（§7）。
3. **方案與模型**：API 要付費方案；從 Civitai 匯入 Illustrious／NoobAI 這類模型要 Creator 方案以上（§6、§7）。

**建議做法**：先花 1–2 天做 spike（§8），量到延遲、單張成本與自評準確度，再決定第一版做「定稿後生圖」還是直接做「輪內閉環」（§5.2）。

---

## 2. 「線上 ComfyUI API」有哪些選擇

| 服務 | 介面 | 計費 | 對這個專案 |
| :--- | :--- | :--- | :--- |
| **Comfy Cloud**（官方） | v2 `/api/v2/jobs`；舊的 v1 `/api/prompt` 已標為 deprecated | 月費方案附 credits；GPU 只算實際執行的秒數 | **建議**。維運最少，$20／月起，每個付費方案都含 API |
| Comfy API 專屬部署（官方，`<deployment>.run.comfy.app`） | 同 v2 | 依 GPU 秒計費（RTX PRO 6000 $4.54／小時、H100 $6.23／小時） | 流量變大、要固定模型或自訂節點時再升級，client 不用改 |
| RunComfy Serverless API | 自家 API（不是 v2） | 依 GPU 實例開機秒數 | 備選；要另寫一個 client |
| RunPod worker-comfyui | RunPod 的 `/run`、`/runsync`，工作流放在 `input.workflow` | 依 GPU 秒，通常最便宜 | 要自己做映像檔、處理冷啟動，維運最多 |
| 本機 ComfyUI＋comfy-api-proxy | 同 v2（proxy 在本機提供 v2 端點） | 電費 | 選配：docker compose profile，需要 GPU |

選 Comfy Cloud 的關鍵是第一列跟最後兩列（專屬部署、本機）**共用同一套 v2 介面**，之後換後端只改組態。

---

## 3. Comfy API v2 重點

依官方 `openapi-v2.yaml`（v2.0.0）整理，只列這個專案會用到的：

| 項目 | 內容 |
| :--- | :--- |
| Base URL | Comfy Cloud `https://cloud.comfy.org`；專屬部署 `https://{deployment}.run.comfy.app`；本機 proxy `http://127.0.0.1:8189` |
| 認證 | `Authorization: Bearer <api-key>`；本機 proxy 預設不驗證 |
| 送出 | `POST /api/v2/jobs`，body `{ "workflow": <API 格式的 graph>, "extra_data"?: {...} }`，回 `201` 與 Job。UI 格式（有 `nodes`／`links` 的那種匯出）直接拒絕（`workflow_format_ui`）。`inputs`、`webhook_url` 欄位**還沒開放**，所以提示詞與 seed 要我們自己寫進 graph 的節點 |
| 冪等 | header `Idempotency-Key`（建議 UUID）。**只能用一次**：同一把 key 再送一律 `422 idempotency_key_reuse`，不會重播結果。送出結果不明（逾時、5xx）時 key 仍被占用，規格要求**去查工作，不要重送** |
| 輪詢 | `GET /api/v2/jobs/{id}`：`status`（`queued` → `running` → `succeeded`／`failed`／`expired`，取消是 `canceling` → `canceled`）、`progress`、`outputs[]`、`metrics`（`queue_ms`、`execution_ms`） |
| 即時事件 | `GET /api/v2/jobs/{id}/events`（SSE）。規格明說只是加強體驗，輪詢才是準的 |
| 取消 | `POST /api/v2/jobs/{id}/cancel` |
| 取圖 | `outputs[].id` 是 asset id。`GET /api/v2/assets/{id}/content` 要帶 key；另有不用 key 的簽名網址，Cloud 上約 6 小時失效 |
| 錯誤 | `402 insufficient_credits`；`422 invalid_workflow`（附逐節點細節）；`429` 分 `queue_full`、`rate_limited` 等，附 `Retry-After` |
| 沒有的 | .NET SDK（只有 Python、TypeScript，且標 beta）、webhook |

**對我們重試層的影響**：現在 [`ResilientChatCompletion`](../src/PromptCopilot.Api/Llm/ResilientChatCompletion.cs) 遇到傳輸錯誤就重送。生圖 client 不能照抄：送出那一步結果不明時要查工作而不是重送，否則會重複扣 credits，而且第二次還會被 `422` 擋下。

---

## 4. 跟現有架構怎麼接

逐項對照提案 §6.1，每一項都有現成的前例可以照做：

| 要做的 | 放在哪 | 照哪個前例 |
| :--- | :--- | :--- |
| `IRenderClient`／`ComfyRenderClient`：送出、輪詢、取圖、取消 | 新資料夾 `Render/`，`AddHttpClient` 註冊 | [`GeminiEmbeddingClient`](../src/PromptCopilot.Api/Llm/GeminiEmbeddingClient.cs)：沒走 SK，直接打 REST |
| 工作流範本 | `Configuration/comfy/txt2img-sdxl.json`（API 格式）；正向、負向、seed、輸出節點的 id 寫在組態 | [`facets.yaml`](../src/PromptCopilot.Api/Configuration/facets.yaml) 放在 `Configuration/`、建置時複製 |
| 組態 | `Render` 區段：`BaseUrl`、`ApiKey`、`WorkflowPath`、節點 id、`PollIntervalMs`、`RenderTimeoutSeconds`、`MaxRendersPerTurn`、每個 session 與每日上限 | [`Options.cs`](../src/PromptCopilot.Api/Configuration/Options.cs) |
| 工具 `RenderPreview`、`CritiqueImage` | 新的 `RenderPlugin`，名稱加進 `ToolNames` | [`KnowledgePlugin`](../src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs) |
| 只在動手輪、只在生圖開著時給 | [`ToolSetBuilder.Build`](../src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs) | 知識庫開關（`RetrievalEnabled`）拿掉檢索工具的寫法 |
| 每輪生圖預算 | `ToolBudgetFilter` 旁邊加一個 | [`ToolBudgetFilter`](../src/PromptCopilot.Api/Filters/ToolBudgetFilter.cs) |
| 生圖前先審提示詞 | `RenderPreview` 加進 `OutputSafetyFilter` 的審查清單：花錢之前先擋 | [`OutputSafetyFilter`](../src/PromptCopilot.Api/Filters/OutputSafetyFilter.cs) |
| 生圖後審圖、自評 | 各打一次 Gemini：user 訊息放 `ImageContent`（PNG bytes），回結構化 JSON；兩者平行跑 | [`SafetyClassifier`](../src/PromptCopilot.Api/Safety/SafetyClassifier.cs)：不帶 kernel、`ResponseSchema` |
| 定稿閘門 | 生圖開著、還有沒過的 facet、預算有剩時擋回；生圖失敗或逾時時放行 | `FinalizePrompt` 的 `UnaskedMissing` 與 `ForcedFinalize` |
| 圖片給前端 | 圖片 bytes 存在 session（只留最近幾張），新端點 `GET /api/sessions/{id}/renders/{renderId}` 提供；前端的 transcript 只記 `renderId` | session 過期一起清掉，等於提案 §6.1 的「預覽圖是暫存」 |
| 工具卡、定稿卡 | `ToolResultEvent.Detail` 放 `{ renderId, jobId, queueMs, executionMs }`；定稿卡顯示圖與自評結果 | `SearchPresetsDetail` |
| 稽核 | `Turn_Completed` 的 payload 加生圖數、job id、`queue_ms`／`execution_ms`、自評結果 | 現有的 `searches`、`ragSplit` 欄位 |
| 開關與部署 | `.env` 加 `COMFY_API_KEY`，compose 映射成 `Render__ApiKey`。沒設 key 時功能自動關掉、啟動時記一行警告，`docker compose up` 不受影響 | `GEMINI_API_KEY` 沒設時的啟動警告 |
| 測試 | `FakeRenderClient`；v2 回應形狀寫成契約測試；CI 不打真的 API | [`GeminiContractTests`](../src/PromptCopilot.Api.Tests/Llm/GeminiContractTests.cs)、[`FakeChatCompletion`](../src/PromptCopilot.Api.Tests/Fakes/FakeChatCompletion.cs) |

幾個設計細節：

- **圖片不進主對話的 `ChatHistory`。** 自評在工具內部另外呼叫 Gemini，回給主模型的只有 JSON 文字。理由有二：SK 的 Google connector 把圖片放進工具結果的寫法，在 main 分支上註明只支援 Gemini 3 以上，1.80.1-alpha 有沒有還沒確認；另外圖片留在 history 裡，之後每一輪都要重送，token 會一直漲。放在一般 user 訊息裡的 `ImageContent`，connector 會轉成 Gemini 的 `inlineData`（已讀過 connector 原始碼確認）。
- **同一個 session 固定 seed。** 改 tag 再生時，畫面差異才是 tag 造成的，不是換了 seed。自評比對「改前、改後」才有意義。
- **回滾碰不到外部的錢。** 一輪失敗會回滾 session，但已經送出的生圖工作扣掉的 credits 拿不回來。逾時或客戶端斷線時順手呼叫 cancel（盡力而為），送出前的文字審查也是為了不在會被擋下的內容上花錢。
- **不把 Comfy 的網址或 key 給前端。** 圖片一律經我們的端點轉送；簽名網址約 6 小時就過期，前端重新整理後也會失效。

---

## 5. 延遲

### 5.1 估算

| 區段 | 時間 | 依據 |
| :--- | :--- | :--- |
| 現在的動手輪 | 中位數 6.1–7.9 秒 | 實測（[檢索時機實驗](experiments/2026-10-06-retrieval-timing.md)） |
| Comfy 排隊＋冷啟動 | **未知** | spike 量 `metrics.queue_ms`；第一張與間隔一段時間後的第一張要分開量 |
| SDXL 1024²、25–30 步 | 估計幾秒（Comfy Cloud 用 96 GB 的 RTX PRO 6000 Blackwell） | 未實測；spike 量 `metrics.execution_ms` |
| 下載一張 1–2 MB 的 PNG | 不到 1 秒 | 估計 |
| 審圖＋自評（兩次 Gemini 平行） | 估計 2–5 秒 | 未實測；多了圖片輸入，會比現在的文字分類器慢 |
| 模型看完自評再決定下一步 | 每圈多一趟模型呼叫，約 1–3 秒 | 估計 |

暖機狀態下一圈（生圖 → 審圖＋自評 → 模型決定）估計 10–20 秒，兩圈約 30–50 秒，在 120 秒逾時之內；**冷啟動或排隊是最大的變數**。Standard 方案的 API 併發只有 1，同時有兩個人在用，第二個就要排隊。

不管哪種形狀都要做的保護：

- 每張圖有自己的逾時（例如 45 秒）。逾時就取消工作，工具回錯誤字串「生圖逾時，請直接定稿」，這一輪不失敗、不回滾。
- `429 queue_full`、`402 insufficient_credits` 一樣降級成「這一輪不生圖」，不讓整輪失敗。
- 工具卡顯示「排隊中／生成中 x%」（輪詢拿得到 `progress`），等待看得見。

### 5.2 兩種形狀

| | A. 輪內閉環（提案 §6.1 原設計） | B. 定稿後生圖 |
| :--- | :--- | :--- |
| 流程 | 動手輪內：草稿 → 生圖 → 自評 → 改 → 再生 → 定稿 | 定稿照現在一樣先出；之後生圖、審圖、自評，結果補在定稿卡上；卡上有「依自評修正」按鈕，按下去是一個新的動手輪 |
| 誰決定修正 | 模型（真正的「做 → 看 → 改」） | 使用者按按鈕 |
| 延遲 | 動手輪變長，要看 spike 數字 | 定稿不變慢；圖晚幾秒補上 |
| 失敗的影響 | 在交易裡，要處理逾時與降級 | 跟[整套組合推薦](superpowers/specs/2026-09-25-set-recommendations-design.md)一樣在 final 之後跑、自己的逾時、失敗只記 audit，不回滾 |
| 改動範圍 | 工具、閘門、預算、system prompt、前端 | client、審圖、自評、端點、前端；不動工具清單與閘門 |

**建議先做 B，再做 A。** B 用到的元件（client、工作流範本、審圖、自評、圖片端點、前端顯示）A 全部沿用，等於先把風險最高的外部依賴接穩；B 跑出來的延遲與自評準確度，正好決定 A 的預算與逾時要設多少。只是 B 本身還不是 agent（修正由使用者觸發），README 改定位（提案 §8 階段 6）要等 A 做完。

spike 數字夠好的話（§8 的門檻），可以跳過 B 直接做 A。

---

## 6. 成本

| 項目 | 數字 | 來源 |
| :--- | :--- | :--- |
| Standard 方案 | $20／月，4,200 credits；API 併發 1 | Comfy 價格頁 |
| Creator 方案 | $35／月，7,400 credits；API 併發 3；可從 Civitai／Hugging Face 匯入模型 | Comfy 價格頁、匯入模型文件 |
| GPU 消耗 | 只算實際執行，約 0.266 credits／秒（舊資料寫 0.39，2026-01 降價過） | Comfy 支援文章 |
| 換算 | 4,200 credits ≈ 每月 4.4 GPU 小時 | 計算 |

**估算**（單張 GPU 時間未實測）：一張 SDXL 若花 5–10 秒 GPU，就是 1.3–2.7 credits，約 $0.006–0.013；Standard 方案一個月約 1,500–3,000 張。一次定稿生兩張，加上審圖與自評的 Gemini 呼叫，約 $0.02–0.03。eval 若 30 個劇本、每個生兩張、每週跑一次，一個月約 240 張，額度綽綽有餘。

**API 沒有認證，要自己設上限。** 這個專案的 API 誰都能打（[`SafetyOptions`](../src/PromptCopilot.Api/Configuration/Options.cs) 的註解也提到這點），公開 demo 時任何人都能燒 credits。方案額度用完會回 `402`，是天然上限，但要再加每個 session 與每天的生圖上限，用完就降級成不生圖。

---

## 7. 風險與待確認

| 項目 | 風險 | 處理 |
| :--- | :--- | :--- |
| 模型與知識庫方言 | 知識庫混了 SD 1.5、SDXL、NoobAI、Illustrious、Pony 的提示詞（[`strata.py`](../scripts/pipeline/strata.py) 的抓取配額）。一個 checkpoint 寫實與動漫兩頭都畫不好時，自評沒過的原因是模型做不到，而不是提示詞寫錯，模型會白白一直改 | 依 `style.genre` 選工作流：動漫走 Illustrious／NoobAI，寫實走寫實系 SDXL。自評結果要能標「模型限制」；生圖預算讓迴圈一定會停 |
| Cloud 內建模型 | 官方沒有公開完整的預裝模型清單；SDXL Base 1.0、JuggernautXL 有官方頁面，Illustrious 沒找到 | spike 第一步在 Model Library 確認；沒有就要 Creator 方案匯入 |
| 自評可靠度 | 視覺模型判斷細節（85mm、景深、特定髮型）會有雜訊，判錯會讓閘門亂擋 | 自評只檢查畫面上看得出來的 facet；spike 量自評與人工判讀的一致率，不到門檻前自評只顯示、不擋定稿 |
| 圖片安全 | 乾淨的提示詞也可能生出不當內容；Gemini 也可能拒收圖片 | 負向詞固定帶 NSFW 相關詞；每張圖都審，沒過就不給使用者看，照 `OutputBlockedException` 的路徑處理；Gemini 拒收也視同攔截 |
| Comfy 的內容政策 | Comfy Cloud 服務條款對生成內容的規定這次沒查到明確條文 | 動工前讀完 [服務條款](https://www.comfy.org/terms-of-service)；我們自己的審查本來就比較嚴 |
| v2 還很新 | SDK 標 beta；v1 已 deprecated | 只接 v2；以 v2 規格寫契約測試；v2 承諾只做加法 |
| 沒有 webhook | 只能輪詢 | 規格本來就以輪詢為準；輪詢間隔 1–2 秒，量不大 |
| 匯入模型的授權 | Comfy 說它的模型庫可商用，但從 Civitai 匯入的 checkpoint 各有授權 | 選模型時看授權；本專案非商業，但 README 要寫清楚 |
| 主規格 | 主規格 §2.2 明列「實際生成圖片（只產 prompt）」不做 | 定案後先改主規格（提案 §9 第 3 項） |

---

## 8. 建議的 spike

目標：用數字回答上面的未知數。預估 1–2 天，花費不超過一個月的 Standard 方案。

1. 開 Standard 方案、在 platform.comfy.org 拿 API key（只放 `.env`，不進版控）。在 Cloud 編輯器做最小的 SDXL txt2img 工作流，用 File → Export Workflow (API) 匯出；在 Model Library 確認有哪些 SDXL checkpoint。
2. 寫一支腳本**直接打 v2 的 REST 端點**（不用 Python SDK，因為正式實作的 C# 也是直接打 REST）：送出 → 輪詢 → 取圖。跑 20 張，記 `queue_ms`、`execution_ms`、總時間、credits 扣了多少；隔 30 分鐘以上再跑第一張，量冷啟動。
3. 拿 [eval 案例](eval-cases.md)裡已經定稿過的 10 個提示詞生圖，用 Gemini 做 facet 自評，人工對照判讀，算一致率。
4. 同一批圖跑看圖審查，看有沒有誤判或漏判。

**判斷門檻**（建議值，可以調）：

| 量到的結果 | 下一步 |
| :--- | :--- |
| 單張（含取圖）P50 ≤ 15 秒、P95 ≤ 40 秒 | 可以直接做 A（輪內閉環） |
| 比上面慢 | 先做 B（定稿後生圖），A 等換專屬部署或調整工作流（例如少步數、小尺寸）後再評估 |
| 自評一致率 ≥ 80% | 定稿閘門可以依自評擋回 |
| 自評一致率 < 80% | 自評只顯示，不擋定稿 |

---

## 9. 對既有文件的影響（定案後才改）

| 文件 | 要改的地方 |
| :--- | :--- |
| [Agent 化提案](Agent化提案.md) | §6.1 的生圖後端表格：「託管的 SDXL API」與「本機 ComfyUI」合併成「ComfyUI v2 API（Comfy Cloud 為主，本機經 comfy-api-proxy 為選配）」；§9 第 2 項標為已回答 |
| [主規格](superpowers/specs/2026-09-21-genai-prompt-copilot-design.md) | §2.2 移除「實際生成圖片」；§4.2 工具表；§4.3 工具清單規則 |
| [SK 架構說明](SK架構說明.md) | 四層圖加上生圖 client（跟 embedding 一樣不走 SK）；審圖與自評的 `ImageContent` 用法 |
| [README](../README.md) | 生圖的設定方式、費用、預覽圖不轉存的說明 |
| [資料來源](資料來源.md) | 預覽圖是暫存、不進知識庫與種子；跟「上游圖片一律不轉存」是兩回事 |

---

## 10. 需要專案擁有者決定的事

1. **方案**：Standard（$20，只用內建模型），還是 Creator（$35，可以匯入 Illustrious／NoobAI，API 併發 3）。
2. **順序**：先做 B（定稿後生圖）再做 A（輪內閉環），還是等 spike 數字直接決定。
3. **費用上限**：每月生圖費用上限；公開 demo 時每個 session、每天各幾張。
4. **spike 誰跑**：需要一把 Comfy Cloud 的 API key。

---

## 來源

- [Comfy API v2 OpenAPI 規格](https://docs.comfy.org/openapi-v2.yaml)、[v2 概覽](https://docs.comfy.org/api-reference/v2/overview)、[SDK 說明](https://docs.comfy.org/development/api-development/sdks)、[快速開始](https://docs.comfy.org/development/api-development/quickstart)
- [v1 Cloud API 概覽（deprecated）](https://docs.comfy.org/development/cloud/overview)、[v1 API 參考](https://docs.comfy.org/development/cloud/api-reference)
- [Comfy 價格頁](https://comfy.org/pricing/)、[How credits work in Comfy](https://support.comfy.org/articles/5846341390-how-credits-work-in-comfy)、[Comfy Cloud 價格調整公告](https://blog.comfy.org/p/comfy-cloud-new-features-and-pricing)
- [Comfy Cloud 匯入模型](https://docs.comfy.org/cloud/import-models)、[SDXL Base 1.0 支援模型頁](https://comfy.org/p/supported-models/sd-xl-base-1-0)
- [Comfy 服務條款](https://www.comfy.org/terms-of-service)
- [RunComfy Serverless 計費](https://docs.runcomfy.com/serverless/about-billing)、[RunPod Serverless 計費](https://docs.runpod.io/serverless/pricing)、[runpod/worker-comfyui](https://hub.docker.com/r/runpod/worker-comfyui)
- [Semantic Kernel Google connector：`GeminiRequest.cs`](https://github.com/microsoft/semantic-kernel/blob/main/dotnet/src/Connectors/Connectors.Google/Core/Gemini/Models/GeminiRequest.cs)（`ImageContent` 轉 `inlineData`）
