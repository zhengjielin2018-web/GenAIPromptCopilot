# 線上 ComfyUI API 整合可行性評估

日期：2026-10-08（2026-10-09 更新：評估並排除 NovelAI，§2.1；後端架構，§6；限制條件與 RunPod 試用，§2.2、§3.1、§7、§9）
狀態：**評估，未定案**。對應 [Agent 化提案](Agent化提案.md) §6.1（P0 閉環）與 §9 第 2 項（生圖後端）。已決定：生圖後端走 ComfyUI（2026-10-09）；只用雲端、風格偏動漫、每月 NT$1,000 內，先試 RunPod Serverless（§2.2）。先後順序等其餘事項見 §11；定案後照慣例另開 `superpowers/specs/` 設計文件與 `superpowers/plans/` 計畫。
對象：決定生圖後端的人。讀完應該知道：線上 ComfyUI API 能不能接、接在程式的哪裡、後端怎麼處理延遲與同時數上限、要付出什麼、哪些還沒驗證、建議怎麼開始。

外部服務的規格、價格與條款是 2026-10-08／09 查到的，動工前要再確認一次（來源見文末）。

---

## 1. 結論

**可行，建議採用**，並取代提案 §6.1 原本分開列的「託管的 SDXL API」與「本機 ComfyUI」兩個選項。

| 理由 | 說明 |
| :--- | :--- |
| 方言相符 | ComfyUI 跑的是 SDXL／Illustrious／NoobAI 這類 checkpoint，吃的就是我們產出的 SD tag 語法。Gemini／Imagen 不吃 tag 語法，驗證的是替代品（提案 §6.1 已點出） |
| 介面穩定 | 官方的 Comfy API v2 是有版本號的 REST 介面（v2 內只做加法、破壞性變更要等 v3），先輪詢、送出可冪等。C# 直接用 `HttpClient` 打，不需要 SDK |
| 一份工作流、三種跑法 | 同一個 API 格式的 workflow JSON、同一組 v2 端點，只換 base URL 就能跑 Comfy Cloud、Comfy API 專屬部署、或本機 ComfyUI（透過官方的 comfy-api-proxy）。「託管為主、本機選配」從兩套程式變成一個 client |
| 架構接點都在 | 工具清單限權、預算 filter、輸出審查 filter、SSE 工具卡、整輪回滾都已存在（§4），不必改骨架 |
| 允許程式自主生圖 | agent 化的核心是模型自己決定「生圖 → 看 → 改 → 再生」。NovelAI 技術上更簡單，但條款要求每次生圖都由人觸發，也不准讓別人用你的帳號（§2.1），只有 ComfyUI 能走到自主閉環 |

**動工前要先量的三件事**（不是擋路，但會改變設計）：

1. **延遲**：一張圖加上自評要幾秒還沒量過。現在動手輪中位數 6.1–7.9 秒，整輪逾時 120 秒（§5）。後端因此把生圖當成排隊的工作，不在對話輪裡等（§6）。
2. **圖片安全**：SD 系模型（尤其動漫 finetune）就算提示詞乾淨也可能生出不當內容。看圖審查必須做，不是選配（§8）。
3. **方案與模型**：API 要付費方案；從 Civitai 匯入 Illustrious／NoobAI 這類模型要 Creator 方案以上（§7、§8）。

**建議做法**：先花 1–2 天做 spike（§9），量到延遲、單張成本與自評準確度，再決定第一版做「定稿後生圖」還是直接做「自主閉環」（§5.2）。

---

## 2. 「線上 ComfyUI API」有哪些選擇

| 服務 | 介面 | 計費 | 對這個專案 |
| :--- | :--- | :--- | :--- |
| **Comfy Cloud**（官方） | v2 `/api/v2/jobs`；舊的 v1 `/api/prompt` 已標為 deprecated | 月費方案附 credits；GPU 只算實際執行的秒數 | 維運最少，$20／月起，每個付費方案都含 API；但內建模型沒有 Illustrious／NoobAI，匯入要 Creator（§2.2） |
| Comfy API 專屬部署（官方，`<deployment>.run.comfy.app`） | 同 v2 | 依 GPU 秒計費（RTX PRO 6000 $4.54／小時、H100 $6.23／小時） | 流量變大、要固定模型或自訂節點時再升級，client 不用改 |
| RunComfy Serverless API | 自家 API（不是 v2） | 依 GPU 實例開機秒數 | 備選；要另寫一個 client |
| **RunPod worker-comfyui** | RunPod 的 `/run`、`/status`，工作流放在 `input.workflow`（§3.1） | 依 GPU 秒，通常最便宜 | **試用中（2026-10-09，§2.2）**。要自己做映像檔（GitHub 整合可代為建置）、處理冷啟動 |
| 本機 ComfyUI＋comfy-api-proxy | 同 v2（proxy 在本機提供 v2 端點） | 電費 | 選配：docker compose profile，需要 GPU |

Comfy Cloud 的好處是第一列跟專屬部署、本機**共用同一套 v2 介面**，之後換後端只改組態。RunPod 的傳輸介面不同（§3.1），但吃同一份 API 格式的 workflow JSON，多寫一個轉接器即可，§6 的佇列與 Dispatcher 不變。

### 2.1 NovelAI（評估後不採用，2026-10-09）

專案擁有者已經付費訂閱 NovelAI，所以一併評估。**技術上比 ComfyUI 簡單，但條款擋住 agent 化，不採用。**

技術上簡單在哪：

- 同步 API：一個 `POST https://image.novelai.net/ai/generate-image` 直接回圖（zip 裡一張 PNG，或帶 `Accept: application/json` 回 base64），不必送出、輪詢、取圖三段。社群實測 V5、832×1216、28 步，一張約 2–2.5 秒（只有一份測量）。
- 不必維護 workflow JSON、選模型、匯入模型。
- Opus 方案在單張、面積 ≤ 1024×1024、≤ 28 步、不用底圖時不扣 Anlas。V5 另有會自動回充的使用上限（官方沒公布容量），V4.5 以前的模型不受影響。
- 社群回報每個帳號同時約 1 個生成；被限流（`429`）時官方文件寫不要重試。

不採用的理由，官方 Image Generation API 文件在 `/ai/generate-image` 上寫：

> According to our Terms of Service, all generation requests must be initiated by a human action. Automating text or image generation to create excessive load on our systems is not allowed.

服務條款（2025-12-08 版）另有 §9.1.3「Enable or allow others to use the Services or Software using your account information」、§5.3.2「Allow third parties to access the account remotely」；API 文件也要求做給其他使用者用的應用，要請每個使用者提供他自己的 Persistent API token。對這個專案的影響：

| 用途 | NovelAI 可不可以 |
| :--- | :--- |
| 模型自己決定生圖、看圖、再生（P0 閉環 A） | 不行：不是人觸發的生成 |
| eval 排程自動跑生圖（提案 §6.2） | 不行：同上 |
| 公開 demo，訪客用擁有者的帳號生圖 | 不行：讓別人透過你的帳號使用服務。改成訪客貼自己的 token，大部分訪客沒有 NovelAI 訂閱 |
| 只有擁有者自己用，每張圖都按一次按鈕 | 可以 |

另外兩個限制：NovelAI 只畫動漫或插畫，知識庫裡 SDXL 1.0、SD 1.5 的寫實提示詞對不上；V4 以後的請求格式（`v4_prompt` 的 `base_caption`、自動附加品質詞）跟 SD tag 不同，要多一層轉換。違反條款的後果是付費帳號可能被停權（條款 §9.2）。

**保留的可能（NovelAI）**：生圖後端的介面標明「能不能由程式自主生圖」。日後要做「只有自己用、每張都按按鈕」的模式時，NovelAI 可以當成標「必須由人觸發」的後端：`ToolSetBuilder` 不把生圖工具給模型，生圖只能從前端按鈕觸發。限制由程式保證，跟工具清單限權同一個原則。目前不做。

### 2.2 限制條件與選擇（2026-10-09）

專案擁有者的條件：**只能用雲端**（沒有可用的本機 GPU）、**風格偏動漫**、**每月 NT$1,000 內**（以 1 美元 ≈ 31.8 台幣，約 US$31）。

| 選項 | 每月約 | 動漫模型 | 結論 |
| :--- | :--- | :--- | :--- |
| Comfy Cloud Standard（月繳） | NT$640 | 只能用內建；官方支援模型清單沒有 Illustrious、NoobAI、Animagine、Pony，動漫類只看到 Anima、NetaYume 這類非 SDXL 的新架構模型 | 跟知識庫的 tag 相容度沒把握 |
| Comfy Cloud Creator（年繳） | NT$890 | 可從 Civitai 匯入 | 要先付 US$336（約 NT$10,700），還沒驗證就綁一年 |
| Comfy Cloud Creator（月繳） | NT$1,110 | 可匯入 | 超出預算 |
| **RunPod Serverless＋worker-comfyui** | 估計 NT$150–400（用多少付多少，§7） | 自己放進映像檔；先用 NoobAI-XL 1.1 | **先試這個** |

選 RunPod 的理由：預算內用得到知識庫動漫提示詞最多的那一系（NoobAI 是 Illustrious 的 finetune）；預付制，餘額就是花費上限；RunPod 的 GitHub 整合從 repo 的 Dockerfile 建置映像檔，不需要本機 Docker。代價是多一份映像檔要維護，以及閒置後第一張要多等冷啟動（第三方估計 20–60 秒，spike 量實際數字）。

試用的東西都在 repo 裡：[`render/runpod/`](../render/runpod/)（Dockerfile 與部署步驟）、[`render/workflows/txt2img-sdxl.json`](../render/workflows/txt2img-sdxl.json)、[`scripts/render_spike.py`](../scripts/render_spike.py)。

2026-10-09 已部署：透過 Runpod 的 MCP connector，模型放在 US-IL-1 的網路磁碟上，endpoint 用官方 base 映像檔（README 的做法 A），不必經過 GitHub 建置。實測見 §9.1。

---

## 3. Comfy API v2 重點

依官方 `openapi-v2.yaml`（v2.0.0）整理，只列這個專案會用到的：

| 項目 | 內容 |
| :--- | :--- |
| Base URL | Comfy Cloud `https://cloud.comfy.org`；專屬部署 `https://{deployment}.run.comfy.app`；本機 proxy `http://127.0.0.1:8189` |
| 認證 | `Authorization: Bearer <api-key>`；本機 proxy 預設不驗證 |
| 送出 | `POST /api/v2/jobs`，body `{ "workflow": <API 格式的 graph>, "extra_data"?: {...} }`，回 `201` 與 Job。UI 格式（有 `nodes`／`links` 的那種匯出）直接拒絕（`workflow_format_ui`）。`inputs`、`webhook_url` 欄位**還沒開放**，所以提示詞與 seed 要我們自己寫進 graph 的節點 |
| 冪等 | header `Idempotency-Key`（建議 UUID）。**只能用一次**：同一把 key 再送一律 `422 idempotency_key_reuse`，不會重播結果。送出結果不明（逾時、5xx）時 key 仍被占用，規格要求**去查工作，不要重送** |
| 輪詢 | `GET /api/v2/jobs/{id}`：`status`（`queued` → `running` → `succeeded`／`failed`／`expired`，取消是 `canceling` → `canceled`）、`queue_position`、`progress`、`outputs[]`、`metrics`（`queue_ms`、`execution_ms`） |
| 即時事件 | `GET /api/v2/jobs/{id}/events`（SSE）。規格明說只是加強體驗，輪詢才是準的 |
| 取消 | `POST /api/v2/jobs/{id}/cancel` |
| 取圖 | `outputs[].id` 是 asset id。`GET /api/v2/assets/{id}/content` 要帶 key；另有不用 key 的簽名網址，Cloud 上約 6 小時失效 |
| 錯誤 | `402 insufficient_credits`；`422 invalid_workflow`（附逐節點細節）；`429` 分 `queue_full`、`rate_limited` 等，附 `Retry-After` |
| 沒有的 | .NET SDK（只有 Python、TypeScript，且標 beta）、webhook |

**對我們重試層的影響**：現在 [`ResilientChatCompletion`](../src/PromptCopilot.Api/Llm/ResilientChatCompletion.cs) 遇到傳輸錯誤就重送。生圖 client 不能照抄：送出那一步結果不明時要查工作而不是重送，否則會重複扣 credits，而且第二次還會被 `422` 擋下。

### 3.1 RunPod Serverless 的差異

依 RunPod 官方文件與 [worker-comfyui](https://github.com/runpod-workers/worker-comfyui) README（5.x）整理：

| 項目 | 內容 |
| :--- | :--- |
| Base URL | `https://api.runpod.ai/v2/{endpoint_id}` |
| 認證 | `Authorization: Bearer <RunPod API key>` |
| 送出 | `POST /run`，body `{"input": {"workflow": <API 格式的 graph>}}`，立刻回 job id。另有 `/runsync`（等結果，結果只留 1 分鐘），不用 |
| 輪詢 | `GET /status/{id}`：`IN_QUEUE` → `IN_PROGRESS` → `COMPLETED`／`FAILED`／`CANCELLED`／`TIMED_OUT`；回應帶 `delayTime`（排隊＋冷啟動）與 `executionTime`（毫秒）。`/run` 的結果留 30 分鐘 |
| 取圖 | 不必另外下載：`output.images[]` 直接帶 base64（沒設 S3 時） |
| 取消 | `POST /cancel/{id}` |
| 時限 | `executionTimeout` 預設 10 分鐘、`ttl`（含排隊的總壽命）預設 24 小時，可在 endpoint 或單一請求設定 |
| 冪等 | 沒有 `Idempotency-Key`：送出失敗或結果不明時一樣不重送，查工作狀態 |
| 同時數 | 由 endpoint 的 Max Workers 決定；多出來的工作在 RunPod 排隊 |
| 計費 | worker 從啟動算到停止（載入模型、執行、idle timeout），四捨五入到秒；拉映像檔不計費 |

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

暖機狀態下一圈（生圖 → 審圖＋自評 → 模型決定）估計 10–20 秒，兩圈約 30–50 秒；如果在同一輪裡等，已經逼近 120 秒逾時。**冷啟動或排隊是最大的變數**：Standard 方案的 API 併發只有 1，同時有兩個人在用，第二個就要排隊。所以後端不在對話輪裡等圖（§6）。

不管哪種形狀都要做的保護：

- 每張圖有自己的逾時（例如 45 秒，從送到 Comfy 開始算；在我們自己佇列裡的等待由准入控制管，§6.2）。逾時就取消工作，降級成直接定稿，不讓整輪失敗。
- `429 queue_full`、`402 insufficient_credits` 一樣降級成「這次不生圖」。
- 前端顯示排第幾、生成中 x%（輪詢拿得到 `queue_position` 與 `progress`），等待看得見。

### 5.2 兩種形狀

| | A. 自主閉環（提案 §6.1 的目標） | B. 定稿後生圖 |
| :--- | :--- | :--- |
| 流程 | 模型自己決定：草稿 → 生圖 → 自評 → 改 → 再生 → 定稿。等圖不在對話輪裡，生圖回來由伺服器觸發下一輪接著做（§6.4） | 定稿照現在一樣先出；之後生圖、審圖、自評，結果補在定稿卡上；卡上有「依自評修正」按鈕，按下去是一個新的動手輪 |
| 誰決定修正 | 模型（真正的「做 → 看 → 改」） | 使用者按按鈕 |
| 延遲 | 每圈等一次排隊加生圖；使用者看到的是連續的事件，不是一輪卡住 | 定稿不變慢；圖晚幾秒補上 |
| 失敗的影響 | 每個伺服器觸發的輪各自是交易；生圖失敗或逾時降級成直接定稿 | 跟[整套組合推薦](superpowers/specs/2026-09-25-set-recommendations-design.md)一樣在 final 之後跑、自己的逾時、失敗只記 audit，不回滾 |
| 改動範圍 | 工具、閘門、預算、system prompt、新的輪種類、session 事件流、前端 | client、佇列、審圖、自評、端點、session 事件流、前端；不動工具清單與閘門 |

**建議先做 B，再做 A。** B 用到的元件（client、工作流範本、審圖、自評、圖片端點、前端顯示）A 全部沿用，等於先把風險最高的外部依賴接穩；B 跑出來的延遲與自評準確度，正好決定 A 的預算與逾時要設多少。只是 B 本身還不是 agent（修正由使用者觸發），README 改定位（提案 §8 階段 6）要等 A 做完。

spike 數字夠好的話（§9 的門檻），可以跳過 B 直接做 A。

---

## 6. 後端架構：生圖是排隊的工作

### 6.1 為什麼不在對話輪裡等

現在的一輪是：一個 `POST /messages` 連線、拿 session 鎖、120 秒逾時的交易；逾時用的是連線本身的取消信號（`http.RequestAborted`），斷線就取消並回滾（[`SessionEndpoints`](../src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs)）。在輪裡等 GPU 有三個問題：

1. **排隊時間全算在對話上**：等候時間計入 120 秒逾時，而且等的時候 session 一直鎖著。
2. **錢白花**：使用者等太久重新整理，這一輪就回滾，但圖已經生了、credits 已經扣了。
3. **看起來像當機**：同時呼叫數只有 1 時，第二個使用者的整輪卡住，畫面沒有任何反應。

所以拆成兩條時間線：**對話輪**短、是交易、持有鎖；**生圖工作**長、排隊、不持有鎖。兩條線之間用事件接起來。

### 6.2 元件

```text
POST /messages（一輪，SSE）            GET /sessions/{id}/events（長連線 SSE）
      │                                        ▲
      ▼                                        │ render_queued / progress / ready / reviewed
 TurnRunner ──排一個生圖──► RenderQueue ──► Dispatcher（槽位數 = 方案的同時工作數）
 （交易、持鎖）  立刻返回      （自己的佇列）        │
      ▲                                           ▼
      │                                   ComfyBackend（v2：送出 → 輪詢 → 取圖）
      │                                           │
      └──── 伺服器觸發的下一輪（A 才有）◄──── 審圖 ∥ 自評（Gemini，另一組限流）
```

| 元件 | 做什麼 |
| :--- | :--- |
| `RenderJob` | 狀態：`Queued`（在自己的佇列）→ `Submitted` → `Running` → `Reviewing` → `Ready`，或 `Failed`／`Canceled`／`Stale`。帶著 sessionId、排隊當下的 session 狀態版本、提示詞與 seed、優先級、`Idempotency-Key`、Comfy 的 job id、各段耗時 |
| `RenderDispatcher` | `BackgroundService`＋`Channel`；`SemaphoreSlim` 的槽位數等於後端的同時工作數（Comfy Cloud Standard 1、Creator 3；RunPod 是 endpoint 的 Max Workers），工作在遠端結束才釋放 |
| 排程規則 | 每個 session 同時最多一個在跑、各 session 輪流取；互動優先於 eval；預估等待（前面的工作數 × 平均耗時 ÷ 槽位數）超過門檻（建議 60 秒）就直接降級，不讓使用者乾等；同一個 session 排了新的生圖或使用者送出新訊息時，取消它還在排隊的舊工作 |
| `ComfyBackend` | 送出、輪詢、取圖、取消；`429` 照 `Retry-After` 退避；送出結果不明時查工作、不重送（§3） |
| 後處理 | 取圖後審圖與自評平行跑；兩者都打 Gemini，另配一組限流（Gemini 也有每分鐘呼叫上限）。圖片存在 session、經我們的端點給前端（§4） |
| Session 事件流 | 新端點 `GET /api/sessions/{id}/events`，長連線 SSE，沿用 [`SseWriter`](../src/PromptCopilot.Api/Streaming/SseWriter.cs) 的格式；每個 session 保留一小段環狀緩衝並編事件 id，斷線重連用 `Last-Event-ID` 補 |

### 6.3 跟連線池的差別

槽位那部分本身就是一個池（一個 `SemaphoreSlim(N)`），但池管不住 Comfy 的上限：v2 送出就回 `201`，工作在 Comfy 那邊繼續跑，沒有連線被占著。連線池只開 1 條也能在一秒內連送 10 個工作；反過來 1 個工作在跑時，可能有好幾個請求在輪詢它。方案限制的是同時跑的工作數，只能追蹤每個工作的狀態、在它結束時才釋放槽位。底下的 `HttpClient` 照樣用連線池處理送出、輪詢、取圖這些短請求，兩者是不同層。

| | 連線池 | 生圖工作佇列 |
| :--- | :--- | :--- |
| 管什麼 | 可重複使用的連線 | 供應商的執行額度 |
| 誰在等 | 呼叫端 `await`，算在呼叫端的請求裡 | 沒人卡住；排隊的是工作記錄 |
| 呼叫端斷線 | 什麼都沒留下 | 工作還在，結果之後從事件流送達 |
| 什麼時候釋放 | 呼叫端用完歸還 | 輪詢看到遠端工作結束；送出後結果不明的也要當成占用中 |
| 重試 | 重連通常無害 | 重送會重複扣錢，`Idempotency-Key` 只能用一次 |

Comfy 那邊也有自己的佇列（還會回 `queue_position`），但它不知道哪些工作屬於同一個 session、哪個是 eval、哪個已經過時，排滿就回 `429`；工作送進 Comfy 後取消只能盡力而為，還在我們佇列裡時取消則是零成本。所以自己這端要多一層。

NovelAI（§2.1）是同步 API，請求結束就代表圖生完了，一個 `SemaphoreSlim(1)` 就能守住上限；條款擋掉自主生圖之後，這個簡化也用不上。

### 6.4 Agent 迴圈怎麼接

**B（定稿後生圖）**：`FinalizePrompt` 成功後排一個生圖工作，跟推薦一樣在 final 之後、失敗不回滾；結果從事件流補到定稿卡。「依自評修正」是一般的動手輪，自評結果像確認句一樣帶進去。

**A（自主閉環）**：

- 生圖工具只排隊、立刻回；這一輪以新的終止結果「等生圖」收尾並提交（草稿狀態存起來）。
- 工作進入 `Ready` 後，Dispatcher 觸發一個新種類的輪（`TurnKind.Observe`），由伺服器觸發、不是使用者：一樣拿鎖、一樣有快照與回滾；使用者訊息由伺服器組一句，跟採用句、確認句的做法一樣；工具只有改 tag、再生圖、定稿。
- 預算算在「一個使用者要求」上（例如最多 3 張），存在 session，不是每輪各算。
- 版本戳：工作回來時 session 版本已經變了（使用者中途又改了要求），就標成 `Stale`，不觸發下一輪，只把圖顯示成「舊版預覽」。
- 事件流超過 N 分鐘沒有訂閱者，就停在「等生圖」，不再自動生下一張，免得沒人看還一直燒額度。
- [`AgenticOrchestrator`](../src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs) 要把「跑一輪」從 HTTP 請求裡抽出來，讓使用者與事件都能觸發；伺服器觸發的輪，取消來源是工作本身與 session 過期，不是連線。

### 6.5 讓有限的槽位做更多事

瓶頸是同時工作數，不是 GPU 秒數：

- **一個工作出多張**：`EmptyLatentImage` 的 `batch_size` 設 2–3（不同 seed），只占一個槽位；自評看「多數 seed 都有」才算過，順便減少靠 seed 運氣的雜訊。
- **迴圈用草稿畫質**：「看 → 改」用少步數、Lightning 類或小尺寸，定稿才出完整畫質。草稿與完整版內容是否一致，spike 時確認。
- **快取**：workflow、提示詞、seed 都相同就直接回舊圖。
- **盡早取消**：決定定稿或使用者改主意時立刻取消，讓出槽位。

量級（假設一個工作 8 秒，待 spike 實測）：Standard 方案每分鐘約 7 個工作；一個要求平均 2 個工作，每分鐘約 3–4 個要求；排第 k 位要等約 k × 8 秒。demo 夠用，同時 5 人以上就會明顯排隊，所以准入控制一定要有。

### 6.6 分階段做，不必一開始做滿

| 階段 | 做法 |
| :--- | :--- |
| B | Semaphore＋記憶體裡的工作記錄＋session 事件流。生圖本來就在 final 之後，不占對話輪 |
| 同時使用人數變多 | 加每個 session 的公平、准入控制、預估等待 |
| A | 加 `TurnKind.Observe`、版本戳、取消與取代 |

不建議：為這個加 Redis／RabbitMQ（現在只有一個 instance、session 在記憶體裡，in-process 的 `Channel` 就夠；要水平擴充時，現有的 PostgreSQL 可以當工作表，用 `FOR UPDATE SKIP LOCKED` 取工作、`LISTEN/NOTIFY` 推事件，但前提是先做提案 §6.5 的 session 持久化）；改用 WebSocket（只需要伺服器單向推送，SSE 就夠，前後端也已經有 SSE 的程式）。

---

## 7. 成本

| 項目 | 數字 | 來源 |
| :--- | :--- | :--- |
| Standard 方案 | $20／月，4,200 credits；API 併發 1 | Comfy 價格頁 |
| Creator 方案 | $35／月，7,400 credits；API 併發 3；可從 Civitai／Hugging Face 匯入模型 | Comfy 價格頁、匯入模型文件 |
| GPU 消耗 | 只算實際執行，約 0.266 credits／秒（舊資料寫 0.39，2026-01 降價過） | Comfy 支援文章 |
| 換算 | 4,200 credits ≈ 每月 4.4 GPU 小時 | 計算 |

**估算**（單張 GPU 時間未實測）：一張 SDXL 若花 5–10 秒 GPU，就是 1.3–2.7 credits，約 $0.006–0.013；Standard 方案一個月約 1,500–3,000 張。一次定稿生兩張，加上審圖與自評的 Gemini 呼叫，約 $0.02–0.03。eval 若 30 個劇本、每個生兩張、每週跑一次，一個月約 240 張，額度綽綽有餘。

**RunPod（試用中）**：24 GB 的 L4、A5000、3090 每小時 US$0.69，24 GB PRO 的 4090 每小時 US$1.10；網路磁碟每 GB 每月 US$0.07。估算（未實測）：用 4090、每月 500 張、每張 6 秒執行加 5 秒 idle timeout，再加約 80 次冷啟動，約 US$3–4；用量變成三倍也還在預算內。`scripts/render_spike.py` 的報表會依實測秒數重算每張費用與每月 500 張的台幣金額，準確數字以 RunPod 帳單為準。Comfy 的價格頁沒寫是否含稅，加上海外刷卡手續費，兩家都建議抓一成緩衝。

**API 沒有認證，要自己設上限。** 這個專案的 API 誰都能打（[`SafetyOptions`](../src/PromptCopilot.Api/Configuration/Options.cs) 的註解也提到這點），公開 demo 時任何人都能燒 credits。方案額度用完會回 `402`，是天然上限，但要再加每個 session 與每天的生圖上限，用完就降級成不生圖。

---

## 8. 風險與待確認

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

## 9. 建議的 spike

目標：用數字回答上面的未知數。2026-10-09 起改在 RunPod 上做（§2.2），花費預估幾美元。

1. 照 [`render/runpod/README.md`](../render/runpod/README.md) 部署 endpoint：RunPod 的 GitHub 整合從 `render/runpod/Dockerfile` 建置（worker-comfyui 5.10.0 加 NoobAI-XL 1.1），API key 與 endpoint id 只放 `.env`。
2. 跑 [`scripts/render_spike.py`](../scripts/render_spike.py)：直接打 RunPod 的 REST 端點（正式實作的 C# 也是直接打 REST），送出 → 輪詢 → 取圖，一次一張。先跑 `--runs 4`（五個動漫提示詞各四張、共 20 張），記 `delayTime`、`executionTime`、來回總時間、P50／P95；隔 30 分鐘以上、worker 縮回 0 之後再跑一次，量冷啟動。跑前跑後各看一次 RunPod 餘額，對照腳本的估計。
3. 看 spike 的圖：每個提示詞要確認的要素寫在報表最後，先人工判讀，再用 Gemini 做 facet 自評，算一致率（自評的腳本之後補）。
4. 同一批圖跑看圖審查，看有沒有誤判或漏判。

原本規劃在 Comfy Cloud 上做的版本（開 Standard 方案、打 v2 端點、記 `queue_ms`／`execution_ms` 與 credits）保留為換回 Comfy Cloud 時的做法。

### 9.1 第一輪實測（2026-10-09）

Runpod endpoint（4090、US-IL-1、模型在網路磁碟上），用 `render/workflows/txt2img-sdxl.json`（832×1216、28 步）跑 `render_spike.py` 的四個提示詞，各一張。透過 MCP 送出，輸出另外縮成 64×96 只為了讓回傳的 base64 夠小，生成本身照常是全尺寸。

| 提示詞 | 情況 | delayTime | executionTime |
| :--- | :--- | ---: | ---: |
| rain-neon | 第一張（endpoint 剛建好，冷啟動） | 8.8 秒 | 24.9 秒（含第一次從磁碟載入模型） |
| sakura | 隔約兩分鐘再送，FlashBoot 復原 | 0.5 秒 | 5.0 秒 |
| cafe | 跟上一張一起送，排在後面 | 6.1 秒 | 4.8 秒 |
| lake-sunset | 同上，排第三 | 10.7 秒 | 5.0 秒 |

- **暖機時每張約 5 秒**，冷啟動加上第一次載入模型，第一張約 34 秒，落在第三方估計的 20–60 秒內。比暖機時慢約 7 倍，所以使用者開始對話時先暖機（§6.4）值得做。
- **同時數 1 的排隊效應看得到**：三張一起送，第三張等了 10.7 秒，就是前兩張的執行時間，跟 §6.5 的「排第 k 位等 k × 單張時間」一致。
- **單張費用**：4090 每小時 US$1.10，暖機時每張約 5 秒執行加 5 秒 idle timeout，約 US$0.003；每月 500 張約 US$1.5（約 NT$50），加網路磁碟 US$1.05，遠低於每月 NT$1,000 的預算。帳單在實測當下還沒入帳，這裡是依價目表與實測秒數估算。
- **四張縮圖都對得上提示詞的主體**（銀髮雨衣霓虹雨夜、櫻花、咖啡廳、湖景夕陽）。縮圖太小，判斷不了細節，自評一致率要等全尺寸的圖。

還沒做的：`--runs 4` 共 20 張的 P50／P95、閒置 30 分鐘以上的冷啟動、全尺寸圖的人工判讀與 Gemini 自評、看圖審查（§9 的第 2–4 步）。

**判斷門檻**（建議值，可以調）：

| 量到的結果 | 下一步 |
| :--- | :--- |
| 單張（含取圖）P50 ≤ 15 秒、P95 ≤ 40 秒 | 可以直接做 A（自主閉環） |
| 比上面慢 | 先做 B（定稿後生圖），A 等換專屬部署或調整工作流（例如少步數、小尺寸）後再評估 |
| 自評一致率 ≥ 80% | 定稿閘門可以依自評擋回 |
| 自評一致率 < 80% | 自評只顯示，不擋定稿 |

---

## 10. 對既有文件的影響（定案後才改）

| 文件 | 要改的地方 |
| :--- | :--- |
| [Agent 化提案](Agent化提案.md) | §6.1 的生圖後端表格：「託管的 SDXL API」與「本機 ComfyUI」合併成「ComfyUI v2 API（Comfy Cloud 為主，本機經 comfy-api-proxy 為選配）」；§9 第 2 項標為已回答 |
| [主規格](superpowers/specs/2026-09-21-genai-prompt-copilot-design.md) | §2.2 移除「實際生成圖片」；§4.2 工具表；§4.3 工具清單規則 |
| [SK 架構說明](SK架構說明.md) | 四層圖加上生圖 client（跟 embedding 一樣不走 SK）；審圖與自評的 `ImageContent` 用法 |
| [README](../README.md) | 生圖的設定方式、費用、預覽圖不轉存的說明 |
| [資料來源](資料來源.md) | 預覽圖是暫存、不進知識庫與種子；跟「上游圖片一律不轉存」是兩回事 |

---

## 11. 需要專案擁有者決定的事

已決定（2026-10-09）：

- 生圖後端走 ComfyUI，不用 NovelAI（§2.1）。
- 只用雲端、風格偏動漫、每月 NT$1,000 內；先在 RunPod Serverless 上試，模型先用 NoobAI-XL 1.1（§2.2）。

還沒決定的：

1. **順序**：先做 B（定稿後生圖）再做 A（自主閉環），還是等 spike 數字直接決定。
2. **公開 demo 的上限**：每個 session、每天各幾張（每月總額已定為 NT$1,000 內）。
3. **降級門檻**：排隊預估超過幾秒就不生圖（建議 60 秒，§6.2）。
4. **eval 與 demo**：共用同一個 endpoint（eval 優先級較低），還是 eval 排在離峰。
5. **spike 之後**：RunPod 的冷啟動與費用可以接受就留在 RunPod；不行再評估 Comfy Cloud（內建模型或 Creator）。

---

## 來源

- [Comfy API v2 OpenAPI 規格](https://docs.comfy.org/openapi-v2.yaml)、[v2 概覽](https://docs.comfy.org/api-reference/v2/overview)、[SDK 說明](https://docs.comfy.org/development/api-development/sdks)、[快速開始](https://docs.comfy.org/development/api-development/quickstart)
- [v1 Cloud API 概覽（deprecated）](https://docs.comfy.org/development/cloud/overview)、[v1 API 參考](https://docs.comfy.org/development/cloud/api-reference)
- [Comfy 價格頁](https://comfy.org/pricing/)、[How credits work in Comfy](https://support.comfy.org/articles/5846341390-how-credits-work-in-comfy)、[Comfy Cloud 價格調整公告](https://blog.comfy.org/p/comfy-cloud-new-features-and-pricing)
- [Comfy Cloud 匯入模型](https://docs.comfy.org/cloud/import-models)、[SDXL Base 1.0 支援模型頁](https://comfy.org/p/supported-models/sd-xl-base-1-0)
- [Comfy 服務條款](https://www.comfy.org/terms-of-service)
- [RunComfy Serverless 計費](https://docs.runcomfy.com/serverless/about-billing)、[RunPod Serverless 計費](https://docs.runpod.io/serverless/pricing)、[runpod/worker-comfyui](https://hub.docker.com/r/runpod/worker-comfyui)
- RunPod：[價格](https://www.runpod.io/pricing)、[送出請求與狀態](https://docs.runpod.io/serverless/endpoints/send-requests)、[GitHub 整合](https://docs.runpod.io/serverless/github-integration)、[worker-comfyui（GitHub）](https://github.com/runpod-workers/worker-comfyui)
- 模型：[NoobAI-XL 1.1](https://huggingface.co/Laxhar/noobai-XL-1.1)（FAIPL-1.0-SD）；備選 [Illustrious-XL v2.0](https://huggingface.co/OnomaAIResearch/Illustrious-XL-v2.0)（CreativeML OpenRAIL-M）、[Animagine XL 4.0](https://huggingface.co/cagliostrolab/animagine-xl-4.0)（OpenRAIL++）
- [Comfy 支援模型清單](https://comfy.org/p/supported-models)
- [Semantic Kernel Google connector：`GeminiRequest.cs`](https://github.com/microsoft/semantic-kernel/blob/main/dotnet/src/Connectors/Connectors.Google/Core/Gemini/Models/GeminiRequest.cs)（`ImageContent` 轉 `inlineData`）
- NovelAI：[Image Generation API 文件](https://image.novelai.net/docs/index.html)（規格 `doc.json`）、[Primary API 文件](https://api.novelai.net/docs/)、[服務條款](https://novelai.net/terms)、[Diffusion V5](https://novelai.net/v5)、[Opus 使用上限說明](https://journal.novelai.net/opus-usage-limit-explained/)、[V5 API 實測（dev.to）](https://dev.to/ilan_kim/calling-the-novelai-v5-api-directly-nai-diffusion-5-full-request-body-paramsversion-4310-133a)、[V5 Opus 用量實測（dev.to）](https://dev.to/ilan_kim/novelai-v5-on-opus-usage-limits-the-2026-09-21-subscription-anlas-reset-and-the-apinovelainet-567l)
