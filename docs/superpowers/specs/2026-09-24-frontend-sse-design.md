# 子專案 3：Nuxt 3 前端 + SSE — 設計規格

日期：2026-09-24
狀態：已定案，未實作
主規格：[2026-09-21-genai-prompt-copilot-design.md](2026-09-21-genai-prompt-copilot-design.md) §10、§11、§14 第 3 列
多輪設計：[2026-09-22-multi-turn-dialogue-design.md](2026-09-22-multi-turn-dialogue-design.md) §5.1、§5.4、§5.6、§11 最後一條

---

## 1. 目標與範圍

把子專案 2 的 headless API 接上一個能在瀏覽器裡跑完整段對話的介面：對話流、六維度儀表板、preset 抽屜、定稿與入庫。
作品集用途不變：每個技術點要有看得見的實證，功能深度其次。

### 1.1 做

- `src/PromptCopilot.Frontend/`：Nuxt 3（`ssr: false`）+ Tailwind + Pinia + vitest，元件自己寫，不用 UI 套件。
- 後端三個小改動：`GET /api/sessions/{id}`、`FinalizePrompt.intentSummary`、`OutputSafetyFilter` 欄位表加一格。
- 整頁重載後回得來：權威狀態從後端拿，對話流從 `sessionStorage` 拿。
- 前端回滾：跟後端「一輪是一個交易」對稱。

### 1.2 不做

- 打字機動畫。後端從不發 `token`（所有回覆內容都是終止型 tool 的參數，一次到位）。reducer 仍照 §10.2 處理 `token` 事件，但不對一次到位的文字做逐字揭露——那是演的。SSE 的即時感由「tool call 卡片一張張冒出、儀表板輪中變燈」展示。
- 後端持久化 session、對話歷史列表、刪除 session。session 仍只在 `IMemoryCache`、兩小時滑動過期（主規格 §2.2）。商品化的 session 生命週期是之後另一個設計題。
- API 加 CORS。開發用 Nuxt `devProxy`，子專案 4 用 nginx 反代同一路徑。
- Playwright／瀏覽器自動化。驗收是人工跑 eval。
- Markdown 渲染、i18n、鍵盤快捷鍵、深色模式切換（跟隨系統即可）。
- 動 `.github/`（子專案 4）。

### 1.3 前置條件

這台開發機目前沒有 Node／npm。要先裝 **Node LTS 22**；套件管理用 npm。

## 2. 後端契約變更

### 2.1 `GET /api/sessions/{id}`

唯讀，**不拿 session 鎖**。重載時不可能有一輪在跑：連線隨頁面一起斷，後端 `RequestAborted` 會回滾。兩個分頁共用同一個 id 的情況下讀到半途狀態是可接受的最壞情況。

```json
{
  "sessionId": "…",
  "status": "Collecting | Finalized",
  "profile": "portrait | landscape | object | vehicle | null",
  "turnIndex": 3,
  "askCount": 1,
  "askLimit": 2,
  "facetStates": { "style.genre": "covered", "scene.location": "missing", "…": "…" },
  "lastFinal": { "positive": "…", "negative": "…", "tips": "…", "intentSummary": "…" }
}
```

- `facetStates` 的值用 `FacetStateParser.ToWire` 的字串（跟 `dimensions` 事件一致）。
- `lastFinal` 未定稿時為 `null`。
- `404`：session 不存在或已過期，body 同 `messages` 的 `ErrorBody`。
- 不回 `ChatHistory`（SK 內部結構，含 tool 訊息、截到 10 輪）、不回 ledger（只給模型用）。

### 2.2 `FinalizePrompt.intentSummary`

`DialogPlugin.FinalizePrompt` 多一個必填參數：

```text
intentSummary：繁中一句話描述使用者這次的需求（題材、主要風格、場景），不含提問與閒聊，20–40 字
```

- 空白時回 `錯誤：intentSummary 不可為空`，跟 `positivePrompt` 同一套，讓模型重試。
- 存進 `FinalPrompt` record（`Positive, Negative, Tips, IntentSummary`）。
- `final` 事件 `kind: "finalized"` 多帶 `intentSummary`。
- 每次重新定稿摘要跟著更新；中途改題材自然反映在最後一次定稿。
- system prompt 加一段說明這個欄位的用途：它會成為共享庫的檢索鍵，要寫成「另一個使用者會怎麼描述同樣的需求」。

**為什麼放在 `FinalizePrompt` 而不是存檔時另外呼叫 LLM**：定稿當下的模型握有整段對話與 facet 狀態，零額外呼叫；`ChatHistory` 截到 10 輪，存檔時再摘要會漏掉前面的；少一份 prompt 要維護。

### 2.3 `OutputSafetyFilter`

多輪設計 §5.2 的欄位表加 `FinalizePrompt.intentSummary`。它會進共享庫，跟 `positivePrompt` 同一等級。

### 2.4 `save-to-shared` 不變

`intent` 仍由客戶端送。前端預填 `intentSummary`，使用者可改。摘要品質靠 prompt 不靠程式保證，護欄是「使用者可編輯」，跟定稿本身一樣。

### 2.5 跨埠

API 不加 CORS。Nuxt `nitro.devProxy` 把 `/api` 與 `/health` 轉到 `http://localhost:5000`。SSE 經 proxy 不得被緩衝——實作計畫列為獨立驗證項（主規格 §10.3 說這是最容易卡的點）。子專案 4 的 nginx 用同一條路徑反代，加 `proxy_buffering off`。

### 2.6 同步文件

`docs/單輪流程說明.md`、主規格 §4.2／§6.2／§10.1／§10.2、Swagger 描述，跟程式同一個 commit。

## 3. 前端架構與資料流

### 3.1 檔案

```text
src/PromptCopilot.Frontend/
├─ app.vue                      # 版面骨架：主區 + sticky 側欄 + 抽屜出口
├─ composables/useSse.ts        # fetch + ReadableStream，逐行解析 event:/data:，yield {type, data}
├─ composables/useApi.ts        # createSession / getSession / saveToShared / getFacets / getPreset
├─ stores/session.ts            # 唯一的 Pinia store；動作只做 I/O，狀態變更全走 reducer
├─ stores/reducer.ts            # applyEvent(state, event) → state，純函式，無 import 副作用
├─ stores/persist.ts            # sessionStorage 的存讀，只管 transcript 與 sessionId
├─ stores/composer.ts           # chip 累積與前綴組字，純函式
├─ components/                  # §4
└─ tests/                       # vitest
```

### 3.2 store 狀態

單一物件，方便 `structuredClone` 做快照：

```text
sessionId, turnIndex, status, profile
facetCatalog                 # GET /api/config/facets，開頁載一次
facetStates: {facetId: state}
askCount, askLimit           # GET session 拿；輪中不變
transcript: Entry[]          # 顯示用，每筆是 user | tool | final | failure 之一
lastFinal | null
pending: { text, snapshot } | null   # 這一輪送出的原文與送出前的快照
drawer: presetId | null
```

`Entry` 的四種：

| 種類 | 內容 | 來源 |
| :--- | :--- | :--- |
| `user` | `{ text }` | 使用者送出 |
| `tool` | `{ callId, name, argsSummary, summary?, presets?, done }` | `tool_call` 建立、`tool_result` 以 `callId` 配對填入 |
| `final` | `final` 事件的 `data` 原樣，依 `kind` 分四種 | `final` |
| `failure` | `{ reason \| code, message, originalText }` | `error`／`blocked`／串流異常結束 |

### 3.3 一輪的資料流

1. 使用者送出 → store 記 `pending = { text, snapshot: structuredClone(state) }`，推一筆 `user` 條目，開 `useSse` 連 `POST /messages`。
2. 每個事件進 `applyEvent`：
   - `session`：更新 `turnIndex`、`status`。
   - `tool_call`／`tool_result`：見上表。
   - `dimensions`：覆寫 `profile` 與 `facetStates`。
   - `final`：推條目；`finalized` 同時更新 `lastFinal` 與 `status`；`ask` 記下要高亮的維度（到下一輪 `session` 事件為止）。
   - `token`：照 §10.2 接進最近一筆可承接文字的條目；現況不會收到。
   - `error`／`blocked`：restore 到 `pending.snapshot`，再推一筆 `failure` 條目（含原文）。失敗那一輪的 tool 卡片與儀表板半成品全部消失，跟後端回滾對稱；使用者仍看得到自己送過什麼與為什麼失敗。
   - 未知 `event:` 或 JSON 解析失敗：忽略該筆並 `console.warn`，不中斷串流。
3. 串流結束 → `pending = null`，`transcript` 與 `sessionId` 寫進 `sessionStorage`。
4. 串流**沒有**以 `final`／`error`／`blocked` 收尾就斷了（網路斷、API 重啟）→ 視同 `error`：本地生一筆 `failure`（code `stream_ended`）並 restore。後端那邊 `RequestAborted` 會觸發回滾，兩邊一致。

跟主規格 §11.3 的差異只有一處：**snapshot 的時機從「收到 `session` 事件」提前到「送出當下」**，因為第 4 點的斷線可能發生在第一個事件之前，那時還沒有快照可以回。

### 3.4 重載重建

開頁：

1. 讀 `sessionStorage` 的 `sessionId`。沒有 → 開新 session。
2. 有 → `GET /api/sessions/{id}`。成功則以回傳的權威狀態（`status`、`profile`、`facetStates`、`askCount`、`lastFinal`、`turnIndex`）覆蓋 store，`transcript` 用本地那份。
3. `404` → 清掉本地紀錄、開新 session、顯示一行「上次的對話已過期」。

存前端的只有顯示用的 transcript。它被改動只影響使用者自己看到的畫面，不會回頭影響後端；從它流回後端的只有重試時的原文與存檔時的 `intent`，兩者本來就是使用者能打的東西。

## 4. 元件與互動

| 元件 | 職責 |
| :--- | :--- |
| `ChatStream` | 依 `transcript` 逐筆挑元件渲染；自動捲到底 |
| `UserBubble` | 使用者訊息 |
| `ToolCallCard` | 行內卡片：「🔍 查詢知識庫：鏡頭 → 池 2147 → 3 筆」。`tool_result` 到之前顯示進行中；完成後摺疊成一行，點開看 `summary` 與 preset 縮圖列，縮圖可開抽屜 |
| `AskCard` | `kind: ask`：`preamble` 在上，每則 ask 一區（維度標題、`question`、一排 chip）。該維度在儀表板高亮，直到下一輪 `session` 事件為止 |
| `MessageBubble` | `kind: message`：氣泡 + 輕量「參考方向」列表（有 `presetId` 的可開抽屜）；儀表板不高亮 |
| `FinalCard` | `kind: finalized`：正／負向 prompt 各自有複製鈕、`tips`、「儲存至共享知識庫」。按下展開確認區：預填 `intentSummary` 的輸入框 + 「確認儲存」；成功後按鈕變「已儲存」並失效；`save-to-shared` 回 `409`／`400` 時把後端的 `error` 字串顯示在確認區內，按鈕可再按 |
| `SaveConsentNotice` | `kind: save_consent_requested`：一筆短條目，同時把**最近一張** `FinalCard` 的確認區展開並捲過去 |
| `FailureNotice` | `error`／`blocked`：原因用 `reason`／`code` 對到繁中文案、訊息用後端的 `message`；「重試」把 `originalText` 填回輸入框並聚焦，不自動送 |
| `Composer` | 輸入框 + 送出；輪次進行中鎖住送出（避免 `409`）；chip 在此累積 |
| `Dashboard` | 側欄，每維度一列 facet chip，四態照主規格 §11.2（`covered` 實心飽和／`missing` 空心描邊／`notApplicable` 極淡／`waived` 實心去飽和加「略」記號），hover 顯示 label 與狀態。`profile` 為 null 時整面淡化並標「尚未判定題材」；`notApplicable` 整維度整列淡化。頂部顯示題材與「追問 n/askLimit」 |
| `PresetDrawer` | 右側滑出蓋住儀表板：`GET /api/presets/{id}`，顯示 `imageUrl`、title、`promptSnippet`、`negativeSnippet`、tags、facet 標籤。圖片是外站 URL，載入失敗顯示佔位不報錯 |
| `TopBar` | 專案名、「新對話」（清 `sessionStorage`、開新 session；輪次進行中失效） |

**chip 規則**（`stores/composer.ts`，純函式）：

- 點選是 toggle，再點取消。
- 輸入框內容 = 已選 chip 依維度分組組字 + 使用者手打的文字。同一維度的 chip 用「、」接在前綴後：`[風格] 寫實攝影、柔光`；多個維度各佔一行。
- 使用者一旦手動改過輸入框，chip 就只做「附加到尾端」不再重組，避免蓋掉手打的字。
- `message` 的「參考方向」也用同一套，只是視覺較輕。

**視覺方向**：實作時走 frontend-design skill，本文件不定色票。原則兩條：儀表板四態不靠顏色也分得出來；tool call 卡片是配角，摺疊後一行高。

## 5. 錯誤處理

除 §3.3 的串流失敗與 §3.4 的重載：

| 情況 | 處理 |
| :--- | :--- |
| `POST /messages` 回 `404` | session 過期：清本地紀錄、開新 session、顯示「上次的對話已過期」，原文留在輸入框讓使用者再送 |
| `POST /messages` 回 `409` | `Composer` 已鎖住，正常不會發生（兩個分頁共用同一個 id 除外）；顯示「這個對話還有一輪在跑」，不回滾 |
| `save-to-shared` 回 `409`／`400` | 錯誤字串顯示在確認區，按鈕可再按 |
| `GET /api/config/facets` 失敗 | 開頁就失敗屬於 API 沒起來：整頁顯示「連不到後端」與重試鈕，不讓使用者對著空儀表板打字 |
| `GET /api/presets/{id}` 失敗 | 抽屜內顯示錯誤，不影響對話 |
| 串流中按「新對話」 | 按鈕失效，不提供中斷；一輪最多 120 秒 |
| 未知事件／JSON 解析失敗 | 忽略該筆並 `console.warn` |

## 6. 測試

vitest，不跑瀏覽器：

- `reducer.test.ts`：每種事件各一條；`tool_call`／`tool_result` 以 `callId` 配對；`error`／`blocked` 還原到快照且 `failure` 條目帶原文；`finalized` 更新 `lastFinal` 與 `status`；`ask` 記下高亮維度、下一個 `session` 清掉；未知事件不改狀態。
- `useSse.test.ts`：假的 `ReadableStream` 餵進解析器，驗證 chunk 切在一行中間、一個 chunk 含兩筆事件、`data:` 含中文與換行逸出都能正確組出事件；串流未以終止事件收尾時產生 `stream_ended`。
- `composer.test.ts`：chip 累積、前綴組字、手動改過後只附加。

xUnit（後端）：

- `GET /api/sessions/{id}`：200 形狀（含 `lastFinal` 為 null 與非 null）、404。
- `FinalizePrompt` 缺 `intentSummary` 回錯誤字串、有則進 `LastFinal`。
- `OutputSafetyFilter` 的取材包含 `intentSummary`。

不引 Playwright。

## 7. 驗收

主規格 §14 第 3 列：瀏覽器端到端跑完 eval 第 1、3、6、11 條。加本子專案自己的三條：

| # | 操作 | 應該看到 |
| :--- | :--- | :--- |
| F1 | 定稿後整頁重載 | 儀表板、定稿卡片、對話流都回來；`GET /api/sessions/{id}` 有被呼叫 |
| F2 | 送出 NSFW 輸入 → `blocked` → 按「重試」 | 原文回到輸入框並聚焦；儀表板與上一輪相同；改寫後送出正常 |
| F3 | 定稿後按「儲存至共享知識庫」 | 確認區預填 `intentSummary`；改一個字後送出成功，按鈕變「已儲存」；資料庫有那一筆（試完刪） |

結果記回 `docs/eval-cases.md`。

`npm run build` 與 `npm test` 在本機過；CI 留給子專案 4。

## 8. 主規格改寫清單

實作完成後折回主規格（跟子專案 2 的做法一致）：

| 主規格 | 改動 |
| :--- | :--- |
| §4.2 `FinalizePrompt` | 加 `intentSummary` 參數 |
| §6.2 欄位表 | 加 `FinalizePrompt.intentSummary` |
| §10.1 端點表 | 加 `GET /api/sessions/{id}` |
| §10.2 `final.finalized` | 加 `intentSummary`；`token` 加註「現況不會發」 |
| §11.3 | snapshot 時機改為送出當下；重載重建；`sessionStorage` |
| §13 | `src/PromptCopilot.Frontend/` 內部結構 |
| §14 第 3 列 | 加 F1–F3 |
| 狀態列 | 子專案 3 完成日期 |

多輪設計 §11 最後一條「前端整頁重載後 session 狀態拿不回來」標記為已由本文件 §3.4 處理。

## 9. 決定紀錄

| 項目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 重載恢復 | `GET /api/sessions/{id}` + 前端 `sessionStorage` 存 transcript | 後端只多一支唯讀端點；transcript 是顯示用，不需要權威來源。後端存 transcript 等於在不持久化的 session 上再長一個結構 |
| 打字機 | 不做假的 | 文字早就到齊，逐字動畫是演的；即時感由 tool call 卡片與儀表板展示 |
| `intent` 來源 | `FinalizePrompt.intentSummary`，前端預填可編輯 | 定稿當下模型握有整段對話，零額外呼叫；串訊息再另外清洗多一次呼叫、多一份 prompt，且 history 截到 10 輪 |
| UI 套件 | 不用 | 元件面積小，套件省不了多少；自己寫的不像模板 |
| 框架 | Nuxt 3 而非純 Vite | 主規格列為展示重點 |
| 跨埠 | devProxy／nginx 反代，不加 CORS | 開發與部署同一套路徑，API 不用管來源 |
| snapshot 時機 | 送出當下 | 斷線可能發生在第一個事件之前 |
| 串流異常結束 | 視同 `error`，code `stream_ended` | 後端 `RequestAborted` 會回滾，前端要對稱 |
| chip 累積 | 手動改過後只附加 | 不能蓋掉使用者手打的字 |
| 測試 | vitest 純函式 + xUnit 端點，不引 Playwright | 驗收本來就是人工跑 eval |
| session 商品化 | 延後 | 本階段先能 demo；後端 session 壽命是另一個設計題 |
