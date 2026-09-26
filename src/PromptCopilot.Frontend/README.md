# PromptCopilot.Frontend

Nuxt 3 SPA（`ssr: false`）。對話流、六維度儀表板、preset 抽屜。

## 跑起來

需要 Node LTS 22。API 要先在 `http://localhost:5000` 跑著（`python manual-tests/start_api.py`）。

    npm install
    npm run dev          # http://localhost:3000；/api 與 /health 經 devProxy 轉到 API

devProxy 不會緩衝 SSE：2026-09-24 實測，一輪的事件在 12 秒內逐筆抵達，不是最後一次吐出。

## 測試

    npm test             # vitest：lib/ 底下的純函式（SSE 解析、reducer、persist、composer、dashboard、options、prefs、trace、adopt、safety、copy）

`lib/` 不依賴 Nuxt，測試在 node 環境跑，不需要瀏覽器或 API。

## 容器

`docker/Dockerfile.frontend`：`nuxi generate` 產靜態檔交給 nginx，`docker/nginx.conf` 反代 `/api`、`/health`、`/swagger` 到 api 容器且不緩衝 SSE。`runtimeConfig.public.apiBase` 維持 `''`，開發與容器走同一條同源路徑。

## 結構

- `types/api.ts`：後端 DTO 與 SSE 事件型別，唯一定義處
- `lib/`：純函式（reducer、SSE 解析、persist、composer、dashboard、copy、options、prefs、trace、adopt、safety）
- `composables/useApi.ts`：HTTP 呼叫
- `stores/session.ts`：唯一的 Pinia store
- `components/`：畫面元件

頂列的三個開關：「使用知識庫」（下一段新對話生效，存 localStorage）、「顯示檢索細節」（即時，存 localStorage）、「程式端審查」（後端 `GET /api/config/safety` 回 `canDisable: true` 才出現，不保存，重新整理回到開著）。

設計：`docs/superpowers/specs/2026-09-24-frontend-sse-design.md`；開關與檢索細節見 `2026-09-25-retrieval-switch-and-trace-design.md`，參考組合與採用見 `2026-09-25-set-recommendations-design.md`。
