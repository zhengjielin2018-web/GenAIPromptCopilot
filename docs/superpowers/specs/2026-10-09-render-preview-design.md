# 定稿後生成預覽圖：設計

日期：2026-10-09
來源：2026-10-09 brainstorm。前情是 [ComfyUI 整合可行性](../../ComfyUI整合可行性.md)：生圖後端走 ComfyUI、先在 RunPod Serverless 上試（§2.2），spike 實測延遲過門檻（§9.2），專案擁有者決定直接相信 Gemini 自評、不量一致率（§9.2），換模型暫緩（§9.3）。

---

## 1. 問題與目標

主規格 §2.2 明列「實際生成圖片（只產 prompt）」不做。使用者拿到定稿只能自己帶去別的地方生，看不到這組 tag 畫出來長什麼樣，也不知道哪些要素其實沒畫出來。

本案做可行性 §5.2 的 **B：定稿後生圖**：定稿照現在一樣先出，使用者在定稿卡上按「生成預覽」，幾秒後圖、審圖結果與自評補在卡上。這是 [Agent 化提案](../../Agent化提案.md) §6.1 自主閉環（A）的前一步：本案的生圖 client、工作流範本、佇列、審圖、自評、圖片端點與前端顯示，A 全部沿用。

**目標**：

- 使用者在定稿卡上看到這份提示詞實際畫出來的圖。
- 看到哪些已講定的要素有畫出來、哪些沒有（自評，只顯示）。
- 花費與濫用有上限，排隊太久就直接說，不讓使用者乾等。
- 審查開關的語意跟對話輪一致。

## 2. 決定紀錄

| 題目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 第一版的形狀 | **定稿後生圖**（可行性 §5.2 的 B），不做自主閉環 | 自評不量一致率，A 要靠自評驅動、會自動改提示詞；B 不動工具清單與閘門 |
| 何時生圖 | **使用者按「生成預覽」**才生，不在每次定稿自動生 | 還在調的中途稿不白生；公開 demo 的額度好管；冷啟動時使用者知道自己在等什麼 |
| 上限 | **每個 session 10 張、全站每天 200 張**，都放設定檔 | 一個人試幾種定稿夠用；全站每天打滿約 US$0.7，一個月仍在 NT$1,000 預算內。上限主要防濫用 |
| 寫實風的定稿 | **照樣給按鈕**，`style.genre` 是寫實時提醒「預覽會是動漫風」 | 不擋使用者；目前只有動漫模型。之後加寫實模型時依 genre 選 workflow（可行性 §8） |
| 結果怎麼送到前端 | **送出後立刻回 `renderId`，前端輪詢** | 跟「換一批」一樣是獨立端點，不碰對話輪的鎖與回滾；重新整理或斷線回來還查得到。開一條 SSE 等 10–40 秒會在重新整理時斷掉；常駐的 session 事件流留給 A |
| 自評 | **只顯示**，不擋、不改提示詞 | 專案擁有者決定直接相信 Gemini、不做人工判讀；判錯時「明明畫了說沒畫」會讓自動修正改壞提示詞，只顯示就沒有這個風險（可行性 §9.2） |
| 審查開關 | 跟對話輪同一套：`safety: off` 要後端開放才收；**關著時完全不跑審查分類器** | 專案擁有者指定。關著時本來就是測試，不必多花一次 Gemini 呼叫 |
| 生圖前要不要審提示詞 | 定稿當時審過就不再審；定稿是在審查關著時產生的（`LastFinal.Reviewed == false`）、這次又開著，就先補審 | 審過的不重花錢；沒審過的不能直接拿去生 |
| 圖存哪 | **存在 session 裡，跟 session 一起過期**；不寫資料庫、不進知識庫與種子 | 預覽圖是暫存，跟「上游圖片一律不轉存」（[資料來源](../../資料來源.md)）是兩回事，但同樣不留 |
| 同一個 session 的 seed | **建 session 時隨機一次，之後固定** | 重新定稿再生時，畫面差異才是 tag 造成的（可行性 §4） |
| 用哪個 endpoint | 做法 B（GitHub 建置、模型包進映像檔）的 `GenAIPromptCopilot` | 冷啟動載入快、沒有網路磁碟月費（可行性 §9.1）；做法 A 留作換模型的試驗台 |

## 3. 範圍

### 3.1 做

1. 後端：`RenderOptions`、`RunPodClient`、`RenderWorkflow`、`RenderService`、`RenderQueue` 與背景服務、`ImageReviewer`、`SelfChecker`、`Session.Renders`、三支端點與 `GET /api/config/render`、audit。
2. 前端：定稿卡的「生成預覽」按鈕、狀態顯示、圖、自評清單、寫實風提醒、審查關閉的標示；重新整理後接回。
3. 測試（§9）與 `docs/eval-cases.md` 驗收案例。
4. 文件同步（§10）。

### 3.2 不做

- 自主閉環（模型自己生圖、看圖、改、再生）、依自評自動修正、自評擋定稿。
- 換模型、寫實模型、依 genre 選 workflow（可行性 §9.3，暫緩）。
- 常駐的 session 事件流。
- 把圖存進資料庫或任何持久儲存。
- 一次出多張（`batch_size` > 1）。
- eval 腳本生圖（可行性 §11 第 4 項：這版 eval 不生圖，沒有共用 endpoint 的問題）。

## 4. 後端元件

| 元件 | 位置 | 做什麼 |
| :--- | :--- | :--- |
| `RenderOptions` | `Configuration/Options.cs`，設定節 `Render` | 見 §4.1 |
| `RunPodClient` | `Rendering/RunPodClient.cs`，typed `HttpClient` | `SubmitAsync(workflow) → jobId`、`WaitAsync(jobId) → job`、`CancelAsync(jobId)`。行為照 [`scripts/render_spike.py`](../../../scripts/render_spike.py)：送出失敗不重送；沒等到終止狀態就離開（逾時、查狀態出錯、取消）一律先送 cancel（盡力而為）再丟出去 |
| `RenderWorkflow` | `Rendering/RenderWorkflow.cs` | 讀 [`render/workflows/txt2img-sdxl.json`](../../../render/workflows/txt2img-sdxl.json)（csproj 連結進輸出目錄），複製後填正向詞（節點 6）、負向詞（節點 7）、seed（節點 3）；節點的 `class_type` 對不上就丟例外。負向詞＝定稿的負向詞＋固定的 `nsfw` 等詞（同 spike 腳本的 `NEGATIVE`，去重） |
| `RenderService` | `Rendering/RenderService.cs` | **收件與建立工作的唯一入口**：輸入是中性的 `RenderRequest`（正向詞、負向詞、自評項目、seed、`safety`、定稿當時是否審過、來源 `turnIndex`），做 §5.1 裡跟 session 狀態無關的檢查（上一張還沒好、張數上限、預估等待）、必要時補審提示詞（§6）、建 `RenderRecord`、排進佇列。**不讀 `LastFinal`、不拿 session 鎖**：那些是呼叫端的事（§5.1），之後自主閉環的工具從一輪對話裡呼叫它時，那一輪已經拿著鎖（§12） |
| `RenderQueue` | `Rendering/RenderQueue.cs` | `Channel<RenderJob>`、排第幾、最近 10 張的平均耗時、每日計數 |
| `RenderWorker` | `Rendering/RenderWorker.cs`，`BackgroundService` | 單一消費者（對應 Runpod Max Workers 1）：組 workflow → 送出 → 等 → 取圖 → 審圖與自評平行跑（審圖過了就先放出圖，自評繼續跑）→ 收尾。**收尾集中在一個方法**（寫最終狀態、更新平均耗時、寫 audit），`done`／`failed`／`blocked` 都走它；自主閉環要在圖好了時觸發下一輪，就在這裡多一步（§12）。服務停止時取消手上的工作 |
| `ImageReviewer` | `Safety/ImageReviewer.cs` | 看圖審查：nsfw、真實人物，回 JSON（`ResponseSchema`、`Temperature = 0`），寫法照 `SafetyClassifier`；圖片放在 user 訊息的 `ImageContent`（PNG bytes）。缺 `reason` 視為解析失敗 |
| `SelfChecker` | `Rendering/SelfChecker.cs` | 自評：列出要檢查的項目（§4.2），請 Gemini 逐項回 `present`／`absent`／`unclear` 與一句理由 |
| `RenderRecord` | `Rendering/RenderRecord.cs` | 一張圖的狀態、圖片 bytes、審圖與自評結果、耗時、`safety` |
| `Session.Renders` | `Sessions/Session.cs` | `ConcurrentDictionary<string, RenderRecord>`；`Session.RenderSeed` 建 session 時隨機一次。不進 `SessionSnapshot`：對話輪回滾碰不到它 |

Gemini 的模型用 `Llm:Model`（跟 `SafetyClassifier` 同一個），走同一個 `IChatCompletionService`。

### 4.1 設定（`Render` 節）

| 鍵 | 預設 | 說明 |
| :--- | :--- | :--- |
| `EndpointId` | 空 | Runpod endpoint id；空的就視為沒開 |
| `ApiKey` | 空 | **只從環境變數讀**（`Render__ApiKey`；docker compose 由 `.env` 的 `RUNPOD_API_KEY` 帶入），不寫進任何設定檔；空的就視為沒開 |
| `PerSessionLimit` | 10 | 每個 session 送去 Runpod 的張數上限 |
| `DailyLimit` | 200 | 全站每天的張數上限，台灣時間午夜歸零 |
| `MaxEstimatedWaitSeconds` | 60 | 預估等待超過就不收（可行性 §11 第 3 項） |
| `JobTimeoutSeconds` | 90 | 從送出 Runpod 起算；冷啟動實測 36.8 秒（可行性 §9.2）再留餘裕 |
| `PollIntervalMs` | 1000 | 後端輪詢 Runpod 的間隔 |
| `DefaultImageSeconds` | 10 | 還沒有實測資料時，預估等待用的每張秒數 |

`EndpointId` 或 `ApiKey` 有一個是空的，`GET /api/config/render` 回 `{"enabled": false}`，`POST /renders` 回 `404`，背景服務照常啟動但不會收到工作。

### 4.2 自評的檢查項目

定稿當下 `FacetStates` 是 `Covered`、而且 `FacetTags` 有 tag 的 facet，每個一項：`{facetId, label（facets.yaml 的中文標籤）, tag}`，例如 `{appearance.hair, 髮型, long silver hair}`。不另外挑「畫面看得出來的」facet：鏡頭焦段、參照畫師這類看不出來的，交給 Gemini 回 `unclear`。

項目在收件時跟定稿一起做成快照，之後使用者再改設定也不影響這張圖的自評。

### 4.3 寫實風的判斷

`FacetTags["style.genre"]` 有值就看它，沒有就看定稿的正向詞；含 `photorealistic`、`realistic`、`photo`、`photograph`、`raw photo` 其中一個（不分大小寫、整詞比對）就算寫實。判斷在前端做，後端在 `GET /api/sessions/{id}` 已經回 `facetTags` 與定稿；判錯的代價只是多一行或少一行提醒。

## 5. 端點與資料流

```
按「生成預覽」
  → POST /api/sessions/{id}/renders  {turnIndex, safety?}
      收件檢查（§5.1）→ 必要時補審提示詞（§6）→ 排進佇列 → 202 {renderId}
  → 背景服務：組 workflow → 送 Runpod → 輪詢 → 取圖
              → 審圖 ∥ 自評（審查關著時只跑自評）
              → 審圖過了：狀態 self_checking、圖可以拿 → 自評好了：done → audit
  ← 前端每 1.5 秒 GET /api/sessions/{id}/renders/{renderId}
  ← 完成後 GET /api/sessions/{id}/renders/{renderId}/image
```

### 5.1 `POST /api/sessions/{id}/renders`

body：`{"turnIndex": n, "safety": "on" | "off"}`，`safety` 可省略（預設 `on`）。

依序檢查，第一個不過的就回：

| 情況 | 回應 |
| :--- | :--- |
| 生圖沒開（§4.1） | `404` |
| session 不存在或過期 | `404` |
| `safety` 不是 `on`／`off` | `400` |
| `safety: off` 但後端沒開 `Safety:AllowDisable` | `403`（訊息同 `messages`） |
| 這個 session 有一輪在跑 | `409`：收件要讀定稿與 facet 狀態，照存進共享庫的做法 `Lock.WaitAsync(0)` 拿不到就回；拿到後做快照（正向詞、負向詞、`Reviewed`、`turnIndex`、自評項目、seed）立刻放鎖 |
| 還沒定稿，或 `turnIndex` 不是最後一次定稿的那輪 | `409` |
| 這個 session 已經有一張還沒結束（`queued`／`generating`／`reviewing`／`self_checking`） | `409`「上一張還在生」 |
| 這個 session 已送 Runpod 達 `PerSessionLimit` | `429` |
| 全站今天已送 Runpod 達 `DailyLimit` | `429` |
| 預估等待 > `MaxEstimatedWaitSeconds` | `503`「目前人多，稍後再試」 |

預估等待＝（佇列裡排在前面的張數＋處理中的 1 張）× 最近 10 張的平均耗時（從送出 Runpod 到取回圖；還沒有資料時用 `DefaultImageSeconds`）。

**誰檢查哪幾列**：前四列與「有一輪在跑」「還沒定稿／不是最新」由端點做——端點拿 session 鎖、讀 `LastFinal` 與 facet 狀態、組成 `RenderRequest`、放鎖，再交給 `RenderService`。後四列（上一張還沒好、兩種張數上限、預估等待）由 `RenderService` 做；「檢查上一張」到「建立紀錄」在 `RenderService` 自己的鎖裡一次做完，兩個請求同時到也只有一個過得去。

通過後：需要補審就補審（§6），被擋則建一筆 `blocked` 的紀錄、不排隊；否則建一筆 `queued` 的紀錄排進佇列。兩種都回 `202 {"renderId": "..."}`，讓前端走同一條顯示路徑。被拒絕（`429`、`503`）寫一筆 `Render_Rejected` audit。

**張數只算真的送去 Runpod 的**：補審被擋、在佇列裡就失敗的不算；送出之後失敗、被看圖審查擋下的都算（錢已經花了）。

### 5.2 `GET /api/sessions/{id}/renders/{renderId}`

```json
{
  "renderId": "…", "turnIndex": 7, "status": "queued | generating | reviewing | self_checking | done | failed | blocked",
  "position": 2, "safety": "on", "message": null,
  "selfCheck": { "status": "pending | ok | unavailable",
                 "items": [ { "facetId": "appearance.hair", "label": "髮型", "tag": "long silver hair",
                              "verdict": "present | absent | unclear", "reason": "…" } ] },
  "timings": { "queueMs": 0, "delayMs": 120, "executionMs": 4600, "reviewMs": 1800, "selfCheckMs": 2100 }
}
```

- `position` 只在 `queued` 時有值（1 表示下一張就是它）。
- `generating`：已送 Runpod（Runpod 那邊可能還在冷啟動）。
- `reviewing`：圖已取回，看圖審查還沒過（審查關著時跳過這個狀態）；圖還拿不到。
- `self_checking`：看圖審查過了（或審查關著），**圖已經拿得到**，自評還在跑。審圖與自評平行跑，自評比審圖先好時，審圖一過就直接到 `done`。
- `done`：圖與自評都好了（自評可能是 `unavailable`）。
- `message`：`failed`／`blocked` 時給使用者看的一句話（§6、§7）；細節只進 audit。
- `404`：session 或 `renderId` 不存在。

### 5.3 `GET /api/sessions/{id}/renders/{renderId}/image`

`status` 是 `self_checking` 或 `done` 才回 `image/png`，其他狀態回 `404`。審查開著時，這兩個狀態一定表示看圖審查過了；`reviewing` 時圖還不給，被擋的圖不存，根本拿不到。

### 5.4 `GET /api/config/render`

`{"enabled": true | false}`，跟 `/api/config/safety` 同一套；前端依它決定要不要顯示按鈕。

## 6. 審查

| 定稿當時 | 這次 `safety` | 生圖前 | 生圖後 |
| :--- | :--- | :--- | :--- |
| 審過 | `on` | 不再審 | 看圖審查；沒過就 `blocked` |
| 沒審過 | `on` | **補審正向詞**：denylist＋`ClassifyOutputAsync`，跟 `OutputSafetyFilter` 同一套；沒過就 `blocked`，不送 Runpod | 同上 |
| 任一 | `off` | 不審 | **不跑看圖審查**；圖直接給，前端標「審查已關閉（測試用）」 |

- 看圖審查判 nsfw 或真實人物：圖丟掉不存，`message` 是「預覽圖被判定為不當內容，沒有顯示」，判定理由只進 audit（同 `SafetyGuard` 的 `BlockDetail` 原則）。
- 看圖審查本身失敗（Gemini 連不上、回非 JSON、缺 `reason`）或 Gemini 拒收圖片：**不給看圖**，狀態 `blocked`，`message`「預覽圖沒有通過審查，沒有顯示」。跟 `SafetyClassifier` 判不出來就不放行一致。
- 自評不是審查，開關不影響它。審查關著時自評若被 Gemini 拒收，只把自評標 `unavailable`，圖照給。
- 補審被擋的訊息沿用輸出審查的寫法。

## 7. 錯誤處理

| 情況 | 狀態與處理 |
| :--- | :--- |
| 送出 Runpod 失敗（HTTP 錯誤、餘額不足） | `failed`；不重送 |
| 查狀態出錯 | 先 cancel 再 `failed` |
| 超過 `JobTimeoutSeconds` | 先 cancel 再 `failed`，`message`「生成逾時，可以再按一次」 |
| Runpod 回 `FAILED`／`CANCELLED`／`TIMED_OUT`，或 `COMPLETED` 但沒有圖、不是 base64 | `failed` |
| 自評失敗或被拒收 | 不影響狀態；`selfCheck.status = unavailable` |
| 背景服務停止 | cancel 手上的工作；佇列裡的標 `failed` |
| 單張處理丟出未預期的例外 | 那張 `failed`，背景服務繼續處理下一張 |

`failed` 給使用者的 `message` 一律是一句不含細節的話（「預覽圖生成失敗，可以再按一次」，逾時另寫），細節進 audit。

**跟現有流程的邊界**：

- 生圖途中重新定稿：不取消。這張圖屬於它的 `turnIndex`，好了照樣顯示在舊的定稿卡上；按鈕只在最新的定稿卡。
- 對話輪回滾：`Session.Renders` 不在 `SessionSnapshot` 裡，碰不到。
- session 過期：背景服務手上的那張照樣跑完、寫進已經沒人拿得到的 `Session` 物件；前端輪詢拿到 `404` 就顯示「session 已過期」。
- 記憶體：每個 session 最多 10 張、每張約 1.3 MB，跟 session 一起在滑動 120 分鐘後過期。公開上線後有壓力再改成只留最新幾張。

## 8. 前端

- **按鈕**：`/api/config/render` 回 `enabled: true` 時，只有最新那張定稿卡（`FinalCard.vue`）顯示「生成預覽」。寫實風（§4.3）在按鈕旁提醒「預覽會是動漫風」。
- **按鈕停用**（使用者按了也只會撞到 `409` 的情況，前端先擋）：
  - 對話輪正在跑（串流中）：停用，不另外寫字；那一輪結束就恢復。
  - 這個 session 有一張預覽還沒結束（排隊、生圖、審查或自評中，不管在哪張卡上）：停用，旁邊寫「上一張還在生成」；那張到了 `done`／`failed`／`blocked` 就自動恢復。
  - 後端仍照 §5.1 回 `409`：兩個分頁或前端狀態過期時，訊息照常顯示在按鈕下方。
- **送出**：body 用 [`lib/safety.ts`](../../../src/PromptCopilot.Frontend/lib/safety.ts) 的 `messageBody` 同樣的規則帶 `safety: off`（後端開放而且使用者關掉了才帶）。`409`／`429`／`503` 的訊息直接顯示在按鈕下方。
- **狀態**：每 1.5 秒輪詢，每個階段各有自己的字：`queued`「排第 k 位」、`generating`「生圖中（閒置後第一張可能要半分鐘）」、`reviewing`「審查圖片中」、`self_checking`「自評中」。拿到 `done`／`failed`／`blocked` 就停；元件卸下時也停。
- **自評中就顯示圖**：一進 `self_checking` 就載入圖片顯示，自評清單的位置先顯示「自評中…」，`done` 時換成清單。
- **完成**：顯示圖與自評清單（✓ 有畫出來／✗ 沒有／？看不出來，加理由）；`selfCheck.status = unavailable` 時顯示「這張的自評無法進行」。審查關著時圖上方標「審查已關閉（測試用）」。
- **在畫面外完成的提示**：預覽到了 `done`／`blocked`／`failed` 時，那張定稿卡若不在畫面內（`IntersectionObserver`），畫面底部出現一個小提示「預覽好了」（被擋或失敗時寫「預覽沒有完成」），點了捲到那張卡；幾秒後自動消失，卡在畫面內時不出現。
- **一張卡可以生好幾次**：每次按都是新的 `renderId`，卡上只顯示最新一張。
- **重新整理後接回**：`renderId` 跟著定稿卡存進前端的對話紀錄（同現有的前端持久化）；重新載入時對有 `renderId` 的卡打一次 `GET`，還在跑就繼續輪詢，`404` 就顯示「預覽已過期」。

## 9. 稽核

| 事件 | 時機 | payload |
| :--- | :--- | :--- |
| `Render_Completed` | 圖給了使用者 | `renderId`、`turnIndex`、`safety`、Runpod `jobId`、`queueMs`、`delayMs`、`executionMs`、`reviewMs`、`selfCheckMs`、自評統計（present／absent／unclear 各幾項）、`selfCheck.status` |
| `Render_Blocked` | 補審或看圖審查擋下 | 上面那些有的就帶，加 `stage`（`prompt`／`image`）、判定與理由 |
| `Render_Failed` | `failed` | 上面那些有的就帶，加錯誤種類與訊息 |
| `Render_Rejected` | `429`／`503` | 原因、目前的 session 張數與全站張數、預估等待 |

audit 寫失敗只記 log，不改變回應（同推薦的做法）。

## 10. 文件同步

跟實作同一批 commit（照慣例，動到流程就同步流程說明文件）：

| 文件 | 改什麼 |
| :--- | :--- |
| [主規格](2026-09-21-genai-prompt-copilot-design.md) | §2.2 拿掉「實際生成圖片（只產 prompt）」，改成「定稿後可按鈕生成預覽（見本設計）」；狀態列補一句 |
| [SK 架構說明](../../SK架構說明.md) | 四層圖加上生圖 client（跟 embedding 一樣不走 SK）；審圖與自評的 `ImageContent` 用法 |
| [README](../../../README.md) | 生圖怎麼開（`RUNPOD_API_KEY`、endpoint id）、費用、上限、預覽圖不轉存 |
| [資料來源](../../資料來源.md) | 預覽圖是暫存，不進知識庫、不進種子，跟 session 一起過期 |
| [單輪流程說明](../../單輪流程說明.md) | 生圖不在對話輪裡；定稿卡多了「生成預覽」入口 |
| [ComfyUI 整合可行性](../../ComfyUI整合可行性.md) | §11 第 1–4 項標已決定、指向本設計；§9 補上實測的審圖、自評耗時 |
| [`render/runpod/README.md`](../../../render/runpod/README.md) | 正式用的是做法 B 的 endpoint；金鑰除了 spike 腳本也給 API 用 |
| [`docs/eval-cases.md`](../../eval-cases.md) | 新的一節：§11 的驗收案例與結果 |

## 11. 測試

### 11.1 後端單元（xUnit）

- **`RunPodClient`**（假的 `HttpMessageHandler`）：輪詢到終止狀態；逾時、查狀態回 503、讀取逾時都會先送 cancel；送出失敗不重送；取消權杖觸發時也送 cancel。
- **`RenderWorkflow`**：填入正向詞、負向詞、seed，不改到範本；節點的 `class_type` 對不上會丟；負向詞有固定的 `nsfw` 而且不重複；repo 裡的 workflow 的 `ckpt_name` 在 `render/runpod/Dockerfile` 下載的清單裡（同 `test_render_spike.py`）。
- **`RenderQueue`**（假的時鐘、假的 client）：
  - 上一張還沒好 → 拒絕；session 張數到上限 → 拒絕；全站到上限 → 拒絕；台灣時間過午夜歸零。
  - 預估等待：沒有資料時用預設秒數，有資料時用最近 10 張的平均；超過門檻 → 拒絕。
  - 只有送去 Runpod 的才計數：補審被擋不算、送出後失敗算。
- **`RenderService`**：不需要 `Session.Lock`、不讀 `LastFinal` 也能收件（直接給 `RenderRequest`）；同一個 session 兩個請求同時到，只有一個建立紀錄。
- **審查組合**（`FakeChatCompletion`）：定稿審過／沒審過 × `on`／`off` 四種：
  - 沒審過＋`on` 會補審；補審被擋不呼叫 Runpod。
  - `off` 時審查分類器（文字與看圖）一次都沒被呼叫。
  - 看圖審查判 nsfw → `blocked`、圖不存；看圖審查回非 JSON 或缺 `reason` → `blocked`。
- **自評**：項目從 covered＋有 tag 的 facet 產生、用快照；Gemini 出錯 → `unavailable`、圖照給；審查擋下時自評結果不回。
- **狀態順序**（可控制完成順序的假審圖、假自評）：審圖沒好 → `reviewing`、圖拿不到；審圖過了、自評沒好 → `self_checking`、圖拿得到；自評先好、審圖後過 → 直接 `done`；審查關著 → 取圖後直接 `self_checking`。
- **`RenderWorker`**：單張丟出未預期的例外後繼續處理下一張；停止時 cancel 手上的工作。
- **契約**（`GeminiContractTests`）：審圖與自評送出的請求裡，圖片是 `inlineData`、`mimeType` 是 `image/png`。

### 11.2 端點（`EndpointTests`）

- `POST /renders`：`202`、`400`、`403`、`404`（沒開、session 不存在）、`409`（有一輪在跑、`turnIndex` 不是最新、上一張還沒好）、`429`、`503`。
- `GET /renders/{id}` 的 JSON 形狀；`GET /image` 在 `self_checking`、`done` 時回圖，`reviewing`、`blocked` 拿不到。
- `GET /api/config/render`：有沒有設定 `EndpointId`／`ApiKey` 時各回什麼。

### 11.3 前端（Vitest）

- 只有最新的定稿卡有按鈕；`enabled: false` 時沒有按鈕；寫實風有提醒。
- 按鈕停用：對話輪串流中停用、結束恢復；有一張預覽未完成時停用並顯示「上一張還在生成」，那張完成後恢復（包含那張在舊卡上的情況）。
- 畫面外完成的提示：卡不在畫面內才出現、點了捲到那張卡、卡在畫面內時不出現。
- 輪詢的狀態順序與停止條件（`done`／`failed`／`blocked`、元件卸下）；每個狀態顯示的字。
- `self_checking` 時圖已顯示、自評位置顯示「自評中…」；`done` 時換成清單。
- 審查開關關著（而且後端開放）時 body 帶 `safety: off`。
- 重新載入時有 `renderId` 的卡會接回；`404` 顯示已過期。

### 11.4 真機驗收（打真的 Runpod，手動，花費幾美分）

寫進 `docs/eval-cases.md`：

1. 動漫風定稿 → 按生成預覽 → 依序看到排隊／生圖中／審查圖片中 → 自評中時圖已經出現 → 自評清單補上。
2. 按下後按鈕立刻停用並顯示「上一張還在生成」；對話輪跑的時候按鈕也是停用的。另開一個分頁對同一個 session 再按：後端回 `409`，訊息顯示在按鈕下方。
2a. 按下後繼續聊天到定稿卡捲出畫面：預覽完成時底部出現「預覽好了」，點了捲回那張卡。
3. 寫實風定稿：按鈕旁有提醒，生出來是動漫風。
4. 重新定稿後在新的卡上再生：同一個 seed，畫面差異來自 tag。
5. 生圖途中重新整理頁面：回來後接著顯示。
6. 審查開關關著（本機 `Safety:AllowDisable=true`）：圖上方標「審查已關閉」，audit 沒有審圖的耗時。
7. 把 `PerSessionLimit` 暫時設 1：第二張回 `429`。
8. 從 audit 讀出審圖、自評的實際耗時，記進可行性 §9。

## 12. 往自主閉環的接點

本案是可行性 §5.2 的 B；之後做 A（模型自己生圖、看、改、再生，可行性 §6.4）時，下面這張表說明哪些直接沿用、哪些要新增。

| 本案的東西 | A 怎麼用 |
| :--- | :--- |
| `RunPodClient`、`RenderWorkflow`、`RenderQueue`、`RenderWorker` | 原樣沿用：送出、等、取圖、排隊、上限、預估等待 |
| `RenderService` | A 的生圖工具在一輪對話裡直接呼叫它，送的是草稿而不是 `LastFinal`；那一輪已經拿著 session 鎖，所以它本來就不拿鎖、不讀 `LastFinal`（§4） |
| `ImageReviewer`、`SelfChecker` | 原樣沿用。自評結果改成 JSON 文字回給模型；圖片不進 `ChatHistory`（可行性 §4） |
| `RenderWorker` 的收尾方法 | A 在這裡多一步：圖好了就通知 Dispatcher 觸發下一輪 |
| `RenderRecord`、`Session.Renders`、圖片端點、audit | 原樣沿用；A 可能要在紀錄上加 session 版本戳，回來時版本變了就標 `Stale`（可行性 §6.4） |
| session 固定 seed、同一個 session 一次一張 | A 本來就需要 |
| 前端的圖與自評清單 | 沿用顯示；更新來源從輪詢改成事件流 |

A 要新增、跟本案無關的：生圖工具與「等生圖」的終止結果、伺服器觸發的新輪種類（`TurnKind.Observe`）、把「跑一輪」從 HTTP 請求抽出來（`AgenticOrchestrator`）、常駐的 session 事件流、每個要求的生圖預算、system prompt 的規則、依自評自動修正（要先決定是否量自評一致率，可行性 §9.2）。
