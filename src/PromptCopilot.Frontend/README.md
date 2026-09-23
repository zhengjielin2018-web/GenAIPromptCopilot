# PromptCopilot.Frontend

Nuxt 3 SPA（`ssr: false`）。對話流、六維度儀表板、preset 抽屜。

## 跑起來

需要 Node LTS 22。API 要先在 `http://localhost:5000` 跑著（`python manual-tests/start_api.py`）。

    npm install
    npm run dev          # http://localhost:3000；/api 與 /health 經 devProxy 轉到 API

devProxy 不會緩衝 SSE：2026-09-24 實測，一輪的事件在 12 秒內逐筆抵達，不是最後一次吐出。

## 測試

    npm test             # vitest：lib/ 底下的純函式（SSE 解析、reducer、persist、composer、dashboard）

`lib/` 不依賴 Nuxt，測試在 node 環境跑，不需要瀏覽器或 API。

## 結構

- `types/api.ts`：後端 DTO 與 SSE 事件型別，唯一定義處
- `lib/`：純函式（reducer、SSE 解析、persist、composer、dashboard、copy）
- `composables/useApi.ts`：HTTP 呼叫
- `stores/session.ts`：唯一的 Pinia store
- `components/`：畫面元件

設計：`docs/superpowers/specs/2026-09-24-frontend-sse-design.md`。
