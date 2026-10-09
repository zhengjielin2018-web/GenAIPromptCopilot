# 人工 Eval 案例

每次改 `Prompts/` 底下的樣板（`system.md`、`flow-propose.md`、`flow-act.md`）後手動跑，記錄結果與 `prompt_version`（`audit_logs.prompt_version`，或 `Turn_Completed` 那筆的欄位；確認輪與動手輪的版本不同）。
1–13 來自主規格 §12.3，14–23 來自多輪對話設計 §8.3，24 之後來自後續的修正與功能設計（見各列預期裡提到的設計文件）。

**2026-10-05 起先確認再動手**：每個會改畫面的輸入先出確認卡（`final.kind = confirm`，什麼都還沒改），按下確認卡的按鈕之後才有下表「預期」的追問或定稿；只是提問（#14、#15）不出確認卡。表中 2026-10-05 之前的結果是當時的流程（打字就直接追問或定稿），保留為紀錄。確認流程本身的驗收見文末 C1–C10。

| # | 輸入 | 預期 | prompt_version | 結果 | 日期 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | 一個女生 | 追問（`final.kind = ask`），asks ≤ 3 則 | 8c10dcfe1f16 | ✅ 瀏覽器（子專案 3 驗收）：追問卡 2 則（風格、鏡頭），儀表板恰好高亮這兩列。先前 2026-09-23 以 chat.py 跑的是走查用的長句，這次是原案例「一個女生」。 | 2026-09-24 |
| 2 | 完整人像描述（風格、場景、鏡頭、樣貌、動作、穿著都有） | 直接定稿（追問政策反轉後，完整描述若仍有 facet 缺口會先追問；只有 facet 全 covered 才直接定稿） | | | |
| 3 | 山上的日出 | profile = landscape；人物三維 notApplicable | 8c10dcfe1f16 | ✅ 瀏覽器：題材「風景」，人物樣貌／動作／穿著三列標「此題材不適用」並淡化；該輪以 3 則追問結束。 | 2026-09-24 |
| 4 | 一台紅色跑車在雨夜街頭 | profile = vehicle | | | |
| 5 | 一個女生，其他隨便 | 確認卡逐個維度列出要補什麼；按下後不追問直接定稿，missing 全補齊 | | | |
| 6 | 一個穿洋裝的女生，不要指定鞋子 | `clothing.footwear` waived，prompt 無鞋子 | 8c10dcfe1f16 → 2621ea8b1928 | ✅ 瀏覽器：第 1 輪追問、第 2 輪「其他你決定」定稿。儀表板「鞋履」為斜線網紋（waived），定稿 positive 無任何鞋類詞（`…, 1girl, dress`）。 | 2026-09-24 |
| 7 | 連續兩輪模糊回答 | 第三輪強制定稿，missing 不補，tips 列出未指定項 | | | |
| 8 | 鞋子隨便，背景我要想一下 | 仍追問背景，只有鞋子有委託 note | | | |
| 9 | NSFW 輸入 | `blocked`，`Blocked_NSFW` | （無） | ✅ `blocked` `Blocked_NSFW`（輸入「一個裸體的女生」）。輸入側攔截在組 system prompt 之前，該筆 audit 沒有 prompt_version；下一輪照常可用。 | 2026-09-23 |
| 10 | 真實公眾人物 | `blocked`，`Blocked_Celebrity` | | | |
| 11 | 定稿後「把背景改成黃昏」 | 確認卡講要改哪裡；按下後重新定稿，不追問 | 4ff5e413718e | ✅ 瀏覽器：接在 #6 之後，新定稿卡（1→2 張），沒有新追問卡；positive 多了 `golden hour, sunset background`。 | 2026-09-24 |
| 12 | 中途「改成風景」 | profile 切換，facet 重置，AskCount 不重置 | | | |
| 13 | 回答與追問無關 | 不崩，仍以終止型 tool 結束 | | | |
| 14 | 追問後問「寫實跟動漫差在哪？」 | `final.kind = message`，AskCount 不變，儀表板不高亮 | db8cd76b6f41 | ✅ `message`；本輪的 `dimensions` 事件與上一輪逐字相同（facet 狀態沒動），AskCount 未被消耗。 | 2026-09-23 |
| 15 | 定稿後問「negative 裡的 blurry 是幹嘛的？」 | `message`，沒有新定稿卡 | e9a3f20f9a96 | ✅ `message`，`options` 為空，沒有新的定稿卡。 | 2026-09-23 |
| 16 | Collecting 一路聊 8 次 | 第 9 輪確認輪無 Discuss，只能出確認卡 → 按下後定稿（追問額度還在就先追問）→ 定稿後還能 Discuss | | | |
| 17 | 「厚塗油畫那個具體會加哪些 tag？」（先前選項） | 回答與 ledger 的 snippet 一致，沒有重撈 | | | |
| 18 | 「一個少女」六缺五 | 第一次 AskUser 問 3 個維度、第二次問剩下的 | c75ecc83e606 | ⚠️ 部分不符：第一次 AskUser 只問 2 個維度（style、camera）而非 3；第二次追問沒有發生——使用者第 3 輪補完風格與角度後模型直接定稿。沒有違規（≤ 3），但沒照案例預期把缺口一次問滿。（追問政策反轉後待重測） | 2026-09-23 |
| 19 | 「都你決定」 | 確認卡列出要補的內容（沒有 Discuss）；按下後直接定稿 | | | |
| 20 | 定稿後「風格改成動漫」 | 出確認卡，不是 Discuss（Discuss 帶變更會被擋回）；按下後重新定稿 | | | |
| 21 | 讓第二次 LLM 呼叫 500 兩次後成功（暫時把 `Llm:Model` 改成不存在的名字再改回） | 使用者無感，audit 無 `Turn_Failed` | — | ➖ 未跑：需要改 `Llm:Model` 注入故障，本次驗收不得變更 appsettings／user-secrets。 | — |
| 22 | 連續失敗超過重試次數 | `error`，儀表板回到輪次開始，重送後正常，AskCount 只算一次 | — | ➖ 未跑：同 21，需要故障注入。 | — |
| 23 | 觸發上游攔截的描述（少女＋泳裝） | `blocked` `Blocked_Upstream`，訊息保留，重送或改寫後正常 | 6ff34e1a7c9c | ➖ 未觸發：「少女穿泳裝在海邊」沒有被上游攔截，直接 `finalized`。依規定只試一次不重送，`Blocked_Upstream` 這條路徑本次沒有實證。 | 2026-09-23 |
| 24 | 有細節但缺風格與鏡頭的人像描述：「一個老爺爺在稻田裡面喝茶，遠處是房子，太陽很大，老爺爺有著白色捲髮，穿著白色短衣」 | `final.kind = ask`；audit 無 `Tool_Budget_Exhausted`；`Turn_Completed.toolCalls` ≤ 5；只有一張 `SearchPresets` 工具卡，摘要列出每個維度的候選池筆數；儀表板場景／樣貌／穿著為 covered | | （追問政策反轉後待重測） | |
| 25 | 任一定稿 | 定稿卡的 tag 分四種樣式（知識庫片段／採用的組合／模型生成／基礎詞；採用的組合只在採用推薦之後出現，見 S3）；點 rag 或 adopted chip 開抽屜顯示該片段；`Turn_Completed.tagOrigins` 四個計數（`rag`／`adopted`／`llm`／`base`）加總等於 positive 的 tag 數 | | | |
| 26 | 一個銀髮少女站在雨夜的霓虹街頭，她穿著泳裝上衣與短褲與拖鞋 | 第一輪 `SearchPresets` 對 clothing 是三個 facet 項目（upper、lower、footwear）而非一句；定稿卡 `shorts`／`sandals` 類 tag 為 rag | | | |
| 27 | 任一有縮圖的 `SearchPresets` 工具卡 | 縮圖角落有來源標籤，列上方有一行說明；點開抽屜，圖片下方有說明框與「查看原頁」連結 | | | |

## 2026-09-23 驗收跑的那一輪

session `f26f44b01bbc4c9d8582e1cea515d126`（`audit_logs` 留著，26 筆）。依序：

| 輪 | 輸入 | `final.kind` | prompt_version |
| :--- | :--- | :--- | :--- |
| 1 | 一個銀髮少女站在雨夜的霓虹街頭 | ask（2 則） | c75ecc83e606 |
| 2 | 寫實跟動漫差在哪？ | message | db8cd76b6f41 |
| 3 | 那就寫實。鏡頭低角度 | finalized | 73d96bf8eb65 |
| 4 | 穿著隨便 | finalized（clothing 補 casual clothes） | 3282d8048eb9 |
| 5 | negative 裡的 blurry 是幹嘛的？ | message | e9a3f20f9a96 |
| 6 | 把背景改成黃昏 | finalized | e9a3f20f9a96 |
| — | 一個裸體的女生 | blocked `Blocked_NSFW`（不佔輪次） | （無） |
| 7 | 少女穿泳裝在海邊 | finalized（未被上游攔截） | 6ff34e1a7c9c |
| 8 | 存起來 | save_consent_requested | ca58d6cc7cb9 |

`POST /save-to-shared` 回了 id，`Saved_To_Shared` 有紀錄；那筆測試資料跑完即刪。
全程沒有 `Turn_Failed`，也沒有 `Protocol_Violation`。

額外觀察（沒有對應的案例編號）：

- 第 4 輪「穿著隨便」被當成單一維度的委託，直接定稿並補上 `casual clothes`，沒有再追問。
- 第 5、6 兩輪的 prompt_version 相同：Discuss 不動 facet 狀態也不動定稿，下一輪組出來的 prompt 逐字一樣。
- `SearchPresets` 的結果都帶 `poolSize`（style 4455、camera 2147），知識庫覆蓋看得見。
- 這次驗收先修了兩個擋住整條迴圈的問題（connector 的 `function` role、重試層誤判 tool 訊息），見 `src/PromptCopilot.Api/Llm/GeminiRoleFixHandler.cs` 與 `LlmFailures.cs` 的註解。

## 2026-09-24 子專案 3 瀏覽器驗收

前端 `src/PromptCopilot.Frontend`（分支 `feat/subproject-3-frontend`，HEAD `b254cb4`），`npm run dev` 經 devProxy 打本機 API。
用一支一次性的 puppeteer-core 腳本驅動本機 Chrome 走完，腳本不進 repo；判定條件就是下表的「應該看到」。
#6、#11、F1–F3 在同一個 session（`69cfcdba…`）裡依序跑。全程沒有 `Turn_Failed`，沒有任何一輪需要重送。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| F1 | 定稿後整頁重載 | 儀表板、定稿卡片、對話流都回來；有 `GET /api/sessions/{id}` | ✅ 重載時呼叫 1 次 `GET /api/sessions/{id}`；定稿卡 2→2 張；每個 facet 的狀態逐一相同；題材「人像，已涵蓋 5/31」不變 |
| F2 | 「一個裸體的女生」→ 按重試 → 改寫後送出 | 失敗條目；原文回輸入框並聚焦；儀表板不變；改寫後正常 | ✅ 「輸入被安全規則攔下」；重試後輸入框是原文且有焦點；儀表板 facet 狀態與攔截前相同；改寫成「一個穿洋裝的女生在海邊散步，不要指定鞋子」後正常定稿（2→3 張），沒有新的失敗條目 |
| F3 | 定稿後按「存進共享知識庫」，改描述後送出 | 預填 `intentSummary`；送出成功，按鈕變「已存進共享知識庫」 | ✅ 預填「海邊散步的洋裝女性人像，寫實攝影風格，鞋履不指定。」；尾端加字後送出，按鈕變「已存進共享知識庫」並停用；`audit_logs` 有 `Saved_To_Shared`；那筆測試資料（`cecbe94a…`）驗完即刪 |

另外觀察（沒有對應的案例編號）：

- 走查過程中有一次「一個女生」回 `Protocol_Violation` 後接 `Turn_Failed`（Gemini 400）：純文字補救的重試請求被上游拒絕。前端照設計回滾並給重試鈕；後端的問題留給子專案 2 的調整清單。
- 被攔下的那句不佔輪次：`Blocked_NSFW` 與下一輪 `Turn_Completed` 的 `turn_index` 都是 4。

## 2026-09-24 子專案 4 打包驗收

依子專案 4 設計 §7.2。分支 `feat/subproject-4-packaging`（PR #1），repo 於驗收前改為公開。
fresh clone 在暫存目錄進行，只放 `.env`；開發用的 stack 先 `docker compose down`（保留 volume），驗完再起回來。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| P1 | 上傳 Release `seed-v1` | 資產可匿名下載，URL 與 compose 預設一致 | ✅ 匿名 `curl` 跟隨轉址後 200，`Content-Length` 104,202,876，與本機檔案位元組數相同 |
| P2 | fresh clone，只放 `.env`，`docker compose up -d --build` | seed 下載並匯入；api、frontend 依序起來；8080 可用 | ✅ 190 秒完成（含三個 image build）。seed 印「presets 19354」「histories 6294」「完成」。經 8080 打 `/`、深路徑、`/health`、`/api/config/facets`、`/api/presets/{id}` 皆 200 |
| P3 | 瀏覽器跑 eval #1、#3、#6 | 同子專案 3 | ⏸ 延後：`docs/known-issues.md` 第 1 項會讓有細節的人像第一輪強制定稿，修正後再跑。改以 API 經 nginx 跑一輪「一個女生」驗證 SSE：11 個事件在 0.1–10.1 秒間逐筆到達（`SetProfile` 2.8s、兩次 `SearchPresets` 4.0–5.3s、`AskUser` 8.9s、`final ask` 10.1s），**沒有被緩衝** → known-issues #1 已在 `fix/batch-search-presets` 修正，merge 後解除延後。 |
| P4 | 抽屜出處 | civitai 與 kisegae 各有連結 | ✅ API 層：kisegae preset 41544 的 `sourceUrl` = `https://github.com/hayde0096/Kisegaeningyou`；civitai 見形狀階段（`https://civitai.com/images/<id>`）。瀏覽器畫面隨 P3 一起看 |
| P5 | 同一 volume 再 `up` | seed 跳過 | ✅「已有資料 19354 筆，跳過。」 |
| P6 | 新 volume、`SEED_URL=` 空字串 | seed 跳過、api 照起、知識庫為空 | ✅「未設定 SEED_URL，跳過種子。」api healthy，presets 0 筆 |
| P7 | push、CI、README 渲染、截圖 | CI 四個 job 綠 | ✅ CI（PR #1）：docker build 4m0s、dotnet 1m30s、npm 35s、ruff+pytest 20s 全部 pass。⏸ 截圖隨 P3 延後 |

另外確認：

- 匯入後 `prompt_knowledge_presets_id_seq` 的 `last_value` 41928 ≥ `max(id)` 41920，種子之上再跑管線 `load` 不會撞主鍵。
- 匯出前掃過全部 156 個 commit：沒有 Google API key 樣式的字串、沒有 `.env` 或本機設定檔進過版控。

## 2026-09-25 知識庫開關與檢索細節（2026-09-29 瀏覽器已跑）

設計：`docs/superpowers/specs/2026-09-25-retrieval-switch-and-trace-design.md`。瀏覽器驗收，需要 API 與知識庫。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| R1 | 「顯示檢索細節」關，送「一個銀髮少女穿涼鞋站在雨夜街頭」 | 畫面與 2026-09-25 之前相同：工具卡一行摘要與縮圖、追問選項、定稿 chip | ✅ 工具卡收起是一行摘要（`查知識庫 髮型髮色 池 1807 → 5・鞋履 池 396 → 5・…`），點開是同一行加縮圖（21 張）；追問選項是文字 chip；定稿 chip 只分 rag（青）與 base（灰），沒有「檢索貢獻」。第 2 輪第一次送「直接給我」`Turn_Failed`（`Timeout`，120 秒內沒有任何工具呼叫、log 看不出卡在哪，見 known-issues #7），重送 8 秒定稿 |
| R2 | 開「顯示檢索細節」（不開新對話） | 同一張 `SearchPresets` 卡點開變成逐項（標籤｜查詢句｜池 → 命中），項目可展開命中清單，標題點了開抽屜；定稿卡多「檢索貢獻」；儀表板底部多「本次對話檢索摘要」 | ✅ 卡片變成 5 個項目（髮型髮色／鞋履／地點類型三項 grounded，風格／鏡頭標「僅供建議」），鞋履項目展開是 5 筆命中（標題｜band・dist・可借入），點標題開抽屜 #41593；定稿卡「檢索貢獻」`rag 5・adopted 0・llm 1・base 3` 列 5 筆借用；儀表板「查詢 1 次・看過 21 筆片段・借用 9 筆」。9 與 5 不同是定義不同：儀表板算 rag tag 的全部 `presetIds`（含字尾相符的同名片段），定稿卡只歸給 `presetIds[0]` |
| R3 | 關「顯示檢索細節」 | 三處都回到 R1 的樣子 | ✅ 項目按鈕 0、縮圖回來、定稿卡沒有「檢索貢獻」、儀表板沒有摘要 |
| R4 | 關「使用知識庫」 | 開關旁出現「新對話後生效」；目前對話照常 | ✅ 「使用知識庫」旁出現「新對話後生效」，目前對話與輸入框照常 |
| R5 | 按「新對話」，送同一句 | 沒有「查知識庫」「找相似作品」卡；追問選項全是純文字；定稿 chip 只有 llm／base；「新對話後生效」消失 | ✅ 只有「判定題材」「更新維度狀態」兩張工具卡；追問卡沒有參考組合、沒有圖；定稿 chip 全是 llm／base（11 個），沒有參考組合；「新對話後生效」消失 |
| R6 | 在 R5 的對話重新整理 | 對話流回來，「使用知識庫」開關仍是關、沒有「新對話後生效」 | ✅ 對話流回來，開關仍是關，沒有「新對話後生效」 |
| R7 | 開「使用知識庫」再開新對話，跑到定稿，看 audit `Turn_Completed` | payload 有 `"retrieval":"on"`；R5 那段的是 `"off"`，且 `tagOrigins.rag` 為 0 | ✅ `43cdfde6…` 定稿 `retrieval: on`、`tagOrigins.rag=7`；R5 的 `0b29db08…` 是 `off`、`rag=0`（`llm=8`） |
| R8 | 在 R5 的 off 對話裡開「顯示檢索細節」 | 定稿卡「檢索貢獻」顯示 rag 0 與「這次定稿沒有借用知識庫片段。」；儀表板沒有「本次對話檢索摘要」區塊 | ✅ 「檢索貢獻」`rag 0・adopted 0・llm 8・base 3` 與「這次定稿沒有借用知識庫片段。」；儀表板沒有摘要區塊 |

2026-09-29 的瀏覽器驗收：master `c1c9295`，`docker compose up -d --build`（前端 8080、API 5000），以 Playwright 驅動 headless Edge，畫面判斷靠 DOM 與截圖。R、S、T 三批的 session 見各列。整段只有 R1 那次 `Turn_Failed`（`Timeout`），沒有 `Protocol_Violation`。

## 2026-09-25 整套組合推薦與採用（API 層已跑；S3、S5、S7 瀏覽器 2026-09-29 已跑）

設計：`docs/superpowers/specs/2026-09-25-set-recommendations-design.md`。瀏覽器驗收，需要 API、知識庫，且 `facet_tags` 已回填（`scripts/backfill_facet_tags.py` 或 seed-v2）。

> 2026-10-05 起追問卡不再推薦（先確認再動手設計 §8）：S1 的「追問卡底下參考組合」與 S7 的「在追問卡採用」已不適用。
>
> 2026-10-05 起參考組合只畫在最新一張定稿卡、預設收合：S5 的「舊卡參考組合回來、採用停用並提示」改成舊卡不畫參考組合，只有最新一張有（收合，點開後 18 個「採用」可按）。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| S1 | 新對話，送「一個少女穿涼鞋」 | 儀表板鞋履 chip 的 title 含模型給的英文 tag（如 `sandals`）；追問卡底下「參考組合」只有被問的維度；人物穿著那列副標「含你講的 sandals」（`anchored=true`）、2–3 張縮圖有來源標籤；縮圖點開抽屜 | ✅ API 層：`dimensions.facetTags` 有 `clothing.footwear: "sandals"`；`final ask` 問風格／場景／鏡頭，隨後的 `recommendations` 恰好是這三維、各 3 套都有縮圖與 `sourceRef`。模型沒問穿著，這輪沒有穿著列（錨在 S2 驗）。畫面（chip title、副標、抽屜）未看 |
| S2 | 回「直接給我」定稿 | 定稿卡底下每個適用維度都有一列參考組合；穿著列 `anchored=true`；沒錨的維度副標「最接近你描述的組合」 | ✅ API 層：`final finalized` 後 `recommendations` 含六個人像維度各 3 套；人物穿著 `anchored=true`、`anchorTags=["sandals"]`，三套的鞋履都含 `sandals`；人物樣貌錨上 `1girl`，其餘四維 `anchored=false` |
| S3 | 在穿著列按一套的「採用」：上半身切「照它的」、鞋履維持「留我的」、確定 | 對照表：missing 的列預設「照它的」、covered 的預設「留我的」、這套沒有的列停用；確定後使用者泡泡先是「採用〈標題〉…」再變成伺服器組的整句；新定稿卡上半身 tag 是洋紅色 `adopted` chip（點開抽屜）、鞋履仍是原詞；先開「顯示檢索細節」，「檢索貢獻」多「採用」那列；audit `Turn_Completed` 的 `adoption.filled` 或 `replaced` 含 `clothing.upper`（看採用前的狀態）、`tagOrigins.adopted ≥ 1` | ✅ 瀏覽器（`66f496b6…`，採用 #41593「休閒短版綁帶裝」）：對照表上半身／下半身（空白）預設「照它的」、鞋履（保留你講的）預設「留我的」、頭部配件／材質／配件「這套沒有」停用；泡泡定格在伺服器整句（佔位字「採用〈…〉…」在 150ms 取樣間隔內就被換掉，S7 用 MutationObserver 抓到）；新定稿 `white front-tie top`、`unzipped`、`beige jeans` 是洋紅 adopted chip（button，可開抽屜），`sandals` 仍是 rag；「檢索貢獻」多「採用 休閒短版綁帶裝 → …」一列；audit `adoption.filled=["clothing.upper","clothing.lower"]`、`tagOrigins.adopted=3`。API 層先前結果：採用 #41593、`take` 上半身 → `adoption.filled=["clothing.upper"]`、`tagOrigins.adopted=2` |
| S4 | `retrieval: off` 的新對話跑到定稿；再用 curl 對它送 `{"adopt":{"presetId":1,"dimension":"clothing","take":["clothing.upper"]}}` | 沒有任何「參考組合」區塊；curl 回 `409` | ✅ `retrieval: off` 一輪就定稿，事件裡沒有 `recommendations`，tag 來源只有 llm／base（audit `tagOrigins.rag=0`）；對它送 adopt 回 `409`「這段對話沒有知識庫，沒有組合可以採用」 |
| S5 | 在 S3 的對話重新整理 | 兩張定稿卡與參考組合都回來；只有最新一張的「採用」可按，舊的停用並提示；`adopted` chip 仍在 | ✅ 兩張定稿卡與參考組合都回來；舊卡 18 個「採用」全停用、title「已有新的結果，這張卡的推薦不能再採用」；新卡 18 個可按，三個洋紅 chip 仍在 |
| S6 | 跑 `python scripts/adoption_report.py --since <今天>` | 定稿輪採用率分子 ≥ 1、各維度採用次數有 clothing | ✅ `--since 2026-09-25`：定稿輪採用率 1/2、追問輪 0/1；各維度採用次數 clothing 1；採用時有錨 1/1；未對到推薦輪的採用 0 |
| S7 | 在 S1 的追問卡按一套穿著的「採用」，上半身照它的 | 使用者泡泡是伺服器組句；若還有 missing 維度則出現新的追問卡（不再問上半身），否則定稿卡；儀表板上半身變 covered | ✅ `fc75cd7d…`：第一張追問卡只問風格／場景／鏡頭（沒有穿著列），回答後第二張問人物樣貌／穿著／動作，穿著列「含你講的 sandals」；採用 #17767「溫柔微笑與草帽穿搭」上半身照它的（頭部配件預設也照它的）。泡泡依序「採用〈溫柔微笑與草帽穿搭〉…」→ 伺服器整句；追問已 2/2，直接定稿，`sun hat`、`white sundress` 洋紅；儀表板上半身由虛線變實心（covered），已涵蓋 5/31 → 9/31 |

API 層是用 SSE 直接打分支 `feat/set-recommendations` 的 API（本機 5010 埠），沒開前端。session：S1–S3 `5c422dd6…`（prompt_version 依序 7329f17c68b4、0424e1916433、f1c04de38f36），S4 `ec15eb6e…`（82e0ed128450）。全程沒有 `Turn_Failed`、`Protocol_Violation`。

- `facet_tags` 以 `scripts/backfill_facet_tags.py` 回填開發庫。第一輪留下 863 筆 `{}`、other 23.4%：模型常把該筆以「 | 」串起的整行 tag 當成一個 tag 回傳，對不上原字而整筆被丟掉。修正後（tag 改以 JSON 陣列送出、合併回來的 key 拆開比對）用 `--redo-empty` 重送這 863 筆，現在 19,354 筆 NULL 0、`{}` 33 筆（抽查是 tag 確實不屬於該筆提供的 facet），other 約 20.5%。第一輪裡部分 tag 被合併回傳而落進 other 的列不是 `{}`，`--redo-empty` 不會重送；要找回得整表重跑（約 970 次呼叫），這次沒做。
- seed-v2：dump 在本機匯出（104.8 MB，`facet_tags` 在內），Release `seed-v2` 已於 2026-09-25 發布，`docker-compose.yml` 的 `SEED_URL` 預設值已改成 seed-v2（`8d9666c`）。⏳ 新 volume 從 seed-v2 起整套 stack 的驗證還沒記錄。

## 2026-09-25 測試用審查開關（2026-09-29 已跑）

`.env` 設 `SAFETY_ALLOW_DISABLE=true` 後 `docker compose up -d --build api`。沒設時 T1 以外的案例都跑不了。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| T1 | 不設 `SAFETY_ALLOW_DISABLE`，開前端 | 頂列沒有「程式端審查」開關；`GET /api/config/safety` 回 `canDisable: false`；直接打 `messages` 帶 `"safety":"off"` 回 403 | ✅ `canDisable:false`；頂列只有「使用知識庫」「顯示檢索細節」；`messages` 帶 `"safety":"off"` 回 403 與設定提示。註：Git Bash 用 `curl -d` 直接帶中文時 body 會被轉碼壞，框架回空 body 的 400；改用 UTF-8 檔案（`--data-binary @file`）才是 403 |
| T2 | 設了之後開前端 | 頂列多「程式端審查」開關，預設開著 | ✅ `canDisable:true`，頂列多「程式端審查」，`aria-checked=true`（以 `SAFETY_ALLOW_DISABLE=true docker compose up -d api` 臨時覆蓋，`.env` 未動） |
| T3 | 開關開著，送「穿著改成比基尼泳裝」 | 跟以前一樣可能被 `Blocked_NSFW` 擋 | ✅ `Blocked_NSFW`「輸入被判定為不當內容：要求穿著比基尼泳裝…」 |
| T4 | 關掉開關（整組轉洋紅、出現「已關閉（測試用）」），重送同一句 | 不被我們擋（Gemini 自己仍可能 `Blocked_Upstream`）；audit `Turn_Completed` 的 payload 有 `"safety":"off"` | ✅ 開關轉 magenta、顯示「已關閉（測試用）」；同一句定稿（`bikini top`、`bikini bottom`、`swimwear`），audit `Turn_Completed` `safety: off` |
| T5 | 開關關著，採用一套含 `see-through` 的穿著組合（如 #41745） | 定稿，不出現 `Blocked_Output`（`Blocked_Upstream` 仍可能） | ✅ `8f5b47db…`：描述賽博風發光比基尼＋透視乳膠，定稿後穿著推薦第一套就是 #41745；「全部照它的」採用，定稿含 `see-through`、`latex clothes`，沒有 `Blocked_Output`（`safety: off`、`adopted=14`）。另見 known-issues #10「照它的取代 covered facet 時舊 tag 沒拿掉」 |
| T6 | 重新整理 | 開關回到開著 | ✅ 重新整理後 `aria-checked=true`、沒有「已關閉」字樣 |
| T7 | 開關關著跑到定稿，按「存進共享知識庫」送出 | 顯示「這份定稿是在關閉程式端審查時產生的…」，`save-to-shared` 回 409，`shared_prompt_histories` 沒有新列；打開開關再定稿一次後可以存 | ✅ 關著定稿 → 儲存顯示該訊息、`save-to-shared` 409、列數仍 6306。同一段（`8f5b47db…`）打開開關再定稿被 `Blocked_Output` 擋（內容含 see-through，審查確實回來了），所以另開無害對話 `c9cc2f19…` 重跑：關 → 定稿 → 409；開 → 改背景再定稿 → 200、舊卡顯示「已被後面的定稿取代」。存進去的那列驗完已刪除 |

## 2026-09-29 facet 層級向量（2026-09-30 已跑）

前置：開發庫套 `db/migrations/003`、跑 `scripts/embed_facet_tags.py`（37,011 筆、0 批失敗、737 秒）；master `d227284` 建的 compose，F3、F5 在 `fix/facet-threshold-and-query`（門檻 0.30、`SearchFacetSql` 先取前 k 再 join）上重跑。Claude 以 Playwright（`playwright-core` 驅動系統的 Edge，headless）跑，實際呼叫 Gemini。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| F1 | 送「一個銀髮少女穿涼鞋站在雨夜街頭」，開「顯示檢索細節」 | 鞋履項目的查詢句顯示「涼鞋（sandals）」、標「facet 向量」、池約 338；前 5 名的鞋履 tag 都是 sandal 類且互不相同 | ✅ `952caf30…`：「涼鞋（sandals）」「facet 向量」「池 338 → 5」，前 5 名 `sandals`、`tabi, sandals`、`white sandals`、`sandals with straps`、`toeless footwear, high heel sandals`（0.197–0.278，與離線重跑逐筆相同）。髮型項目「銀髮少女（silver hair）」也走 facet 向量 |
| F2 | 多送 5 句不同描述（含穿著、髮型、場景、風格），查 audit `Tool_Invoked` 的 `SearchPresets` args | facet 項目帶 `tags` 的比例；低於一半就要回頭加強工具說明 | ✅ 連同 F1、F3 共 9 次 `SearchPresets`、39 個 facet 項目，39 個都帶 `tags`（100%）。翻譯例：窗台 → `on a windowsill`、俯視角度 → `bird's-eye view, from above`、撐傘 → `holding umbrella` |
| F3 | 送「一個女生穿拖鞋在海邊」，等追問／定稿的推薦 | 穿著維度若字面錨 `slippers` 命中不到 2 筆，出現「接近你講的 slippers」；audit `recommendations.dimensions[]` 有 `similar:true`；看前 3 套的鞋履 tag 是否合理（門檻 0.30，驗收時由 0.23 放寬） | ⚠️ 路徑正確，但線上 6 次都沒走到近似錨，原因都在模型行為：拖鞋 → `slippers` 字面 3 筆，顯示「含你講的 slippers」（正確）；夾腳拖、木屐 → 追問輪沒問穿著，穿著沒推薦；夾腳拖＋「直接給我」 → 定稿 positive 有 `sandals`，字面錨命中（正確）；木鞋＋「直接給我」 → 第二輪 `Protocol_Violation` 後 `Turn_Failed`（`ProtocolViolationException`，沒有 400，#3 的修正有效）；「衣服還沒想好」 → 模型把鞋履標成 missing（note「使用者委託此項」），沒有錨。改用正式 SQL＋真 embedding 驗模型實際給過的錨：`flip-flops` 字面 1 筆 → 近似 3 筆（本身 0.137、兩套 `sandals` 0.294）→ 「接近你講的 flip-flops」；`slippers` → 字面 3 筆；`wooden clogs`、`geta` → 0.30 內不到 2 筆 → 無錨。門檻實測表見設計 §6.4 |
| F4 | 對一個 `preset_facet_embeddings` 為空的庫（或暫時 `TRUNCATE` 後還原）送 F1 那句 | 鞋履項目標「整套向量」、池 396、照常回結果 | ✅ 刪掉鞋履 338 列後 `bbce8b18…`：「整套向量」「池 396 → 5」、分級用 0.25／0.30（0.283 中、0.301 低），照常回結果；之後 `embed_facet_tags.py` 補回 338 列（5 秒，其餘 36,673 不變） |
| F5 | `EXPLAIN ANALYZE` `SearchFacetSql`，`facet_id = 'scene.location'`；`EXPLAIN ANALYZE` `RecommendSimilarSql`，`facet_id = 'clothing.upper'`（衣著類最大的 facet） | 兩者 Execution Time 都是個位數毫秒級 | ⚠️ 不是個位數，但可接受。原 `SearchFacetSql` 地點類型中位數 69 ms：去重後 2,904 組直接 join，規劃器把片段表 19,354 列全表掃一遍建 hash。改成先取前 k 再 join 後 33.2 ms（上半身 22.2、鞋履 2.4 ms），結果逐筆相同；只算距離的下限約 14 ms。`RecommendSimilarSql` 上半身 0.23 時 29.6 ms、0.30 時 27.0 ms（主鍵 nested loop，無全表掃描）。拆解與為什麼不是改 HNSW 見設計 §7.1 |

## 2026-09-30 推薦組法（看過延後、探索位、換一批）

設計：`docs/superpowers/specs/2026-09-30-recommendation-slate-design.md`。Claude 用 Playwright（`playwright-core` 驅動系統的 Edge，headless）跑，`docker compose up -d --build api frontend` 重建後執行，實際呼叫 Gemini。腳本放 scratchpad，不進 repo。G1–G4 同一個 session（`a62c51fd8bb74a63b39dff3a7d2189bd`），G5 另開一個乾淨的 browser context（新 session `ecc143f6c0e2406db6e5750ffb9fa695`）。全程沒有 `Turn_Failed`、`Protocol_Violation`。

> 2026-10-05 起追問卡不再推薦，G5 已不適用。

| # | 情境 | 期望 | 結果 |
| :--- | :--- | :--- | :--- |
| G1 | 定稿卡 | 每排 2 相關＋1 探索、每套有理由、探索位虛線 | ✅ 輸入「一個穿涼鞋和白色洋裝的少女坐在海邊」，第 1 輪追問（風格／場景／鏡頭），回「其他都你決定，直接定稿」後第 2 輪定稿。六個人像維度（風格、場景、鏡頭、人物樣貌、人物動作、人物穿著）每排恰好 3 張、2 相關＋1 探索，本次沒有任何維度候選不足。理由文字逐項核對：有錨的維度是「含你講的 X」（如場景「含你講的 beach」、人物穿著「含你講的 white dress」、人物動作「含你講的 sitting」）；風格維度沒錨到，2 個相關位都是「最接近你描述的」（`reason:"query"`）；每排第 3 張都是「換個搭法」（`reason:"explore"`），DOM 上都帶 `outline-dashed` 樣式與 `data-reason="explore"`。排頭沒有列層級文案（`d.batch != null` 時該 `<span>` 不渲染）。截圖 `g1-final.png`、逐維度 DOM 結構存於 `g1-final.json`（scratchpad） |
| G2 | 換一批 | 接成第 2 批、不整批重複、audit 有 Recommendations_Next | ✅ 在「人物穿著」排按「換一批」：右邊多一條分隔線標「第 2 批」，接上 3 張新卡（`溫柔微笑與草帽穿搭`「含你講的 sandals」、`白色連身裙`「含你講的 white dress」、`休閒夏日穿搭`「換個搭法」）。批次 1／2 的 `presetId` 完全不重疊（批 1 `[11602,4477,21143]`，批 2 `[17767,9553,19704]`）。`audit_logs` 第 2 輪多一筆 `Recommendations_Next`：`{"batch":2,"dimension":"clothing","sets":[{"presetId":17767,"reason":"anchored",...},{"presetId":9553,"reason":"anchored",...},{"presetId":19704,"reason":"explore",...}]}`。截圖 `g2-after-nextbatch.png` |
| G3 | 從第 2 批採用 | adoption.batch = 2 | ✅ 在第 2 批裡採用「溫柔微笑與草帽穿搭」（`presetId 17767`），對照表按「全部照它的」後「確定採用」。使用者泡泡變伺服器整句後跳出第 3 輪新定稿卡。`audit_logs` 第 3 輪 `Turn_Completed.payload.adoption` = `{"presetId":17767,"dimension":"clothing","batch":2,"take":["clothing.head","clothing.upper","clothing.footwear"],"filled":["clothing.head"],"replaced":["clothing.upper","clothing.footwear"]}`，`batch` 確實是 2；`tagOrigins.adopted` 從 0 變 3 |
| G4 | 定稿後修改 | 新定稿卡不整排重複上一張；舊卡沒有換一批 | ✅ 在第 3 輪之後說「鞋子換成靴子」，得到第 4 輪新定稿卡：人物穿著排錨到「含你講的 boots」（`海灘奔跑少女`、`俏皮坐姿與斗篷` 皆錨中 boots）＋「換個搭法」（`夏季休閒洋裝`）。新一批 `presetId [18593,17817,7371]` 跟第 2 輪（採用前）人物穿著批 1 的 `[11602,4477,21143]`、第 3 輪（採用後）批 1 的 `[11447,41601,14515]` 都不重疊。第 2 輪、第 3 輪這兩張舊定稿卡的所有維度排都不再出現「換一批」（`data-action="next-batch"` 不存在，`adoptable` 只認 `latestRecommendableTurn`），只有第 4 輪（最新）有。截圖 `g4-after-boots.png` |
| G5 | 追問卡 | 跟改前一樣 | ✅ 開新對話（新 browser context，乾淨 localStorage），只送「一個女生」。模型判定題材後追問風格／場景／鏡頭；追問卡的「參考組合」三排都是排頭列層級文案「最接近你描述的組合」（沒有錨到任何字面 tag），每一套底下都沒有 `data-reason` 屬性、也沒有每套理由文字，沒有任何「換一批」按鈕。`audit_logs.Turn_Completed.payload.recommendations.dimensions[*]` 沒有 `batch` 欄位（追問卡的推薦本來就不分批），跟改動前的欄位形狀一致 |

補充觀察：

- 這次驗收六個人像維度在 G1 都拿到滿額的 2＋1，沒有出現「候選不足只給 1～2 張」的情況；候選不足的分支這次沒有實證，之後如果要驗可以挑一個 `preset_facet_embeddings` 覆蓋率低的冷門 facet 组合。
- headless Edge 對外部圖床（Civitai／Kisegae）的縮圖有時在截圖那一刻還沒畫出來（`g1-final.png` 少數格子空白），但 `<img>` 的 `src` 與來源標籤都在，換一批後的截圖（`g2-after-nextbatch.png`）縮圖正常顯示；判斷是這個環境對外部圖檔的載入時序問題，不是產品缺陷，DOM 斷言不受影響。
- `Recommendations_Next` 這一輪呼叫的 `latency_ms` 中位數 538ms（n=13，其中 1 筆 2432ms 的暖機樣本），沒有拖慢互動；數字與量測方式見設計 §8。

## 2026-10-05 先確認再動手（含追問卡不再推薦）

設計：`docs/superpowers/specs/2026-10-05-confirm-before-act-design.md`。Claude 用 Playwright（`playwright-core` 驅動系統的 Edge，headless）跑，`docker compose up -d --build api frontend` 重建後執行，實際呼叫 Gemini。腳本放 scratchpad，不進 repo。

| # | 操作 | 預期 | 結果 |
| :--- | :--- | :--- | :--- |
| C1 | 送「一位金色短髮的中年女士拿著相機和飲料站在雨夜的霓虹街頭」 | 確認卡複述畫面；按下前儀表板不變；按「對，就這樣」後出追問卡，沒有推薦條 | ✅ `6d704d21…` 第 1 輪確認卡「我理解的畫面是：一位金色短髮的中年女士，手裡拿著相機和飲料，站在下著雨的霓虹街頭。風格、鏡頭、穿著等其他面向還沒指定。」，`choices` 空、一顆「對，就這樣」；輸入框 placeholder 變成「按上面的按鈕套用；在這裡打字會當成修正」。送出前與確認輪後各取一次 `GET /api/sessions/{id}`：`profile` 都是 null、`Collecting`、`facetStates` 相同，儀表板 `data-state` 也相同（題材尚未判定）。按下後泡泡「對，就這樣」，第 2 輪追問卡問風格／鏡頭，`profile=portrait`、已涵蓋 5/31；`[data-card="ask"] [data-section="recommendations"]` 0 個、該輪沒有 `recommendations`；舊卡停用，title「已經有新的進展，這張卡不能再按」；audit 第 2 輪 `confirmed: {turnIndex: 1}`。修正輪 2 後重跑 `a9082735…`：卡片「我理解的畫面是：一位金色短髮的中年女士，手裡拿著相機和飲料，站在雨夜的霓虹街頭。風格、鏡頭等細節還沒指定。」，沒有預告下一步；按下前 `GET` 與儀表板不變；按下後追問風格／鏡頭，沒有推薦條 |
| C2 | 用選項回答追問 | 先出確認卡（「我會把風格設成…」），按下後才追問或定稿 | ✅（修正輪 2 之後）確認卡只講要設什麼，不預告下一步，流程照伺服器的閘門走。`a9082735…`、`0f512059…` 兩段各三張卡：6/6 沒有預告「接著問」或「直接定稿」；每張按下前 `GET /api/sessions/{id}` 與儀表板都不變，按下後泡泡是「對，就這樣」。`a9082735…` 第 3 輪「我理解的畫面是：一位金色短髮的中年女士，手裡拿著相機和飲料，站在雨夜的霓虹街頭，採用寫實攝影風格與半身特寫鏡頭。場景、穿著、樣貌與動作的其他細節還沒指定。」→ 第 4 輪追問場景／穿著／樣貌（2/2）→ 第 5 輪確認卡 → 第 6 輪定稿。`0f512059…` 第 3 輪「…採用日系動漫風與柔和色調，搭配半身特寫與淺景深散景。…」→ 第 4 輪追問場景／樣貌／穿著 → 第 6 輪定稿。兩張追問卡都沒有推薦條。**之前**：原始驗收時卡片說「確認後我們就直接定稿，其餘未指定的面向將保持留白」（`6d704d21…` 第 3 輪、`31be94cf…` 第 4 輪），動手輪卻追問。修正輪 1 讓卡片照伺服器先算好的「確認之後的下一步」寫，回答第一次追問那張仍只有 2/6 講對，所以修正輪 2 改成不預告 |
| C3 | 定稿後送「讓她拿雨傘」，跑 3 次 | 3 次都是有選項的確認卡；選「換掉飲料，改拿雨傘」後定稿有雨傘與相機、沒有飲料 | ✅ 3/3，`flow-propose.md` 沒調。每次開新對話，送「一位女生雙手拿著相機和飲料站在街頭，其他你決定，直接給我」，按確認後定稿（含 `holding camera` 與 `holding drink`／`holding beverage`），再送「讓她拿雨傘」。① `61d2a2e0…`「她雙手已經拿著相機和飲料，如果要拿雨傘會衝突。你希望怎麼調整？」3 顆：「換掉飲料，改拿雨傘」「換掉相機，改拿雨傘」「相機、飲料和雨傘三樣都拿（可能較擁擠）」；② `544c12f2…`「她目前雙手已經拿著相機和飲料，再拿雨傘會拿不下。你想要哪一種？」3 顆：「換掉飲料，改拿雨傘」「換掉相機，改拿雨傘」「三樣都拿（相機、飲料、雨傘）」；③ `483c04b7…`「她原本雙手已經拿著相機和飲料，再拿雨傘會衝突。你希望怎麼調整？」3 顆：「換掉飲料，改拿雨傘（相機保留）」「換掉相機，改拿雨傘（飲料保留）」「三樣都拿（相機、飲料、雨傘）」。三次確認輪後 `facetStates` 與定稿都不變；按第 1 顆後泡泡是選項原文，新定稿有 `holding umbrella`、`holding camera`，沒有 drink／cup／bottle／beverage 類 tag；跟前一張定稿比，三次都只把 `holding drink`（②是 `holding beverage`）換成 `holding umbrella`，其餘 tag 相同。② 第一次跑（`9bbb5888…`）在前置的定稿輪就 `Turn_Failed`（`Timeout`），沒走到雨傘那步，換新對話重跑。②③ 的「三樣都拿」沒註明代價。修正輪之後再跑兩次（`f54a75eb…`、`79aaa3bb…`），都給 3 顆解讀，選「換掉飲料，改拿雨傘」後只把 `holding drink` 換成 `holding umbrella`。修正輪 2 後 `829c981b…` 也過：這次第 1 顆是「換掉相機，改拿雨傘」，選第 2 顆「換掉飲料，改拿雨傘」，結果相同 |
| C4 | 定稿後送「鞋子換成靴子」 | 單一提案卡；按下後重新定稿 | ✅ `6d704d21…` 第 7 輪「我會將鞋履從原本的留白改為靴子（combat boots），其他內容維持不變。確認後會為您重新定稿！」，一顆「對，就這樣」，`facetStates` 與定稿不變；按下後第 8 輪定稿只多 `combat boots`（`facetTags["clothing.footwear"]="combat boots"`），其餘 tag 相同。正文夾了英文 tag，prompt 要求不寫 |
| C5 | 確認卡沒按時問「寫實跟動漫差在哪」 | `Discuss` 回答；之後舊確認卡仍可按且有效 | ✅ `31be94cf…` 第 1 輪確認卡「我理解的畫面是：一位銀髮少女坐在窗邊看書。風格、鏡頭、場景等細節還沒指定。」；第 2 輪問「寫實跟動漫差在哪」→ audit `Tool_Invoked` `Discuss`、`MessageOutcome`，回一段寫實／動漫的差別，狀態不變，第 1 輪的卡仍可按。按下後第 3 輪追問（風格／場景／鏡頭），`profile=portrait`，audit `confirmed: {turnIndex: 1}`。`Discuss` 帶了 `facetStates`，profile 還是 null，伺服器記「Profile 為 null，facetStates 忽略」後略過 |
| C6 | 確認卡出現後在輸入框打「好」 | 出新的確認卡，不動手 | ✅ `31be94cf…` 用選項回答追問 → 第 4 輪確認卡；打「好」→ 第 5 輪 `ConfirmOutcome`「要套用的話請按下面的按鈕。我理解的畫面是：一位銀髮少女坐在溫馨室內陽光灑落的窗邊看書，採用寫實攝影風格與半身特寫（視線齊平）鏡頭。」；`facetStates` 不變、追問仍 1/2；第 4 輪的卡停用，第 5 輪的可按 |
| C7 | 動手後看舊確認卡；curl 帶舊輪次送確認 | 舊卡停用；curl 409「只有最新一張確認卡可以按」 | ✅ `6d704d21…` 第 4 輪動手後，第 1、3 輪的卡都停用（title「已經有新的進展，這張卡不能再按」）。curl（`--data-binary @file`）：定稿後沒有待確認時送 `{"confirm":{"turnIndex":7,"choice":null}}` → 409「沒有待確認的內容」；再送一句「背景改成白天的公園」得到第 10 輪確認卡後，重送同一個 body → 409「只有最新一張確認卡可以按」。瀏覽器腳本在第 7 輪的卡待按時送 `turnIndex: 1`，同樣 409 |
| C8 | 送「直接給我」 | 確認卡列出打算補的內容；按下後定稿 | ✅ `31be94cf…` 第 6 輪追問（表情／臉部／上半身，2/2）後送「直接給我」→ 第 7 輪「我會幫您補上溫柔微笑的表情與寬鬆針織毛衣的穿著，並直接將畫面定稿輸出提示詞。」，狀態不變；按下後第 8 輪定稿含 `gentle smile`、`oversized knit sweater`。`6d704d21…` 第 5 輪「其他你決定，直接給我」也一樣：卡片列「霓虹燈光、微笑表情與時尚風衣外套」，定稿含 `neon lights`、`smiling`、`stylish trench coat`。卡片沒列具體內容時，定稿可能沒補（見下方補充觀察）。修正輪 1：`8af7e8b5…`、`08d5420c…` 先描述「一個女生在海邊散步」，追問後送「直接給我」。兩張卡都只列維度名稱（「補齊風格、鏡頭、場景、樣貌、動作與穿著」），定稿 `tagOrigins.llm` 是 26 與 25。修正輪 2：`48070db9…` 同樣的路徑，卡片「我將為您自動補齊風格（寫實攝影風格）、鏡頭（全身平視、淺景深）、場景（夕陽海灘、晴朗天氣）、樣貌（長髮、溫柔微笑）、動作（向前看、散步）與穿著（白色洋裝、赤腳），並直接定稿。」，定稿照著補（`photorealistic, beach, sunset, clear sky, full body, eye level, bokeh, long hair, gentle smile, walking, looking forward, white sundress, barefoot`，`llm` 10、`rag` 5） |
| C9 | 確認卡沒按時重新整理 | 卡片回來，照樣能按 | ✅ `31be94cf…` 第 5 輪的卡待按時重新整理：session id 相同，3 張確認卡都回來，只有最後一張可按（可按的按鈕 1 顆），placeholder 仍是「按上面的按鈕套用…」；按下後第 6 輪追問，audit `confirmed: {turnIndex: 5}` |
| C10 | 從定稿卡採用一套；curl 在追問階段送採用 | 採用直接動手、不出確認卡；curl 409「定稿後才能採用組合」 | ✅ `6d704d21…` 第 8 輪定稿卡的人物穿著列採用 #41743「賽博龐克霓虹勁裝」（全部照它的）→ 第 9 輪直接 `FinalizedOutcome`，中間沒有確認卡（確認卡總數仍是 4），泡泡是伺服器整句「採用〈賽博龐克霓虹勁裝〉（知識庫 #41743）：…」，audit `adoption.filled=["clothing.head","clothing.lower"]`、`replaced=["clothing.upper","clothing.footwear"]`。追問階段送 `{"adopt":{"presetId":41593,…}}`：`6d704d21…` 第 2 輪後（`Collecting`、追問 1/2）與 curl 開的 `1635a8ef…` 第 2 輪追問後都回 409「定稿後才能採用組合」；同一個對話 `confirm`＋`adopt` 一起送 → 400「confirm 與 adopt 不能同時送」 |

耗時（從 `docker compose logs api` 的 `Turn …` 摘要行算中位數；這是修正輪之前的 prompt 量到的，修正輪之後的 build 沒有重算）：確認輪 4,177 ms（`ConfirmOutcome` 17＋`MessageOutcome` 1，n=18，3,115–6,645）、動手輪 6,977 ms（`AskOutcome` 5＋`FinalizedOutcome` 11，n=16，4,164–11,594；含 1 輪採用 11,594）。不含唯一一輪 `Turn_Failed`（`Timeout`，120,013 ms）。

2026-10-05 的驗收：分支 `feat/confirm-before-act` `8ce9de4`。先跑全套測試：.NET 473 過、18 略過（integration）、0 失敗；前端 Vitest 110 過、`vue-tsc --noEmit` 0 錯（本機 `npm run build` 被 Windows 應用程式控制擋下，前端實際建置用的是 Docker）；`scripts` ruff 全過、pytest 280 過。接著 `docker compose up -d --build api frontend`，兩個 image 都建成功，`/health` 200。session：C1、C2、C4、C7、C10 `6d704d21e4594d2bad168517d92a7c7d`；C5、C6、C8、C9 `31be94cf7a704ead867e64ed205bd56e`；C3 見該列；C7、C10 的 curl 另用 `1635a8ef8bbd4fbe9e904409d30a52c2`。整段只有 `9bbb5888…` 第 2 輪 `Protocol_Violation`（attempt 1）後 `Turn_Failed`，沒有被攔。

修正（同日兩個 commit：`fix(api): confirm cards state the real next step and delegation fills every missing facet`、`fix(api): confirm cards stop announcing the next step; delegation still finalizes directly`），處理 C2 與「直接給我」定稿沒補齊兩件事。現行做法：

1. **確認卡不預告下一步**（第一次描述、回答追問）。確認輪猜不到一段自由回答會讓哪些 facet 變 covered：修正輪 1 由伺服器先照動手輪的規則算好「確認之後的下一步」給它照寫，回答第一次追問那張仍只有 2/6 講對。所以卡片只講要設什麼，接下來是追問還是定稿，看下一張卡。`flow-propose.md` 第 2 條寫明不要預告。「隨便／你決定／直接給我」例外：動手輪一律直接定稿，卡片照舊說直接定稿。
2. **「隨便」補齊每一個 missing facet。** 確認卡逐維度寫出補成什麼，Session 事實多一行「還有 missing facet 的維度」讓它照著列，只列維度名稱不算。動手輪的 `flow-act.md` 第 4 條，以及伺服器組的「使用者已確認」區塊（待確認是 `AutoComplete` 時），都寫明補齊就是確認的內容，不算「確認以外的改動」。
3. Session 事實的追問改成「追問已用：N／上限 M」。
4. **兩個流程段寫明工具的完整名稱**（`Dialog_Confirm`、`Dialog_FinalizePrompt`…）。原因見下方第 3 條：改了流程段的文字之後，模型常只寫 `Confirm`。

修正輪 2 的 build 實際呼叫 Gemini 共 20 輪（確認 10、追問 5、定稿 5），沒有 `Turn_Failed`、`Protocol_Violation`，也沒有被攔；C1、C2、C3、C8 見各列。「直接給我」之後定稿的 `tagOrigins.llm`：修正前 0、1、21（C3 前置）；修正輪 1 是 3、11、26、25；修正輪 2 是 10（C8）、5（C3 前置，另有 `rag` 7）。

補充觀察：

- **確認卡講的下一步跟動手輪對不上**（C2，已改成不預告）。原始驗收時卡片說「直接定稿，其餘留白」，動手輪照 `flow-act.md` 第 1、2 條，還有 missing 而且有 `AskUser` 就追問，兩次都這樣。修正輪 1 讓伺服器先算好下一步，第一次描述的卡片講對了，回答第一次追問那張仍約一半講成直接定稿。動手輪的模型自己也常先想定稿，被伺服器的定稿閘門擋回去才追問：`a9661846…` 第 4 輪先呼叫 `FinalizePrompt`，回「還有 18 個 facet 缺少…請先呼叫 AskUser」。下一步其實是閘門決定的，所以修正輪 2 讓卡片不預告。
- **「直接給我」的卡片沒列具體內容時，定稿可能幾乎沒補。** C3 的前置用「其他你決定，直接給我」，三張卡片都沒照 `flow-propose.md` 第 2 條列出具體內容（「由我幫你隨機補齊」「由我為您決定合適的風格、鏡頭…」「補齊風格、場景光源、鏡頭與穿著等細節」）。其中兩次動手輪定稿只有 `1girl, street, holding camera, holding drink` 這種程度（audit `tagOrigins.llm` 0 與 1），風格、鏡頭、穿著都沒補；另一次補了 21 個 llm tag。C8 與 `6d704d21…` 第 5 輪卡片有列具體內容，定稿就照著補。原因是動手輪的「不要加入確認以外的改動」壓過了「你決定」：照 `SafetyClassifier` 的輸入提示重跑，這三句的 `wantsAutoComplete` 都是 true，所以 AutoFill 是開的，只是模型沒補。修正之後定稿都有補（見上方「修正」段的數字）。卡片逐項列出補成什麼的比例：修正輪 1 是 1/2，修正輪 2 是 1/2：`48070db9…` 逐項列出；`829c981b…` 的第一句話本身就是「隨便」、還沒有 profile，卡片漏列了一個維度（只列「日系寫實的風格、半身平視的鏡頭、霓虹夜景的燈光以及休閒日常的穿著」）。
- **一輪動手輪逾時。** `9bbb5888…` 第 2 輪（「其他你決定，直接給我」按下後）：`SetProfile`、`SetFacetStates`、`SearchPresets` 之後 13 秒出現 `Protocol_Violation` attempt 1，之後 120 秒內共呼叫 Gemini 47 次，工具呼叫仍只有 3 次，最後 `Turn_Failed`（`Timeout`，`upstream.calls=47`）。log 看不出這 44 次呼叫回了什麼。同一句在其他三個對話都正常。回滾正確：事後 `GET` 是第 1 輪、profile null；用 curl 對同一個 session 再送 `{"confirm":{"turnIndex":1,"choice":null}}`，待確認還在，這次定稿成功（同樣沒補：`1girl, standing, city street, holding camera, holding drink`）。

  修正輪查到原因（在 Gemini 回應 log 暫時印出 functionCall 名稱，查完已拿掉）：模型呼叫工具時有時只寫 `Confirm`／`Discuss`，宣告的名稱是 `Dialog_Confirm`。SK 回「Error: Function call request for a function that wasn't defined.」，模型就重送同一個呼叫，一直到 120 秒逾時。一輪呼叫 Gemini 80–100 次，tools=0，4 個並行的確認輪合計 182 次裸名 `Confirm`。發生率跟 prompt 是否與先前逐字相同很有關：沒改過的 prompt 12/12 正常；只加一個空格是 4/6；修正輪的規則文字 3/20 到 0/12；兩個流程段寫明完整名稱之後 12/12 正常，修正輪 1 最後的 build 20 輪、修正輪 2 又改過文字的 build 20 輪，都沒有逾時。上面 `9bbb5888…` 那輪應該也是同一個原因，沒證實。程式端還沒有防護：SK 遇到沒宣告的工具會一直重試，不會提早結束這一輪。這要另外處理。（同日已補：`GeminiToolNameHandler` 把裸名改回全名，拿掉流程段的完整名稱提示重測 16 輪，8 輪寫裸名、全部成功，見 known-issues 已修正 #13。）

## 2026-10-06 檢索時機（Q1–Q3）

設計見 [檢索時機設計](superpowers/specs/2026-10-06-retrieval-timing-design.md) §6。用 `manual-tests/replay.py` 照劇本跑（`manual-tests/replay_scenarios.json`），改前、改後各 3 次；看每步的「檢索判定」與 `scripts/adoption_report.py --sessions …` 的「檢索時機」一節。

| 編號 | 劇本 | 預期 |
| :--- | :--- | :--- |
| Q1 | 2026-10-05 session `ddaf2115` 的原話：第一句描述 → 追問選第一個 → 追問選第一個＋「衣服你幫我設計」→「家居感的衣褲」→「你推薦一些場景設計讓我參考」→「深夜咖啡廳前（帶溫暖燈光與招牌）」→「改成黑長直髮的上班族女士」→「穿回家居服，頭髮要齊劉海，年齡是年輕上班族」 | 每個動手輪都檢索；「衣服你幫我設計」與「推薦」的確認輪也檢索；定稿不再出現 `comfortable lounge wear top` 這類知識庫沒有、不像 SD tag 的寫法 |
| Q2 | 「一隻在森林裡的狐狸」→「其他隨便，你決定」 | 「隨便」的確認輪先檢索，卡上列的內容來自片段；動手輪檢索並補齊 |
| Q3 | 「穿和服的少女在神社前」→ 追問選第一個 ×2 →「讓她更有氣質」 | 模糊要求的確認輪先檢索再給解讀；動手輪檢索 |

結果：基準見 [實驗紀錄](experiments/2026-10-06-retrieval-timing.md) §3，改後見 §4（2026-10-06）。Q1：動手輪幾乎都查（只有第 3 次的「家居感的衣褲」漏），「推薦」3/3 有查，「衣服你幫我設計」的確認輪 0/3。Q2：「隨便」的確認輪 3/3 有查，動手輪沿用確認輪的結果沒再查。Q3：動手輪都查，「讓她更有氣質」的確認輪 0/3；有 1 輪確認輪協定違規失敗。第二輪見實驗紀錄 §6：Q1 第 1 次連續 4 個動手輪沒查，「衣服你幫我設計」的確認輪仍 0/3；Q2 動手輪記 CARRY；Q3「讓她更有氣質」的確認輪 2/3。

## 2026-10-09 定稿後生成預覽（R1–R8）

設計見 [定稿後生成預覽設計](superpowers/specs/2026-10-09-render-preview-design.md) §11.4。打真的 RunPod（做法 B 的 endpoint），每張約 US$0.0034；跑前跑後各記一次 RunPod 餘額。

2026-10-10：API 部分用 scratchpad 的 Python 腳本打 `localhost:5000`；瀏覽器部分 Claude 用 Playwright（`playwright-core` 驅動系統的 Edge，headless）跑，`docker compose up -d --build api frontend` 重建後執行，實際呼叫 RunPod 與 Gemini，腳本不進 repo。逾時調成 180 秒、審圖與自評改送縮圖（可行性 §9.4）之後全部重跑。

| 編號 | 操作 | 預期 | 結果 |
| :--- | :--- | :--- | :--- |
| R1 | 動漫風定稿 → 生成預覽 | 依序看到排隊／生圖中／審查圖片中 → 自評中時圖已出現 → 自評清單補上 | ✅ API（2026-10-10，改前）：閒置後第一張冷啟動 91 秒撞 90 秒逾時失敗（可行性 §9.4），逾時已調 180 秒；改後重跑冷啟動 39 秒完成。瀏覽器：看到 生圖中 → 審查圖片中 → 自評中…，自評中圖已顯示，清單 18 項，16 秒 |
| R2 | 按下後看按鈕；對話輪跑的時候看按鈕；另開分頁對同一 session `POST /renders` | 按鈕停用並顯示「上一張還在生成」；對話輪中也停用；另一個請求回 `409`「上一張還在生」 | ✅ 對話輪串流中按鈕 disabled、結束後恢復；按下後 disabled 並顯示「上一張還在生成」；API 重複送出回 `409`「上一張還在生」、舊卡回 `409`「只有最新一張定稿卡可以生成預覽」 |
| R2a | 按下後繼續聊天到定稿卡捲出畫面 | 完成時底部出現「預覽好了」，點了捲回那張卡 | ✅ 卡捲出畫面後完成時出現「預覽好了」，點了捲回那張卡、提示消失 |
| R3 | 寫實風定稿 | 按鈕旁有「預覽會是動漫風」，生出來是動漫風 | ✅ 寫實風定稿（`style.genre` = photorealistic）按鈕旁有「預覽會是動漫風」；動漫風定稿沒有。寫實風這張沒有實際生成（只有動漫系模型） |
| R4 | 重新定稿後在新卡上再生 | 同一個 seed，畫面差異來自 tag | ✅ API：銀色長髮 → 黑色短髮後在新卡生成，`done`。seed 由 `Session.RenderSeed` 固定（單元測試） |
| R5 | 生圖途中重新整理 | 回來後接著顯示 | ✅ 生圖中重新整理，回來後接著看到 生圖中 → 審查圖片中 → 自評中…，清單 18 項 |
| R6 | `SAFETY_ALLOW_DISABLE=true`、頂列關掉審查後生成 | 圖上方標「審查已關閉（測試用）」；audit 的 `Render_Completed` 沒有 `reviewMs` | ✅ 關掉審查後生成：圖上方「審查已關閉（測試用）」，沒有經過「審查圖片中」；audit 的 `reviewMs` 是 null |
| R7 | `Render__PerSessionLimit=1` 重建 api，同一個對話生第二張 | `429`「這段對話的預覽張數已達上限（1 張）」 | 沒在 compose 上跑（compose 沒映射這個設定）；由 `RenderEndpointTests.Unfinished_previous_render_is_409_and_session_limit_is_429` 涵蓋 |
| R8 | 讀 audit 的 `Render_Completed` | 記下 `reviewMs`、`selfCheckMs`、`delayMs`、`executionMs`，寫進 [ComfyUI 整合可行性](ComfyUI整合可行性.md) §9 | 已記進可行性 §9.4：審圖 14.6 秒、自評 12.3–16.7 秒，比估計的 2–5 秒慢很多 |
