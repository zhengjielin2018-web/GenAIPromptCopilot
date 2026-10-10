# 預覽圖的修正建議（手動輔助）：設計

日期：2026-10-10
來源：2026-10-10 brainstorm，接在[符合度評分](2026-10-10-intent-fit-scoring-design.md)（以下稱「評分設計」）之後。評分已經能判出每條要求是「prompt 漏了」還是「沒畫出來」，但沒有修正流程：使用者只能自己打字請助理改 prompt，而「沒畫出來」幾乎無解——同一個對話的 seed 固定，同一份 prompt 再生還是同一張圖（[eval-cases](../../eval-cases.md) R12）。專案擁有者決定先做手動輔助、自主閉環之後再做，兩案做完一起 merge。

---

## 1. 問題與目標

**目標**：

- 每張評完分的圖給**一個建議的下一步**，由程式依固定規則決定；一張圖同時有「prompt 漏了」與「沒畫出來」時，程式要判斷現在先做哪一個。
- 使用者按一下就照建議做：改 prompt 的建議送給助理（照常經過確認卡），換 seed 的建議直接重生。
- 「換 seed 重生」要真的做得到。
- 這套規則就是之後閉環的規則（評分設計 §14），手動版先驗證、累積數據（換第幾個 seed 才畫出來）。

## 2. 決定紀錄

| 題目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 兩種問題同時出現 | **先修 prompt、seed 不變；只剩沒畫出來才換 seed；同一條換了 N 個 seed 都畫不出來才改寫 tag** | prompt 有缺漏時換 seed 也補不出那樣東西，白花一張；seed 不變，前後兩張的差異才看得出是 prompt 造成的 |
| 按下建議之後 | **改 prompt 類直接送給助理**，照常跳確認卡；換 seed 直接重生 | 確認卡本來就是使用者把關的地方，不用在送出前多一道；想自己補充的人可以直接打字 |
| 改完 prompt 要不要自動生圖 | **不自動**，使用者自己按「生成預覽」 | 手動輔助的定位：每張圖都由使用者觸發，額度好掌握；自動生圖留給閉環 |
| 改寫的門檻 N | **設定 `Render:SeedsBeforeRewrite`，預設 2** | 額度有限（每段對話 10 張）；之後從 audit 看實際「換第幾個 seed 才畫出來」再調 |
| 委託項目畫錯 | **不建議修，但要註記** | 那是 prompt 與圖對不上，使用者沒在意、也不計分；不值得花一張額度，但要讓使用者知道 |
| 判斷放哪 | **後端純函式 `FixAdvisor`**，評分完成時算好存在紀錄上 | 「換了幾個 seed」要看同一段對話先前幾張圖，只有後端有；規則可測，閉環直接沿用。前端算不出跨圖次數；交給模型判斷不穩定，也違反「關鍵判斷由程式把關」 |
| 「剛好畫出來」的條目 | **只在本來就要改 prompt 時順便補**，不單獨觸發修正 | 換 seed 後如果不見了，會變成「prompt 漏了」，下一輪自然會補 |
| 換 seed 後的圖 | **取代卡上原本那張**；圖下方顯示 seed | 同一張卡不並排比較；並排要另外設計畫面與儲存，留給閉環 |
| 「請助理修改」按過之後 | **前端停用並寫「已送出修正」**，狀態不存後端 | 避免重複送出；重新整理後可再按，最多多送一句，確認卡照樣可以不按 |
| 記不記使用者有沒有照建議改 prompt | **不記** | 要記得在對話訊息的 API 加「來源」欄位，牽動對話流程；換 seed 由 audit 的 `reroll` 就看得到 |

## 3. 範圍

### 3.1 做

1. 後端：`FixAdvisor`、可換的 seed、`reroll` 參數、對外格式（`seed`、`selfCheck.suggestion`）、audit 欄位、`SeedsBeforeRewrite` 設定。
2. 前端：建議那一行、按鈕、註記、seed 顯示、`followSuggestion`。
3. 文件同步（§10）與實機驗收（§9.3）。

### 3.2 不做

- 自主閉環、自動生圖、自動改 prompt。
- 同一張卡並排顯示多張圖。
- 委託項目的修正（只註記）。
- 記錄使用者有沒有照「改 prompt」的建議做。
- 及格線、評分雜訊的處理（評分設計 §3.2）。

## 4. 建議的判斷規則（`FixAdvisor`）

**什麼時候算**：評分完成（`selfCheck.status` 是 `ok`）時算一次，跟評分結果一起存進紀錄。評分 `unavailable`、圖被擋下、生圖失敗都沒有建議。

**只看使用者的要求**（`source: user`），`unclear` 的條目不看。依序判斷，符合就停：

| 順序 | 條件 | `kind` | `text`（給使用者看的一行） |
| :--- | :--- | :--- | :--- |
| 1 | 有 `issue: prompt_missing` | `fix_prompt` | `建議：請助理補上「{text}」、「{text}」，seed 不變再生一張` |
| 2 | 有 `issue: not_rendered` 的條目，「沒畫出來的 seed 數」≥ N | `rewrite_tags` | `建議：請助理改寫「{text}」的寫法（換了 {k} 個 seed 都沒畫出來），seed 不變再生一張` |
| 3 | 還有 `issue: not_rendered` | `reroll` | `建議：換一個 seed 重生（「{text}」這次沒畫出來）` |
| 4 | 其他 | `none` | `null` |

- 第 2 條只列達到 N 的那幾條；第 3 條列全部 `not_rendered` 的。多條時用「、」串接。
- `itemIds`：這個建議針對的條目 id（第 1、2 條另含下面的「剛好畫出來」條目）。

**「沒畫出來的 seed 數」**：在同一段對話（`RenderRecord.Owner.Renders`，含這一張）裡，取評分 `ok`、`listKey` 跟這張相同的圖；對這條要求（同一個 id），只算 `issue` 是 `not_rendered`、而且 prompt（正向詞與負向詞）跟這張一樣**或**這條的 `tags`＋`negativeTags`（當成集合比較）跟這張相同的那些圖，數它們用了幾個**不同的 seed**。改 prompt 時 seed 不變，同一個 seed 生兩張只算一次；prompt 改過、tag 也改寫過就從頭算。（2026-10-10 實機後補上「prompt 一樣」：文字步每張重新對 tag，prompt 沒變也會對到不同的 tag，只比 tag 會被雜訊歸零，見 [eval-cases](../../eval-cases.md) R16。手動流程裡同一份清單下的圖 prompt 一定一樣——改 prompt 要使用者開口，一開口清單就換了；比 tag 留給閉環在同一份清單下改 prompt 的情況。）

**「剛好畫出來」**：使用者的要求、`verdict: met`、`tags` 與 `negativeTags` 都空。
- `kind` 是 `fix_prompt` 或 `rewrite_tags`：一併放進送給助理的話與 `itemIds`。
- `kind` 是 `reroll` 或 `none`：只加註記 `「{text}」prompt 沒寫，這次剛好畫出來；換 seed 可能就不見`（每條一則）。

**註記**（`notes`，依序）：
1. 委託項目（`source: delegated`）`verdict: unmet`：一則 `另外：模型幫你挑的「{text}」、「{text}」沒畫出來（不計分，不建議修）`。
2. 「剛好畫出來」（只在 `reroll`／`none`）：見上。
3. 同一段對話裡已經有一張圖的 seed、正向詞、負向詞都跟這張一樣：`這張的 prompt 和 seed 跟之前某張一樣，畫面不會變；上次的修正可能沒有改到 prompt`。

**送給助理的話**（`message`，只有 `fix_prompt`／`rewrite_tags` 有）：

- `fix_prompt`：`請修改 prompt：補上「{text}」、「{text}」。這是我要的，但目前的 prompt 沒寫進去。`＋（有剛好畫出來的條目時）`另外「{text}」這次剛好畫出來，也請寫進 prompt。`＋`其他地方不要動。`
- `rewrite_tags`：`請改寫 prompt 裡這些要求的寫法：「{text}」（目前是 {tags}，換了 {k} 個 seed 都沒畫出來）。可以換同義的 tag 或加權重。`＋（剛好畫出來）同上＋`其他地方不要動。`
  - `{tags}`：正向 tag 用「, 」串接；負向的寫成 `負向：{tag}`。

**門檻 N**：`RenderOptions.SeedsBeforeRewrite`，預設 2；`FixAdvisor` 用 `Math.Max(1, N)`，設成 0 或負數時當 1（現有的 `RenderOptions` 沒有啟動檢查，不為這一項新增）。

## 5. seed

- `Session.RenderSeed` 從「建 session 時隨機一次、之後固定」改成「目前的 seed」：可寫入，初值照舊隨機。
- 一般的「生成預覽」（含改完 prompt 後再按的）用目前的 seed，所以改 prompt 時 seed 自然不變。
- **換 seed 重生**：端點拿著 session 鎖時，隨機挑一個跟這段對話所有 `Renders` 的 seed、也跟目前 seed 都不同的值，放進 `RenderRequest`。`RenderService` **收件成功**（`RenderAdmission.Accepted`，而且不是在補審就被擋下）後，端點才把它寫回 `Session.RenderSeed`；被額度、「上一張還在生」、「人多」擋下時目前的 seed 不變。
- 只有生圖端點讀寫這個欄位；同一段對話一次只有一張預覽在跑，兩個換 seed 的請求同時到時，第二個會被「上一張還在生」擋下，不會搶寫。
- 不進 `SessionSnapshot`（對話回滾碰不到它，同現在）。

**端點**：`POST /api/sessions/{id}/renders` 的 body 多 `reroll`（可省，預設 `false`）。其他檢查照舊（只有最新的定稿卡、張數上限都算）。

```json
{ "turnIndex": 4, "safety": "on", "reroll": true }
```

## 6. 對外格式

`GET /api/sessions/{id}/renders/{renderId}`：最外層多 `seed`；`selfCheck` 多 `suggestion`。

```json
{
  "seed": 1834729,
  "selfCheck": {
    "status": "ok", "score": 80, "summary": "…", "items": [ … ],
    "suggestion": {
      "kind": "fix_prompt",
      "text": "建議：請助理補上「抱著貓」，seed 不變再生一張",
      "message": "請修改 prompt：補上「抱著貓」。這是我要的，但目前的 prompt 沒寫進去。其他地方不要動。",
      "itemIds": ["r3"],
      "notes": ["另外：模型幫你挑的「白色洋裝」沒畫出來（不計分，不建議修）"]
    }
  }
}
```

- `kind`：`fix_prompt`／`rewrite_tags`／`reroll`／`none`。
- `text`：`none` 時 `null`。`message`：只有 `fix_prompt`、`rewrite_tags` 有，其他 `null`。
- `suggestion` 跟 `score`、`items` 一樣，**只在 `done` 而且評分 `ok` 時有**，否則 `null`。
- `seed` 一律有（收件時就定了）。

## 7. 後端元件

| 元件 | 位置 | 改什麼 |
| :--- | :--- | :--- |
| `FixAdvisor` | `Rendering/FixAdvisor.cs`，新增，純函式 | `Advise(SelfCheckResult current, long seed, string positive, string negative, IReadOnlyList<PastRender> others, int seedsBeforeRewrite) → FixSuggestion`；`PastRender(long Seed, string Positive, string Negative, SelfCheckResult Result)` |
| `FixSuggestion` | `Rendering/Requirements.cs` | `record FixSuggestion(string Kind, string? Text, string? Message, IReadOnlyList<string> ItemIds, IReadOnlyList<string> Notes)` |
| `SelfCheckResult` | 同上 | 多 `FixSuggestion? Suggestion`（預設 `null`） |
| `RenderPipeline` | 改 | 評分完成後從 `r.Owner?.Renders`（排除這一張）取出評分 `ok` 的紀錄當 `others`，呼叫 `FixAdvisor`；例外時記 log、`Suggestion` 給 `null`；結果 `with { Suggestion = … }` 存進紀錄 |
| `RenderRecord` | 改 | view 多 `Seed`；`SelfCheckView` 多 `Suggestion`（只在 `done`） |
| `RenderRequest` | 改 | 多 `bool Reroll = false`（給 audit） |
| `RenderPostRequest` | `Endpoints/RenderEndpoints.cs` | 多 `bool Reroll = false` |
| `RenderEndpoints` | 改 | 換 seed 時挑新 seed；收件成功才寫回 `Session.RenderSeed` |
| `Session.RenderSeed` | 改 | `{ get; set; }` |
| `RenderOptions` | 改 | 新增 `SeedsBeforeRewrite = 2` |

## 8. 前端

**位置**：預覽區分數與說明下面、「你的要求」上面。

```
符合度 80
使用者要求 5 條，4 條符合；不符合：抱著貓

建議：請助理補上「抱著貓」，seed 不變再生一張     [請助理修改]
另外：模型幫你挑的「白色洋裝」沒畫出來（不計分，不建議修）

你的要求
 …
```

- **建議那一行**：`suggestion.text`＋按鈕。`fix_prompt`／`rewrite_tags` 的按鈕是「請助理修改」，按下去用一般對話流程（`runTurn`）送出 `suggestion.message`，畫面出現使用者泡泡、接著是確認卡；`reroll` 的按鈕是「換 seed 重生」，按下去用 `reroll: true` 送生圖請求，新圖取代卡上原本那張。`none` 時不顯示這一行。
- **註記**：`notes` 每則一行灰字；`kind` 是 `none` 也顯示。
- **seed**：圖片下面一行灰字 `seed {seed}`。
- **按鈕不能按的情況**（同「生成預覽」，前端先擋）：不是最新的定稿卡（舊卡只顯示文字、不給按鈕）；對話輪正在跑；這段對話有預覽還沒結束；「請助理修改」已經按過（旁邊寫「已送出修正」，只記在前端，重新整理後恢復）。
- **store**：新增 `followSuggestion(turnIndex)`，依 `kind` 呼叫 `runTurn` 或 `requestRender(turnIndex, { reroll: true })`；`requestRender` 與 `api.requestRender` 多一個可省的 `reroll`。
- **純函式**（`lib/render.ts`）：`suggestionButton({ kind, isLatest, busy, inFlight, sent })` 回按鈕顯不顯示、文字、停用與否、旁邊的說明。

## 9. 錯誤處理、測試與驗收

### 9.1 錯誤處理

| 情況 | 處理 |
| :--- | :--- |
| `FixAdvisor` 丟例外 | 記 log，`suggestion` 為 `null`；分數與圖照給 |
| 評分 `unavailable`、圖被擋下、生圖失敗 | 沒有建議 |
| 換 seed 被拒絕（429／409／503） | 目前的 seed 不變；按鈕下方顯示後端訊息（同「生成預覽」被拒） |
| 額度已滿但建議是換 seed | 照樣顯示；按下去收到 429「這段對話的預覽張數已達上限（10 張）」 |
| 助理沒改到 prompt（重新定稿後一字不差） | §4 註記 3 |
| 紀錄沒有 `Owner`（測試） | `others` 為空，只看這一張 |

### 9.2 測試（TDD）

- **`FixAdvisor`**：四條規則的順序；只看 user、`unclear` 不看；不同 seed 計數（同一個 seed 兩張算一次）；tag 變了重算；`listKey` 不同不算；N 照參數、≤0 當 1；「剛好畫出來」在 `fix_prompt`／`rewrite_tags` 時進 `message` 與 `itemIds`，在 `reroll`／`none` 時只進註記；委託註記；`message` 與 `text` 的組法；prompt 與 seed 相同的註記。
- **`RenderPipeline`**：建議用 `Owner.Renders` 的紀錄算；評分 `unavailable` 沒有建議；`FixAdvisor` 例外時 `Suggestion` 為 `null`、圖照給；audit 記 `seed`、`reroll`、`suggestion`、`suggestionItems`。
- **端點**：換 seed 用一個這段對話沒用過的 seed、收件成功才寫回；被拒時 seed 不變；一般生圖用目前的 seed；回傳有 `seed` 與 `suggestion`；Swagger 說明寫了 `reroll`、`seed`、`suggestion`。
- **`RenderOptions`**：`SeedsBeforeRewrite` 預設 2。
- **前端** `render.test.ts`：`suggestionButton` 的各種情況。

### 9.3 實機驗收（寫進 `docs/eval-cases.md`）

| 編號 | 操作 | 預期 |
| :--- | :--- | :--- |
| R14 | 出現「prompt 漏了」→ 按「請助理修改」→ 確認 → 定稿 → 生成預覽 | 送出的是程式組的那句話；新圖的 seed 跟上一張相同；那條要求這次有對應的 tag |
| R15 | 只剩「沒畫出來」→ 按「換 seed 重生」 | 新圖的 seed 不同；audit 的 `reroll: true` |
| R16 | 同一條要求在 2 個 seed 下都沒畫出來 | 建議變成「改寫」 |
| R17 | 委託項目畫錯 | 建議旁邊有註記 |

這些情況沒辦法刻意製造，多跑幾段對話、碰到哪種記哪種；碰不到的由單元測試涵蓋，eval-cases 寫明「實機沒碰到」。

## 10. 文件同步

| 文件 | 改什麼 |
| :--- | :--- |
| [評分設計](2026-10-10-intent-fit-scoring-design.md) §14 | 註明手動版已做（指向本設計），閉環沿用 `FixAdvisor` |
| [預覽設計](2026-10-09-render-preview-design.md) | §2「同一個 session 的 seed」那列與 §12 表格：seed 不再固定，換 seed 重生時才變（指向本設計） |
| [單輪流程說明](../../單輪流程說明.md) | 生成預覽那段補上修正建議與換 seed |
| `RenderEndpoints` 的 Swagger 說明 | POST 的 `reroll`；GET 的 `seed`、`selfCheck.suggestion` |
| [README](../../../README.md) | 生成預覽那行提到修正建議 |
| [eval-cases](../../eval-cases.md) | R14–R17 |

分支：接在 `intent-fit-scoring` 上做；手動輔助做完、驗收過，兩案一起 merge。
