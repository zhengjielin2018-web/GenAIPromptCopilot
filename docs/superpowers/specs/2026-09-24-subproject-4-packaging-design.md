# 子專案 4：收尾與展示 — 設計規格

日期：2026-09-24
狀態：已定案，未實作
主規格：[2026-09-21-genai-prompt-copilot-design.md](2026-09-21-genai-prompt-copilot-design.md) §2.1 第 4 項、§13、§14 第 4 列
前端設計：[2026-09-24-frontend-sse-design.md](2026-09-24-frontend-sse-design.md) §2.5（nginx 反代同一條路徑）
資料來源：[docs/資料來源.md](../../資料來源.md)

---

## 1. 目標與範圍

讓看作品集的人 clone 下來、填一把 Gemini key、`docker compose up`，就能在瀏覽器裡跑完整段對話，而且知識庫是有資料的。同時把 CI、README、授權與資料來源說明補齊，讓 repo 本身就是展示品。

分兩個階段：**先把整個形狀做出來**（所有檔案到位、能 build），**再進真實測試**（fresh clone 從零起、截圖、CI 綠）。子專案 2 的效果調整維持延後。

### 1.1 做

- `docker/Dockerfile.api`、`docker/Dockerfile.frontend`、`docker/Dockerfile.seed`、`docker/nginx.conf`。
- `docker-compose.yml` 從只有 `db` 擴成 `db` → `seed` → `api` → `frontend` 四個服務。
- 知識庫種子：`scripts/export_seed.py` 匯出、GitHub Release 存放、`seed` 服務首次啟動匯入。
- `.github/workflows/ci.yml`：dotnet、frontend、python、docker 四個 job，不部署。
- 根目錄 `README.md`（繁中）與 `LICENSE`（MIT，只涵蓋程式碼）。
- `docs/資料來源.md` 新增「公開散布與免責聲明」一節。
- 署名補進產品：`GET /api/presets/{id}` 多回 `sourceRef`、`sourceUrl`；抽屜多一行出處連結。
- 主規格、`manual-tests/README.md`、`.env.example` 同步。

### 1.2 不做

- Azure 或任何雲端部署；不推 image 到 registry。
- HTTPS、網域、反向代理以外的任何基礎設施。
- Session 持久化（主規格 §2.2）。
- CI 跑整合測試（`PC_INTEGRATION`、pytest `integration` marker 維持預設跳過）。
- Playwright／瀏覽器自動化；驗收仍是人工跑 eval。
- 對 `SearchPresets` 結果縮圖列加出處（縮圖點開就是抽屜，出處在抽屜顯示）。

## 2. 容器化

### 2.1 服務與相依

```text
db (pgvector/pgvector:pg16)
 └─ healthy ──► seed (一次性；空庫才灌 dump)
                 └─ completed_successfully ──► api (:5000 → 8080)
                                               └─ healthy ──► frontend (nginx :8080)
```

`docker compose up` 一條指令等到前端可用。相依用 `depends_on` 的 `condition` 表達：`service_healthy`（db、api）與 `service_completed_successfully`（seed）。

### 2.2 `api`

- 多階段 build：`mcr.microsoft.com/dotnet/sdk:10.0` 編譯與 `dotnet publish -c Release`，`mcr.microsoft.com/dotnet/aspnet:10.0` 執行。`Configuration/facets.yaml` 與 `Prompts/system.md` 已設 `CopyToOutputDirectory`，隨 publish 帶入。
- 環境變數（compose 由 `.env` 組出）：
  - `Llm__ApiKey=${GEMINI_API_KEY}`
  - `Database__ConnectionString=Host=db;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}`
  - `ASPNETCORE_ENVIRONMENT=Production`、`ASPNETCORE_URLS=http://+:8080`
- 對主機開 `5000`（映射到容器 8080），`manual-tests/chat.py` 與 Swagger 直連照用。
- Swagger 維持開啟（`Program.cs` 現況無條件註冊，不改）。
- healthcheck：`bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080'`，aspnet image 沒有 curl。間隔 5 秒、起始寬限 20 秒。
- 不再需要 user-secrets 才能跑；本機開發（`start_api.py`）的路徑不變。

### 2.3 `frontend`

- 第一階段 `node:22-alpine`：`npm ci` → `npx nuxi generate`（`ssr: false`，產出純靜態檔在 `.output/public`）。
- 第二階段 `nginx:alpine`：複製靜態檔與 `docker/nginx.conf`。對主機開 `8080`。
- `nginx.conf`：
  - `/api/`、`/health`、`/swagger` → `proxy_pass http://api:8080`，`proxy_buffering off`、`proxy_cache off`、`proxy_http_version 1.1`、`proxy_set_header Connection ''`、`proxy_read_timeout 180s`（單輪上限 120 秒，留退避空間）。
  - 其餘路徑 `try_files $uri /index.html`。
- 前端 `runtimeConfig.public.apiBase` 維持 `''`（同源），跟 devProxy 同一條路徑，不加 CORS。

### 2.4 `seed`

- `docker/Dockerfile.seed`：`FROM pgvector/pgvector:pg16`，`apt-get install curl`，複製 `docker/seed.sh` 當 entrypoint。用同一個底圖是為了 `pg_restore` 版本跟 db 一致。
- `seed.sh` 流程：
  1. `SEED_URL` 空 → 印「未設定 SEED_URL，跳過種子」退出 0。
  2. `psql -tAc "SELECT count(*) FROM prompt_knowledge_presets"` 非零 → 印「已有資料 N 筆，跳過」退出 0。
  3. `curl -fL --retry 3 -o /tmp/seed.dump "$SEED_URL"`。
  4. `pg_restore --data-only --no-owner --dbname=... /tmp/seed.dump`。
  5. 印出匯入後兩張表筆數。
  任一步失敗以非零退出：compose 停在這裡並印出原因，api 不會起來，不會留下半空的庫。重跑冪等（第 2 步）。
- 環境：`PGHOST=db`、`PGUSER`／`PGPASSWORD`／`PGDATABASE` 由 `POSTGRES_*` 映射；`SEED_URL=${SEED_URL-https://github.com/zhengjielin2018-web/GenAIPromptCopilot/releases/download/seed-v1/prompt_copilot_seed_v1.dump}`。用 `-` 而不是 `:-`：`.env` **沒有**這個變數時用預設值，**有但為空**時保持空字串（= 跳過種子）。
- `restart: "no"`。

### 2.5 `db`

現有設定不變：對主機開 `5432`（管線與本機開發用）、`db/init` 建 schema、healthcheck。

### 2.6 `.env.example`

加：

```text
# ---- docker compose 一鍵啟動 ----
# GEMINI_API_KEY 同時給 Python 管線與 api 容器（compose 映射成 Llm__ApiKey）
# SEED_URL：首次啟動灌進知識庫的 dump。不寫這行 = 用 compose 裡的預設 Release 資產；
# 想自己跑 seed_data.py 而不灌種子，把下一行的註解拿掉（設成空字串）
# SEED_URL=
```

`SEED_URL` 三種狀態：`.env` 沒這行 → compose 預設值；有這行且為空 → 跳過種子；有值 → 用該 URL。

## 3. 知識庫種子

### 3.1 匯出：`scripts/export_seed.py`

只用標準函式庫，透過 `docker compose exec -T db` 執行：

1. `CREATE DATABASE seed_export TEMPLATE prompt_copilot`（要求沒有其他連線；API 若在跑先停）。
2. 在 `seed_export` 裡 `DELETE FROM shared_prompt_histories WHERE source = 'user'`。使用者按「存進共享知識庫」的紀錄是個人測試資料，不公開。
3. `pg_dump -Fc --data-only -t prompt_knowledge_presets -t shared_prompt_histories seed_export` → 從容器複製到 `scripts/data/seed/prompt_copilot_seed_v<N>.dump`（`scripts/data/` 已在 `.gitignore`）。
4. `DROP DATABASE seed_export`，失敗也要 drop（`finally`）。
5. 印出兩張表筆數、`kisegae:` 與 `civitai:` 各幾筆、檔案大小，以及下一步的指令：

```text
gh release create seed-v<N> scripts/data/seed/prompt_copilot_seed_v<N>.dump --title "知識庫種子 v<N>" --notes "..."
```

不自動上傳。版本號用 `--version N` 參數給，預設 1。

`--data-only` 是刻意的：schema 仍由 `db/init/001_schema.sql` 單一來源，dump 只灌資料。`audit_logs` 不在 `-t` 清單裡，自然不會進去。

### 3.2 存放與版本

- GitHub Release，tag `seed-v1`，資產 `prompt_copilot_seed_v1.dump`。實測壓縮後約 104 MB（2026-09-24，presets 19,354 筆、histories 6,294 筆）。
- README 記錄：匯出日期、兩張表筆數、embedding 模型（`gemini-embedding-001`）與維度（768）。
- 換 embedding 模型或維度 = dump 與 `001_schema.sql` 的 `VECTOR(768)` 一起換版本，compose 預設 `SEED_URL` 跟著改。舊 dump 灌進新 schema 會在 `pg_restore` 階段失敗，不會靜默出錯。

### 3.3 免責聲明（`docs/資料來源.md` 新一節「公開散布與免責聲明」）

要點，README 摘要其中前四條並連到全文：

1. 種子 dump 是 Civitai 公開 API 與 Kisegaeningyou 的**衍生物**：原始 prompt 經 Gemini 重新詮釋為繁中描述與結構化片段，不是逐字轉載。
2. **圖片一律不轉存**：dump 只含指向上游的 URL，由使用者瀏覽器向上游請求；上游下架即失效。
3. Kisegaeningyou 上游**未標示授權**，`prompt_snippet` 保留了上游的服裝英文 tag；本專案以署名（產品內出處連結＋本文件）與非商業用途為據收錄。
4. 本專案為**非商業求職作品集**用途；來源方或權利人提出要求即移除對應資料並重發 dump。
5. dump 的授權狀態**不等於**程式碼的 MIT：`LICENSE` 只涵蓋本 repo 的程式碼與文件，不涵蓋 Release 資產。
6. 使用者透過「存進共享知識庫」寫入的紀錄不在公開 dump 裡（§3.1 第 2 步）。

## 4. 署名補進產品

`docs/資料來源.md` 原本就要求「顯示 `image_url` 時，依 `source_ref` 前綴一併顯示出處連結」，子專案 3 沒做。資料一旦公開散布，這條不能再拖。

### 4.1 API

- `PresetRepository.GetAsync` 多讀 `source_ref`。
- `PresetDetail` 多兩個欄位：`SourceRef`（原字串，可為 null）、`SourceUrl`（可為 null）。
- `SourceUrl` 由伺服器算，放在 `Data/SourceAttribution.cs` 的靜態純函式 `UrlFor(string? sourceRef)`：
  - `civitai:<imageId>:<idx>` 或 `civitai:<imageId>` → `https://civitai.com/images/<imageId>`
  - `kisegae:<...>` → `https://github.com/hayde0096/Kisegaeningyou`
  - null、空、未知前綴 → null
- Swagger 描述加這兩個欄位。`tool_result.presets` 與 `PresetHit` 不動。

### 4.2 前端

- `types/api.ts` 的 `PresetDetail` 加 `sourceRef: string | null`、`sourceUrl: string | null`。
- `PresetDrawer` 底部那行「圖片來自來源網站，本服務不轉存。」改成：`sourceUrl` 非 null 時顯示「出處：<來源名稱>（連結，新分頁）。圖片不轉存。」，來源名稱依 `sourceRef` 前綴對到「Civitai」或「Kisegaeningyou」；為 null 時維持原句。
- 出處名稱的對照放在 `lib/copy.ts`（純函式，已有的文案模組），可測。

### 4.3 測試

- xUnit `SourceAttributionTests`：civitai 三段式、civitai 兩段式、kisegae、null、未知前綴。
- xUnit `ReferenceEndpointsTests`（既有）加：`GET /api/presets/{id}` 回應含 `sourceUrl`。
- vitest `copy.test.ts`：前綴 → 名稱的對照。

## 5. CI

`.github/workflows/ci.yml`，觸發 `push` 到 `master` 與所有 `pull_request`。四個平行 job：

| job | 環境 | 步驟 |
| :--- | :--- | :--- |
| `dotnet` | `ubuntu-latest`、`actions/setup-dotnet` 10.0.x | `dotnet restore` → `dotnet build src/PromptCopilot.sln -c Release --no-restore` → `dotnet test --no-build`。Integration 測試因沒設 `PC_INTEGRATION` 自動 Skip |
| `frontend` | `actions/setup-node` 22、npm cache、工作目錄 `src/PromptCopilot.Frontend` | `npm ci` → `npm test` → `npm run build` |
| `python` | `actions/setup-python` 3.12、工作目錄 `scripts` | `pip install -r requirements.txt` → `ruff check .` → `pytest`（`pyproject.toml` 已預設 `-m 'not integration'`） |
| `docker` | `ubuntu-latest` | `docker build -f docker/Dockerfile.api .` 與 `docker build -f docker/Dockerfile.frontend .`，不推、不起容器；用 `docker/build-push-action` 的 `push: false` 拿 GHA cache |

- `concurrency` 以 branch 分組、取消進行中的舊 run。
- 不部署、不發 Release（種子 Release 是手動的）。
- README 頂部放 workflow badge。

## 6. README 與文件同步

### 6.1 根目錄 `README.md`（繁中）

章節順序：

1. 專案名、一句話定位、CI badge。
2. 截圖兩張（`docs/images/chat.png`：對話流＋儀表板；`docs/images/final.png`：定稿卡）。真實測試階段拍，形狀階段先留檔名。
3. 架構圖：mermaid，依主規格 §3 畫（SPA ↔ API ↔ PostgreSQL，管線離線寫入），GitHub 直接渲染。
4. 技術對照表：Semantic Kernel（全 agentic function calling、filters、RAG）、ASP.NET Core（SSE、每輪交易式回滾）、PostgreSQL + pgvector（分維度檢索、GIN）、Python 管線、Nuxt 3（儀表板、tool call 可視化）。每列連到對應的 spec 章節或程式目錄。
5. 一鍵跑起來：`cp .env.example .env` 填 `GEMINI_API_KEY` → `docker compose up` → 開 `http://localhost:8080`。說明首次會下載約 100 MB 種子、Swagger 在 `http://localhost:5000/swagger`。
6. 本機開發：連到 `manual-tests/README.md`、`scripts/README.md`、`src/PromptCopilot.Frontend/README.md`。
7. 資料來源與免責聲明：§3.3 前四條摘要，連到 `docs/資料來源.md`。
8. 文件索引：specs、eval-cases、單輪流程說明。
9. 授權：程式碼 MIT；資料見上一節。

### 6.2 `LICENSE`

MIT，著作權人用 git 設定的名字。只涵蓋 repo 內容，README §9 與資料來源文件都寫明不涵蓋 Release 資產。

### 6.3 同步清單

| 文件 | 改動 |
| :--- | :--- |
| 主規格狀態列 | 子專案 4 的狀態與日期 |
| 主規格 §13 | `docker/` 內容（四個檔）、`LICENSE`、`README.md`、`scripts/export_seed.py`、`docs/images/` |
| 主規格 §10.1 | `GET /api/presets/{id}` 說明加「含出處」 |
| 主規格 §14 第 4 列 | 加本文件 §7 的驗收細項 |
| `docs/資料來源.md` | 新增「公開散布與免責聲明」；署名機制那段標記「已於子專案 4 實作」 |
| `manual-tests/README.md` | 開頭加「只想看 demo：`docker compose up`，見根目錄 README」 |
| `.env.example` | §2.6 |
| Swagger | §4.1 |

跟程式同一個 commit（記憶：動流程就要同步流程說明文件）。

## 7. 兩階段驗收

### 7.1 形狀階段（先做）

- 所有 §1.1 的檔案到位。
- `dotnet test` 與 `npm test` 綠（含 §4.3 新測試）。
- `ruff check scripts` 與 `pytest`（scripts）綠。
- 本機 `docker compose config` 合法、`docker compose build` 四個 image 都過。
- `scripts/export_seed.py` 在本機實跑一次，產出 dump 並印出上傳指令。
- **不**從零起整套，**不**上傳 Release，**不**拍截圖。

### 7.2 真實測試階段（形狀出來後）

| # | 操作 | 應該看到 |
| :--- | :--- | :--- |
| P1 | `gh release create seed-v1 …` 上傳 dump | Release 資產可公開下載，URL 與 compose 預設一致 |
| P2 | 暫存目錄 fresh clone，只放 `.env`（填 key），`docker compose up` | seed 印下載與匯入筆數；api、frontend 依序 healthy；`http://localhost:8080` 開得起來 |
| P3 | 瀏覽器跑 eval #1、#3、#6 | 跟子專案 3 驗收相同的行為；`SearchPresets` 的 `poolSize` 跟本機相近 |
| P4 | 開一張 preset 抽屜 | 底部有「出處：Civitai」連結；找一筆 kisegae 的看到「出處：Kisegaeningyou」 |
| P5 | Ctrl+C 後再 `docker compose up` | seed 印「已有資料 N 筆，跳過」，不重新下載 |
| P6 | `.env` 加一行 `SEED_URL=`（空字串），換新 volume 重起 | seed 印「未設定 SEED_URL，跳過」，api 照起，知識庫為空 |
| P7 | 截圖進 `docs/images/`，push 到 GitHub | CI 四個 job 綠，README badge 綠、截圖與 mermaid 正常渲染 |

結果記進 `docs/eval-cases.md`，主規格狀態列更新。P6 完成後把 volume 清掉重灌，不要留空庫。

## 8. 決定紀錄

| 項目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 知識庫公開範圍 | 整份，含 kisegae 377 筆 | 使用者選擇；靠產品內署名與免責聲明，非商業作品集用途 |
| 存放 | GitHub Release 資產 | 104 MB 不能進 git；LFS 的頻寬配額對會被 clone 的公開 repo 不友善 |
| 匯入 | 一次性 `seed` 服務，非 db 的 initdb 腳本 | 冪等、失敗看得見；initdb 失敗後 volume 已被標記完成，重跑不會再試，且會拖慢 db healthy |
| dump 形式 | `-Fc --data-only`，只含兩張知識表 | schema 單一來源不變；`audit_logs` 與使用者存檔不公開 |
| 匯出走暫時複製的資料庫 | 是 | `pg_dump` 不能過濾列，複製一份再刪 `source='user'` 最直接，本機 DB 不動 |
| CI 含 docker build | 是 | Dockerfile 唯一的自動驗證；多三到五分鐘 |
| 前端執行時 | nginx 靜態 | `ssr: false` 不需要 Node；nginx 同時做反代 |
| api 埠對主機開 | 是 | `chat.py` 與 Swagger 直連照用；demo 不需要藏 |
| 出處 URL | 伺服器算 | 前端不需要知道前綴規則；來源多一種只改一處 |
| 授權檔 | 程式碼 MIT，資料另述 | 兩者授權狀態不同，混寫會誤導 |
| README 語言 | 繁中 | 全部文件與 UI 都是繁中；受眾一致 |
| 截圖時機 | 真實測試階段 | 形狀階段沒有從零起的環境，拍本機開發版會跟 compose 版有差 |
