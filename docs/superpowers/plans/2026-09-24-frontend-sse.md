# 子專案 3：Nuxt 3 前端 + SSE — 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把子專案 2 的 headless API 接上一個能在瀏覽器跑完整段對話的 Nuxt 3 介面：對話流、六維度儀表板、preset 抽屜、定稿與入庫、失敗重試、整頁重載恢復。

**Architecture:** 後端加一支唯讀 `GET /api/sessions/{id}` 與 `FinalizePrompt.intentSummary`；前端是 `ssr: false` 的 Nuxt 3 SPA，SSE 用 `fetch` + `ReadableStream` 解析成事件，事件全部交給純函式 reducer 更新單一 Pinia store，失敗時還原到送出前的快照（跟後端「一輪是一個交易」對稱）。對話流存 `sessionStorage`，權威狀態重載時從後端拿。

**Tech Stack:** .NET 10 / ASP.NET Core minimal API / xUnit（既有）；Node LTS 22、Nuxt 3、Vue 3、Pinia、Tailwind（`@nuxtjs/tailwindcss`）、vitest、TypeScript。

**Spec:** [docs/superpowers/specs/2026-09-24-frontend-sse-design.md](../specs/2026-09-24-frontend-sse-design.md)。主規格 [2026-09-21-genai-prompt-copilot-design.md](../specs/2026-09-21-genai-prompt-copilot-design.md) §10、§11；多輪設計 [2026-09-22-multi-turn-dialogue-design.md](../specs/2026-09-22-multi-turn-dialogue-design.md) §5.1、§5.4、§5.6。

## Global Constraints

- 前端專案位置固定 `src/PromptCopilot.Frontend/`；Nuxt **3.x**（不是 4），`ssr: false`；套件管理 **npm**；Node **LTS 22**。
- API 不加 CORS。開發用 Nuxt `nitro.devProxy` 把 `/api` 與 `/health` 轉到 `http://localhost:5000`。若 devProxy 會緩衝 SSE（Task 5 驗證），允許的備案是 `runtimeConfig.public.apiBase` 指向 `http://localhost:5000` 並在 API 的 **Development 環境**加 CORS，且要記回 spec §2.5。
- 不做打字機動畫；reducer 仍要接受 `token` 事件（接到最近一筆可承接文字的條目），但不對一次到位的文字做逐字揭露。
- UI 文案一律繁體中文；提示詞與 tag 英文。錯誤文案固定句子，不把例外訊息或後端內部字串送到畫面上（後端 `message` 除外，那是設計給使用者看的）。
- 儀表板四態不能只靠顏色區分：`covered` 實心飽和／`missing` 空心描邊／`notApplicable` 極淡／`waived` 實心去飽和加「略」記號。
- `save-to-shared` 的 `intent` 仍由客戶端送；預填 `intentSummary`，使用者可改。
- 快照時機是**送出當下**（不是收到 `session` 事件時）。
- 後端契約變更（`intentSummary`、`GET /api/sessions/{id}`）與主規格 §4.2／§6.2／§10.1／§10.2、Swagger 描述**同一個 commit** 更新。
- 每個 commit 前 `cd src && dotnet test`（後端任務）或 `npm test` + `npm run build`（前端任務）必須全綠。**API 還在跑時不能 `dotnet build`／`dotnet test`**（dll 被鎖住）。
- Commit 訊息結尾加 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`。

## Review Focus

Spec 暗示但原本沒有測試覆蓋的五種情況，每條都已把測試釘進擁有那段程式的任務：

1. **`tool_result` 永遠不會來的 tool**（`SetProfile`、`SetFacetStates`、四個終止型 tool 都不發 `tool_result`）：卡片不能一直轉圈。→ Task 7 `endTurn` 把所有 `done: false` 的 tool 條目標成 done。
2. **`tool_result` 的 `callId` 對不到任何 `tool_call`**：不能丟掉，也不能炸。→ Task 7 測「孤兒 `tool_result` 變成一筆已完成的獨立卡片」。
3. **SSE chunk 切在多位元組字元中間**（中文一定會遇到）：→ Task 6 測把「霓」的 UTF-8 三個位元組拆在兩個 chunk。
4. **`sessionStorage` 不可用或拋例外**（隱私模式、被停用）：頁面要照常能用。→ Task 8 用會拋例外的 storage stub 測 `load` 回 null、`save` 不拋。
5. **`dimensions` 事件帶了 catalog 裡沒有的 facet id**（yaml 與後端不同步時）：儀表板不能炸。→ Task 10 `dashboardRows` 測未知 id 以原 id 當 label 顯示。

---

## 檔案結構

**後端（修改）**

| 檔案 | 責任 |
| :--- | :--- |
| `src/PromptCopilot.Api/Sessions/Session.cs` | `FinalPrompt` 加 `IntentSummary` |
| `src/PromptCopilot.Api/Plugins/DialogPlugin.cs` | `FinalizePrompt` 加 `intentSummary` 參數與空白檢查 |
| `src/PromptCopilot.Api/Streaming/AgentEvent.cs` | `FinalEvent` 加 `IntentSummary` |
| `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs` | `ToFinal` 帶出 `IntentSummary` |
| `src/PromptCopilot.Api/Filters/TurnContextExtensions.cs` | `OutputTextFor` 的 `FinalizePrompt` 分支加 `intentSummary` |
| `src/PromptCopilot.Api/Prompts/system.md` | 說明 `intentSummary` 怎麼寫 |
| `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs` | `GET /api/sessions/{id}` |
| 測試：`DialogPluginTests.cs`、`FiltersTests.cs`、`EndpointTests.cs` 與四處 `new FinalPrompt(...)` 呼叫點 | |

**前端（新建，`src/PromptCopilot.Frontend/`）**

| 檔案 | 責任 |
| :--- | :--- |
| `package.json`、`nuxt.config.ts`、`tsconfig.json`、`vitest.config.ts`、`tailwind.config.ts`、`assets/css/main.css` | 專案骨架 |
| `types/api.ts` | 後端 DTO 與 SSE 事件的 TypeScript 型別，**唯一**定義處 |
| `lib/sse.ts` | `readSse(stream)`：`ReadableStream` → 逐筆 `{ event, data }`，純函式，無 Nuxt 依賴 |
| `lib/reducer.ts` | `initialState()`、`beginTurn`、`applyEvent`、`endTurn`、`hydrate`：純函式 |
| `lib/persist.ts` | `sessionStorage` 存讀 `{ sessionId, transcript }`，try/catch 包住 |
| `lib/composer.ts` | chip 累積與前綴組字，純函式 |
| `lib/dashboard.ts` | `dashboardRows(catalog, profile, facetStates)`：儀表板要畫的列，純函式 |
| `lib/copy.ts` | 錯誤原因（`reason`／`code`）→ 繁中文案 |
| `composables/useApi.ts` | `createSession`、`getSession`、`getFacets`、`getPreset`、`saveToShared`、`openStream` |
| `stores/session.ts` | 唯一的 Pinia store：持有 `ChatState`、composer 狀態、抽屜狀態；動作只做 I/O 與呼叫 reducer |
| `components/*.vue` | `TopBar`、`ChatStream`、`UserBubble`、`ToolCallCard`、`AskCard`、`MessageBubble`、`FinalCard`、`SaveConsentNotice`、`FailureNotice`、`Composer`、`Dashboard`、`PresetDrawer` |
| `app.vue` | 版面骨架與開頁流程 |
| `tests/*.test.ts` | vitest：`sse`、`reducer`、`persist`、`composer`、`dashboard` |
| `README.md` | 怎麼跑、怎麼測 |

`lib/` 裡的檔案**只能 import `types/` 與彼此**，不能用 Nuxt auto-import，vitest 才能在 node 環境直接跑。

---

### Task 1: `FinalizePrompt.intentSummary` 進 `FinalPrompt` 與 `final` 事件

**Files:**
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs:8`
- Modify: `src/PromptCopilot.Api/Plugins/DialogPlugin.cs:58-72`
- Modify: `src/PromptCopilot.Api/Streaming/AgentEvent.cs`（`FinalEvent`）
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs:244`
- Modify: `src/PromptCopilot.Api/Prompts/system.md`
- Modify: `src/PromptCopilot.Api.Tests/Plugins/DialogPluginTests.cs`
- Modify（呼叫點補第 4 個參數）: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs:75`、`Orchestration/SystemPromptBuilderTests.cs:37`、`Orchestration/ToolSetBuilderTests.cs:15,37,51`、`Plugins/DialogPluginTests.cs:21`、`Sessions/SessionTests.cs:48,75`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` §4.2 表（第 105 行）與 §10.2 `finalized` 那行

**Interfaces:**
- Produces: `FinalPrompt(string Positive, string Negative, string Tips, string IntentSummary)`；`FinalEvent` 多一個 `string? IntentSummary = null` 具名參數；SSE `final` 的 `finalized` 多 `intentSummary` 欄位。

- [ ] **Step 1: 寫失敗的測試**

在 `src/PromptCopilot.Api.Tests/Plugins/DialogPluginTests.cs` 加兩個測試（放在既有 `FinalizePrompt` 測試附近）：

```csharp
[Fact]
public void FinalizePrompt_requires_intent_summary()
{
    var (p, turn, s) = Make();
    var r = p.FinalizePrompt("1girl", "lowres", "t", "   ", Array.Empty<FacetStateEntry>());
    Assert.Contains("intentSummary", r);
    Assert.Null(turn.Outcome);
    Assert.Equal(SessionStatus.Collecting, s.Status);
}

[Fact]
public void FinalizePrompt_stores_trimmed_intent_summary_and_emits_it()
{
    var (p, turn, s) = Make();
    var r = p.FinalizePrompt("1girl", "lowres", "t", "  雨夜霓虹街頭的銀髮少女，寫實攝影  ", Array.Empty<FacetStateEntry>());
    Assert.Equal("ok", r);
    Assert.Equal("雨夜霓虹街頭的銀髮少女，寫實攝影", s.LastFinal!.IntentSummary);
    var ev = AgenticOrchestrator.ToFinal(turn.Outcome!);
    Assert.Equal("finalized", ev.Kind);
    Assert.Equal("雨夜霓虹街頭的銀髮少女，寫實攝影", ev.IntentSummary);
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src && dotnet test --filter "FullyQualifiedName~DialogPluginTests" 2>&1 | tail -20`
Expected: 編譯錯誤（`FinalizePrompt` 沒有 5 個參數、`FinalPrompt` 沒有 `IntentSummary`）。

- [ ] **Step 3: 改 record、plugin、事件、ToFinal**

`Session.cs` 第 8 行：

```csharp
public sealed record FinalPrompt(string Positive, string Negative, string Tips, string IntentSummary);
```

`DialogPlugin.cs` 的 `FinalizePrompt`：

```csharp
[KernelFunction(ToolNames.FinalizePrompt)]
[Description("定稿：產出可直接用的 SD/SDXL 英文 tag 提示詞。使用者講的必須完整反映；missing 的 facet 不自行發明（AutoFill 除外）；基礎畫質詞與負向詞永遠生成。")]
public string FinalizePrompt(
    [Description("英文、逗號分隔 tag")] string positivePrompt,
    [Description("英文、逗號分隔 tag")] string negativePrompt,
    [Description("繁中生成建議：哪些 facet 留白、可以怎麼補")] string tips,
    [Description("繁中一句話（20–40 字）描述使用者這次的需求：題材、主要風格、場景。不含提問與閒聊。會成為共享庫的檢索鍵，要寫成另一個使用者會怎麼描述同樣的需求")] string intentSummary,
    [Description("目前每個 facet 的狀態")] FacetStateEntry[] facetStates)
{
    if (S.Profile is null) return "錯誤：請先呼叫 SetProfile";
    if (string.IsNullOrWhiteSpace(positivePrompt)) return "錯誤：positivePrompt 不可為空";
    if (string.IsNullOrWhiteSpace(intentSummary)) return "錯誤：intentSummary 不可為空";
    SessionPlugin.Apply(turn, catalog, facetStates);
    S.RecordFinalize(new FinalPrompt(positivePrompt.Trim(), negativePrompt.Trim(), tips.Trim(), intentSummary.Trim()));
    turn.Outcome = new FinalizedOutcome(S.LastFinal!);
    return "ok";
}
```

`AgentEvent.cs` 的 `FinalEvent`：

```csharp
public sealed record FinalEvent(
    string Kind,
    string? Preamble = null, IReadOnlyList<AskItem>? Asks = null,
    string? Message = null, IReadOnlyList<OptionItem>? Options = null,
    string? Positive = null, string? Negative = null, string? Tips = null,
    string? IntentSummary = null) : AgentEvent("final");
```

`AgenticOrchestrator.cs` 第 240 行的 `ToFinal` 從 `internal static` 改成 `public static`（測試專案沒有 `InternalsVisibleTo`，Step 1 的測試要直接呼叫它），第 244 行改成：

```csharp
FinalizedOutcome f => new FinalEvent("finalized", Positive: f.Final.Positive, Negative: f.Final.Negative, Tips: f.Final.Tips, IntentSummary: f.Final.IntentSummary),
```

- [ ] **Step 4: 補所有 `new FinalPrompt(...)` 呼叫點的第 4 個參數**

Run: `grep -rn "new FinalPrompt(" src --include=*.cs | grep -v "/bin/\|/obj/"`

每一處加一個字串引數，例如 `new FinalPrompt("p", "n", "t", "i")`、`new FinalPrompt("1girl", "lowres", "tips", "一個女生")`、`new FinalPrompt("mountain", "lowres", "tips", "山上的日出")`。`ToolSetBuilderTests.cs:51` 是 `SessionSnapshot` 建構子裡的那個，一樣補。

- [ ] **Step 5: system prompt 加說明**

`src/PromptCopilot.Api/Prompts/system.md` 的「## 提示詞規則」最後加一條：

```markdown
- `intentSummary` 用繁中一句話（20–40 字）描述使用者這次要的畫面：題材、主要風格、場景。不寫提問與閒聊、不寫 tag。它會成為共享庫的檢索鍵，要寫成「另一個使用者會怎麼描述同樣的需求」，例如「雨夜霓虹街頭的銀髮少女，寫實攝影風格，低角度」。重新定稿時照最新狀態重寫。
```

- [ ] **Step 6: 跑全部測試**

Run: `cd src && dotnet test 2>&1 | tail -5`
Expected: 全綠（整合測試顯示 Skipped 是正常的）。

- [ ] **Step 7: 同步主規格**

`docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`：

- §4.2 第 105 行那列改成：`| \`DialogPlugin.FinalizePrompt\` | \`positivePrompt: string, negativePrompt: string, tips: string, intentSummary: string, facetStates: FacetStateEntry[]\` | **終止型** |`，並在表後補一句：「`intentSummary`：繁中一句話的需求描述，存入 `LastFinal`、隨 `finalized` 事件送出，前端用它預填 `save-to-shared` 的 `intent`（子專案 3 設計 §2.2）。」
- §10.2 的 `{ kind: "finalized", positive, negative, tips }` 改成 `{ kind: "finalized", positive, negative, tips, intentSummary }`。

- [ ] **Step 8: Commit**

```bash
git add src docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): FinalizePrompt carries intentSummary into LastFinal and the finalized event

子專案 3 設計 §2.2：前端拿它預填 save-to-shared 的 intent。

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `OutputSafetyFilter` 也檢 `intentSummary`

**Files:**
- Modify: `src/PromptCopilot.Api/Filters/TurnContextExtensions.cs:46-49`
- Modify: `src/PromptCopilot.Api.Tests/Filters/FiltersTests.cs:146-160`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` §6.2 欄位表

**Interfaces:**
- Consumes: Task 1 的 `intentSummary` 參數名。
- Produces: `OutputTextFor("FinalizePrompt", args)` 的回傳文字包含 `intentSummary`。

- [ ] **Step 1: 改既有測試**

`FiltersTests.cs` 的 `OutputTextFor_finalize_takes_positive_and_tips_only` 改名為 `OutputTextFor_finalize_takes_positive_tips_and_intent_summary`，`KernelArguments` 加 `["intentSummary"] = "雨夜霓虹街頭的少女"`，斷言加：

```csharp
Assert.Contains("雨夜霓虹街頭的少女", text);
```

XML 註解補一句：「`intentSummary` 會進共享庫，跟 `positivePrompt` 同一等級（子專案 3 設計 §2.3）。」

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src && dotnet test --filter "FullyQualifiedName~FiltersTests" 2>&1 | tail -15`
Expected: FAIL，缺「雨夜霓虹街頭的少女」。

- [ ] **Step 3: 加欄位**

`TurnContextExtensions.cs` 的 `FinalizePrompt` 分支：

```csharp
case ToolNames.FinalizePrompt:
    parts.Add(Field(args, "positivePrompt"));
    parts.Add(Field(args, "tips"));
    parts.Add(Field(args, "intentSummary"));
    break;
```

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src && dotnet test 2>&1 | tail -5`
Expected: 全綠。

- [ ] **Step 5: 同步主規格 §6.2**

欄位表第一列改成 `| \`FinalizePrompt\` | \`positivePrompt\`、\`tips\`、\`intentSummary\` |`。

- [ ] **Step 6: Commit**

```bash
git add src docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): output safety also screens FinalizePrompt.intentSummary

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `GET /api/sessions/{id}`

**Files:**
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`
- Modify: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` §10.1 端點表
- Modify: `docs/superpowers/specs/2026-09-22-multi-turn-dialogue-design.md` §11 最後一條

**Interfaces:**
- Consumes: `OrchestratorOptions.MaxAskCount`、`FacetStateParser.ToWire`、Task 1 的 `FinalPrompt.IntentSummary`。
- Produces: 前端 `types/api.ts` 的 `SessionSnapshotDto`（Task 5）就是這個回應：

```json
{ "sessionId": "…", "status": "Collecting", "profile": "portrait", "turnIndex": 3, "askCount": 1, "askLimit": 2,
  "facetStates": { "style.genre": "covered" }, "lastFinal": { "positive": "…", "negative": "…", "tips": "…", "intentSummary": "…" } }
```

- [ ] **Step 1: 寫失敗的測試**

`EndpointTests.cs` 加：

```csharp
[Fact]
public async Task Get_session_returns_authoritative_state_and_null_final_before_finalize()
{
    var id = (await (await _client.PostAsync("/api/sessions", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
    var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/sessions/{id}");
    Assert.Equal(id, doc.GetProperty("sessionId").GetString());
    Assert.Equal("Collecting", doc.GetProperty("status").GetString());
    Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.GetProperty("profile").ValueKind);
    Assert.Equal(0, doc.GetProperty("askCount").GetInt32());
    Assert.Equal(2, doc.GetProperty("askLimit").GetInt32());
    Assert.Equal(0, doc.GetProperty("facetStates").EnumerateObject().Count());
    Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.GetProperty("lastFinal").ValueKind);
}

[Fact]
public async Task Get_session_returns_facets_and_last_final_after_finalize()
{
    var s = FinalizedSession();
    var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/sessions/{s.Id}");
    Assert.Equal("Finalized", doc.GetProperty("status").GetString());
    Assert.Equal("portrait", doc.GetProperty("profile").GetString());
    Assert.Equal("missing", doc.GetProperty("facetStates").GetProperty("style.genre").GetString());
    var f = doc.GetProperty("lastFinal");
    Assert.Equal("1girl", f.GetProperty("positive").GetString());
    Assert.Equal("一個女生", f.GetProperty("intentSummary").GetString());
}

[Fact]
public async Task Get_session_404_for_unknown()
{
    Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/sessions/nope")).StatusCode);
}
```

`OpenApi_lists_the_status_codes_each_route_returns` 的 `InlineData` 加一列：

```csharp
[InlineData("/api/sessions/{id}", "get", "200,404")]
```

`FinalizedSession()` 裡的 `new FinalPrompt("1girl", "lowres", "tips", "一個女生")`（Task 1 已補，確認第 4 個引數就是「一個女生」）。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src && dotnet test --filter "FullyQualifiedName~EndpointTests" 2>&1 | tail -15`
Expected: 三個新測試 FAIL（404），OpenApi 那條多一列 FAIL。

- [ ] **Step 3: 實作端點**

`SessionEndpoints.cs` 的 record 區加：

```csharp
public sealed record FinalDto(string Positive, string Negative, string Tips, string IntentSummary);
public sealed record SessionSnapshotDto(string SessionId, string Status, string? Profile, int TurnIndex, int AskCount, int AskLimit,
    IReadOnlyDictionary<string, string> FacetStates, FinalDto? LastFinal);
```

`Map` 裡、`g.MapPost("/", …)` 之後加：

```csharp
g.MapGet("/{id}", (string id, SessionStore store, OrchestratorOptions options) =>
{
    var s = store.TryGet(id);
    if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
    // 不拿 session 鎖：重載時連線已隨頁面斷掉、那一輪已回滾；兩個分頁共用同一個 id 時讀到半途狀態是可接受的最壞情況。
    var final = s.LastFinal is { } f ? new FinalDto(f.Positive, f.Negative, f.Tips, f.IntentSummary) : null;
    return Results.Ok(new SessionSnapshotDto(s.Id, s.Status.ToString(), s.Profile, s.TurnIndex, s.AskCount, options.MaxAskCount,
        s.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)), final));
})
.WithSummary("讀 session 目前的狀態")
.WithDescription("""
    給前端整頁重載後重建畫面用：狀態（`Collecting`／`Finalized`）、題材、每個 facet 的狀態、追問已用幾次（`askCount`／`askLimit`）、最後一次定稿（未定稿為 `null`）。

    不含對話紀錄：對話流由前端自己保存。唯讀，不影響 session 的過期計時以外的任何狀態。

    - `404`：session 不存在或已過期
    """)
.Produces<SessionSnapshotDto>(StatusCodes.Status200OK)
.Produces<ErrorBody>(StatusCodes.Status404NotFound);
```

`Results.Ok` 的 `Status` 用 `s.Status.ToString()` 會得到 `Collecting`／`Finalized`，跟 `session` 事件一致。

- [ ] **Step 4: 跑全部測試**

Run: `cd src && dotnet test 2>&1 | tail -5`
Expected: 全綠。

- [ ] **Step 5: 同步文件**

主規格 §10.1 端點表 `POST /api/sessions` 那列之後加：
`| \`GET\` | \`/api/sessions/{id}\` | session 目前的權威狀態（status、profile、facetStates、askCount/askLimit、lastFinal）；前端重載重建用；\`404\` 同上 |`

多輪設計 §11 最後一條（「前端整頁重載後 session 狀態拿不回來」）結尾加：「→ 子專案 3 設計 §3.4 以 `GET /api/sessions/{id}` + 前端 `sessionStorage` 處理。」

- [ ] **Step 6: Commit**

```bash
git add src docs/superpowers/specs
git commit -m "feat(api): GET /api/sessions/{id} for page-reload recovery

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: 手動確認一輪真的帶 `intentSummary`

不寫自動測試（要真打 Gemini）。目的：確認模型會填這個欄位、system prompt 的說明夠用。

- [ ] **Step 1: 起 API**

Run（另開終端機，之後一直掛著）: `python manual-tests/start_api.py`

- [ ] **Step 2: 跑一輪到定稿**

Run: `python manual-tests/chat.py --raw`
輸入「一個銀髮少女站在雨夜的霓虹街頭，寫實攝影，其他隨便」。
Expected: `final` 事件的原始 JSON 有 `"intentSummary"`，內容是一句繁中描述，不是 tag、不是空字串。若模型沒填而回「錯誤：intentSummary 不可為空」後重試成功，也算過；若連續失敗，把 `system.md` 那條說明改具體一點（加一個範例）再試。

- [ ] **Step 3: 停 API**

Ctrl+C 停 `start_api.py`（後面 `dotnet test` 才跑得動）。若 Step 2 有改 `system.md`，commit：

```bash
git add src/PromptCopilot.Api/Prompts/system.md
git commit -m "docs(prompt): sharpen the intentSummary instruction after a live run

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Nuxt 專案骨架、devProxy、SSE 穿透驗證

**Files:**
- Create: `src/PromptCopilot.Frontend/package.json`、`nuxt.config.ts`、`tsconfig.json`、`vitest.config.ts`、`tailwind.config.ts`、`assets/css/main.css`、`app.vue`（暫時版）、`types/api.ts`、`README.md`
- Modify: `.gitignore`（已有 `node_modules/`、`.nuxt/`、`.output/`、`dist/`，確認即可）

**Interfaces:**
- Produces: `types/api.ts` 的所有型別，後續每個任務都 import 它。`useRuntimeConfig().public.apiBase`（預設 `''`，同源）。

- [ ] **Step 1: 確認 Node**

Run: `node --version && npm --version`
Expected: `v22.x.x`。沒有的話先裝 Node LTS 22（<https://nodejs.org>），裝完重開終端機。這是前置條件，裝不了就停在這裡回報。

- [ ] **Step 2: 建專案（手寫 package.json，不用 `nuxi init`，避免拿到 Nuxt 4 的目錄結構）**

建 `src/PromptCopilot.Frontend/package.json`：

```json
{
  "name": "promptcopilot-frontend",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "nuxt dev",
    "build": "nuxt build",
    "preview": "nuxt preview",
    "postinstall": "nuxt prepare",
    "test": "vitest run",
    "test:watch": "vitest"
  }
}
```

Run:

```bash
cd src/PromptCopilot.Frontend
npm install nuxt@3 vue pinia @pinia/nuxt
npm install -D @nuxtjs/tailwindcss vitest typescript
```

Expected: `package.json` 裡 `nuxt` 是 `^3.x`。若 `@pinia/nuxt` 的 peer 版本跟 `pinia` 衝突，照 npm 的錯誤訊息把 `pinia` 降到它要的主版本。

- [ ] **Step 3: 設定檔**

`nuxt.config.ts`：

```ts
export default defineNuxtConfig({
  ssr: false,
  compatibilityDate: '2026-09-24',
  devtools: { enabled: false },
  modules: ['@pinia/nuxt', '@nuxtjs/tailwindcss'],
  pinia: { storesDirs: ['./stores/**'] },   // 讓 useSessionStore 可以 auto-import
  css: ['~/assets/css/main.css'],
  app: {
    head: {
      title: 'Prompt Copilot',
      htmlAttrs: { lang: 'zh-Hant' },
      meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1' }],
    },
  },
  runtimeConfig: {
    public: {
      // '' = 同源（經 devProxy／nginx 反代）。devProxy 若會緩衝 SSE，改成 http://localhost:5000 並在 API 的 Development 開 CORS（spec §2.5 備案）。
      apiBase: '',
    },
  },
  nitro: {
    devProxy: {
      '/api': { target: 'http://localhost:5000/api', changeOrigin: true },
      '/health': { target: 'http://localhost:5000/health', changeOrigin: true },
    },
  },
})
```

`tsconfig.json`：

```json
{ "extends": "./.nuxt/tsconfig.json" }
```

`vitest.config.ts`（純 node 環境，不載 Nuxt）：

```ts
import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts'],
  },
})
```

`tailwind.config.ts`：

```ts
import type { Config } from 'tailwindcss'

export default <Partial<Config>>{
  content: ['./components/**/*.vue', './app.vue', './lib/**/*.ts'],
  theme: { extend: {} },
}
```

`assets/css/main.css`：

```css
@tailwind base;
@tailwind components;
@tailwind utilities;

html, body, #__nuxt { height: 100%; }
body { @apply bg-neutral-50 text-neutral-900 antialiased; }
@media (prefers-color-scheme: dark) {
  body { @apply bg-neutral-950 text-neutral-100; }
}
```

暫時版 `app.vue`（Task 12 會換掉）：

```vue
<template>
  <main class="p-6">
    <h1 class="text-xl font-semibold">Prompt Copilot</h1>
    <p class="mt-2 text-sm text-neutral-500">骨架已就緒；health = {{ health }}</p>
  </main>
</template>

<script setup lang="ts">
const health = ref('…')
onMounted(async () => {
  try {
    const r = await fetch(`${useRuntimeConfig().public.apiBase}/health`)
    health.value = r.ok ? 'ok' : `HTTP ${r.status}`
  } catch { health.value = '連不到後端' }
})
</script>
```

- [ ] **Step 4: 型別檔**

`types/api.ts`（後端契約的唯一 TS 定義處；欄位名對照 `AgentEvent.cs`、`SessionEndpoints.cs`、`ReferenceEndpoints.cs`、`PresetRepository.cs`）：

```ts
export type FacetState = 'covered' | 'missing' | 'waived' | 'notApplicable'
export type SessionStatus = 'Collecting' | 'Finalized'

export interface OptionItem { label: string; tags: string; presetId: number | null }
export interface AskItem { dimension: string; question: string; missingFacetIds: string[]; options: OptionItem[] }
export interface PresetRef { id: number; title: string; imageUrl: string | null }

export interface FinalizedData { kind: 'finalized'; positive: string; negative: string; tips: string; intentSummary: string }
export type FinalData =
  | { kind: 'ask'; preamble: string; asks: AskItem[] }
  | { kind: 'message'; message: string; options?: OptionItem[] }
  | FinalizedData
  | { kind: 'save_consent_requested' }

export type AgentEvent =
  | { type: 'session'; sessionId: string; turnIndex: number; status: SessionStatus }
  | { type: 'tool_call'; callId: string; name: string; argsSummary: string }
  | { type: 'tool_result'; callId: string; name: string; summary: string; presets?: PresetRef[] }
  | { type: 'dimensions'; profile: string | null; facetStates: Record<string, FacetState> }
  | { type: 'token'; text: string }
  | ({ type: 'final' } & FinalData)
  | { type: 'blocked'; reason: string; message: string }
  | { type: 'error'; code: string; message: string }

export type AgentEventType = AgentEvent['type']
export const AGENT_EVENT_TYPES = ['session', 'tool_call', 'tool_result', 'dimensions', 'token', 'final', 'blocked', 'error'] as const satisfies readonly AgentEventType[]

/** GET /api/sessions/{id} */
export interface SessionSnapshotDto {
  sessionId: string
  status: SessionStatus
  profile: string | null
  turnIndex: number
  askCount: number
  askLimit: number
  facetStates: Record<string, FacetState>
  lastFinal: { positive: string; negative: string; tips: string; intentSummary: string } | null
}

/** GET /api/config/facets */
export interface FacetCatalog {
  dimensions: { key: string; label: string; facets: { id: string; label: string; hint: string }[] }[]
  profiles: Record<string, { labels: Record<string, string>; dimensions: Record<string, string[]> }>
}

/** GET /api/presets/{id} */
export interface PresetDetail {
  id: number
  title: string
  category: string
  description: string
  tags: string[]
  facetIds: string[]
  promptSnippet: string
  negativeSnippet: string | null
  imageUrl: string | null
}

/** 終止型 tool 與純狀態 tool 不發 tool_result；卡片在輪次結束時收尾。終止型的內容由 final 條目呈現，不另畫卡片。 */
export const TERMINAL_TOOLS: ReadonlySet<string> = new Set(['AskUser', 'Discuss', 'FinalizePrompt', 'RequestSaveConsent'])
```

- [ ] **Step 5: build 與 dev 跑得起來**

Run: `cd src/PromptCopilot.Frontend && npm run build`
Expected: 成功，產出 `.output/`。

Run（另開終端機）: `python manual-tests/start_api.py`，再 `cd src/PromptCopilot.Frontend && npm run dev`
開 `http://localhost:3000`。Expected: 看到「health = ok」——代表 devProxy 通了。

- [ ] **Step 6: 驗證 SSE 經 devProxy 不被緩衝（spec §2.5 的獨立驗證項）**

在專案根目錄用 Git Bash：

```bash
SID=$(curl.exe -s -X POST localhost:3000/api/sessions | sed -E 's/.*"sessionId":"([^"]+)".*/\1/')
printf '{"text":"一個銀髮少女站在雨夜的霓虹街頭"}' > "$TEMP/body.json"
curl.exe -N -X POST "localhost:3000/api/sessions/$SID/messages" -H 'content-type: application/json' --data-binary "@$TEMP/body.json"
```

Expected: `event: session` 立刻出現，之後 `tool_call`／`tool_result` **逐筆**出現（間隔數秒），不是等十幾秒一次全吐。
若是一次全吐：啟用備案——`runtimeConfig.public.apiBase` 改成 `'http://localhost:5000'`，並在 `Program.cs` 加（只在 Development）：

```csharp
if (builder.Environment.IsDevelopment())
    services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));
// … builder.Build() 之後：
if (app.Environment.IsDevelopment()) app.UseCors();
```

並把 spec §2.5 改成記錄這個結果。備案要在 `dotnet test` 全綠後另外 commit。

- [ ] **Step 7: README**

`src/PromptCopilot.Frontend/README.md`：

```markdown
# PromptCopilot.Frontend

Nuxt 3 SPA（`ssr: false`）。對話流、六維度儀表板、preset 抽屜。

## 跑起來

需要 Node LTS 22。API 要先在 `http://localhost:5000` 跑著（`python manual-tests/start_api.py`）。

    npm install
    npm run dev          # http://localhost:3000；/api 與 /health 經 devProxy 轉到 API

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
```

- [ ] **Step 8: Commit**

停掉 `npm run dev` 與 `start_api.py`。

```bash
git add src/PromptCopilot.Frontend .gitignore
git commit -m "feat(frontend): Nuxt 3 skeleton with devProxy, Tailwind, Pinia, vitest and the API types

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: SSE 解析器 `lib/sse.ts`

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/sse.ts`
- Test: `src/PromptCopilot.Frontend/tests/sse.test.ts`

**Interfaces:**
- Produces: `readSse(stream: ReadableStream<Uint8Array>): AsyncGenerator<SseFrame>`，`SseFrame = { event: string; data: string }`。只負責切 frame，不解析 JSON。

- [ ] **Step 1: 寫失敗的測試**

`tests/sse.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { readSse } from '../lib/sse'

const enc = new TextEncoder()
function streamOf(chunks: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(c) {
      for (const ch of chunks) c.enqueue(typeof ch === 'string' ? enc.encode(ch) : ch)
      c.close()
    },
  })
}
async function collect(s: ReadableStream<Uint8Array>) {
  const out = []
  for await (const f of readSse(s)) out.push(f)
  return out
}

describe('readSse', () => {
  it('parses one frame', async () => {
    const out = await collect(streamOf(['event: session\ndata: {"a":1}\n\n']))
    expect(out).toEqual([{ event: 'session', data: '{"a":1}' }])
  })

  it('handles two frames in one chunk', async () => {
    const out = await collect(streamOf(['event: a\ndata: 1\n\nevent: b\ndata: 2\n\n']))
    expect(out.map(f => f.event)).toEqual(['a', 'b'])
  })

  it('handles a frame split across chunks mid-line', async () => {
    const out = await collect(streamOf(['event: too', 'l_call\ndata: {"x":', '"y"}\n\n']))
    expect(out).toEqual([{ event: 'tool_call', data: '{"x":"y"}' }])
  })

  it('handles a multibyte character split across chunks', async () => {
    const bytes = enc.encode('event: token\ndata: {"text":"霓虹"}\n\n')
    const cut = 'event: token\ndata: {"text":"'.length + 1   // 「霓」是 3 個位元組，切在第 1 個之後
    const out = await collect(streamOf([bytes.slice(0, cut), bytes.slice(cut)]))
    expect(out).toEqual([{ event: 'token', data: '{"text":"霓虹"}' }])
  })

  it('joins multiple data lines with newline and ignores comments', async () => {
    const out = await collect(streamOf([': keep-alive\nevent: x\ndata: l1\ndata: l2\n\n']))
    expect(out).toEqual([{ event: 'x', data: 'l1\nl2' }])
  })

  it('defaults event to "message" when absent and tolerates CRLF', async () => {
    const out = await collect(streamOf(['data: 1\r\n\r\n']))
    expect(out).toEqual([{ event: 'message', data: '1' }])
  })

  it('emits a trailing frame that has no final blank line', async () => {
    const out = await collect(streamOf(['event: error\ndata: {"code":"x"}']))
    expect(out).toEqual([{ event: 'error', data: '{"code":"x"}' }])
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/sse.test.ts`
Expected: FAIL，找不到 `../lib/sse`。

- [ ] **Step 3: 實作**

`lib/sse.ts`：

```ts
export interface SseFrame { event: string; data: string }

/** 把 text/event-stream 切成 frame。只切格式，不解析 JSON；chunk 邊界與多位元組字元由 TextDecoder(stream) 處理。 */
export async function* readSse(stream: ReadableStream<Uint8Array>): AsyncGenerator<SseFrame> {
  const reader = stream.getReader()
  const decoder = new TextDecoder('utf-8')
  let buf = ''
  try {
    while (true) {
      const { value, done } = await reader.read()
      buf += decoder.decode(value ?? new Uint8Array(), { stream: !done })
      buf = buf.replace(/\r\n/g, '\n')
      let idx: number
      while ((idx = buf.indexOf('\n\n')) >= 0) {
        const block = buf.slice(0, idx)
        buf = buf.slice(idx + 2)
        const frame = parseBlock(block)
        if (frame) yield frame
      }
      if (done) break
    }
    const tail = parseBlock(buf)
    if (tail) yield tail
  } finally {
    reader.releaseLock()
  }
}

function parseBlock(block: string): SseFrame | null {
  let event = 'message'
  const data: string[] = []
  for (const line of block.split('\n')) {
    if (!line || line.startsWith(':')) continue
    const colon = line.indexOf(':')
    const field = colon < 0 ? line : line.slice(0, colon)
    let value = colon < 0 ? '' : line.slice(colon + 1)
    if (value.startsWith(' ')) value = value.slice(1)
    if (field === 'event') event = value
    else if (field === 'data') data.push(value)
  }
  if (data.length === 0) return null
  return { event, data: data.join('\n') }
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/sse.test.ts`
Expected: 7 passed。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/lib/sse.ts src/PromptCopilot.Frontend/tests/sse.test.ts
git commit -m "feat(frontend): SSE frame parser over ReadableStream

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: reducer `lib/reducer.ts`

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/reducer.ts`
- Test: `src/PromptCopilot.Frontend/tests/reducer.test.ts`

**Interfaces:**
- Consumes: `types/api.ts`。
- Produces（後面 store 與元件都靠這些名字）：

```ts
export interface UserEntry    { kind: 'user'; text: string }
export interface ToolEntry    { kind: 'tool'; callId: string; name: string; argsSummary: string; summary: string | null; presets: PresetRef[]; done: boolean }
export interface FinalEntry   { kind: 'final'; turnIndex: number; data: FinalData }
export interface FailureEntry { kind: 'failure'; source: 'error' | 'blocked' | 'stream_ended' | 'http'; code: string; message: string; originalText: string }
export type Entry = UserEntry | ToolEntry | FinalEntry | FailureEntry
export interface ChatState {
  sessionId: string | null; turnIndex: number; status: SessionStatus; profile: string | null
  facetStates: Record<string, FacetState>; askCount: number; askLimit: number
  transcript: Entry[]; lastFinal: FinalizedData | null
  highlighted: string[]              // ask 之後高亮的維度，下一個 session 事件清掉
  pending: { text: string; settled: boolean; snapshot: ChatState } | null
}
export function initialState(): ChatState
export function hydrate(state, dto: SessionSnapshotDto, transcript: Entry[]): ChatState
export function beginTurn(state, text): ChatState          // 快照 + 推 user 條目
export function applyEvent(state, ev: AgentEvent): ChatState
export function endTurn(state): ChatState                  // 串流結束：未 settled → stream_ended 失敗；tool 條目全部 done
export function failHttp(state, code, message): ChatState  // 非 200 回應：還原快照 + failure(http)
```

所有函式回**新物件**，不改參數。

- [ ] **Step 1: 寫失敗的測試**

`tests/reducer.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { initialState, beginTurn, applyEvent, endTurn, failHttp, hydrate, type ChatState } from '../lib/reducer'
import type { AgentEvent } from '../types/api'

const session = (turnIndex = 1): AgentEvent => ({ type: 'session', sessionId: 's1', turnIndex, status: 'Collecting' })
const call = (id: string, name = 'SearchPresets'): AgentEvent => ({ type: 'tool_call', callId: id, name, argsSummary: 'dimension: style' })
const result = (id: string): AgentEvent => ({ type: 'tool_result', callId: id, name: 'SearchPresets', summary: '風格 池 4455 → 3', presets: [{ id: 7, title: 't', imageUrl: null }] })
const dims: AgentEvent = { type: 'dimensions', profile: 'portrait', facetStates: { 'style.genre': 'covered', 'scene.location': 'missing' } }
const ask: AgentEvent = { type: 'final', kind: 'ask', preamble: 'p', asks: [{ dimension: 'style', question: 'q', missingFacetIds: ['style.genre'], options: [{ label: 'a', tags: 't', presetId: null }] }] }
const finalized: AgentEvent = { type: 'final', kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' }

function started(): ChatState {
  let s = initialState()
  s = { ...s, sessionId: 's1' }
  s = beginTurn(s, '一個女生')
  return applyEvent(s, session())
}

describe('beginTurn', () => {
  it('snapshots before pushing the user entry and marks pending', () => {
    const s = beginTurn({ ...initialState(), sessionId: 's1' }, 'hi')
    expect(s.transcript).toEqual([{ kind: 'user', text: 'hi' }])
    expect(s.pending?.text).toBe('hi')
    expect(s.pending?.settled).toBe(false)
    expect(s.pending?.snapshot.transcript).toEqual([])
    expect(s.pending?.snapshot.pending).toBeNull()
  })
})

describe('applyEvent', () => {
  it('session sets turnIndex/status and clears highlights', () => {
    const s = applyEvent({ ...started(), highlighted: ['style'] }, session(2))
    expect(s.turnIndex).toBe(2)
    expect(s.status).toBe('Collecting')
    expect(s.highlighted).toEqual([])
  })

  it('tool_call appends a pending tool entry; tool_result fills it by callId', () => {
    let s = applyEvent(started(), call('c1'))
    const t = s.transcript.at(-1)
    expect(t).toMatchObject({ kind: 'tool', callId: 'c1', done: false, summary: null })
    s = applyEvent(s, result('c1'))
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'tool', callId: 'c1', done: true, summary: '風格 池 4455 → 3' })
    expect((s.transcript.at(-1) as any).presets[0].id).toBe(7)
  })

  it('terminal tool calls are not turned into tool entries', () => {
    const s = applyEvent(started(), call('c9', 'FinalizePrompt'))
    expect(s.transcript.some(e => e.kind === 'tool')).toBe(false)
  })

  it('an orphan tool_result becomes a standalone done entry', () => {
    const s = applyEvent(started(), result('nope'))
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'tool', callId: 'nope', done: true, summary: '風格 池 4455 → 3' })
  })

  it('dimensions overwrite profile and facetStates', () => {
    const s = applyEvent(started(), dims)
    expect(s.profile).toBe('portrait')
    expect(s.facetStates).toEqual({ 'style.genre': 'covered', 'scene.location': 'missing' })
  })

  it('final ask pushes entry, bumps askCount, highlights dimensions, settles', () => {
    const s = applyEvent(started(), ask)
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'final', turnIndex: 1, data: { kind: 'ask' } })
    expect(s.askCount).toBe(1)
    expect(s.highlighted).toEqual(['style'])
    expect(s.pending?.settled).toBe(true)
  })

  it('final finalized sets lastFinal and status', () => {
    const s = applyEvent(started(), finalized)
    expect(s.lastFinal).toEqual({ kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' })
    expect(s.status).toBe('Finalized')
    expect(s.askCount).toBe(0)
  })

  it('error restores the snapshot and appends a failure with the original text', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, dims)
    s = applyEvent(s, { type: 'error', code: 'timeout', message: '逾時' })
    expect(s.profile).toBeNull()
    expect(s.transcript.filter(e => e.kind === 'tool')).toHaveLength(0)
    expect(s.transcript.filter(e => e.kind === 'user')).toHaveLength(0)
    expect(s.transcript.at(-1)).toEqual({ kind: 'failure', source: 'error', code: 'timeout', message: '逾時', originalText: '一個女生' })
    expect(s.pending?.settled).toBe(true)
  })

  it('blocked behaves like error with source blocked', () => {
    const s = applyEvent(started(), { type: 'blocked', reason: 'Blocked_NSFW', message: '被攔' })
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'failure', source: 'blocked', code: 'Blocked_NSFW' })
  })

  it('token appends text to the latest message entry and is a no-op otherwise', () => {
    let s = applyEvent(started(), { type: 'final', kind: 'message', message: 'ab' })
    s = applyEvent(s, { type: 'token', text: 'c' })
    expect((s.transcript.at(-1) as any).data.message).toBe('abc')
    const before = started()
    expect(applyEvent(before, { type: 'token', text: 'x' })).toEqual(before)
  })

  it('unknown events leave state untouched', () => {
    const before = started()
    expect(applyEvent(before, { type: 'whatever' } as any)).toEqual(before)
  })
})

describe('endTurn', () => {
  it('clears pending and marks all tool entries done when settled', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, ask)
    s = endTurn(s)
    expect(s.pending).toBeNull()
    expect(s.transcript.find(e => e.kind === 'tool')).toMatchObject({ done: true })
  })

  it('treats an unsettled end as stream_ended failure with rollback', () => {
    let s = applyEvent(started(), dims)
    s = endTurn(s)
    expect(s.pending).toBeNull()
    expect(s.profile).toBeNull()
    expect(s.transcript.at(-1)).toMatchObject({ kind: 'failure', source: 'stream_ended', originalText: '一個女生' })
  })

  it('is a no-op when nothing is pending', () => {
    const s = initialState()
    expect(endTurn(s)).toEqual(s)
  })
})

describe('failHttp', () => {
  it('rolls back and records an http failure', () => {
    const s = failHttp(beginTurn({ ...initialState(), sessionId: 's1' }, 'hi'), 'http_409', '這個對話還有一輪在跑')
    expect(s.pending).toBeNull()
    expect(s.transcript).toEqual([{ kind: 'failure', source: 'http', code: 'http_409', message: '這個對話還有一輪在跑', originalText: 'hi' }])
  })
})

describe('hydrate', () => {
  it('takes authoritative fields from the dto and the transcript from storage', () => {
    const s = hydrate(initialState(), {
      sessionId: 's1', status: 'Finalized', profile: 'landscape', turnIndex: 4, askCount: 2, askLimit: 2,
      facetStates: { 'scene.location': 'covered' },
      lastFinal: { positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' },
    }, [{ kind: 'user', text: 'x' }])
    expect(s).toMatchObject({ sessionId: 's1', status: 'Finalized', profile: 'landscape', turnIndex: 4, askCount: 2, askLimit: 2 })
    expect(s.lastFinal).toEqual({ kind: 'finalized', positive: 'P', negative: 'N', tips: 'T', intentSummary: 'I' })
    expect(s.transcript).toEqual([{ kind: 'user', text: 'x' }])
    expect(s.pending).toBeNull()
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/reducer.test.ts`
Expected: FAIL，找不到 `../lib/reducer`。

- [ ] **Step 3: 實作**

`lib/reducer.ts`：

```ts
import { TERMINAL_TOOLS, type AgentEvent, type FacetState, type FinalData, type FinalizedData, type PresetRef, type SessionSnapshotDto, type SessionStatus } from '../types/api'

export interface UserEntry { kind: 'user'; text: string }
export interface ToolEntry { kind: 'tool'; callId: string; name: string; argsSummary: string; summary: string | null; presets: PresetRef[]; done: boolean }
export interface FinalEntry { kind: 'final'; turnIndex: number; data: FinalData }
export interface FailureEntry { kind: 'failure'; source: 'error' | 'blocked' | 'stream_ended' | 'http'; code: string; message: string; originalText: string }
export type Entry = UserEntry | ToolEntry | FinalEntry | FailureEntry

export interface ChatState {
  sessionId: string | null
  turnIndex: number
  status: SessionStatus
  profile: string | null
  facetStates: Record<string, FacetState>
  askCount: number
  askLimit: number
  transcript: Entry[]
  lastFinal: FinalizedData | null
  highlighted: string[]
  pending: { text: string; settled: boolean; snapshot: ChatState } | null
}

export function initialState(): ChatState {
  return {
    sessionId: null, turnIndex: 0, status: 'Collecting', profile: null,
    facetStates: {}, askCount: 0, askLimit: 2,
    transcript: [], lastFinal: null, highlighted: [], pending: null,
  }
}

export function hydrate(state: ChatState, dto: SessionSnapshotDto, transcript: Entry[]): ChatState {
  return {
    ...state,
    sessionId: dto.sessionId, status: dto.status, profile: dto.profile, turnIndex: dto.turnIndex,
    askCount: dto.askCount, askLimit: dto.askLimit, facetStates: { ...dto.facetStates },
    lastFinal: dto.lastFinal ? { kind: 'finalized', ...dto.lastFinal } : null,
    transcript: [...transcript], highlighted: [], pending: null,
  }
}

/** 送出當下：先快照（不含 pending），再推 user 條目。斷線可能發生在第一個事件之前，所以不能等 session 事件才快照。
 *  用 JSON 複製而不是 structuredClone：store 傳進來的是 Vue 的 reactive proxy，structuredClone 會丟 DataCloneError；
 *  狀態全是純資料（沒有 undefined／Date／函式），JSON 來回不失真。 */
export function beginTurn(state: ChatState, text: string): ChatState {
  const snapshot: ChatState = JSON.parse(JSON.stringify({ ...state, pending: null }))
  return { ...state, transcript: [...state.transcript, { kind: 'user', text }], pending: { text, settled: false, snapshot } }
}

export function applyEvent(state: ChatState, ev: AgentEvent): ChatState {
  switch (ev.type) {
    case 'session':
      return { ...state, sessionId: ev.sessionId, turnIndex: ev.turnIndex, status: ev.status, highlighted: [] }

    case 'tool_call': {
      if (TERMINAL_TOOLS.has(ev.name)) return state
      const entry: ToolEntry = { kind: 'tool', callId: ev.callId, name: ev.name, argsSummary: ev.argsSummary, summary: null, presets: [], done: false }
      return { ...state, transcript: [...state.transcript, entry] }
    }

    case 'tool_result': {
      const idx = state.transcript.findIndex(e => e.kind === 'tool' && e.callId === ev.callId)
      const presets = ev.presets ?? []
      if (idx < 0) {
        const orphan: ToolEntry = { kind: 'tool', callId: ev.callId, name: ev.name, argsSummary: '', summary: ev.summary, presets, done: true }
        return { ...state, transcript: [...state.transcript, orphan] }
      }
      const transcript = state.transcript.slice()
      transcript[idx] = { ...(transcript[idx] as ToolEntry), summary: ev.summary, presets, done: true }
      return { ...state, transcript }
    }

    case 'dimensions':
      return { ...state, profile: ev.profile, facetStates: { ...ev.facetStates } }

    case 'token': {
      const idx = findLastIndex(state.transcript, e => e.kind === 'final' && e.data.kind === 'message')
      if (idx < 0) return state
      const transcript = state.transcript.slice()
      const entry = transcript[idx] as FinalEntry
      if (entry.data.kind !== 'message') return state
      transcript[idx] = { ...entry, data: { ...entry.data, message: entry.data.message + ev.text } }
      return { ...state, transcript }
    }

    case 'final': {
      // 拿掉 type 欄位；用 ev.kind 收窄（rest 物件會失去 discriminated union）
      const { type: _t, ...rest } = ev
      const data = rest as FinalData
      const entry: FinalEntry = { kind: 'final', turnIndex: state.turnIndex, data }
      let next: ChatState = { ...state, transcript: [...state.transcript, entry], pending: settle(state.pending) }
      if (ev.kind === 'ask') {
        next = { ...next, askCount: state.askCount + 1, highlighted: ev.asks.map(a => a.dimension) }
      } else if (ev.kind === 'finalized') {
        next = { ...next, lastFinal: data as FinalizedData, status: 'Finalized' }
      }
      return next
    }

    case 'error':
      return fail(state, 'error', ev.code, ev.message)

    case 'blocked':
      return fail(state, 'blocked', ev.reason, ev.message)

    default:
      return state
  }
}

/** 串流關閉。沒收到終止事件就當 stream_ended（後端 RequestAborted 會回滾，這裡對稱）；所有 tool 卡片收尾。 */
export function endTurn(state: ChatState): ChatState {
  if (!state.pending) return state
  const s = state.pending.settled ? state : fail(state, 'stream_ended', 'stream_ended', '連線在這一輪結束前中斷了。已還原到送出前的狀態，可以直接再送一次。')
  return {
    ...s,
    pending: null,
    transcript: s.transcript.map(e => (e.kind === 'tool' && !e.done ? { ...e, done: true } : e)),
  }
}

export function failHttp(state: ChatState, code: string, message: string): ChatState {
  return { ...fail(state, 'http', code, message), pending: null }
}

function fail(state: ChatState, source: FailureEntry['source'], code: string, message: string): ChatState {
  const p = state.pending
  const base = p ? p.snapshot : state
  const failure: FailureEntry = { kind: 'failure', source, code, message, originalText: p?.text ?? '' }
  return { ...base, transcript: [...base.transcript, failure], pending: p ? { ...p, settled: true } : null }
}

function settle(p: ChatState['pending']) { return p ? { ...p, settled: true } : null }

function findLastIndex<T>(xs: T[], pred: (x: T) => boolean): number {
  for (let i = xs.length - 1; i >= 0; i--) if (pred(xs[i])) return i
  return -1
}
```

注意 `fail` 從快照還原時，快照裡的 `sessionId` 可能是 null（第一輪送出前還沒建 session 的情況不會發生——store 一定先建 session 再 `beginTurn`），不用特別處理。

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/reducer.test.ts`
Expected: 全部 passed。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/lib/reducer.ts src/PromptCopilot.Frontend/tests/reducer.test.ts
git commit -m "feat(frontend): pure reducer with per-turn snapshot and rollback

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: `lib/persist.ts`、`lib/composer.ts`、`lib/copy.ts`

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/persist.ts`、`lib/composer.ts`、`lib/copy.ts`
- Test: `src/PromptCopilot.Frontend/tests/persist.test.ts`、`tests/composer.test.ts`

**Interfaces:**
- Produces:

```ts
// persist.ts
export interface Persisted { v: 1; sessionId: string; transcript: Entry[] }
export interface StorageLike { getItem(k: string): string | null; setItem(k: string, v: string): void; removeItem(k: string): void }
export function loadPersisted(storage?: StorageLike): Persisted | null
export function savePersisted(p: Omit<Persisted, 'v'>, storage?: StorageLike): void
export function clearPersisted(storage?: StorageLike): void
// composer.ts
export interface Chip { dimension: string | null; label: string }
export function chipKey(c: Chip): string
export function composeDraft(chips: Chip[], dimLabels: Record<string, string>): string
export function appendChip(draft: string, chip: Chip, dimLabels: Record<string, string>): string
// copy.ts
export function failureTitle(source, code): string
```

- [ ] **Step 1: 寫失敗的測試**

`tests/persist.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { loadPersisted, savePersisted, clearPersisted, type StorageLike } from '../lib/persist'

function memStorage(): StorageLike & { map: Map<string, string> } {
  const map = new Map<string, string>()
  return { map, getItem: k => map.get(k) ?? null, setItem: (k, v) => { map.set(k, v) }, removeItem: k => { map.delete(k) } }
}
const throwing: StorageLike = {
  getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('denied') }, removeItem: () => { throw new Error('denied') },
}

describe('persist', () => {
  it('round-trips sessionId and transcript', () => {
    const s = memStorage()
    savePersisted({ sessionId: 'abc', transcript: [{ kind: 'user', text: '嗨' }] }, s)
    expect(loadPersisted(s)).toEqual({ v: 1, sessionId: 'abc', transcript: [{ kind: 'user', text: '嗨' }] })
    clearPersisted(s)
    expect(loadPersisted(s)).toBeNull()
  })

  it('returns null for garbage or wrong version', () => {
    const s = memStorage()
    s.setItem('pc.session', '{not json')
    expect(loadPersisted(s)).toBeNull()
    s.setItem('pc.session', JSON.stringify({ v: 0, sessionId: 'x', transcript: [] }))
    expect(loadPersisted(s)).toBeNull()
  })

  it('never throws when storage is unavailable', () => {
    expect(loadPersisted(throwing)).toBeNull()
    expect(() => savePersisted({ sessionId: 'x', transcript: [] }, throwing)).not.toThrow()
    expect(() => clearPersisted(throwing)).not.toThrow()
  })
})
```

`tests/composer.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { composeDraft, appendChip, chipKey } from '../lib/composer'

const L = { style: '風格', scene: '場景' }

describe('composeDraft', () => {
  it('groups chips by dimension with prefix and 、', () => {
    expect(composeDraft([
      { dimension: 'style', label: '寫實攝影' }, { dimension: 'scene', label: '雨夜街頭' }, { dimension: 'style', label: '柔光' },
    ], L)).toBe('[風格] 寫實攝影、柔光\n[場景] 雨夜街頭')
  })

  it('renders dimension-less chips without a prefix', () => {
    expect(composeDraft([{ dimension: null, label: '厚塗油畫' }, { dimension: null, label: '賽璐璐' }], L)).toBe('厚塗油畫、賽璐璐')
  })

  it('falls back to the raw dimension key when no label is known', () => {
    expect(composeDraft([{ dimension: 'pose', label: '回眸' }], L)).toBe('[pose] 回眸')
  })

  it('is empty for no chips', () => {
    expect(composeDraft([], L)).toBe('')
  })
})

describe('appendChip', () => {
  it('appends on a new line after manual text', () => {
    expect(appendChip('我要一個女生', { dimension: 'style', label: '寫實攝影' }, L)).toBe('我要一個女生\n[風格] 寫實攝影')
  })
  it('does not add a leading newline to an empty draft', () => {
    expect(appendChip('', { dimension: null, label: 'x' }, L)).toBe('x')
  })
})

describe('chipKey', () => {
  it('distinguishes same label across dimensions', () => {
    expect(chipKey({ dimension: 'style', label: 'a' })).not.toBe(chipKey({ dimension: 'scene', label: 'a' }))
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/persist.test.ts tests/composer.test.ts`
Expected: FAIL，模組不存在。

- [ ] **Step 3: 實作**

`lib/persist.ts`：

```ts
import type { Entry } from './reducer'

export interface Persisted { v: 1; sessionId: string; transcript: Entry[] }
export interface StorageLike { getItem(k: string): string | null; setItem(k: string, v: string): void; removeItem(k: string): void }

const KEY = 'pc.session'

function defaultStorage(): StorageLike | null {
  try { return typeof sessionStorage === 'undefined' ? null : sessionStorage } catch { return null }
}

/** 只存顯示用的 transcript 與 sessionId。任何錯誤（隱私模式、被停用、壞資料）都當作沒有。 */
export function loadPersisted(storage: StorageLike | null = defaultStorage()): Persisted | null {
  try {
    const raw = storage?.getItem(KEY)
    if (!raw) return null
    const p = JSON.parse(raw)
    if (!p || p.v !== 1 || typeof p.sessionId !== 'string' || !Array.isArray(p.transcript)) return null
    return p as Persisted
  } catch { return null }
}

export function savePersisted(p: Omit<Persisted, 'v'>, storage: StorageLike | null = defaultStorage()): void {
  try { storage?.setItem(KEY, JSON.stringify({ v: 1, ...p })) } catch { /* 存不進去就算了，重載時會開新對話 */ }
}

export function clearPersisted(storage: StorageLike | null = defaultStorage()): void {
  try { storage?.removeItem(KEY) } catch { /* 同上 */ }
}
```

`lib/composer.ts`：

```ts
export interface Chip { dimension: string | null; label: string }

export function chipKey(c: Chip): string { return `${c.dimension ?? ''}\u0000${c.label}` }

function prefix(dimension: string | null, dimLabels: Record<string, string>): string {
  if (dimension === null) return ''
  return `[${dimLabels[dimension] ?? dimension}] `
}

/** 已選 chip → 輸入框文字。同維度用「、」接在前綴後，不同維度各一行；無維度的排最後、不加前綴。 */
export function composeDraft(chips: Chip[], dimLabels: Record<string, string>): string {
  const groups = new Map<string | null, string[]>()
  for (const c of chips) {
    const list = groups.get(c.dimension) ?? []
    list.push(c.label)
    groups.set(c.dimension, list)
  }
  const lines: string[] = []
  for (const [dim, labels] of groups) if (dim !== null) lines.push(prefix(dim, dimLabels) + labels.join('、'))
  const free = groups.get(null)
  if (free) lines.push(free.join('、'))
  return lines.join('\n')
}

/** 使用者手動改過草稿之後，chip 只做附加，不重組。 */
export function appendChip(draft: string, chip: Chip, dimLabels: Record<string, string>): string {
  const line = prefix(chip.dimension, dimLabels) + chip.label
  return draft ? `${draft}\n${line}` : line
}
```

`lib/copy.ts`：

```ts
import type { FailureEntry } from './reducer'

const TITLES: Record<string, string> = {
  Blocked_NSFW: '輸入被安全規則攔下',
  Blocked_Celebrity: '輸入涉及真實人物，被攔下',
  Blocked_Output: '模型的輸出被安全規則攔下',
  Blocked_Upstream: '上游模型拒絕生成這段內容',
  timeout: '這一輪逾時',
  protocol_violation: '模型沒有依規定結束這一輪',
  turn_failed: '這一輪失敗',
  stream_ended: '連線中斷',
  http_404: '上次的對話已過期',
  http_409: '這個對話還有一輪在跑',
}

export function failureTitle(source: FailureEntry['source'], code: string): string {
  return TITLES[code] ?? (source === 'blocked' ? '被攔下' : '發生錯誤')
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src/PromptCopilot.Frontend && npm test`
Expected: 全部 passed。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/lib src/PromptCopilot.Frontend/tests
git commit -m "feat(frontend): sessionStorage persistence, chip composer and failure copy

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: `useApi` 與 Pinia store

**Files:**
- Create: `src/PromptCopilot.Frontend/composables/useApi.ts`
- Create: `src/PromptCopilot.Frontend/stores/session.ts`

**Interfaces:**
- Consumes: Task 6 `readSse`、Task 7 reducer、Task 8 persist/composer、`types/api.ts`。
- Produces（元件靠這些）：

```ts
// useApi
createSession(): Promise<string>
getSession(id): Promise<SessionSnapshotDto | null>            // 404 → null
getFacets(): Promise<FacetCatalog>
getPreset(id): Promise<PresetDetail | null>                   // 404 → null
saveToShared(id, intent): Promise<{ ok: true; id: string } | { ok: false; status: number; error: string }>
openStream(id, text, signal): Promise<Response>               // 不檢查 status，交給 store
// store useSessionStore()
state: ChatState; catalog: FacetCatalog | null; bootError: string | null; notice: string | null
draft: string; chips: Chip[]; draftDirty: boolean; busy: boolean
drawerPresetId: number | null; expandedSaveTurn: number | null; saveState: Record<number, { status: 'idle'|'saving'|'saved'|'error'; error?: string }>
dimensionLabels: ComputedRef<Record<string, string>>
boot(); newSession(); send(); retry(originalText); toggleChip(chip); setDraft(text)
openDrawer(id); closeDrawer(); expandSave(turnIndex); save(turnIndex, intent)
```

不寫單元測試（都是 I/O 黏合，邏輯在 `lib/`）；Task 12 之後用瀏覽器驗。

- [ ] **Step 1: `composables/useApi.ts`**

```ts
import type { FacetCatalog, PresetDetail, SessionSnapshotDto } from '../types/api'

export function useApi() {
  const base = useRuntimeConfig().public.apiBase as string

  async function createSession(): Promise<string> {
    const r = await fetch(`${base}/api/sessions`, { method: 'POST' })
    if (!r.ok) throw new Error(`createSession HTTP ${r.status}`)
    return (await r.json()).sessionId as string
  }

  async function getSession(id: string): Promise<SessionSnapshotDto | null> {
    const r = await fetch(`${base}/api/sessions/${encodeURIComponent(id)}`)
    if (r.status === 404) return null
    if (!r.ok) throw new Error(`getSession HTTP ${r.status}`)
    return await r.json()
  }

  async function getFacets(): Promise<FacetCatalog> {
    const r = await fetch(`${base}/api/config/facets`)
    if (!r.ok) throw new Error(`getFacets HTTP ${r.status}`)
    return await r.json()
  }

  async function getPreset(id: number): Promise<PresetDetail | null> {
    const r = await fetch(`${base}/api/presets/${id}`)
    if (r.status === 404) return null
    if (!r.ok) throw new Error(`getPreset HTTP ${r.status}`)
    return await r.json()
  }

  async function saveToShared(id: string, intent: string): Promise<{ ok: true; id: string } | { ok: false; status: number; error: string }> {
    const r = await fetch(`${base}/api/sessions/${encodeURIComponent(id)}/save-to-shared`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ intent }),
    })
    if (r.ok) return { ok: true, id: (await r.json()).id }
    let error = `HTTP ${r.status}`
    try { error = (await r.json()).error ?? error } catch { /* 沒 body 就用狀態碼 */ }
    return { ok: false, status: r.status, error }
  }

  function openStream(id: string, text: string, signal: AbortSignal): Promise<Response> {
    return fetch(`${base}/api/sessions/${encodeURIComponent(id)}/messages`, {
      method: 'POST', headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
      body: JSON.stringify({ text }), signal,
    })
  }

  return { createSession, getSession, getFacets, getPreset, saveToShared, openStream }
}
```

- [ ] **Step 2: `stores/session.ts`**

```ts
import { defineStore } from 'pinia'
import { readSse } from '../lib/sse'
import { initialState, beginTurn, applyEvent, endTurn, failHttp, hydrate, type ChatState } from '../lib/reducer'
import { loadPersisted, savePersisted, clearPersisted } from '../lib/persist'
import { composeDraft, appendChip, chipKey, type Chip } from '../lib/composer'
import { AGENT_EVENT_TYPES, type AgentEvent, type FacetCatalog } from '../types/api'

type SaveStatus = { status: 'idle' | 'saving' | 'saved' | 'error'; error?: string }

export const useSessionStore = defineStore('session', () => {
  const api = useApi()

  const state = ref<ChatState>(initialState())
  const catalog = ref<FacetCatalog | null>(null)
  const bootError = ref<string | null>(null)
  const notice = ref<string | null>(null)
  const busy = ref(false)

  const draft = ref('')
  const chips = ref<Chip[]>([])
  const draftDirty = ref(false)

  const drawerPresetId = ref<number | null>(null)
  const expandedSaveTurn = ref<number | null>(null)
  const saveState = ref<Record<number, SaveStatus>>({})

  const dimensionLabels = computed<Record<string, string>>(() => {
    const c = catalog.value
    if (!c) return {}
    const base = Object.fromEntries(c.dimensions.map(d => [d.key, d.label]))
    const p = state.value.profile ? c.profiles[state.value.profile] : null
    return { ...base, ...(p?.labels ?? {}) }
  })

  function persist() {
    if (state.value.sessionId) savePersisted({ sessionId: state.value.sessionId, transcript: state.value.transcript })
  }

  /** 開頁：載 catalog；有舊 session 就用 GET 拿權威狀態 + 本地 transcript 重建，404 就開新的。 */
  async function boot() {
    bootError.value = null
    try { catalog.value = await api.getFacets() }
    catch { bootError.value = '連不到後端。請確認 API 在跑，再按「重試」。'; return }

    const saved = loadPersisted()
    if (saved) {
      try {
        const dto = await api.getSession(saved.sessionId)
        if (dto) { state.value = hydrate(initialState(), dto, saved.transcript); return }
        notice.value = '上次的對話已過期，已開新對話。'
      } catch { bootError.value = '連不到後端。請確認 API 在跑，再按「重試」。'; return }
    }
    await newSession()
  }

  async function newSession() {
    clearPersisted()
    const id = await api.createSession()
    state.value = { ...initialState(), sessionId: id }
    chips.value = []; draft.value = ''; draftDirty.value = false
    expandedSaveTurn.value = null; saveState.value = {}; drawerPresetId.value = null
    persist()
  }

  async function send() {
    const text = draft.value.trim()
    if (!text || busy.value || !state.value.sessionId) return
    busy.value = true
    draft.value = ''; chips.value = []; draftDirty.value = false
    notice.value = null
    state.value = beginTurn(state.value, text)
    const ctl = new AbortController()
    try {
      const r = await api.openStream(state.value.sessionId, text, ctl.signal)
      if (r.status === 404) {
        state.value = failHttp(state.value, 'http_404', '上次的對話已過期，已開新對話。原文留在輸入框，可以直接再送。')
        await newSession()
        draft.value = text
        return
      }
      if (!r.ok || !r.body) {
        const msg = r.status === 409 ? '這個對話還有一輪在跑，等它結束再送。' : `送出失敗（HTTP ${r.status}）。`
        state.value = failHttp(state.value, `http_${r.status}`, msg)
        return
      }
      for await (const frame of readSse(r.body)) {
        if (!(AGENT_EVENT_TYPES as readonly string[]).includes(frame.event)) { console.warn('unknown SSE event', frame.event); continue }
        let data: unknown
        try { data = JSON.parse(frame.data) } catch { console.warn('bad SSE data', frame.event, frame.data); continue }
        state.value = applyEvent(state.value, { ...(data as object), type: frame.event } as AgentEvent)
      }
    } catch (e) {
      console.warn('stream aborted', e)
    } finally {
      state.value = endTurn(state.value)
      persist()
      busy.value = false
    }
  }

  function retry(originalText: string) {
    draft.value = originalText
    draftDirty.value = true
    chips.value = []
  }

  function setDraft(text: string) {
    draft.value = text
    draftDirty.value = true
  }

  function toggleChip(chip: Chip) {
    if (draftDirty.value) { draft.value = appendChip(draft.value, chip, dimensionLabels.value); return }
    const key = chipKey(chip)
    const i = chips.value.findIndex(c => chipKey(c) === key)
    if (i >= 0) chips.value.splice(i, 1); else chips.value.push(chip)
    draft.value = composeDraft(chips.value, dimensionLabels.value)
  }

  function isChipSelected(chip: Chip) { return chips.value.some(c => chipKey(c) === chipKey(chip)) }

  function openDrawer(id: number) { drawerPresetId.value = id }
  function closeDrawer() { drawerPresetId.value = null }

  function expandSave(turnIndex: number) { expandedSaveTurn.value = turnIndex }

  async function save(turnIndex: number, intent: string) {
    const id = state.value.sessionId
    const text = intent.trim()
    if (!id || !text) { saveState.value[turnIndex] = { status: 'error', error: '描述不可為空' }; return }
    saveState.value[turnIndex] = { status: 'saving' }
    const r = await api.saveToShared(id, text)
    saveState.value[turnIndex] = r.ok ? { status: 'saved' } : { status: 'error', error: r.error }
  }

  /** save_consent_requested 到來：展開最近一張定稿卡的確認區。元件 watch transcript 尾端呼叫。 */
  const latestFinalizedTurn = computed(() => {
    for (let i = state.value.transcript.length - 1; i >= 0; i--) {
      const e = state.value.transcript[i]
      if (e.kind === 'final' && e.data.kind === 'finalized') return e.turnIndex
    }
    return null
  })

  return {
    state, catalog, bootError, notice, busy, draft, chips, draftDirty, drawerPresetId, expandedSaveTurn, saveState,
    dimensionLabels, latestFinalizedTurn,
    boot, newSession, send, retry, setDraft, toggleChip, isChipSelected, openDrawer, closeDrawer, expandSave, save,
  }
})
```

- [ ] **Step 3: 型別檢查與 build**

Run: `cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm run build`
Expected: 沒有型別錯誤，build 成功。（`nuxi typecheck` 第一次會問要不要裝 `vue-tsc`，答 yes；它會進 devDependencies。）

- [ ] **Step 4: Commit**

```bash
git add src/PromptCopilot.Frontend
git commit -m "feat(frontend): API composable and the session store wired to the reducer

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: 儀表板 `lib/dashboard.ts` + `Dashboard.vue`

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/dashboard.ts`
- Create: `src/PromptCopilot.Frontend/components/Dashboard.vue`
- Test: `src/PromptCopilot.Frontend/tests/dashboard.test.ts`

**Interfaces:**
- Produces:

```ts
export interface DashboardChip { id: string; label: string; hint: string; state: FacetState }
export interface DashboardRow { key: string; label: string; applicable: boolean; highlighted: boolean; chips: DashboardChip[] }
export function dashboardRows(catalog: FacetCatalog, profile: string | null, facetStates: Record<string, FacetState>, highlighted: string[]): DashboardRow[]
```

- [ ] **Step 1: 寫失敗的測試**

`tests/dashboard.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { dashboardRows } from '../lib/dashboard'
import type { FacetCatalog } from '../types/api'

const catalog: FacetCatalog = {
  dimensions: [
    { key: 'style', label: '風格', facets: [{ id: 'style.genre', label: '藝術流派', hint: 'anime' }] },
    { key: 'pose', label: '人物動作', facets: [{ id: 'pose.gaze', label: '視線', hint: 'looking at viewer' }] },
  ],
  profiles: {
    portrait: { labels: { style: '風格', pose: '人物動作' }, dimensions: { style: ['style.genre'], pose: ['pose.gaze'] } },
    vehicle: { labels: { style: '風格', pose: '運動狀態' }, dimensions: { style: ['style.genre'], pose: [] } },
  },
}

describe('dashboardRows', () => {
  it('without a profile lists every dimension as not yet applicable, chips missing', () => {
    const rows = dashboardRows(catalog, null, {}, [])
    expect(rows.map(r => r.applicable)).toEqual([false, false])
    expect(rows[0].chips[0]).toMatchObject({ id: 'style.genre', state: 'missing' })
  })

  it('uses the profile facet list, its labels, and the live states', () => {
    const rows = dashboardRows(catalog, 'vehicle', { 'style.genre': 'covered' }, ['style'])
    expect(rows[0]).toMatchObject({ key: 'style', label: '風格', applicable: true, highlighted: true })
    expect(rows[0].chips[0].state).toBe('covered')
    expect(rows[1]).toMatchObject({ key: 'pose', label: '運動狀態', applicable: false, chips: [] })
  })

  it('renders unknown facet ids with the raw id as label instead of crashing', () => {
    const rows = dashboardRows(catalog, 'portrait', { 'style.mystery': 'waived' }, [])
    const extra = rows[0].chips.find(c => c.id === 'style.mystery')
    expect(extra).toMatchObject({ label: 'style.mystery', state: 'waived' })
  })

  it('defaults a listed facet with no state to missing', () => {
    const rows = dashboardRows(catalog, 'portrait', {}, [])
    expect(rows[1].chips[0].state).toBe('missing')
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/dashboard.test.ts`
Expected: FAIL，模組不存在。

- [ ] **Step 3: 實作**

`lib/dashboard.ts`：

```ts
import type { FacetCatalog, FacetState } from '../types/api'

export interface DashboardChip { id: string; label: string; hint: string; state: FacetState }
export interface DashboardRow { key: string; label: string; applicable: boolean; highlighted: boolean; chips: DashboardChip[] }

/** 儀表板要畫的列。未知 facet id（yaml 與後端不同步）以原 id 當 label，歸到 id 前綴對應的維度。 */
export function dashboardRows(catalog: FacetCatalog, profile: string | null, facetStates: Record<string, FacetState>, highlighted: string[]): DashboardRow[] {
  const byId = new Map(catalog.dimensions.flatMap(d => d.facets.map(f => [f.id, { ...f, dimension: d.key }] as const)))
  const prof = profile ? catalog.profiles[profile] : undefined
  const rows: DashboardRow[] = catalog.dimensions.map(d => {
    const ids = prof ? (prof.dimensions[d.key] ?? []) : d.facets.map(f => f.id)
    const applicable = prof ? ids.length > 0 : false
    const chips: DashboardChip[] = ids.map(id => {
      const f = byId.get(id)
      return { id, label: f?.label ?? id, hint: f?.hint ?? '', state: facetStates[id] ?? 'missing' }
    })
    return { key: d.key, label: prof?.labels[d.key] ?? d.label, applicable, highlighted: highlighted.includes(d.key), chips }
  })
  for (const [id, state] of Object.entries(facetStates)) {
    if (byId.has(id) && rows.some(r => r.chips.some(c => c.id === id))) continue
    const dim = byId.get(id)?.dimension ?? id.split('.')[0]
    const row = rows.find(r => r.key === dim)
    if (row && !row.chips.some(c => c.id === id)) row.chips.push({ id, label: byId.get(id)?.label ?? id, hint: byId.get(id)?.hint ?? '', state })
  }
  return rows
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/dashboard.test.ts`
Expected: 4 passed。

- [ ] **Step 5: `components/Dashboard.vue`**

```vue
<template>
  <aside class="flex h-full flex-col gap-3 overflow-y-auto p-4">
    <header class="flex items-baseline justify-between">
      <h2 class="text-sm font-semibold tracking-wide text-neutral-500">六維度儀表板</h2>
      <span class="text-xs text-neutral-500">追問 {{ s.state.askCount }}/{{ s.state.askLimit }}</span>
    </header>
    <p class="text-xs" :class="s.state.profile ? 'text-neutral-700 dark:text-neutral-300' : 'text-neutral-400'">
      題材：{{ profileLabel }}
    </p>
    <ul class="flex flex-col gap-2" :class="{ 'opacity-50': !s.state.profile }">
      <li v-for="row in rows" :key="row.key"
          class="rounded-lg border p-2 transition-colors"
          :class="[row.highlighted ? 'border-amber-400 bg-amber-50 dark:bg-amber-950/30' : 'border-neutral-200 dark:border-neutral-800',
                   row.applicable || !s.state.profile ? '' : 'opacity-30']">
        <div class="mb-1 flex items-center justify-between text-xs font-medium">
          <span>{{ row.label }}</span>
          <span v-if="s.state.profile && !row.applicable" class="text-neutral-400">不適用</span>
        </div>
        <div class="flex flex-wrap gap-1">
          <span v-for="c in row.chips" :key="c.id" :title="`${c.label}：${stateLabel(c.state)}`"
                class="rounded-full border px-2 py-0.5 text-[11px] leading-4" :class="chipClass(c.state)">
            {{ c.label }}<span v-if="c.state === 'waived'" aria-hidden="true"> ·略</span>
          </span>
        </div>
      </li>
    </ul>
  </aside>
</template>

<script setup lang="ts">
import { dashboardRows } from '../lib/dashboard'
import type { FacetState } from '../types/api'

const s = useSessionStore()
const rows = computed(() => s.catalog ? dashboardRows(s.catalog, s.state.profile, s.state.facetStates, s.state.highlighted) : [])
const PROFILE_LABELS: Record<string, string> = { portrait: '人像', landscape: '風景', object: '物件', vehicle: '載具' }
const profileLabel = computed(() => s.state.profile ? (PROFILE_LABELS[s.state.profile] ?? s.state.profile) : '尚未判定')

function stateLabel(st: FacetState) {
  return { covered: '已涵蓋', missing: '未提供', waived: '使用者略過', notApplicable: '不適用' }[st]
}
/** 四態不只靠顏色：實心／空心／極淡／去飽和加「略」。 */
function chipClass(st: FacetState) {
  switch (st) {
    case 'covered': return 'border-emerald-600 bg-emerald-600 text-white font-medium'
    case 'missing': return 'border-neutral-400 bg-transparent text-neutral-700 dark:text-neutral-300 border-dashed'
    case 'waived': return 'border-neutral-300 bg-neutral-300 text-neutral-600 line-through decoration-neutral-500 dark:bg-neutral-700 dark:border-neutral-700 dark:text-neutral-300'
    case 'notApplicable': return 'border-neutral-200 text-neutral-300 opacity-40 dark:border-neutral-800 dark:text-neutral-700'
  }
}
</script>
```

- [ ] **Step 6: build 確認**

Run: `cd src/PromptCopilot.Frontend && npm run build`
Expected: 成功（元件還沒掛進 `app.vue`，只驗編譯）。

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Frontend
git commit -m "feat(frontend): six-dimension dashboard with four visually distinct facet states

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: 對話流元件

**Files:**
- Create: `components/ChatStream.vue`、`UserBubble.vue`、`ToolCallCard.vue`、`AskCard.vue`、`MessageBubble.vue`、`FinalCard.vue`、`SaveConsentNotice.vue`、`FailureNotice.vue`、`OptionChips.vue`

**Interfaces:**
- Consumes: store（Task 9）、`Entry` 型別（Task 7）、`failureTitle`（Task 8）。
- Produces: `OptionChips` 的 props `{ options: OptionItem[]; dimension: string | null; light?: boolean }`。

- [ ] **Step 1: `components/OptionChips.vue`**（追問 chip 與參考方向共用）

```vue
<template>
  <div class="flex flex-wrap gap-1.5">
    <button v-for="o in options" :key="o.label" type="button"
            class="group inline-flex items-center gap-1 rounded-full border px-2.5 py-1 text-xs transition-colors"
            :class="[selected(o) ? 'border-neutral-900 bg-neutral-900 text-white dark:border-neutral-100 dark:bg-neutral-100 dark:text-neutral-900'
                                 : light ? 'border-neutral-200 text-neutral-600 hover:border-neutral-400 dark:border-neutral-800 dark:text-neutral-400'
                                         : 'border-neutral-300 bg-white text-neutral-800 hover:border-neutral-500 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-200']"
            :title="o.tags" @click="s.toggleChip({ dimension, label: o.label })">
      <span>{{ o.label }}</span>
      <span v-if="o.presetId !== null" role="link" class="text-[10px] opacity-60 hover:opacity-100" title="看 preset"
            @click.stop="s.openDrawer(o.presetId!)">↗</span>
    </button>
  </div>
</template>

<script setup lang="ts">
import type { OptionItem } from '../types/api'
const props = defineProps<{ options: OptionItem[]; dimension: string | null; light?: boolean }>()
const s = useSessionStore()
const selected = (o: OptionItem) => s.isChipSelected({ dimension: props.dimension, label: o.label })
</script>
```

- [ ] **Step 2: `components/UserBubble.vue`**

```vue
<template>
  <div class="flex justify-end">
    <div class="max-w-[80%] whitespace-pre-wrap rounded-2xl rounded-br-sm bg-neutral-900 px-4 py-2 text-sm text-white dark:bg-neutral-100 dark:text-neutral-900">{{ text }}</div>
  </div>
</template>
<script setup lang="ts">
defineProps<{ text: string }>()
</script>
```

- [ ] **Step 3: `components/ToolCallCard.vue`**

```vue
<template>
  <div class="text-xs">
    <button type="button" class="flex w-full items-center gap-2 rounded-md border border-neutral-200 bg-white px-2 py-1 text-left text-neutral-600 hover:bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-900 dark:text-neutral-400"
            @click="open = !open">
      <span aria-hidden="true">{{ icon }}</span>
      <span class="font-medium">{{ title }}</span>
      <span class="truncate text-neutral-400">{{ entry.done ? (entry.summary ?? '') : entry.argsSummary }}</span>
      <span v-if="!entry.done" class="ml-auto animate-pulse text-neutral-400">進行中…</span>
      <span v-else class="ml-auto text-neutral-400">{{ open ? '收起' : '展開' }}</span>
    </button>
    <div v-if="open" class="mt-1 rounded-md border border-neutral-100 bg-neutral-50 p-2 dark:border-neutral-800 dark:bg-neutral-950">
      <p v-if="entry.argsSummary" class="font-mono text-[11px] text-neutral-500">{{ entry.argsSummary }}</p>
      <p v-if="entry.summary" class="mt-1">{{ entry.summary }}</p>
      <ul v-if="entry.presets.length" class="mt-2 flex gap-2 overflow-x-auto">
        <li v-for="p in entry.presets" :key="p.id">
          <button type="button" class="block w-24 text-left" @click="s.openDrawer(p.id)">
            <img v-if="p.imageUrl" :src="p.imageUrl" :alt="p.title" class="h-24 w-24 rounded object-cover" loading="lazy" @error="($event.target as HTMLImageElement).style.visibility = 'hidden'">
            <div v-else class="flex h-24 w-24 items-center justify-center rounded bg-neutral-200 text-neutral-400 dark:bg-neutral-800">無圖</div>
            <span class="mt-1 block truncate text-[11px]">{{ p.title }}</span>
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ToolEntry } from '../lib/reducer'
const props = defineProps<{ entry: ToolEntry }>()
const s = useSessionStore()
const open = ref(false)
const TITLES: Record<string, [string, string]> = {
  SearchPresets: ['🔍', '查詢知識庫'], SearchSimilarPrompts: ['📚', '找相似作品'],
  SetProfile: ['🎯', '判定題材'], SetFacetStates: ['🧭', '更新維度狀態'],
}
const icon = computed(() => TITLES[props.entry.name]?.[0] ?? '⚙️')
const title = computed(() => TITLES[props.entry.name]?.[1] ?? props.entry.name)
</script>
```

- [ ] **Step 4: `components/AskCard.vue`**

```vue
<template>
  <div class="rounded-2xl border border-amber-300 bg-amber-50/60 p-4 dark:border-amber-700 dark:bg-amber-950/20">
    <p class="text-sm">{{ data.preamble }}</p>
    <section v-for="a in data.asks" :key="a.dimension" class="mt-3">
      <h3 class="text-xs font-semibold text-amber-800 dark:text-amber-300">{{ s.dimensionLabels[a.dimension] ?? a.dimension }}</h3>
      <p class="mt-0.5 text-sm">{{ a.question }}</p>
      <OptionChips class="mt-2" :options="a.options" :dimension="a.dimension" />
    </section>
    <p class="mt-3 text-[11px] text-neutral-500">點選項會填進輸入框，可以多選、可以再改，送出時才會送。</p>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
defineProps<{ data: Extract<FinalData, { kind: 'ask' }> }>()
const s = useSessionStore()
</script>
```

- [ ] **Step 5: `components/MessageBubble.vue`**

```vue
<template>
  <div class="max-w-[85%]">
    <div class="whitespace-pre-wrap rounded-2xl rounded-bl-sm border border-neutral-200 bg-white px-4 py-2 text-sm dark:border-neutral-800 dark:bg-neutral-900">{{ data.message }}</div>
    <div v-if="data.options?.length" class="mt-1.5 pl-1">
      <p class="mb-1 text-[11px] text-neutral-500">參考方向</p>
      <OptionChips :options="data.options" :dimension="null" light />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
defineProps<{ data: Extract<FinalData, { kind: 'message' }> }>()
</script>
```

- [ ] **Step 6: `components/FinalCard.vue`**

```vue
<template>
  <div class="rounded-2xl border border-emerald-300 bg-white p-4 shadow-sm dark:border-emerald-800 dark:bg-neutral-900">
    <header class="flex items-center justify-between">
      <h3 class="text-sm font-semibold">定稿</h3>
      <span class="text-[11px] text-neutral-500">第 {{ turnIndex }} 輪</span>
    </header>

    <PromptBlock label="Positive" :text="data.positive" />
    <PromptBlock label="Negative" :text="data.negative" />

    <section v-if="data.tips" class="mt-3">
      <h4 class="text-xs font-medium text-neutral-500">生成建議</h4>
      <p class="mt-1 whitespace-pre-wrap text-sm">{{ data.tips }}</p>
    </section>

    <footer class="mt-4">
      <button v-if="!expanded" type="button" :disabled="save.status === 'saved'"
              class="rounded-md bg-emerald-600 px-3 py-1.5 text-sm text-white hover:bg-emerald-700 disabled:opacity-50"
              @click="s.expandSave(turnIndex)">
        {{ save.status === 'saved' ? '已儲存' : '儲存至共享知識庫' }}
      </button>
      <div v-else class="rounded-md border border-neutral-200 p-3 dark:border-neutral-800">
        <label class="block text-xs text-neutral-500" :for="`intent-${turnIndex}`">一句話描述這張圖（會成為別人檢索到它的依據，可修改）</label>
        <input :id="`intent-${turnIndex}`" v-model="intent" type="text" :disabled="save.status === 'saved' || save.status === 'saving'"
               class="mt-1 w-full rounded border border-neutral-300 bg-white px-2 py-1 text-sm dark:border-neutral-700 dark:bg-neutral-950">
        <div class="mt-2 flex items-center gap-2">
          <button type="button" :disabled="save.status === 'saved' || save.status === 'saving'"
                  class="rounded-md bg-emerald-600 px-3 py-1.5 text-sm text-white hover:bg-emerald-700 disabled:opacity-50"
                  @click="s.save(turnIndex, intent)">
            {{ save.status === 'saving' ? '儲存中…' : save.status === 'saved' ? '已儲存' : '確認儲存' }}
          </button>
          <span v-if="save.status === 'error'" class="text-xs text-red-600">{{ save.error }}</span>
          <span v-if="save.status === 'saved'" class="text-xs text-emerald-700">這份定稿已進共享庫，之後的對話可能撈到它當參考。</span>
        </div>
      </div>
    </footer>
  </div>
</template>

<script setup lang="ts">
import type { FinalizedData } from '../types/api'
const props = defineProps<{ data: FinalizedData; turnIndex: number }>()
const s = useSessionStore()
const intent = ref(props.data.intentSummary)
const expanded = computed(() => s.expandedSaveTurn === props.turnIndex)
const save = computed(() => s.saveState[props.turnIndex] ?? { status: 'idle' as const })
</script>
```

以及 `components/PromptBlock.vue`（複製鈕）：

```vue
<template>
  <section class="mt-3">
    <div class="flex items-center justify-between">
      <h4 class="text-xs font-medium text-neutral-500">{{ label }}</h4>
      <button type="button" class="text-xs text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100" @click="copy">{{ copied ? '已複製' : '複製' }}</button>
    </div>
    <pre class="mt-1 whitespace-pre-wrap break-words rounded bg-neutral-100 p-2 font-mono text-xs dark:bg-neutral-950">{{ text }}</pre>
  </section>
</template>

<script setup lang="ts">
const props = defineProps<{ label: string; text: string }>()
const copied = ref(false)
async function copy() {
  try { await navigator.clipboard.writeText(props.text); copied.value = true; setTimeout(() => (copied.value = false), 1500) }
  catch { /* 非 https 或權限被拒：使用者可以自己選取 */ }
}
</script>
```

- [ ] **Step 7: `components/SaveConsentNotice.vue` 與 `FailureNotice.vue`**

```vue
<!-- SaveConsentNotice.vue -->
<template>
  <p class="rounded-md bg-emerald-50 px-3 py-2 text-xs text-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-300">
    要把這份定稿存進共享知識庫嗎？確認區已在上方的定稿卡片展開。
  </p>
</template>
```

```vue
<!-- FailureNotice.vue -->
<template>
  <div class="rounded-2xl border border-red-300 bg-red-50 p-3 dark:border-red-900 dark:bg-red-950/20">
    <p class="text-xs font-semibold text-red-700 dark:text-red-300">{{ failureTitle(entry.source, entry.code) }}</p>
    <p class="mt-1 text-sm">{{ entry.message }}</p>
    <blockquote v-if="entry.originalText" class="mt-2 whitespace-pre-wrap border-l-2 border-red-300 pl-2 text-xs text-neutral-600 dark:text-neutral-400">{{ entry.originalText }}</blockquote>
    <button v-if="entry.originalText" type="button" class="mt-2 rounded-md border border-red-400 px-3 py-1 text-xs text-red-700 hover:bg-red-100 dark:text-red-300"
            @click="s.retry(entry.originalText)">重試（把原文填回輸入框）</button>
  </div>
</template>

<script setup lang="ts">
import type { FailureEntry } from '../lib/reducer'
import { failureTitle } from '../lib/copy'
defineProps<{ entry: FailureEntry }>()
const s = useSessionStore()
</script>
```

- [ ] **Step 8: `components/ChatStream.vue`**

```vue
<template>
  <div ref="el" class="flex h-full flex-col gap-3 overflow-y-auto px-4 py-4">
    <p v-if="s.notice" class="rounded-md bg-neutral-100 px-3 py-2 text-xs text-neutral-600 dark:bg-neutral-900 dark:text-neutral-400">{{ s.notice }}</p>
    <p v-if="s.state.transcript.length === 0" class="m-auto max-w-sm text-center text-sm text-neutral-500">
      用繁體中文描述你想生成的畫面，例如「一個銀髮少女站在雨夜的霓虹街頭」。我會分析六個維度、追問缺的細節、最後給你英文 prompt。
    </p>
    <template v-for="(e, i) in s.state.transcript" :key="i">
      <UserBubble v-if="e.kind === 'user'" :text="e.text" />
      <ToolCallCard v-else-if="e.kind === 'tool'" :entry="e" />
      <FailureNotice v-else-if="e.kind === 'failure'" :entry="e" />
      <template v-else-if="e.kind === 'final'">
        <AskCard v-if="e.data.kind === 'ask'" :data="e.data" />
        <MessageBubble v-else-if="e.data.kind === 'message'" :data="e.data" />
        <FinalCard v-else-if="e.data.kind === 'finalized'" :data="e.data" :turn-index="e.turnIndex" />
        <SaveConsentNotice v-else-if="e.data.kind === 'save_consent_requested'" />
      </template>
    </template>
    <p v-if="s.busy" class="text-xs text-neutral-400">思考中…</p>
  </div>
</template>

<script setup lang="ts">
const s = useSessionStore()
const el = ref<HTMLElement | null>(null)

watch(() => s.state.transcript.length, async () => {
  await nextTick()
  el.value?.scrollTo({ top: el.value.scrollHeight, behavior: 'smooth' })
})

// save_consent_requested：展開最近一張定稿卡的確認區
watch(() => s.state.transcript.at(-1), (last) => {
  if (last?.kind === 'final' && last.data.kind === 'save_consent_requested' && s.latestFinalizedTurn !== null)
    s.expandSave(s.latestFinalizedTurn)
})
</script>
```

- [ ] **Step 9: build 確認**

Run: `cd src/PromptCopilot.Frontend && npm run build`
Expected: 成功。

- [ ] **Step 10: Commit**

```bash
git add src/PromptCopilot.Frontend/components
git commit -m "feat(frontend): chat stream components — bubbles, tool cards, ask/message/final cards, failure notice

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: `Composer`、`TopBar`、`PresetDrawer`、`app.vue` 組起來

**Files:**
- Create: `components/Composer.vue`、`components/TopBar.vue`、`components/PresetDrawer.vue`
- Modify: `app.vue`（換掉 Task 5 的暫時版）

- [ ] **Step 1: `components/Composer.vue`**

```vue
<template>
  <form class="flex items-end gap-2 border-t border-neutral-200 bg-white p-3 dark:border-neutral-800 dark:bg-neutral-900" @submit.prevent="s.send()">
    <textarea ref="ta" :value="s.draft" rows="2" :disabled="s.busy || !!s.bootError"
              placeholder="描述你想要的畫面…（Enter 送出，Shift+Enter 換行）"
              class="min-h-[2.5rem] flex-1 resize-y rounded-md border border-neutral-300 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-neutral-400 disabled:opacity-60 dark:border-neutral-700 dark:bg-neutral-950"
              @input="s.setDraft(($event.target as HTMLTextAreaElement).value)"
              @keydown.enter.exact="onEnter" />
    <button type="submit" :disabled="s.busy || !s.draft.trim() || !!s.bootError"
            class="rounded-md bg-neutral-900 px-4 py-2 text-sm text-white disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900">
      {{ s.busy ? '進行中…' : '送出' }}
    </button>
  </form>
</template>

<script setup lang="ts">
const s = useSessionStore()
const ta = ref<HTMLTextAreaElement | null>(null)
// retry 把原文填回來時聚焦
watch(() => s.draft, (v, old) => { if (v && !old) ta.value?.focus() })
/** 中文輸入法選字時按 Enter 是「確定選字」不是送出：isComposing 為 true 就放過。 */
function onEnter(e: KeyboardEvent) {
  if (e.isComposing || (e as any).keyCode === 229) return
  e.preventDefault()
  s.send()
}
</script>
```

注意 `textarea` 用 `:value` + `@input` 而不是 `v-model`：chip 組字是由 store 改 `draft`，使用者手打才走 `setDraft`（把 `draftDirty` 設成 true）。

- [ ] **Step 2: `components/TopBar.vue`**

```vue
<template>
  <header class="flex items-center justify-between border-b border-neutral-200 bg-white px-4 py-2 dark:border-neutral-800 dark:bg-neutral-900">
    <div>
      <h1 class="text-base font-semibold">Prompt Copilot</h1>
      <p class="text-[11px] text-neutral-500">多輪追問 → SD/SDXL 英文提示詞</p>
    </div>
    <button type="button" :disabled="s.busy" class="rounded-md border border-neutral-300 px-3 py-1 text-sm hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
            @click="s.newSession()">新對話</button>
  </header>
</template>

<script setup lang="ts">
const s = useSessionStore()
</script>
```

- [ ] **Step 3: `components/PresetDrawer.vue`**

```vue
<template>
  <transition name="drawer">
    <aside v-if="s.drawerPresetId !== null" class="absolute inset-y-0 right-0 z-20 w-full max-w-md overflow-y-auto border-l border-neutral-200 bg-white p-4 shadow-xl dark:border-neutral-800 dark:bg-neutral-900">
      <div class="flex items-center justify-between">
        <h2 class="text-sm font-semibold">Preset #{{ s.drawerPresetId }}</h2>
        <button type="button" class="text-sm text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100" @click="s.closeDrawer()">關閉 ✕</button>
      </div>
      <p v-if="loading" class="mt-4 text-xs text-neutral-500">載入中…</p>
      <p v-else-if="error" class="mt-4 text-xs text-red-600">{{ error }}</p>
      <template v-else-if="preset">
        <img v-if="preset.imageUrl && !imgFailed" :src="preset.imageUrl" :alt="preset.title" class="mt-3 w-full rounded object-cover" @error="imgFailed = true">
        <div v-else class="mt-3 flex h-40 items-center justify-center rounded bg-neutral-100 text-xs text-neutral-400 dark:bg-neutral-800">沒有可顯示的圖片</div>
        <h3 class="mt-3 text-base font-semibold">{{ preset.title }}</h3>
        <p class="text-xs text-neutral-500">{{ preset.category }}</p>
        <p class="mt-2 text-sm">{{ preset.description }}</p>
        <PromptBlock label="Prompt snippet" :text="preset.promptSnippet" />
        <PromptBlock v-if="preset.negativeSnippet" label="Negative snippet" :text="preset.negativeSnippet" />
        <section class="mt-3">
          <h4 class="text-xs font-medium text-neutral-500">Tags</h4>
          <div class="mt-1 flex flex-wrap gap-1">
            <span v-for="t in preset.tags" :key="t" class="rounded bg-neutral-100 px-1.5 py-0.5 font-mono text-[11px] dark:bg-neutral-800">{{ t }}</span>
          </div>
        </section>
        <section class="mt-3">
          <h4 class="text-xs font-medium text-neutral-500">Facets</h4>
          <div class="mt-1 flex flex-wrap gap-1">
            <span v-for="f in preset.facetIds" :key="f" class="rounded-full border border-neutral-300 px-2 py-0.5 text-[11px] dark:border-neutral-700">{{ f }}</span>
          </div>
        </section>
        <p class="mt-4 text-[10px] text-neutral-400">圖片來自來源網站，本服務不轉存。</p>
      </template>
    </aside>
  </transition>
</template>

<script setup lang="ts">
import type { PresetDetail } from '../types/api'
const s = useSessionStore()
const api = useApi()
const preset = ref<PresetDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const imgFailed = ref(false)

watch(() => s.drawerPresetId, async (id) => {
  preset.value = null; error.value = null; imgFailed.value = false
  if (id === null) return
  loading.value = true
  try {
    const p = await api.getPreset(id)
    if (p) preset.value = p; else error.value = '找不到這筆 preset。'
  } catch { error.value = '載入失敗，對話不受影響。' }
  finally { loading.value = false }
}, { immediate: true })
</script>

<style scoped>
.drawer-enter-active, .drawer-leave-active { transition: transform 200ms ease; }
.drawer-enter-from, .drawer-leave-to { transform: translateX(100%); }
</style>
```

- [ ] **Step 4: `app.vue`**

```vue
<template>
  <div class="flex h-full flex-col">
    <TopBar />
    <div v-if="s.bootError" class="m-auto max-w-sm text-center">
      <p class="text-sm">{{ s.bootError }}</p>
      <button type="button" class="mt-3 rounded-md border border-neutral-300 px-3 py-1 text-sm dark:border-neutral-700" @click="s.boot()">重試</button>
    </div>
    <div v-else class="relative grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[minmax(0,1fr)_20rem]">
      <section class="flex min-h-0 flex-col">
        <ChatStream class="min-h-0 flex-1" />
        <Composer />
      </section>
      <Dashboard class="hidden border-l border-neutral-200 md:block dark:border-neutral-800" />
      <PresetDrawer />
    </div>
  </div>
</template>

<script setup lang="ts">
const s = useSessionStore()
onMounted(() => s.boot())
</script>
```

- [ ] **Step 5: 跑起來走一遍**

Run（終端機 A）: `python manual-tests/start_api.py`；（終端機 B）: `cd src/PromptCopilot.Frontend && npm run dev`。開 `http://localhost:3000`。

檢查清單（每項都要真的看到）：

1. 輸入「一個女生」送出 → tool call 卡片逐張出現、儀表板題材變「人像」、燈號變、最後出追問卡，chip 點兩個變成 `[風格] a、b` 在輸入框；儀表板該維度高亮。
2. 送出 chip 組出的文字 → 定稿卡片出現，兩個複製鈕可用，`儲存至共享知識庫` 展開後預填一句繁中摘要。
3. 整頁重載 → 對話流、儀表板、定稿卡片都回來；Network 面板看得到 `GET /api/sessions/{id}`。
4. 輸入「一個裸體的女生」→ 紅色失敗條目，標題「輸入被安全規則攔下」，按「重試」原文回到輸入框並聚焦；儀表板沒變。
5. 停掉 API（Ctrl+C `start_api.py`）再送一句 → 「連線中斷」或「送出失敗」的失敗條目，畫面沒炸；重新起 API 後再送正常。
6. 「新對話」→ 清空、儀表板回「尚未判定」。

任何一項不對，先用 systematic-debugging 找原因再改；改到 `lib/` 的邏輯要補測試。

- [ ] **Step 6: `npm test` + `npm run build`，Commit**

```bash
git add src/PromptCopilot.Frontend
git commit -m "feat(frontend): composer, top bar, preset drawer and the app shell

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 13: 視覺整理（frontend-design skill）

**Files:**
- Modify: `components/*.vue`、`assets/css/main.css`、`tailwind.config.ts`、`app.vue`

目的：這是作品集，第一眼不能像 Tailwind 預設模板。功能與 DOM 結構不改，只調視覺。

- [ ] **Step 1: 載入 frontend-design skill**

用 Skill tool 載入 `frontend-design:frontend-design`，把 spec §4 的兩條原則交給它：儀表板四態不靠顏色也分得出來；tool call 卡片是配角、摺疊後一行高。字型可用 Google Fonts（`nuxt.config.ts` 的 `app.head.link` 加一個 stylesheet），繁中字型優先 Noto Sans TC。

- [ ] **Step 2: 調整**

範圍：色彩 token（`tailwind.config.ts` 的 `theme.extend.colors`）、字型、間距、卡片層次、chip 樣式、深色模式對比。不動 `lib/`、`stores/`、`composables/`。

- [ ] **Step 3: 四態自查**

用瀏覽器的灰階模擬（DevTools → Rendering → Emulate vision deficiencies → Achromatopsia）看儀表板：四態仍要分得出來。

- [ ] **Step 4: `npm test` + `npm run build`，Commit**

```bash
git add src/PromptCopilot.Frontend
git commit -m "style(frontend): visual pass — type, color tokens, card hierarchy, dark mode

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 14: 驗收、eval 紀錄、文件收尾

**Files:**
- Modify: `docs/eval-cases.md`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§11.3、§13、§14、狀態列）
- Modify: `docs/superpowers/specs/2026-09-24-frontend-sse-design.md`（狀態列；§2.5 若用了備案）
- Modify: `manual-tests/README.md`、`src/README.md`

- [ ] **Step 1: 瀏覽器跑 eval**

API 與 `npm run dev` 都跑著。依序跑主規格 §14 第 3 列的四條與本子專案的三條，每條記結果：

| # | 輸入／操作 | 應該看到 |
| :--- | :--- | :--- |
| 1 | 一個女生 | 追問卡，asks ≤ 3 則，儀表板對應維度高亮 |
| 3 | 山上的日出 | 題材「風景」，人物三維整列淡化標「不適用」 |
| 6 | 一個穿洋裝的女生，不要指定鞋子 | `clothing.footwear` chip 變成去飽和加「略」，定稿 prompt 無鞋子 |
| 11 | （定稿後）把背景改成黃昏 | 新的定稿卡，沒有追問卡 |
| F1 | 定稿後整頁重載 | 儀表板、定稿卡片、對話流都回來；Network 有 `GET /api/sessions/{id}` |
| F2 | 一個裸體的女生 → 按「重試」 | 失敗條目；原文回輸入框並聚焦；儀表板與上一輪相同；改寫後送出正常 |
| F3 | 定稿後按「儲存至共享知識庫」 | 預填 `intentSummary`；改一個字後送出成功，按鈕變「已儲存」 |

F3 之後清掉測試資料：

```bash
docker compose exec db psql -U postgres -d prompt_copilot -c "DELETE FROM shared_prompt_histories WHERE user_intent = '<你送出的那句>';"
```

- [ ] **Step 2: 記回 `docs/eval-cases.md`**

第 1、3、6、11 列的「結果／日期」欄填本次瀏覽器跑的結果與 `prompt_version`（從 `audit_logs` 查，指令在 `manual-tests/README.md` §5）。表格下方新增一節：

```markdown
## 2026-09-XX 子專案 3 瀏覽器驗收

| # | 操作 | 結果 |
| :--- | :--- | :--- |
| F1 | 定稿後整頁重載 | … |
| F2 | NSFW → 重試 | … |
| F3 | 儲存共享庫，改摘要後送出 | … |

前端：`src/PromptCopilot.Frontend`，commit `<sha>`。
```

- [ ] **Step 3: 主規格折回**

- 狀態列：「子專案 3（前端 + SSE）已實作並通過 §14 驗收（2026-09-XX）；子專案 4 未開始」。
- §11.3 改成：snapshot 時機為送出當下；`error`／`blocked`／串流異常結束時 restore；對話流存 `sessionStorage`，重載以 `GET /api/sessions/{id}` 重建（子專案 3 設計 §3.3、§3.4）。
- §13 專案結構：`PromptCopilot.Frontend/` 底下列 `types/`、`lib/`、`composables/`、`stores/`、`components/`、`tests/`。
- §14 第 3 列驗收條件加「+ 子專案 3 設計 §7 的 F1–F3」。
- §10.2 的 `token` 那列加註「後端現況不發；前端 reducer 保留處理」。

- [ ] **Step 4: 子專案 3 spec 狀態列**

`2026-09-24-frontend-sse-design.md` 第 4 行改成「狀態：已實作，驗收見 `docs/eval-cases.md`（2026-09-XX）」。若 Task 5 用了 CORS 備案，§2.5 補記。

- [ ] **Step 5: README**

`manual-tests/README.md` 開頭「## 2. 對話試用」之前加一節：

```markdown
## 1.5 用瀏覽器試用

    cd src/PromptCopilot.Frontend && npm install && npm run dev      # http://localhost:3000

API 照第 1 節先起好。前端把 `/api` 轉到 `localhost:5000`，不用改任何設定。
```

`src/README.md` 開頭第一段加一句：「瀏覽器介面在 [`PromptCopilot.Frontend/README.md`](PromptCopilot.Frontend/README.md)。」

- [ ] **Step 6: 最後一次全綠**

停掉 API。Run: `cd src && dotnet test 2>&1 | tail -3`；`cd PromptCopilot.Frontend && npm test && npm run build`。
Expected: 全綠。

- [ ] **Step 7: Commit**

```bash
git add docs manual-tests/README.md src/README.md
git commit -m "docs: subproject 3 acceptance — eval results, spec fold-back, how to run the frontend

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## 自我檢查（已做）

- **Spec 覆蓋**：§1.2 不做（打字機：Task 7 `token` 只接不動畫；CORS：Task 5 只在備案）；§2.1 Task 3；§2.2 Task 1、4；§2.3 Task 2；§2.5 Task 5 Step 6；§2.6 Task 1–3 各自的文件步驟；§3.1–3.3 Task 6–9；§3.4 Task 9 `boot`；§4 Task 10–13；§5 錯誤表 → Task 9 `send`（404／409）、Task 12 抽屜錯誤、`app.vue` bootError；§6 Task 6、7、8、10 前端測試與 Task 1–3 xUnit；§7 Task 14；§8 Task 1–3、14 的文件步驟。
- **型別一致**：`FinalPrompt` 四參數（Task 1）→ `FinalDto`（Task 3）→ `SessionSnapshotDto.lastFinal`（Task 5）→ `hydrate`（Task 7）；`Entry`／`ChatState` 名稱在 Task 7、8、9、11 一致；`Chip`／`chipKey`／`composeDraft`／`appendChip` 在 Task 8、9、11 一致；`TERMINAL_TOOLS`、`AGENT_EVENT_TYPES` 定義在 Task 5、用在 Task 7、9。
- **Review Focus 五條**各有測試：Task 7（1、2）、Task 6（3）、Task 8（4）、Task 10（5）。
