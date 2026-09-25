# 知識庫開關與檢索過程顯示：設計

日期：2026-09-25
來源：`docs/superpowers/plans/2026-09-25-rag-value-and-trace.md` §4.1 與 §6，2026-09-25 討論定案。
前情：子專案 2 修正輪已全部 merge（master `417f49e`）。§8「整套組合建議」排在本案之後，另開 spec。

---

## 1. 問題

使用者實測後的疑慮是「RAG 到底有沒有用」。要回答它需要兩樣東西，現在都沒有：

1. **對照組**：沒有辦法讓同一段對話「不檢索」，所以看不出 RAG 帶來的差別。
2. **可觀測性**：`SearchPresets` 在前端只剩一行「鞋履 池 300 → 5」與縮圖。查了什麼、命中了什麼、距離多少、哪些片段最後真的貢獻了 tag，使用者與開發者都看不到。audit 也不能當來源，`Tool_Invoked` 的 args／result 截到 200 字。

這兩件事合在一份 spec：開關是量測的前提，過程顯示是量測的工具，也是使用者自己感受 on／off 差異的方式。

## 2. 目標與範圍

### 2.1 做

- 每段對話建立時決定要不要用知識庫（`retrieval: on | off`），之後不變。off 時模型拿不到兩個檢索工具，system prompt 也不再要求檢索。
- `tool_result` 事件多帶結構化的 `detail`，`SearchPresets` 與 `SearchSimilarPrompts` 各一種形狀。
- 前端兩個開關：「使用知識庫」（只影響新對話）與「顯示檢索細節」（純顯示，即時生效），都存 localStorage。
- 「顯示檢索細節」開啟時：工具卡逐項展開、定稿卡加「檢索貢獻」、儀表板底部加「本次對話檢索摘要」。關閉時畫面與現在完全一樣。
- audit 的 `Turn_Completed` 記 `retrieval`，事後可分組。

### 2.2 不做

- 離線 A/B 腳本與報表（計畫 §4.2）：等 §8 上線、量過採用率之後再決定。
- 「僅供建議的片段若仍有 tag 被借用就標警告」（計畫 §6.2）：片段被當選項給使用者、使用者選了之後借它的詞是合法的，但 ledger 上那筆命中仍是 ungrounded，警告會誤報。要做對得看 `OfferedAs` 與後續 facet 狀態，不在本案。
- `GET /api/sessions/{id}` 不回 `detail`：對話流本來就在前端的 sessionStorage，不需要後端補。
- 不動 ledger、`TagAttribution`、`HistoryTrimmer`、模型看到的工具回傳內容。
- 不做 on／off 並排比較的 UI；使用者自己開兩段對話比。
- `docs/單輪流程說明.md` 不動：它寫的是 `scripts/demo.py` 的單輪流程，與 agent 無關。

## 3. 後端

### 3.1 建立 session 帶 `retrieval`

`POST /api/sessions` 接受可省略的 JSON body：

```json
{ "retrieval": "off" }
```

- 值只認 `on`／`off`（不分大小寫），省略或 body 為空視為 `on`。其他值回 `400` `{"error": "retrieval 只能是 on 或 off"}`。
- 回應由 `{"sessionId"}` 變成 `{"sessionId", "retrieval": "on" | "off"}`。
- `Session` 加唯讀屬性 `RetrievalEnabled`（建構子參數，預設 `true`）。它在建立時定死、之後不變，所以**不進** `SessionSnapshot`／`Restore`。
- `SessionStore.Create(bool retrievalEnabled = true)`。
- `GET /api/sessions/{id}` 的 `SessionSnapshotDto` 加 `retrieval: "on" | "off"`，前端重載後才知道目前對話是哪種模式。
- Swagger 的 `POST /api/sessions` 描述改寫（現在寫「不用帶 body」）。

### 3.2 工具集

`ToolSetBuilder.Build`：`RetrievalEnabled == false` 時從結果拿掉 `SearchPresets` 與 `SearchSimilarPrompts`。`ToolNames.Always` 保持不變（它是「一般情況永遠有」的宣告），拿掉的動作在 `Build` 裡做。`AgentKernelFactory` 本來就只註冊清單裡的函式，不用改。

### 3.3 System prompt

`Prompts/system.md` 步驟 1 裡的檢索指示，與「提示詞規則」裡講片段可否借入的那一條，各換成一個 placeholder：

| placeholder | on | off |
| :--- | :--- | :--- |
| `{{RETRIEVAL_STEP}}`（步驟 1 內，「再**用一次 `SearchPresets`**……需要風格參考時呼叫 `SearchSimilarPrompts`。」整段） | 原文不變 | `本段對話沒有知識庫：不做檢索，直接依 facet 狀態追問或定稿。` |
| `{{RETRIEVAL_RULE}}`（規則清單裡整條 `- \`SearchPresets\` 回的片段標了……` 那一行） | 原文不變 | `- 本段對話沒有知識庫片段，所有 tag 由你自行產生。` |

- 兩段原文搬進 `SystemPromptBuilder` 的常數，不留在樣板裡；樣板只剩 placeholder。這樣 on 模式的 prompt 內容逐字不變，但因為樣板檔變了，hash 會變一次，之後穩定。
- `Facts(s)` 加一行 `- 知識庫：on|off`，讓模型與讀 audit 的人在 Session 事實區也看得到。
- `Build(Session, tools)` 簽名不變，從 `s.RetrievalEnabled` 讀。

### 3.4 audit

`Turn_Completed` 的 payload 加 `("retrieval", "on"|"off")`。不加新的事件型別。

### 3.5 `tool_result` 的 `detail`

`ToolResultEvent` 加第五個參數 `object? Detail = null`。序列化：camelCase，`null` 時整個欄位省略（沿用 `SseWriter` 的選項；若現在沒有 `DefaultIgnoreCondition = WhenWritingNull`，就在 `Detail` 屬性上加 `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`）。宣告型別是 `object`，STJ 會照實際型別序列化。

兩種形狀，放在 `Streaming/ToolDetails.cs`：

```csharp
public sealed record SearchPresetsDetail(IReadOnlyList<SearchPresetsItem> Items);
public sealed record SearchPresetsItem(
    string Dimension, string? FacetId, string Label, string Query,
    bool Grounded, long PoolSize, int K, string? Error,
    IReadOnlyList<SearchPresetsHit> Hits);
public sealed record SearchPresetsHit(
    long Id, string Title, string Band, double Dist, bool Usable,
    IReadOnlyDictionary<string, string> Facets);

public sealed record SearchSimilarDetail(IReadOnlyList<SearchSimilarHit> Hits);
public sealed record SearchSimilarHit(string Intent, string Profile, double Dist);
```

規則：

- `Items` 依模型送來的 `queries` 原順序，驗證失敗的項目 `Error` 有值、`Hits` 空、`PoolSize` 0、`K` 0、`Grounded` false、`Label` 用模型送的原始 facetId 或 dimension。
- `Label` 是 facet 的中文標籤或維度的中文標籤（`catalog.DimensionLabel`），與現在的摘要字串同一來源。
- `Band` 是現有的「高／中／低」，`Dist` 四捨五入到小數第三位，`Usable = Grounded`（現在是 `"可借入提示詞"`／`"僅供建議"` 字串，事件上用布林）。
- `Facets` 與回給模型的 `facets` 同一份：facetId → `covered|missing|waived|not_applicable` 的 wire 字串。
- **不放** `positive`／`negative` snippet 本文，抽屜已有。
- `SearchSimilarHit.Intent` 取 `UserIntent` 前 40 字（依 `string.Length`，超過補 `…`）。
- 模型看到的 JSON 字串**不變**：`KnowledgePlugin` 先組出 detail 物件，再從它投影出回給模型的 `results`（或反過來），兩邊同一份資料、不各算一次。

`Summary` 與 `Presets` 照舊，前端沒開細節時只用它們。

## 4. 前端

### 4.1 偏好：`lib/prefs.ts`（新）

```ts
export interface Prefs { v: 1; retrieval: 'on' | 'off'; showTrace: boolean }
export function loadPrefs(storage?: StorageLike | null): Prefs   // 缺值、壞資料、拿不到 storage → { v: 1, retrieval: 'on', showTrace: false }
export function savePrefs(p: Prefs, storage?: StorageLike | null): void
```

- key `pc.prefs`，用 **localStorage**（跨對話、跨分頁的偏好；`persist.ts` 的 `pc.session` 留在 sessionStorage 不動）。
- 讀寫都包 try/catch，與 `persist.ts` 同一套 `StorageLike` 介面，方便測試。

### 4.2 store

- `prefs`：`ref(loadPrefs())`，兩個 setter `setRetrievalPref`／`setShowTrace` 改值後 `savePrefs`。
- `state.retrieval: 'on' | 'off'`：`newSession()` 送 `{ retrieval: prefs.retrieval }`，從建立回應帶入；`boot()` 重載時從 `SessionSnapshotDto.retrieval` 帶入；`initialState()` 預設 `'on'`。
- `retrievalMismatch`：computed，`prefs.retrieval !== state.retrieval`。
- `useApi.createSession(retrieval)` 改送 JSON body，回傳整個 `SessionCreated`。

### 4.3 型別與 reducer

- `types/api.ts`：`tool_result` 事件加 `detail?: SearchPresetsDetail | SearchSimilarDetail`，兩個介面對應 §3.5，欄位 camelCase。判別用 `name`（`SearchPresets` → `SearchPresetsDetail`），不在 detail 裡另加 kind 欄位。
- `ToolEntry` 加 `detail?: ToolDetail | null`。`tool_result` 進來時原樣塞入；沒有就 `null`。舊的 sessionStorage transcript 沒有這個欄位，讀到 `undefined` 當 `null`。

### 4.4 TopBar

「新對話」左邊並排兩個小型 toggle（`role="switch"`，`aria-checked`）：

| 標籤 | 綁定 | 附註 |
| :--- | :--- | :--- |
| 使用知識庫 | `prefs.retrieval` | 與目前對話模式不同時，右側 11px 灰字「新對話後生效」 |
| 顯示檢索細節 | `prefs.showTrace` | 即時生效 |

切「使用知識庫」不自動開新對話。

### 4.5 ToolCallCard

展開區的內容依 `prefs.showTrace && entry.detail` 分兩種：

- **關（或沒有 detail）**：現在的樣子。
- **開，`SearchPresets`**：每個 item 一列，`標籤｜查詢句｜池 N → 命中 M`，`grounded === false` 的列標為「僅供建議」並用 muted 色；`error` 有值的列只顯示 `標籤｜錯誤訊息`，紅字。每列可再點開命中清單：`標題｜分級｜dist｜可借入／僅供建議`，標題是按鈕，點了 `openDrawer(id)`。縮圖列照舊放在最下面，來源說明一行照舊。
- **開，`SearchSimilarPrompts`**：每筆一列 `intent（前 40 字）｜profile｜dist`。

命中清單的每個 facet 狀態不畫出來（太細），留在 detail 給之後的 eval 腳本用。

### 4.6 FinalCard：檢索貢獻

`prefs.showTrace` 開、且定稿卡帶 `positiveSources` 或 `negativeSources` 時，在 tips 下方加一區「檢索貢獻」。資料由 `lib/trace.ts` 的純函式算，輸入是 `positiveSources` 與 `negativeSources`：

```ts
export interface Contribution { presetId: number; title: string; sourceRef: string | null; tags: string[] }
export interface ContributionSummary { counts: { rag: number; llm: number; base: number }; byPreset: Contribution[] }
export function contributions(positive: TagSource[], negative: TagSource[]): ContributionSummary
```

- `counts` 只算正向（與 audit 的 `tagOrigins` 一致）。
- `byPreset` 依 presetId 分組，一個 tag 對到多個 presetId 時每個都列；負向 tag 加 `-` 前綴列在同一組。依 tag 數多的在前。
- 畫面：一行 `rag N・llm N・base N`，接著每個片段一列 `標題 → tag, tag, tag`，標題點了開抽屜；`byPreset` 空時顯示計數與一句「這次定稿沒有借用知識庫片段。」

### 4.7 Dashboard：本次對話檢索摘要

`prefs.showTrace` 開且 transcript 裡至少有一張 `SearchPresets` 工具卡帶 detail 時，圖例上方加一區。`lib/trace.ts`：

```ts
export interface RetrievalSummary {
  searches: number                                   // 完成的 SearchPresets 次數
  pools: { dimension: string; label: string; poolSize: number }[]   // 每個維度最近一次查詢的候選池（facet 項目歸到所屬維度，取該維度最後一個 item）
  seen: number                                       // 命中過的不同 preset 數
  borrowed: number                                   // 最新一次定稿裡 rag 來源引用的不同 preset 數
}
export function retrievalSummary(transcript: Entry[]): RetrievalSummary
```

畫面三個數字一列（查詢次數、看過幾筆、借用幾筆），`pools` 用維度標籤加數字的小 chip。

### 4.8 相容性

- 舊 transcript（沒有 detail）：工具卡退回一行摘要，儀表板摘要不顯示（`searches` 為 0 時整區隱藏），定稿卡沒有 sources 時不顯示檢索貢獻（畫 0 會誤導）。
- 後端舊版（沒有 `retrieval` 欄位）：前端 `state.retrieval` 預設 `'on'`，開關仍可切但無效；不特別處理，這是本機開發時前後端版本不齊的暫時狀態。

## 5. 測試

後端（xunit）：

- `ToolSetBuilderTests`：`RetrievalEnabled=false` 時清單不含兩個檢索工具；其餘工具與 on 時完全相同。
- `SystemPromptBuilderTests`：on 的 prompt 含 `SearchPresets` 且與目前的字句相同；off 的不含 `SearchPresets`／`SearchSimilarPrompts` 字樣、含「本段對話沒有知識庫」；兩者 hash 不同；Facts 含「知識庫：off」。
- `EndpointTests`：`POST /api/sessions` 無 body → `retrieval: on`；body `{"retrieval":"off"}` → `off`，`GET` 也回 `off`；`{"retrieval":"maybe"}` → 400。
- `KnowledgePluginTests`：發出的 `ToolResultEvent.Detail` 是 `SearchPresetsDetail`，項目順序與 queries 相同，grounded 項目 `K=5`、非 grounded `K=3`，驗證失敗的項目 `Error` 有值且 `Hits` 空，`Hits` 不含 snippet；`SearchSimilarPrompts` 的 `Intent` 截到 40 字。回給模型的 JSON 與改動前逐字相同（既有測試不改就是驗證）。
- `SseWriterTests`：`Detail` 為 null 時 data 裡沒有 `detail` 鍵；有值時 camelCase。
- `AgenticOrchestratorTests`：`Turn_Completed` payload 含 `retrieval`。

前端（vitest）：

- `tests/prefs.test.ts`（新）：缺值、壞 JSON、storage 拋錯都回預設；round-trip。
- `tests/reducer.test.ts`：`tool_result` 帶 detail 進 `ToolEntry`；不帶時 `detail` 為 `null`；舊資料 hydrate 不壞。
- `tests/trace.test.ts`（新）：`contributions` 的分組、計數、負向前綴、多 presetId；`retrievalSummary` 的四個數字，含沒有 detail 的 transcript 回 `searches: 0`。
- 元件沒有 mount 測試，沿用現況：eval 加一條人工驗收（開細節看工具卡、定稿卡、儀表板；關掉回到原狀；off 對話沒有檢索卡、tag 全 llm）。

## 6. 同步文件（與程式同一個 commit）

- 主規格 `2026-09-21-genai-prompt-copilot-design.md` §10.2 事件表 `tool_result` 列加 `detail?`；`POST /api/sessions` 的說明加 body。
- `2026-09-24-frontend-sse-design.md`：§3.2 store 狀態加 `retrieval`、`prefs`；§4 元件表 `TopBar`、`ToolCallCard`、`FinalCard`、`Dashboard` 列各加一句；§10 實作偏差加一段註明日期。
- `2026-09-24-batch-search-presets-design.md` §3.5 事件摘要加 `detail`。
- `docs/eval-cases.md` 加人工驗收案例。
- `manual-tests/README.md` 若有示範 `POST /api/sessions`，補 body 範例。

## 7. 決定紀錄

| 決定 | 選擇 | 理由 |
| :--- | :--- | :--- |
| 開關的粒度 | 每段對話建立時定死 | 中途切換會讓 ledger、tag 來源、audit 分組都變成混合狀態，量測時沒辦法分組 |
| off 的 prompt 怎麼改 | 兩個 placeholder，原文搬進 C# 常數 | on 的字句逐字不變；比維護兩份樣板檔省事 |
| detail 從哪裡來 | 事件，不是 audit | audit 截 200 字；事件本來就是前端的資料來源，eval 腳本之後也能直接讀 SSE |
| detail 放不放 snippet | 不放 | 抽屜已有；事件大小要控制（24 個項目 × 5 筆命中） |
| 檢索貢獻與儀表板摘要在哪算 | 前端純函式 | 資料都在 transcript；後端不加事件、不加狀態 |
| 「僅供建議卻被借用」警告 | 本案不做 | 會誤報，見 §2.2 |
| 兩個開關存哪 | localStorage，與 `pc.session` 分開 | 偏好跨對話；對話流是每個分頁自己的 |
