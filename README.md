# GenAI Prompt Copilot

[![CI](https://github.com/zhengjielin2018-web/GenAIPromptCopilot/actions/workflows/ci.yml/badge.svg)](https://github.com/zhengjielin2018-web/GenAIPromptCopilot/actions/workflows/ci.yml)

用繁體中文描述想要的畫面，系統以六個維度判斷資訊夠不夠、每個會改動畫面的要求都先跟你確認理解、你按下確認後才動手，主動追問缺的細節、從知識庫推薦可用片段，最後產出 SD／SDXL tag 風格的英文正／負向提示詞，逐個 tag 標示來源（知識庫片段、採用的組合或模型生成）。每次定稿另外推薦知識庫裡真實存在、有圖的整套組合，使用者可逐項採用。定稿後可以生一張預覽圖（選配），系統拿你在對話裡提過的要求逐條看圖、算出 0–100 的符合度，分出是 prompt 漏寫還是圖沒畫出來，再建議下一步（補 prompt、改寫 tag 或換 seed），按一下照做。求職作品集專案：每個技術點都有看得見的實證，功能深度其次。

![對話流與六維度儀表板：確認卡、工具呼叫、追問卡，右側是各維度的涵蓋狀態](docs/images/chat.png)

| 定稿卡：tag 依來源標色 | 生成預覽：符合度評分與建議的下一步 |
| :---: | :---: |
| ![定稿卡](docs/images/final.png) | ![生成預覽與符合度評分](docs/images/render.png) |

## 架構

```mermaid
flowchart LR
    subgraph browser[瀏覽器]
        spa[Nuxt 3 SPA<br/>對話流・六維度儀表板・preset 抽屜]
    end
    subgraph api[ASP.NET Core Web API]
        guard[SafetyGuard<br/>輸入側]
        orch[AgenticOrchestrator<br/>SK Kernel・FunctionChoice.Auto]
        plugins[Plugins<br/>Knowledge／Dialog／Session]
        filters[Filters<br/>TerminalTool・ToolBudget<br/>OutputSafety・Audit]
        store[(SessionStore<br/>in-memory)]
        render[RenderService・RenderWorker<br/>背景佇列・額度]
        judge[審圖・符合度評分<br/>FixAdvisor 下一步]
        guard --> orch --> plugins
        orch --- filters
        orch --- store
        render --> judge
        render --- store
    end
    gemini[[Gemini<br/>對話・embedding・看圖]]
    runpod[[RunPod Serverless<br/>ComfyUI]]
    subgraph pg[PostgreSQL 16 + pgvector]
        presets[(prompt_knowledge_presets)]
        histories[(shared_prompt_histories)]
        audit[(audit_logs)]
    end
    subgraph py[Python 管線（離線）]
        pipe[Civitai API → clean → structure → embed → load]
    end
    spa -- "POST /messages (SSE)" --> guard
    spa -- "POST /renders、輪詢" --> render
    orch --> gemini
    judge --> gemini
    render --> runpod
    plugins --> presets
    plugins --> histories
    filters --> audit
    pipe --> presets
    pipe --> histories
```

一輪對話：使用者送一句話 → 輸入側安全過濾 → 模型在動態組出的工具清單裡自己決定要查知識庫、跟使用者確認、更新儀表板、追問、討論還是定稿 → 每個工具呼叫即時以 SSE 推到前端 → 該輪以一個終止型工具收尾。會改動畫面的要求一律分兩輪：打字的那一輪模型只拿得到確認、討論與檢索，使用者按下確認卡的按鈕後，下一輪才拿得到改狀態的工具（[先確認再動手設計](docs/superpowers/specs/2026-10-05-confirm-before-act-design.md)）。任何一步失敗整輪回滾，跟資料庫交易一樣。細節見[主規格 §4](docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md#4-核心編排全-agentic)；檢索與組裝的逐步拆解（以 Python 單輪 demo 實跑）見 [docs/單輪流程說明.md](docs/單輪流程說明.md)。

生成預覽不經過對話：定稿卡按下按鈕後請求進背景佇列，RenderWorker 組好 ComfyUI workflow 送 RunPod，同時請 Gemini 從對話整理出使用者的要求清單。圖回來先審圖，過了就先給前端看，再逐條判圖、由程式算分，最後 `FixAdvisor` 照固定規則決定建議的下一步。細節見[定稿後生成預覽](docs/superpowers/specs/2026-10-09-render-preview-design.md)、[符合度評分](docs/superpowers/specs/2026-10-10-intent-fit-scoring-design.md)、[修正建議](docs/superpowers/specs/2026-10-10-fix-suggestions-design.md)。

## 技術對照

| 技術 | 這個專案裡做了什麼 | 看哪裡 |
| :--- | :--- | :--- |
| Semantic Kernel | 全 agentic function calling；依輪的種類（確認輪／動手輪）與 session 狀態動態組工具清單，沒經使用者確認就拿不到改畫面的工具；`IAutoFunctionInvocationFilter` 做終止、預算、輸出安全、稽核四道 filter；兩層 RAG | `src/PromptCopilot.Api/Orchestration/`、`Plugins/`、`Filters/`；[主規格 §4](docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md#4-核心編排全-agentic) |
| ASP.NET Core | SSE 串流（`Channel<AgentEvent>` → `IAsyncEnumerable`）；每輪 snapshot／rollback；session 鎖 | `Streaming/`、`Sessions/`、`Endpoints/`；[主規格 §10](docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md#10-api-與-sse-協定) |
| PostgreSQL + pgvector | 分維度檢索：HNSW 先取近鄰、再套該維度的 facet 過濾，iterative scan 補足過濾後不足 k 筆的部分；候選池大小隨結果回報 | `db/init/001_schema.sql`、`Data/`；[檢索設計](docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md) |
| Python | 分階段、可重跑的資料管線：抓取→清洗→Gemini 結構化→向量化→載入；分層抓取解題材偏斜 | `scripts/`；[語料擴增設計](docs/superpowers/specs/2026-09-22-corpus-expansion-design.md) |
| Nuxt 3 | 純函式 reducer 消費 SSE；tool call 卡片與儀表板即時變燈；整頁重載恢復 | `src/PromptCopilot.Frontend/`；[前端設計](docs/superpowers/specs/2026-09-24-frontend-sse-design.md) |
| RunPod＋ComfyUI、Gemini 看圖 | `BackgroundService`＋`Channel` 佇列送 RunPod Serverless 上的 ComfyUI 生圖，每段對話與每天有張數上限、逾時取消；Gemini 審圖後逐條判斷使用者的要求，分數由程式算；`FixAdvisor` 是純函式，依規則決定先補 prompt、換 seed 還是改寫 tag | `Rendering/`、`render/runpod/`；[生成預覽](docs/superpowers/specs/2026-10-09-render-preview-design.md)、[符合度評分](docs/superpowers/specs/2026-10-10-intent-fit-scoring-design.md)、[修正建議](docs/superpowers/specs/2026-10-10-fix-suggestions-design.md) |
| 安全合規 | 輸入側 denylist + 分類器；輸出側對定稿、討論、追問、確認卡的文字與選項全檢；資料側 NSFW 過濾。測試用的關閉開關預設不開放（`SAFETY_ALLOW_DISABLE`） | `Safety/`、`Filters/OutputSafetyFilter.cs`、`scripts/pipeline/nsfw_filter.py` |

## 一鍵跑起來

需要 Docker Desktop 與一把 [Gemini API key](https://aistudio.google.com/apikey)。

```bash
cp .env.example .env        # 填 GEMINI_API_KEY；POSTGRES_PASSWORD 隨意改
docker compose up
```

首次啟動會下載約 250 MB 的知識庫種子灌進資料庫（見下方「資料來源」），之後：

- 前端 <http://localhost:8080>
- Swagger <http://localhost:5000/swagger>

不想灌種子、要自己跑管線：`.env` 加一行 `SEED_URL=`（空字串），再照 [scripts/README.md](scripts/README.md)。

- 沒填 `GEMINI_API_KEY`：整套照樣起來，但每一輪對話都會失敗（`docker compose logs api` 開頭有一行警告）。填好後 `docker compose up -d api` 重建 api 容器即可。
- 生成預覽圖（選配）：定稿卡上的「生成預覽」用 RunPod Serverless＋ComfyUI 生一張動漫風的圖，附看圖審查、「使用者想法符合度」評分與修正建議（按一下請助理補 prompt，或換 seed 重生）。先照 [render/runpod/README.md](render/runpod/README.md) 部署 endpoint，再在 `.env` 填 `RUNPOD_API_KEY`、`RUNPOD_ENDPOINT_ID`，`docker compose up -d --build api`；沒填就不顯示按鈕。每張約 US$0.0034（RunPod 預付制，餘額就是花費上限）；每個對話最多 10 張、全站每天 200 張（`Render` 設定節可調）。預覽圖只存在後端記憶體的對話裡，跟對話一起過期，不轉存、不進知識庫。設計見 [定稿後生成預覽](docs/superpowers/specs/2026-10-09-render-preview-design.md)。
- 重置知識庫：`docker compose down -v` 刪掉資料庫 volume，下次 `up` 重新建表並灌種子。
- 看一輪發生什麼事：`docker compose logs -f api`，每輪結束有一行 `Turn <session>#<turn> <事件> …`，每次呼叫 Gemini 有一行 `Gemini <狀態碼> …`。完整紀錄在資料庫的 `audit_logs`（查法見 [manual-tests/README.md](manual-tests/README.md) 第 5 節）。

## 本機開發

| 想做什麼 | 看哪裡 |
| :--- | :--- |
| 起 API、在終端機逐輪對話 | [manual-tests/README.md](manual-tests/README.md) |
| 跑前端 dev server | [src/PromptCopilot.Frontend/README.md](src/PromptCopilot.Frontend/README.md) |
| 跑資料管線、匯出種子 | [scripts/README.md](scripts/README.md) |
| 部署生圖用的 RunPod endpoint | [render/runpod/README.md](render/runpod/README.md) |
| 人工 eval 案例與歷次結果 | [docs/eval-cases.md](docs/eval-cases.md) |

測試：`cd src && dotnet test`、`cd src/PromptCopilot.Frontend && npm test`、`cd scripts && pytest && ruff check .`。整合測試需要 `PC_INTEGRATION=1` 與本機資料庫，CI 不跑。

## 資料來源與免責聲明

知識庫種子是 [Civitai 公開 API](https://civitai.com) 與 [Kisegaeningyou](https://github.com/hayde0096/Kisegaeningyou) 的**衍生物**：原始提示詞經 Gemini 重新詮釋為繁中描述與結構化片段，不是逐字轉載。**圖片一律不轉存**，只保留指向上游的 URL，由瀏覽器向上游請求；產品內顯示圖片時一併顯示出處連結。Kisegaeningyou 上游未標示授權，本專案以署名與非商業用途為據收錄。本專案為非商業求職作品集，來源方或權利人提出要求即移除對應資料並重發種子。

完整說明、匯入細節與已知取捨：[docs/資料來源.md](docs/資料來源.md)。

## 文件

- [主規格](docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md)：目標、架構、編排、facet 體系、安全、資料模型、API 協定、測試策略
- 子專案設計：[語料擴增](docs/superpowers/specs/2026-09-22-corpus-expansion-design.md)、[分維度檢索](docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md)、[多輪對話](docs/superpowers/specs/2026-09-22-multi-turn-dialogue-design.md)、[批次 SearchPresets](docs/superpowers/specs/2026-09-24-batch-search-presets-design.md)、[前端與 SSE](docs/superpowers/specs/2026-09-24-frontend-sse-design.md)、[收尾與展示](docs/superpowers/specs/2026-09-24-subproject-4-packaging-design.md)、[知識庫開關與檢索細節](docs/superpowers/specs/2026-09-25-retrieval-switch-and-trace-design.md)、[整套組合推薦](docs/superpowers/specs/2026-09-25-set-recommendations-design.md)、[facet 層級向量](docs/superpowers/specs/2026-09-29-facet-vector-retrieval-design.md)、[推薦組法](docs/superpowers/specs/2026-09-30-recommendation-slate-design.md)、[先確認再動手](docs/superpowers/specs/2026-10-05-confirm-before-act-design.md)、[檢索時機](docs/superpowers/specs/2026-10-06-retrieval-timing-design.md)、[定稿後生成預覽](docs/superpowers/specs/2026-10-09-render-preview-design.md)、[符合度評分](docs/superpowers/specs/2026-10-10-intent-fit-scoring-design.md)、[修正建議](docs/superpowers/specs/2026-10-10-fix-suggestions-design.md)
- [ComfyUI 整合可行性](docs/ComfyUI整合可行性.md)：生圖後端怎麼選、RunPod 實測與費用、換模型為什麼暫緩
- [Agent 化提案](docs/Agent化提案.md)：下一步方向的提案（未定案），含現況跟一般 agent 定義的差距
- [SK 架構說明](docs/SK架構說明.md)：哪些是我們寫的、哪些是 Semantic Kernel 與 Google connector 的，一輪在 SK 裡怎麼跑，connector 的怪癖與補丁
- [單輪流程說明](docs/單輪流程說明.md)、[eval 案例](docs/eval-cases.md)、[資料來源](docs/資料來源.md)、[初步想法](docs/初步想法.md)
- [已知問題與待修清單](docs/known-issues.md)

## 授權

程式碼與文件採 [MIT](LICENSE)。知識庫種子（GitHub Release 資產）**不在** MIT 範圍內，其狀態見上一節。
