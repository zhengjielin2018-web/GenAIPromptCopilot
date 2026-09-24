# 批次 `SearchPresets` 與工具預算 — 設計規格

日期：2026-09-24
狀態：已實作（分支 `fix/batch-search-presets`），待瀏覽器驗收（§7）
起因：[docs/known-issues.md](../../known-issues.md) #1「人像題材第一輪常被強制定稿，整個 session 不再追問」
主規格：[2026-09-21-genai-prompt-copilot-design.md](2026-09-21-genai-prompt-copilot-design.md) §4.2 工具表、§4.5 `ToolBudgetFilter`、§4.6 強制定稿、§9 檢索策略、§15 決定紀錄
前置：known-issues #2（HNSW 過濾後回 0 筆）已在分支 `fix/hnsw-iterative-scan` 修正。#2 的 0 筆結果會讓模型換說法重搜、多吃預算，本設計的驗收以 #2 已合併為前提。

---

## 1. 問題

`Prompts/system.md` 第 1 條要模型對每個適用維度各呼叫一次 `SearchPresets`，沒講的維度還要分兩次給對比方向；`ToolBudgetFilter` 對每次工具呼叫計數（含終止型），上限 `MaxToolCallsPerTurn = 8`。人像有 6 個維度，照做一輪要：

```text
SetProfile 1 + 六個維度各 1 + 每個沒講的維度再 1 + 終止 1 = 8 + 沒講的維度數
```

只要有一個維度沒講就超過 8。預算用盡 → 強制定稿 → `Status = Finalized` → `ToolSetBuilder` 不再給 `AskUser`（主規格 §4.3）。而且強制定稿時模型還沒標 facet 狀態，使用者講過的維度也全部成了 missing。

規則與預算在算術上不相容，這不是模型不聽話。

## 2. 目標與範圍

### 2.1 做

- `SearchPresets` 改成一次呼叫帶多組 `(dimension, query)`，一輪的檢索只花一次工具呼叫，embedding 走一次 batch 請求。主規格 §9 本來就寫「多個維度的查詢語句合併為單次 `embed_batch` 呼叫」，Python 的 `scripts/pipeline/retrieval.py` 已經這樣做，這次把 C# 端對齊。
- `MaxToolCallsPerTurn` 8 → 16，作為模型仍拆開呼叫時的保險。
- 強制定稿的提示明說 `facetStates` 要依使用者原話標 `covered`。
- system.md、工具描述、`HistoryTrimmer`、測試、主規格、known-issues、eval 案例同步。

### 2.2 不做

- 不保留單維度的舊簽名，不做兩種簽名並存（模型要多學一條「何時用哪個」的規則，沒有收益）。
- 不改前端。`tool_result` 事件形狀不變（一段摘要字串加一串 preset）。
- 不改 `OutputSafetyFilter`。它只掛在 `FinalizePrompt`／`AskUser`／`Discuss`，不取材 `SearchPresets`。
- 不改預算用盡時的處理（仍是強制定稿，主規格 §4.6）。「Collecting 狀態下預算用盡改成強制追問」是另一個設計題，這輪不動。
- 不改 `SearchSimilarPrompts`。
- 不動 `docs/單輪流程說明.md`，它寫的是 `scripts/demo.py` 的單輪流程，不涉及 C# 工具簽名。

## 3. 工具契約

### 3.1 簽名

```csharp
[KernelFunction(ToolNames.SearchPresets)]
Task<string> SearchPresetsAsync(SearchQuery[] queries, CancellationToken ct);

public sealed record SearchQuery(string Dimension, string Query);
```

- `dimension`：`style | scene | camera | appearance | pose | clothing`。
- `query`：該維度專屬的繁中查詢語句。
- 同一維度可以重複出現：使用者沒講的維度放兩個對比方向，就是兩個項目。
- `facetIds` 與 `k` 仍由伺服器導出，不從參數收（主規格 §15 既有決定不變）。

工具 Description 改成：

> 分維度檢索知識庫片段。**一次呼叫帶上本輪所有要查的維度，不要一個維度一次。** 使用者講過的維度：一個項目，query 逐字用使用者原話。使用者沒講的維度：兩個項目，依整體畫面推想兩個對比方向（例：寫實攝影 vs 動漫插畫）。每個維度各自回傳候選池大小、每筆的相似度分級、可否借入提示詞、每個 facet 對本次使用者是 covered/missing。

### 3.2 伺服器端驗證

| 情況 | 處理 |
| :--- | :--- |
| `Profile` 尚未設定 | 整包回 `錯誤：請先呼叫 SetProfile`（同現行） |
| `queries` 為空 | 整包回錯誤 |
| 項目數超過 12 | 整包回錯誤，訊息說明上限（6 個維度 × 2 個對比方向） |
| 某項目的維度對此 profile 不適用或不存在 | 只有該項目的結果帶 `error`，其餘照跑 |
| 某項目的 `query` 空白 | 同上，只標該項目 |

整包錯誤直接回字串（同現行寫法），不發 `tool_result` 的 preset 清單。

### 3.3 執行

1. 先過濾出合法項目，全部 `query` 合併成一次 `IEmbeddingClient.EmbedAsync(texts, RetrievalQuery)`。
2. 每個合法項目各做一次 `PresetRepository.SearchAsync(vec, facetIds, k)`；`k` 照舊：該維度 grounded 取 `KCovered = 5`，否則 `KMissing = 3`。
3. 候選池筆數每個**維度**查一次（`PoolSizeAsync`），同維度的兩個項目共用。
4. ledger 記法不變：每筆命中 `Ledger.Record(entry, new LedgerHit(dimension, dist, grounded))`。
5. 發一則 `ToolResultEvent`：摘要把各項目依序串起，中間用「・」；preset 清單依 id 去重後串接。

### 3.4 回傳

```jsonc
{
  "results": [
    { "dimension": "style", "query": "寫實攝影", "grounded": false, "poolSize": 4455, "hits": [ /* 同現行每筆的形狀 */ ] },
    { "dimension": "style", "query": "日系動漫插畫", "grounded": false, "poolSize": 4455, "hits": [ … ] },
    { "dimension": "scene", "query": "稻田裡面喝茶，遠處是房子，太陽很大", "grounded": true, "poolSize": 6752, "hits": [ … ] },
    { "dimension": "hair",  "query": "…", "error": "維度 hair 對 portrait 不適用或不存在" }
  ]
}
```

每筆命中的形狀（`id, title, band, dist, usable, facets, positive, negative`）不變。每個維度各自帶 `poolSize`，知識庫覆蓋缺口仍看得見。

### 3.5 事件摘要

```text
風格 池 4455 → 3・風格 池 4455 → 3・場景 池 6752 → 5・鏡頭 池 2147 → 3
```

同維度的兩個對比方向各占一段（模型給了兩個方向就顯示兩段）。有 `error` 的項目顯示「人物髮型 錯誤」。

## 4. 提示、預算、強制定稿

### 4.1 `Prompts/system.md` 第 1 條

改成：

> **使用者第一次描述題材**：先 `SetProfile`（動物歸 object）。接著**用一次 `SearchPresets` 帶上所有適用的維度**——使用者講過的維度一個項目，query 逐字用他的原話；沒講的維度兩個項目，依整體畫面推想兩個對比方向（例：「寫實攝影」與「日系動漫插畫」）。需要風格參考時呼叫 `SearchSimilarPrompts`。然後判斷：資訊足夠就 `FinalizePrompt`；真的缺了沒有就無法定稿的關鍵資訊，才 `AskUser`。**使用者在描述題材時不要用 `Discuss`**，要推進流程。

第 3 條「重新定稿」的情境若要補搜（換風格），同樣是一次呼叫帶要補的維度，不另寫規則。

### 4.2 預算

`MaxToolCallsPerTurn` 8 → 16。改四處：

- `src/PromptCopilot.Api/appsettings.json`
- `src/PromptCopilot.Api/Configuration/Options.cs` 的預設值
- `src/PromptCopilot.Api.Tests/Configuration/OptionsTests.cs` 的斷言
- 主規格 §4.5 `ToolBudgetFilter` 那列「預設 8」

改批次後第一輪的預期呼叫數是 4–5（`SetProfile`、`SearchPresets`、可選的 `SearchSimilarPrompts`、`SetFacetStates`、終止工具）。16 是模型仍逐維度呼叫時的保險，不是設計目標。

### 4.3 強制定稿提示

`AgenticOrchestrator.ForcedFinalizeAsync` 的系統提示改成：

> tool 呼叫預算已用盡。請立即以現有資訊呼叫 FinalizePrompt 定稿；不要再檢索。`facetStates` 依使用者原話標記：使用者講過的 facet 標 covered，真的沒講的才是 missing。

`FinalizePrompt` 本來就收 `facetStates`，不需要在強制定稿前多一次 `SetFacetStates`。

## 5. 連帶修改

### 5.1 `HistoryTrimmer.CompressResult`

現在假設 `SearchPresets` 的結果是 `{dimension, poolSize, hits}`。改成：`results` 陣列逐項壓成 `{dimension, poolSize, hits: [{id, title}]}`，有 `error` 的項目保留 `{dimension, error}`。形狀不對仍整段跳過、不丟例外（多輪 §6.3 的原則）。

### 5.2 `TurnContextExtensions.Summary`

`tool_call` 事件的 `argsSummary` 是參數 JSON 的前 80 字，批次參數會被截斷成「[{"dimension":"style","query":"寫實攝影"},{"dimension":"style","query":"日系動…」。可讀但不好看；這次不特別處理，前端卡片展開後看 `tool_result` 的摘要。

### 5.3 主規格

- §4.2 工具表 `SearchPresets` 那列：參數改 `queries: {dimension, query}[]`，說明改「一次呼叫帶本輪所有維度，見 §9」。
- §4.5 預算列：預設 16。
- §9：「agent 呼叫 `SearchPresets` 時」那段改成批次語意；「多個維度的查詢語句合併為單次 `embed_batch` 呼叫」這句從「應該」變成「就是」。
- §15 決定紀錄補兩列：批次簽名（理由：預算與逐維度規則算術不相容；Python 端本來就是批次）、預算 16（理由：保險，非目標）。

### 5.4 其他文件

- `docs/known-issues.md`：#1 移到「已修正」，寫分支名與摘要，hash 合併後補。#1 段落裡「修正方向」三項的取捨（採 2 + 3，1 當保險）寫進已修正段落。
- `docs/eval-cases.md`：新增一條「有細節但缺風格與鏡頭的人像描述」→ `final.kind = ask`，輸入用 known-issues #1 表裡的「老爺爺在稻田」那句；並在 §14 端到端的備註提醒 P3 那列可以解除延後。

## 6. 測試

全部先寫測試再改實作。

### 6.1 `KnowledgePluginTests`（新檔）

用 `EndpointTests` 既有的 `FakeEmbeddings`／`FakePresets` 做法（`PresetRepository` 已為此拿掉 `sealed`），把 fake 抽到測試專案共用的地方。案例：

| 案例 | 斷言 |
| :--- | :--- |
| 三個項目（style 兩個、scene 一個） | `EmbedAsync` 只被呼叫 1 次、`texts` 有 3 句；`SearchAsync` 被呼叫 3 次；`PoolSizeAsync` 被呼叫 2 次（每個維度一次） |
| grounded 與否決定 k | scene 為 grounded 時 `k = 5`，style 未 grounded 時 `k = 3` |
| 一個項目維度不合法 | 該項目有 `error`，其餘項目正常，整體不是錯誤字串 |
| `queries` 為空 | 回錯誤字串，沒有 embedding 呼叫 |
| 13 個項目 | 回錯誤字串，沒有 embedding 呼叫 |
| 未設 profile | 回錯誤字串（同現行） |
| 事件 | 一則 `ToolResultEvent`，摘要格式如 §3.5，preset 依 id 去重 |
| ledger | 每筆命中都記到 `Session.Ledger`，`LedgerHit.Dimension` 對應各自項目 |

### 6.2 既有測試

- `HistoryTrimmerTests`：`SearchPresets` 的壓縮案例改用 §3.4 的形狀；加一條「有 `error` 項目」的案例；加一條舊形狀（單維度）進來時整段跳過。
- `OptionsTests`：預設值 16。
- `AgenticOrchestratorTests`：若有強制定稿的案例，斷言系統提示含「covered」。

### 6.3 前端

`reducer.test.ts` 的 `tool_result` 案例用的摘要字串是自由文字，不用改。不加前端測試。

## 7. 驗收

前提：`fix/hnsw-iterative-scan` 已合併並套用到開發庫，API 已重啟。

1. 瀏覽器重送 known-issues #1 表裡的兩句：
   - 「一個老爺爺在稻田裡面喝茶，遠處是房子，太陽很大，老爺爺有著白色捲髮，穿著白色短衣」
   - 「中年女士在廚房，正把青菜放入便當內，她穿著圍裙與長褲」

   預期：第一輪出追問卡；儀表板上使用者講過的維度（場景、樣貌、穿著）亮 covered；audit 沒有 `Tool_Budget_Exhausted`；`Turn_Completed.toolCalls` ≤ 5；工具卡只有一張 `SearchPresets`，摘要列出每個維度的候選池筆數。
2. eval #1「一個女生」與 #3「山上的日出」重跑，行為不變（仍追問）。
3. eval #18「一個少女」重跑，記錄追問維度數是否改善（預算不再是原因之一；若仍偏少，記回 known-issues #5）。
4. `dotnet test` 全綠；`PC_INTEGRATION=1` 下 `RepositoryIntegrationTests` 全綠。
