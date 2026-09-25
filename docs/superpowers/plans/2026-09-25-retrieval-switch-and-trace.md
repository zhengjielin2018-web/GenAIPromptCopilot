# 知識庫開關與檢索過程顯示 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每段對話建立時可關掉知識庫檢索（對照組），並讓前端在開關開啟時看到每輪檢索查了什麼、命中什麼、定稿借了誰的詞。

**Architecture:** 後端三處小改：`Session` 多一個建立時定死的 `RetrievalEnabled`，`ToolSetBuilder` 與 `SystemPromptBuilder` 依它拿掉檢索工具與檢索指示；`ToolResultEvent` 多一個 `detail` 物件，由 `KnowledgePlugin` 從已算好的結果投影出來。前端新增 `lib/prefs.ts`（兩個 localStorage 偏好）與 `lib/trace.ts`（檢索貢獻與 session 摘要的純函式），`ToolCallCard`／`FinalCard`／`Dashboard` 在「顯示檢索細節」開啟時多畫一區，關閉時與現在完全相同。

**Tech Stack:** .NET 10 minimal API + Semantic Kernel、xunit；Nuxt 3 + Pinia + vitest；TypeScript 5。

**Spec:** `docs/superpowers/specs/2026-09-25-retrieval-switch-and-trace-design.md`

## Global Constraints

- 分支：從 `master` 開 `feat/retrieval-switch-and-trace`（subagent-driven-development 會用 worktree）。
- 後端測試：`dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~<TestClass>"`；整包 `dotnet test src/PromptCopilot.Api.Tests`。DB／Gemini 整合測試自動跳過（`IntegrationFact`）。
- 前端測試：Node 不在工具 shell 的 PATH 上，每次先 `export PATH="$LOCALAPPDATA/Microsoft/WinGet/Packages/OpenJS.NodeJS.22_Microsoft.Winget.Source_8wekyb3d8bbwe/node-v22.23.2-win-x64:$PATH"`（bash），再 `cd src/PromptCopilot.Frontend && npm test -- tests/<file>.test.ts`；整包 `npm test`；型別 `npx nuxi typecheck`。
- 線上欄位一律 camelCase；`SseWriter` 用 `WhenWritingNull`，可為 null 的欄位在線上會整個不存在，前端型別用 `?:`。
- `retrieval` 的線上值只有 `"on"`／`"off"` 兩個字串；C# 內部是 `bool RetrievalEnabled`。
- 模型看到的 `SearchPresets` 回傳 JSON **逐字不變**（既有 `KnowledgePluginTests` 是驗證）。
- on 模式的 system prompt 字句逐字不變（既有 `SystemPromptBuilderTests` 是驗證）。
- 前端純函式放 `lib/`，不用 Nuxt auto-import；元件沒有 mount 測試，靠 eval 人工驗收。
- 文件與程式同一個 commit：改契約的任務自己更新對應的 spec 段落（每個任務的 Files 已列出）。
- 中文文案用繁體；程式註解沿用現有風格（說「為什麼」，短句）。
- Commit 訊息結尾加 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`。

## Review Focus

1. `POST /api/sessions` 帶 `content-type: application/json` 但 body 為空（`manual-tests/chat.py` 就這樣送）：必須仍建立 on 的 session，不能 400／415。→ Task 3 測試 `Create_session_with_empty_json_body_defaults_on`。
2. off 的 session 在「使用者說隨便」或追問額度用完時，工具集只剩 `SetProfile`／`SetFacetStates`／`FinalizePrompt`，模型仍要能結束一輪。→ Task 1 測試 `Retrieval_off_with_auto_complete_leaves_only_state_tools_and_finalize`。
3. `SearchPresets` 全部項目驗證失敗（沒打 embedding）時 `detail.items` 仍要有每一項且帶 `error`，前端才對得上摘要。→ Task 6 測試 `Detail_keeps_invalid_items_in_order_with_error`。
4. 舊的 sessionStorage transcript（工具卡沒有 `detail`、定稿卡沒有 sources）在開啟「顯示檢索細節」後不能壞：工具卡退回摘要、儀表板摘要整區隱藏、定稿卡只顯示計數。→ Task 8 測試 `tool_result without detail leaves detail null`，Task 10 測試 `retrievalSummary returns searches 0 for a transcript without detail`、`contributions with empty sources`。
5. 「使用知識庫」切換後目前對話沒變：畫面要說「新對話後生效」，而且重載後（從 `GET` 拿回 `retrieval`）這個提示要維持正確。→ Task 8 測試 `hydrate takes retrieval from the dto`；`retrievalMismatch` 在 Task 8 的 store 以 computed 實作，Task 9 只負責顯示。

---

### Task 1: `Session.RetrievalEnabled` 與 `ToolSetBuilder`

**Files:**
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs`
- Modify: `src/PromptCopilot.Api/Sessions/SessionStore.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/ToolSetBuilderTests.cs`

**Interfaces:**
- Produces: `Session(string id, bool retrievalEnabled = true)`；`bool Session.RetrievalEnabled { get; }`；`string Session.RetrievalMode`（`"on"`／`"off"`）；`SessionStore.Create(bool retrievalEnabled = true)`。

- [ ] **Step 1: 寫失敗的測試**

在 `ToolSetBuilderTests.cs` 類別最後加：

```csharp
    /// <summary>計畫 §4.1：off 的 session 是量測用的對照組。拿掉的只有兩個檢索工具，其餘規則照舊。</summary>
    [Fact]
    public void Retrieval_off_removes_both_search_tools_and_nothing_else()
    {
        var on = ToolSetBuilder.Build(S(), false, O);
        var off = ToolSetBuilder.Build(new Session("s", retrievalEnabled: false), false, O);
        Assert.DoesNotContain(ToolNames.SearchPresets, off);
        Assert.DoesNotContain(ToolNames.SearchSimilarPrompts, off);
        Assert.Equal(on.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet(), off);
        Assert.True(ToolNames.Always.IsSubsetOf(on));   // Always 本身不動
    }

    [Fact]
    public void Retrieval_off_with_auto_complete_leaves_only_state_tools_and_finalize()
    {
        var off = ToolSetBuilder.Build(new Session("s", retrievalEnabled: false), true, O);
        Assert.Equal(new HashSet<string> { ToolNames.SetProfile, ToolNames.SetFacetStates, ToolNames.FinalizePrompt }, off);
    }

    [Fact]
    public void Retrieval_mode_defaults_on_and_survives_restore()
    {
        var s = new Session("s");
        Assert.True(s.RetrievalEnabled); Assert.Equal("on", s.RetrievalMode);
        var off = new Session("s", retrievalEnabled: false);
        off.Restore(s.Snapshot());
        Assert.False(off.RetrievalEnabled); Assert.Equal("off", off.RetrievalMode);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~ToolSetBuilderTests"`
Expected: 編譯錯誤，`Session` 沒有 `retrievalEnabled` 參數。

- [ ] **Step 3: 實作**

`Session.cs`：

```csharp
    /// <summary>建立時定死：off 是量測用的對照組（計畫 §4.1）。中途不能切，所以不進 Snapshot／Restore。</summary>
    public bool RetrievalEnabled { get; }
    public string RetrievalMode => RetrievalEnabled ? "on" : "off";
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public Session(string id, bool retrievalEnabled = true) { Id = id; RetrievalEnabled = retrievalEnabled; }
```

（把原本的 `public Session(string id) => Id = id;` 換掉。）

`SessionStore.cs`：

```csharp
    public Session Create(bool retrievalEnabled = true)
    {
        var s = new Session(Guid.NewGuid().ToString("N"), retrievalEnabled);
        _cache.Set(s.Id, s, new MemoryCacheEntryOptions { SlidingExpiration = _sliding });
        return s;
    }
```

`ToolSetBuilder.Build` 第一行之後：

```csharp
        var tools = new HashSet<string>(ToolNames.Always);
        if (!s.RetrievalEnabled)
        {
            // 對照組：模型拿不到檢索工具，就不會「自稱」借用。Always 是一般情況的宣告，這裡減，不改它。
            tools.Remove(ToolNames.SearchPresets);
            tools.Remove(ToolNames.SearchSimilarPrompts);
        }
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~ToolSetBuilderTests"`
Expected: 全部 PASS（含既有的 7 個）。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Sessions/Session.cs src/PromptCopilot.Api/Sessions/SessionStore.cs src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs src/PromptCopilot.Api.Tests/Orchestration/ToolSetBuilderTests.cs
git commit -m "feat(session): RetrievalEnabled fixed at creation; off drops both search tools"
```

---

### Task 2: system prompt 的兩個 placeholder

**Files:**
- Modify: `src/PromptCopilot.Api/Prompts/system.md`（第 5 行步驟 1、第 25 行規則）
- Modify: `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`

**Interfaces:**
- Consumes: `Session.RetrievalEnabled`、`Session.RetrievalMode`（Task 1）。
- Produces: `SystemPromptBuilder.RetrievalStepOn/Off`、`RetrievalRuleOn/Off`（internal const）。

- [ ] **Step 1: 寫失敗的測試**

`SystemPromptBuilderTests.cs` 類別最後加：

```csharp
    private static readonly IReadOnlySet<string> ToolsWithoutSearch =
        ToolNames.Always.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet();

    /// <summary>計畫 §4.1：off 的 prompt 不能再要求檢索，也不能留下講片段可否借入的規則；on 的字句逐字不變。</summary>
    [Fact]
    public void Retrieval_off_prompt_drops_search_step_and_borrow_rule()
    {
        var on = Make().Build(new Session("s"), ToolNames.Always);
        var off = Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch);

        Assert.Contains("用一次 `SearchPresets`", on.Prompt);
        Assert.Contains("「僅供建議」的片段任何詞都不可進提示詞", on.Prompt);
        Assert.Contains("知識庫：on", on.Prompt);

        Assert.DoesNotContain("SearchPresets", off.Prompt);
        Assert.DoesNotContain("SearchSimilarPrompts", off.Prompt);
        Assert.Contains("本段對話沒有知識庫：不做檢索，直接依 facet 狀態追問或定稿。", off.Prompt);
        Assert.Contains("- 本段對話沒有知識庫片段，所有 tag 由你自行產生。", off.Prompt);
        Assert.Contains("知識庫：off", off.Prompt);
        Assert.DoesNotContain("{{", off.Prompt);
        Assert.NotEqual(on.Version, off.Version);
    }

    /// <summary>off 的步驟 1 仍要接得上「然後：只要還有 missing 的維度就 AskUser」，不能因為換掉一段就斷句。</summary>
    [Fact]
    public void Retrieval_off_step_one_still_flows_into_ask_rule()
    {
        var (prompt, _) = Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch);
        Assert.Contains("追問或定稿。然後：**只要還有 missing 的維度就 `AskUser`**", prompt);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SystemPromptBuilderTests"`
Expected: 兩個新測試 FAIL（off 的 prompt 仍含 `SearchPresets`）。

- [ ] **Step 3: 改樣板**

`system.md` 第 5 行：把「再**用一次 `SearchPresets`**：」開始、到「需要風格參考時呼叫 `SearchSimilarPrompts`。」結束的整段（含結尾句號），換成 `{{RETRIEVAL_STEP}}`。改完那一行的中段應長這樣：

```text
……沒講的維持 `missing`，不要猜。{{RETRIEVAL_STEP}}然後：**只要還有 missing 的維度就 `AskUser`**……
```

第 25 行整行（`- \`SearchPresets\` 回的片段標了……不可借與描述矛盾的詞。`）換成：

```text
{{RETRIEVAL_RULE}}
```

- [ ] **Step 4: 改 builder**

`SystemPromptBuilder.cs` 類別開頭（`_template` 欄位之前）加常數。字串要與樣板拿掉的原文**逐字相同**（從 git diff 複製）：

```csharp
    /// <summary>樣板 {{RETRIEVAL_STEP}}／{{RETRIEVAL_RULE}} 的內容。on 是 2026-09-25 之前樣板裡的原文，搬進來只是為了 off 時能整段換掉。</summary>
    internal const string RetrievalStepOn =
        "再**用一次 `SearchPresets`**：使用者講到的每個 facet 各一項，用 `facetId` 加上他描述那一項的原話（例：`clothing.footwear`＋「拖鞋」、`appearance.hair`＋「銀色雙馬尾」）；使用者沒講的維度每個用 `dimension` 給兩個對比方向的項目（例：「寫實攝影」與「日系動漫插畫」）。不要把整句描述丟給一個維度，也不要一個項目一次呼叫。需要風格參考時呼叫 `SearchSimilarPrompts`。";
    internal const string RetrievalStepOff = "本段對話沒有知識庫：不做檢索，直接依 facet 狀態追問或定稿。";
    internal const string RetrievalRuleOn =
        "- `SearchPresets` 回的片段標了「可借入提示詞」或「僅供建議」，以及每個 facet 對本次使用者是 covered 還是 missing：「僅供建議」的片段任何詞都不可進提示詞；「可借入」的片段，標 missing 的 facet 對應的詞也不可進，只可進建議。相似度「低」的片段仍可借用其中與描述相符的詞，不可借與描述矛盾的詞。";
    internal const string RetrievalRuleOff = "- 本段對話沒有知識庫片段，所有 tag 由你自行產生。";
```

`Build` 的 Replace 鏈加兩個：

```csharp
            .Replace("{{RETRIEVAL_STEP}}", s.RetrievalEnabled ? RetrievalStepOn : RetrievalStepOff)
            .Replace("{{RETRIEVAL_RULE}}", s.RetrievalEnabled ? RetrievalRuleOn : RetrievalRuleOff)
```

`Facts` 在 AutoFill 那行之後加：

```csharp
        sb.AppendLine($"- 知識庫：{s.RetrievalMode}");
```

- [ ] **Step 5: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SystemPromptBuilderTests"`
Expected: 全部 PASS。既有的 `Flow_rule_asks_for_one_batched_SearchPresets_call_with_one_item_per_stated_facet` 也要綠，代表 on 的字句沒變。

- [ ] **Step 6: 確認 on 的 prompt 逐字不變**

Run: `git diff src/PromptCopilot.Api/Prompts/system.md`
Expected: 只有兩處 `-`／`+`；拿掉的兩段文字與 `RetrievalStepOn`／`RetrievalRuleOn` 常數逐字一致（包括結尾句號與「- 」前綴）。

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Api/Prompts/system.md src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs
git commit -m "feat(prompt): retrieval step and borrow rule become placeholders; off mode drops them"
```

---

### Task 3: `POST /api/sessions` 帶 `retrieval`，`GET` 回 `retrieval`

**Files:**
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（第 726–727 行的 REST 表）
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`

**Interfaces:**
- Consumes: `SessionStore.Create(bool)`、`Session.RetrievalMode`（Task 1）。
- Produces: `CreateSessionRequest(string? Retrieval)`；`SessionCreated(string SessionId, string Retrieval)`；`SessionSnapshotDto` 末尾加 `string Retrieval`。

- [ ] **Step 1: 寫失敗的測試**

`EndpointTests.cs` 在 `Create_session_returns_id` 之後加：

```csharp
    [Fact]
    public async Task Create_session_without_body_defaults_retrieval_on()
    {
        var r = await _client.PostAsync("/api/sessions", null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("on", (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["retrieval"]);
    }

    /// <summary>manual-tests/chat.py 帶 content-type: application/json 但沒有 body，要當成沒帶。</summary>
    [Fact]
    public async Task Create_session_with_empty_json_body_defaults_on()
    {
        var content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        var r = await _client.PostAsync("/api/sessions", content);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("on", (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["retrieval"]);
    }

    [Fact]
    public async Task Create_session_with_retrieval_off_is_reported_on_create_and_get()
    {
        var r = await _client.PostAsJsonAsync("/api/sessions", new { retrieval = "OFF" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("off", body!["retrieval"]);
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/sessions/{body["sessionId"]}");
        Assert.Equal("off", doc.GetProperty("retrieval").GetString());
        Assert.False(_factory.Services.GetRequiredService<SessionStore>().TryGet(body["sessionId"])!.RetrievalEnabled);
    }

    [Fact]
    public async Task Create_session_rejects_unknown_retrieval_value()
    {
        var r = await _client.PostAsJsonAsync("/api/sessions", new { retrieval = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("on 或 off", (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }
```

並在既有的 `GET` 測試（第 140 行附近，讀 `JsonElement` 的那個）加一行 `Assert.Equal("on", doc.GetProperty("retrieval").GetString());`。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~EndpointTests"`
Expected: 新的四個 FAIL（回應沒有 `retrieval` 鍵，`maybe` 回 201）。

- [ ] **Step 3: 實作**

`SessionEndpoints.cs` 的 record 區：

```csharp
public sealed record CreateSessionRequest(string? Retrieval);
public sealed record SessionCreated(string SessionId, string Retrieval);
public sealed record SessionSnapshotDto(string SessionId, string Status, string? Profile, int TurnIndex, int AskCount, int AskLimit,
    IReadOnlyDictionary<string, string> FacetStates, FinalDto? LastFinal, string Retrieval);
```

`MapPost("/")` 換成：

```csharp
        // body 可省略：minimal API 的 nullable body 參數在沒有 body 或 body 為空時是 null。
        g.MapPost("/", (CreateSessionRequest? req, SessionStore store) =>
        {
            bool? enabled = req?.Retrieval?.Trim().ToLowerInvariant() switch { null or "" or "on" => true, "off" => false, _ => null };
            if (enabled is null) return Results.BadRequest(new ErrorBody("retrieval 只能是 on 或 off"));
            var s = store.Create(enabled.Value);
            return Results.Created($"/api/sessions/{s.Id}", new SessionCreated(s.Id, s.RetrievalMode));
        })
        .WithSummary("開一段新對話")
        .WithDescription("""
            body 可省略：`{"retrieval": "on" | "off"}`，預設 `on`。`off` 的對話不查知識庫（模型拿不到 `SearchPresets` 與 `SearchSimilarPrompts`），是量測用的對照組；建立後不能改。其他值回 `400`。

            回 `201` 與 `{"sessionId": "...", "retrieval": "on" | "off"}`，之後的呼叫都帶這個 id。

            session 只存在記憶體：API 重啟就消失；閒置超過 `Orchestrator:SessionSlidingExpirationMinutes`（預設 120 分鐘）也會過期，之後再用這個 id 會得到 404。
            """)
        .Produces<SessionCreated>(StatusCodes.Status201Created)
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest);
```

`MapGet("/{id}")` 的 `new SessionSnapshotDto(...)` 最後加 `, s.RetrievalMode`；描述文字第一段結尾加「、這段對話是否使用知識庫（`retrieval`）」。

如果 `Create_session_with_empty_json_body_defaults_on` 回 400（框架把空 JSON body 當成無效），把參數改成 `[Microsoft.AspNetCore.Mvc.FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CreateSessionRequest? req`。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~EndpointTests"`
Expected: 全部 PASS。

- [ ] **Step 5: 同步主規格**

`docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` 第 726–727 行改成：

```markdown
| `POST` | `/api/sessions` | body 可省略 `{ retrieval?: "on" \| "off" }`（預設 `on`；`off` 不查知識庫，建立後不可改，2026-09-25 起）→ `{ sessionId, retrieval }` |
| `GET` | `/api/sessions/{id}` | session 目前的權威狀態（status、profile、facetStates、askCount／askLimit、lastFinal，含 tag 來源、retrieval）；前端重載重建用；不拿 session 鎖；`404` 表示不存在或已過期 |
```

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): POST /api/sessions takes retrieval on|off; create and GET report it"
```

---

### Task 4: audit `Turn_Completed` 記 `retrieval`

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs:141-146`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`

**Interfaces:**
- Consumes: `Session.RetrievalMode`（Task 1）。

- [ ] **Step 1: 寫失敗的測試**

在 `AgenticOrchestratorTests.cs` 第 149 行那個測試（讀 `completed.PayloadJson` 的）加一行：

```csharp
        Assert.Contains("\"retrieval\":\"on\"", completed.PayloadJson!);   // 計畫 §4.1：事後分組用
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 該測試 FAIL，payload 沒有 `retrieval`。

- [ ] **Step 3: 實作**

`AgenticOrchestrator.cs` 的 `Turn_Completed` 那筆 `Payload(...)` 在 `("outcome", ...)` 之前加 `("retrieval", session.RetrievalMode),`：

```csharp
                Payload(("retrieval", session.RetrievalMode), ("outcome", turn.Outcome.GetType().Name), ("toolCalls", turn.ToolCalls), ("rejections", turn.Rejections),
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs
git commit -m "feat(audit): Turn_Completed payload records retrieval on|off"
```

---

### Task 5: `ToolResultEvent.Detail` 與兩種 detail 型別

**Files:**
- Create: `src/PromptCopilot.Api/Streaming/ToolDetails.cs`
- Modify: `src/PromptCopilot.Api/Streaming/AgentEvent.cs:15`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§10.2 表 `tool_result` 列）
- Modify: `docs/superpowers/specs/2026-09-24-batch-search-presets-design.md`（§3.5）
- Test: `src/PromptCopilot.Api.Tests/Streaming/SseWriterTests.cs`

**Interfaces:**
- Produces: `ToolResultEvent(string CallId, string Name, string Summary, IReadOnlyList<PresetRef>? Presets, object? Detail = null)`；`SearchPresetsDetail`、`SearchPresetsItem`、`SearchPresetsHit`、`SearchSimilarDetail`、`SearchSimilarHit`（見下）。

- [ ] **Step 1: 寫失敗的測試**

`SseWriterTests.cs` 類別最後加：

```csharp
    /// <summary>前端 types/api.ts 的 SearchPresetsDetail／SearchSimilarDetail 靠這些欄位名；detail 為 null 時整個鍵省略。</summary>
    [Fact]
    public async Task Tool_result_detail_is_camelCase_and_omitted_when_null()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream(); ctx.Response.Body = body;
        var detail = new SearchPresetsDetail(new[]
        {
            new SearchPresetsItem("clothing", "clothing.footwear", "鞋履", "拖鞋", true, 300, 5, null,
                new[] { new SearchPresetsHit(5, "霓虹", "高", 0.201, true, new Dictionary<string, string> { ["clothing.footwear"] = "covered" }) }),
            new SearchPresetsItem("hair", "hair", "hair", "捲髮", false, 0, 0, "維度 hair 對 portrait 不適用或不存在", Array.Empty<SearchPresetsHit>()),
        });
        await SseWriter.WriteOneAsync(ctx.Response, new ToolResultEvent("c1", "SearchPresets", "s", Array.Empty<PresetRef>(), detail), default);
        await SseWriter.WriteOneAsync(ctx.Response, new ToolResultEvent("c2", "SearchSimilarPrompts", "s", null,
            new SearchSimilarDetail(new[] { new SearchSimilarHit("雨夜霓虹街頭的銀髮少女", "portrait", 0.18) })), default);
        await SseWriter.WriteOneAsync(ctx.Response, new ToolResultEvent("c3", "SearchPresets", "s", null), default);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("\"detail\":{\"items\":[{\"dimension\":\"clothing\",\"facetId\":\"clothing.footwear\",\"label\":\"鞋履\",\"query\":\"拖鞋\",\"grounded\":true,\"poolSize\":300,\"k\":5,\"hits\":[{\"id\":5,\"title\":\"霓虹\",\"band\":\"高\",\"dist\":0.201,\"usable\":true,\"facets\":{\"clothing.footwear\":\"covered\"}}]}", text);
        Assert.Contains("\"error\":\"維度 hair 對 portrait 不適用或不存在\",\"hits\":[]", text);
        Assert.Contains("\"detail\":{\"hits\":[{\"intent\":\"雨夜霓虹街頭的銀髮少女\",\"profile\":\"portrait\",\"dist\":0.18}]}", text);
        var third = text.Split("event: tool_result\n")[3];
        Assert.DoesNotContain("\"detail\"", third);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SseWriterTests"`
Expected: 編譯錯誤（沒有 `SearchPresetsDetail`）。

- [ ] **Step 3: 實作**

新檔 `src/PromptCopilot.Api/Streaming/ToolDetails.cs`：

```csharp
namespace PromptCopilot.Api.Streaming;

/// <summary>tool_result 的 detail（子專案 RAG 量測設計 §3.5）：給前端「顯示檢索細節」與之後的 eval 腳本用。
/// 與回給模型的 JSON 同一份資料，不放 snippet 本文（抽屜已有）。audit 截 200 字，不能當來源，所以走事件。</summary>
public sealed record SearchPresetsDetail(IReadOnlyList<SearchPresetsItem> Items);

/// <summary>一個查詢項目。驗證失敗的項目 Error 有值、Hits 空、PoolSize 與 K 為 0、Label 是模型送的原始 facetId 或 dimension。</summary>
public sealed record SearchPresetsItem(
    string Dimension, string? FacetId, string Label, string Query,
    bool Grounded, long PoolSize, int K, string? Error,
    IReadOnlyList<SearchPresetsHit> Hits);

/// <summary>Usable 對應回給模型的「可借入提示詞」（true）／「僅供建議」（false）；Facets 是 facetId → wire 字串。</summary>
public sealed record SearchPresetsHit(
    long Id, string Title, string Band, double Dist, bool Usable,
    IReadOnlyDictionary<string, string> Facets);

public sealed record SearchSimilarDetail(IReadOnlyList<SearchSimilarHit> Hits);

/// <summary>Intent 只取前 40 字。</summary>
public sealed record SearchSimilarHit(string Intent, string Profile, double Dist);
```

`AgentEvent.cs` 第 15 行改成：

```csharp
/// <summary>Detail：SearchPresets 放 <see cref="SearchPresetsDetail"/>、SearchSimilarPrompts 放 <see cref="SearchSimilarDetail"/>，其他 null（線上省略）。
/// 宣告成 object 讓 STJ 照實際型別序列化。</summary>
public sealed record ToolResultEvent(string CallId, string Name, string Summary, IReadOnlyList<PresetRef>? Presets, object? Detail = null) : AgentEvent("tool_result");
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SseWriterTests"`
Expected: 全部 PASS。若 `hits:[]` 之前多了 `"error"` 順序不對，是因為 record 位置參數順序決定序列化順序——照上面的參數順序宣告就會對。

- [ ] **Step 5: 同步文件**

主規格 §10.2 表 `tool_result` 列的 `data:` 欄改成：

```text
`{ callId, name, summary, presets?: [{id, title, imageUrl, sourceRef?}], detail? }`
```

並在同一列的「前端反應」欄結尾加：「`detail`（2026-09-25 起）只有 `SearchPresets`（`{ items: [{ dimension, facetId?, label, query, grounded, poolSize, k, error?, hits: [{ id, title, band, dist, usable, facets }] }] }`）與 `SearchSimilarPrompts`（`{ hits: [{ intent, profile, dist }] }`）帶，給「顯示檢索細節」用，不含 snippet 本文；見 `2026-09-25-retrieval-switch-and-trace-design.md` §3.5」。

`2026-09-24-batch-search-presets-design.md` §3.5 段落最後加一段：

```markdown
2026-09-25 起，同一個事件另帶結構化的 `detail`（每個項目的查詢句、池、k、命中的分級／距離／可否借入），摘要字串不變；見 `2026-09-25-retrieval-switch-and-trace-design.md` §3.5。
```

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Streaming/ToolDetails.cs src/PromptCopilot.Api/Streaming/AgentEvent.cs src/PromptCopilot.Api.Tests/Streaming/SseWriterTests.cs docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md docs/superpowers/specs/2026-09-24-batch-search-presets-design.md
git commit -m "feat(sse): tool_result carries an optional structured detail"
```

---

### Task 6: `KnowledgePlugin` 發出 detail

**Files:**
- Modify: `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs`
- Modify: `src/PromptCopilot.Api/Data/HistoryRepository.cs:27`（`SearchAsync` 加 `virtual`，測試才能假造）
- Test: `src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs`

**Interfaces:**
- Consumes: Task 5 的五個 record 與 `ToolResultEvent.Detail`；`HistoryHit(Guid Id, string UserIntent, string PositivePrompt, string SubjectProfile, double Dist)`（既有）。
- Produces: `SearchPresets` 的 `ToolResultEvent.Detail` 是 `SearchPresetsDetail`；`SearchSimilarPrompts` 的是 `SearchSimilarDetail`。

- [ ] **Step 1: 讓 `HistoryRepository.SearchAsync` 可覆寫**

`HistoryRepository.cs` 第 27 行 `public async Task<IReadOnlyList<HistoryHit>> SearchAsync(` 改成 `public virtual async Task<IReadOnlyList<HistoryHit>> SearchAsync(`（`PresetRepository` 的 `SearchAsync`／`PoolSizeAsync` 已經是 virtual，這裡比照）。

- [ ] **Step 2: 寫失敗的測試**

`KnowledgePluginTests.cs`：把既有的 `private sealed class FakeHistories() : HistoryRepository(null!);` 換成：

```csharp
    private sealed class FakeHistories() : HistoryRepository(null!)
    {
        public IReadOnlyList<HistoryHit> Hits { get; set; } = Array.Empty<HistoryHit>();
        public override Task<IReadOnlyList<HistoryHit>> SearchAsync(float[] query, string profile, int k, CancellationToken ct) => Task.FromResult(Hits);
    }
```

`Make` 加一個可省略的參數，既有測試的呼叫與解構都不用改：

```csharp
    private static (KnowledgePlugin plugin, TurnContext turn, Session s, FakeEmbeddings embed, FakePresets presets, ChannelReader<AgentEvent> events)
        Make(string[]? covered = null, bool profile = true, FakeHistories? histories = null)
    {
        ...（原本的內容）...
        return (new KnowledgePlugin(turn, Catalog, embed, presets, histories ?? new FakeHistories()), turn, s, embed, presets, ch.Reader);
    }
```

類別最後加：

```csharp
    /// <summary>設計 §3.5：detail 與回給模型的 JSON 同一份資料；不放 snippet；順序照 queries。</summary>
    [Fact]
    public async Task Detail_mirrors_results_without_snippets()
    {
        var (p, _, _, _, presets, events) = Make(covered: new[] { "scene.location" });
        presets.Pools["style.genre"] = 4455; presets.Pools["scene.location"] = 6752;
        presets.Hits["style.genre"] = new[] { Hit(1, "寫實", "style.genre", 0.2) };
        presets.Hits["scene.location"] = new[] { Hit(2, "稻田", "scene.location", 0.1234) };

        await p.SearchPresetsAsync(Q(("style", "寫實攝影"), ("scene", "稻田裡面喝茶")), default);

        var ev = Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events)));
        var d = Assert.IsType<SearchPresetsDetail>(ev.Detail);
        Assert.Equal(2, d.Items.Count);

        var style = d.Items[0];
        Assert.Equal("style", style.Dimension); Assert.Null(style.FacetId); Assert.Equal("寫實攝影", style.Query);
        Assert.False(style.Grounded); Assert.Equal(4455, style.PoolSize); Assert.Equal(KnowledgePlugin.KMissing, style.K); Assert.Null(style.Error);
        var hit = Assert.Single(style.Hits);
        Assert.Equal(1, hit.Id); Assert.Equal("寫實", hit.Title); Assert.Equal("高", hit.Band); Assert.Equal(0.2, hit.Dist); Assert.False(hit.Usable);
        Assert.Equal("missing", hit.Facets["style.genre"]);

        var scene = d.Items[1];
        Assert.True(scene.Grounded); Assert.Equal(KnowledgePlugin.KCovered, scene.K); Assert.Equal(6752, scene.PoolSize);
        Assert.True(Assert.Single(scene.Hits).Usable);
        Assert.Equal(0.123, scene.Hits[0].Dist);                                         // 四捨五入到小數第三位
        Assert.Equal("covered", scene.Hits[0].Facets["scene.location"]);
        Assert.DoesNotContain("tags for", System.Text.Json.JsonSerializer.Serialize(d));    // 沒有 snippet
    }

    [Fact]
    public async Task Detail_uses_facet_label_for_facet_items_and_dimension_label_otherwise()
    {
        var (p, _, _, _, presets, events) = Make(covered: new[] { "clothing.footwear" });
        presets.Pools["clothing.footwear"] = 300;
        await p.SearchPresetsAsync(new[] { F("clothing.footwear", "拖鞋"), new SearchQuery("style", "寫實") }, default);
        var d = Assert.IsType<SearchPresetsDetail>(Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events))).Detail);
        Assert.Equal("clothing.footwear", d.Items[0].FacetId); Assert.Equal("clothing", d.Items[0].Dimension);
        Assert.Equal(Catalog.Facets["clothing.footwear"].Label, d.Items[0].Label);
        Assert.Equal(Catalog.DimensionLabel("style", "portrait"), d.Items[1].Label);
    }

    /// <summary>全部項目都不合法時沒有 embedding，但 detail 仍要每項一格、帶 error，前端才對得上摘要。</summary>
    [Fact]
    public async Task Detail_keeps_invalid_items_in_order_with_error()
    {
        var (p, _, _, _, _, events) = Make();
        await p.SearchPresetsAsync(Q(("hair", "a"), ("style", "  "), ("nope", "b")), default);
        var d = Assert.IsType<SearchPresetsDetail>(Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events))).Detail);
        Assert.Equal(3, d.Items.Count);
        Assert.All(d.Items, i => { Assert.NotNull(i.Error); Assert.Empty(i.Hits); Assert.Equal(0, i.PoolSize); Assert.Equal(0, i.K); Assert.False(i.Grounded); });
        Assert.Equal("hair", d.Items[0].Label); Assert.Equal("style", d.Items[1].Label); Assert.Equal("nope", d.Items[2].Label);
        Assert.Contains("query 空白", d.Items[1].Error);
    }

    [Fact]
    public async Task Similar_prompts_detail_truncates_intent_to_40_chars()
    {
        var longIntent = new string('雨', 45);
        var histories = new FakeHistories { Hits = new[] { new HistoryHit(Guid.NewGuid(), longIntent, "p", "portrait", 0.1811) } };
        var (p, _, _, _, _, events) = Make(histories: histories);
        await p.SearchSimilarPromptsAsync("雨夜", 3, default);
        var d = Assert.IsType<SearchSimilarDetail>(Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events))).Detail);
        var h = Assert.Single(d.Hits);
        Assert.Equal(new string('雨', 40) + "…", h.Intent); Assert.Equal("portrait", h.Profile); Assert.Equal(0.181, h.Dist);
    }
```

- [ ] **Step 3: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~KnowledgePluginTests"`
Expected: 新測試 FAIL（`Detail` 為 null）；既有測試仍 PASS。

- [ ] **Step 4: 實作**

`KnowledgePlugin.SearchPresetsAsync`：把 `results`／`summary`／`rows` 的組裝改成先組 detail、再從 detail 投影出回給模型的物件。迴圈區塊改成：

```csharp
        var pools = new Dictionary<string, long>();
        var items = new SearchPresetsItem[queries.Length];
        var presetsOut = new List<PresetRef>();
        var seen = new HashSet<long>();

        for (var vi = 0; vi < valid.Count; vi++)
        {
            var (index, dimension, facetId, query, facetIds) = valid[vi];
            var isGrounded = grounded.Contains(dimension);          // grounded 仍以維度判定
            var k = isGrounded ? KCovered : KMissing;
            var poolKey = string.Join(",", facetIds);
            if (!pools.TryGetValue(poolKey, out var pool))
                pools[poolKey] = pool = await presets.PoolSizeAsync(facetIds, ct);
            var hits = await presets.SearchAsync(vectors[vi], facetIds, k, ct);

            var detailHits = new List<SearchPresetsHit>();
            foreach (var h in hits)
            {
                s.Ledger.Record(new LedgerEntry { Id = h.Id, Title = h.Title, PromptSnippet = h.PromptSnippet, NegativeSnippet = h.NegativeSnippet, FacetIds = h.FacetIds, ImageUrl = h.ImageUrl, SourceRef = h.SourceRef },
                    new LedgerHit(dimension, h.Dist, isGrounded));
                detailHits.Add(new SearchPresetsHit(h.Id, h.Title, Band(h.Dist), Math.Round(h.Dist, 3), isGrounded,
                    h.FacetIds.ToDictionary(f => f, f => FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(f, FacetState.NotApplicable)))));
                if (seen.Add(h.Id)) presetsOut.Add(new PresetRef(h.Id, h.Title, h.ImageUrl, h.SourceRef));
            }
            var label = facetId is null ? catalog.DimensionLabel(dimension, s.Profile) : catalog.Facets[facetId].Label;
            items[index] = new SearchPresetsItem(dimension, facetId, label, query, isGrounded, pool, k, null, detailHits);
            // 模型要看 snippet；detail 不放。hits 與 detailHits 同序，靠索引對回去。
            modelHits[index] = hits;
        }
        foreach (var (i, message) in errors)
        {
            var q = queries[i];
            var raw = string.IsNullOrWhiteSpace(q.FacetId) ? q.Dimension ?? "" : q.FacetId;
            items[i] = new SearchPresetsItem(q.Dimension ?? "", q.FacetId, raw, q.Query ?? "", false, 0, 0, message, Array.Empty<SearchPresetsHit>());
        }

        var summary = items.Select(it => it.Error is null ? $"{it.Label} 池 {it.PoolSize} → {it.Hits.Count}" : $"{it.Label} 錯誤".TrimStart());
        turn.Emit(new ToolResultEvent(turn.CurrentCallId ?? Guid.NewGuid().ToString("N"), ToolNames.SearchPresets, string.Join("・", summary), presetsOut,
            new SearchPresetsDetail(items)));

        // 回給模型的 JSON：欄位名與內容與改動前逐字相同（KnowledgePluginTests 釘住）。
        var results = items.Select((it, i) => it.Error is not null
            ? (object)new { dimension = it.Dimension, facetId = it.FacetId, query = it.Query, error = it.Error }
            : new
            {
                dimension = it.Dimension, facetId = it.FacetId, query = it.Query, grounded = it.Grounded, poolSize = it.PoolSize,
                hits = it.Hits.Select((h, j) => new
                {
                    id = h.Id, title = h.Title, band = h.Band, dist = h.Dist,
                    usable = h.Usable ? "可借入提示詞" : "僅供建議",
                    facets = h.Facets,
                    positive = modelHits[i][j].PromptSnippet, negative = modelHits[i][j].NegativeSnippet ?? "(無)",
                }).ToList(),
            }).ToList();
        return JsonSerializer.Serialize(new { results }, Json);
```

迴圈前宣告 `var modelHits = new IReadOnlyList<PresetHit>[queries.Length];`。

注意：改動前錯誤項目的 `summary` 是 `$"{raw} 錯誤".TrimStart()`，`raw` 取 facetId 或 dimension；上面用 `Label`（錯誤項目的 Label 就是 raw），結果相同。

`SearchSimilarPromptsAsync` 的 Emit 改成：

```csharp
        var detail = new SearchSimilarDetail(hits.Select(h => new SearchSimilarHit(
            h.UserIntent.Length > 40 ? h.UserIntent[..40] + "…" : h.UserIntent, h.SubjectProfile, Math.Round(h.Dist, 3))).ToList());
        turn.Emit(new ToolResultEvent(turn.CurrentCallId ?? Guid.NewGuid().ToString("N"), ToolNames.SearchSimilarPrompts, $"相似作品 {hits.Count}（{s.Profile}）", null, detail));
```

- [ ] **Step 5: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~KnowledgePluginTests"`
Expected: 全部 PASS，包括既有的 `Batch_embeds_once_and_counts_each_dimension_pool_once`（模型 JSON 逐字不變的證據）與 `Event_summary_lists_each_item_and_dedupes_presets`。

- [ ] **Step 6: 跑整包後端測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全綠（整合測試自動跳過）。

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs src/PromptCopilot.Api/Data/HistoryRepository.cs src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs
git commit -m "feat(knowledge): SearchPresets and SearchSimilarPrompts emit structured detail on tool_result"
```

---

### Task 7: 前端偏好 `lib/prefs.ts`

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/prefs.ts`
- Test: `src/PromptCopilot.Frontend/tests/prefs.test.ts`

**Interfaces:**
- Consumes: `StorageLike` from `lib/persist.ts`。
- Produces: `Prefs { v: 1; retrieval: 'on' | 'off'; showTrace: boolean }`、`loadPrefs(storage?)`、`savePrefs(p, storage?)`、`DEFAULT_PREFS`。

- [ ] **Step 1: 寫失敗的測試**

`tests/prefs.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { loadPrefs, savePrefs, DEFAULT_PREFS } from '../lib/prefs'
import type { StorageLike } from '../lib/persist'

function memStorage(): StorageLike & { map: Map<string, string> } {
  const map = new Map<string, string>()
  return { map, getItem: k => map.get(k) ?? null, setItem: (k, v) => { map.set(k, v) }, removeItem: k => { map.delete(k) } }
}
const throwing: StorageLike = {
  getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('denied') }, removeItem: () => { throw new Error('denied') },
}

describe('prefs', () => {
  it('defaults to retrieval on and trace hidden', () => {
    expect(loadPrefs(memStorage())).toEqual({ v: 1, retrieval: 'on', showTrace: false })
    expect(loadPrefs(null)).toEqual(DEFAULT_PREFS)
  })

  it('round-trips both switches under pc.prefs', () => {
    const s = memStorage()
    savePrefs({ v: 1, retrieval: 'off', showTrace: true }, s)
    expect(s.map.has('pc.prefs')).toBe(true)
    expect(loadPrefs(s)).toEqual({ v: 1, retrieval: 'off', showTrace: true })
  })

  it('falls back to defaults on bad json, wrong version or unknown values', () => {
    const s = memStorage()
    s.setItem('pc.prefs', '{not json')
    expect(loadPrefs(s)).toEqual(DEFAULT_PREFS)
    s.setItem('pc.prefs', JSON.stringify({ v: 2, retrieval: 'off', showTrace: true }))
    expect(loadPrefs(s)).toEqual(DEFAULT_PREFS)
    s.setItem('pc.prefs', JSON.stringify({ v: 1, retrieval: 'maybe', showTrace: 'yes' }))
    expect(loadPrefs(s)).toEqual(DEFAULT_PREFS)
  })

  it('swallows storage errors on both read and write', () => {
    expect(loadPrefs(throwing)).toEqual(DEFAULT_PREFS)
    expect(() => savePrefs({ v: 1, retrieval: 'off', showTrace: true }, throwing)).not.toThrow()
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/prefs.test.ts`
Expected: FAIL，找不到 `../lib/prefs`。

- [ ] **Step 3: 實作**

`lib/prefs.ts`：

```ts
import type { StorageLike } from './persist'

/** 跨對話的偏好：用不用知識庫（只影響新開的對話）、要不要顯示檢索細節（純顯示）。存 localStorage，與 pc.session（sessionStorage）分開。 */
export interface Prefs { v: 1; retrieval: 'on' | 'off'; showTrace: boolean }

export const DEFAULT_PREFS: Prefs = { v: 1, retrieval: 'on', showTrace: false }
const KEY = 'pc.prefs'

function defaultStorage(): StorageLike | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage } catch { return null }
}

/** 任何錯誤（隱私模式、被停用、壞資料、不認得的值）都回預設。 */
export function loadPrefs(storage: StorageLike | null = defaultStorage()): Prefs {
  try {
    const raw = storage?.getItem(KEY)
    if (!raw) return { ...DEFAULT_PREFS }
    const p = JSON.parse(raw)
    if (!p || p.v !== 1) return { ...DEFAULT_PREFS }
    if (p.retrieval !== 'on' && p.retrieval !== 'off') return { ...DEFAULT_PREFS }
    if (typeof p.showTrace !== 'boolean') return { ...DEFAULT_PREFS }
    return { v: 1, retrieval: p.retrieval, showTrace: p.showTrace }
  } catch { return { ...DEFAULT_PREFS } }
}

export function savePrefs(p: Prefs, storage: StorageLike | null = defaultStorage()): void {
  try { storage?.setItem(KEY, JSON.stringify(p)) } catch { /* 存不進去就算了，下次開頁回預設 */ }
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/prefs.test.ts`
Expected: 4 個 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/lib/prefs.ts src/PromptCopilot.Frontend/tests/prefs.test.ts
git commit -m "feat(frontend): prefs in localStorage for retrieval and trace switches"
```

---

### Task 8: 前端型別、reducer、API client、store 接上 `retrieval` 與 `detail`

**Files:**
- Modify: `src/PromptCopilot.Frontend/types/api.ts`
- Modify: `src/PromptCopilot.Frontend/lib/reducer.ts`
- Modify: `src/PromptCopilot.Frontend/composables/useApi.ts`
- Modify: `src/PromptCopilot.Frontend/stores/session.ts`
- Test: `src/PromptCopilot.Frontend/tests/reducer.test.ts`

**Interfaces:**
- Consumes: `loadPrefs`／`savePrefs`／`Prefs`（Task 7）；後端 `SessionCreated.retrieval`、`SessionSnapshotDto.retrieval`（Task 3）、`tool_result.detail`（Task 5）。
- Produces:
  - `types/api.ts`：`RetrievalMode = 'on' | 'off'`；`SearchPresetsHit`、`SearchPresetsItem`、`SearchPresetsDetail`、`SearchSimilarHit`、`SearchSimilarDetail`、`ToolDetail = SearchPresetsDetail | SearchSimilarDetail`；`SessionCreated { sessionId; retrieval }`；`SessionSnapshotDto.retrieval?: RetrievalMode`。
  - `lib/reducer.ts`：`ToolEntry.detail?: ToolDetail | null`；`ChatState.retrieval: RetrievalMode`。
  - `useApi.createSession(retrieval: RetrievalMode): Promise<SessionCreated>`。
  - store：`prefs`（ref）、`setRetrievalPref(v)`、`setShowTrace(v)`、`retrievalMismatch`（computed）。

- [ ] **Step 1: 寫失敗的測試**

`tests/reducer.test.ts` 最後加一個 describe：

```ts
describe('retrieval and detail (2026-09-25)', () => {
  const detail = {
    items: [{ dimension: 'style', facetId: null, label: '風格', query: '寫實攝影', grounded: false, poolSize: 4455, k: 3, hits: [
      { id: 7, title: 't', band: '高', dist: 0.2, usable: false, facets: { 'style.genre': 'missing' } },
    ] }],
  }

  it('tool_result with detail stores it on the entry', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, { type: 'tool_result', callId: 'c1', name: 'SearchPresets', summary: '風格 池 4455 → 1', presets: [], detail })
    expect((s.transcript.at(-1) as ToolEntry).detail).toEqual(detail)
  })

  it('tool_result without detail leaves detail null', () => {
    let s = applyEvent(started(), call('c1'))
    s = applyEvent(s, result('c1'))
    expect((s.transcript.at(-1) as ToolEntry).detail).toBeNull()
  })

  it('initial state is retrieval on; hydrate takes retrieval from the dto and defaults to on when absent', () => {
    expect(initialState().retrieval).toBe('on')
    const dto: SessionSnapshotDto = { sessionId: 's9', status: 'Collecting', profile: null, turnIndex: 0, askCount: 0, askLimit: 2, facetStates: {}, lastFinal: null, retrieval: 'off' }
    expect(hydrate(initialState(), dto, []).retrieval).toBe('off')
    const { retrieval: _drop, ...older } = dto
    expect(hydrate(initialState(), older as SessionSnapshotDto, []).retrieval).toBe('on')
  })
})
```

（`ToolEntry` 要從 `../lib/reducer` 的 import 加進來。）

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/reducer.test.ts`
Expected: 三個新測試 FAIL（型別錯誤或 `detail`／`retrieval` 為 undefined）。

- [ ] **Step 3: 型別**

`types/api.ts`：

在 `PresetRef` 之後加：

```ts
export type RetrievalMode = 'on' | 'off'

/** tool_result.detail（2026-09-25 起）：SearchPresets 與 SearchSimilarPrompts 各一種，用事件的 name 分辨。不含 snippet 本文。 */
export interface SearchPresetsHit { id: number; title: string; band: string; dist: number; usable: boolean; facets: Record<string, string> }
export interface SearchPresetsItem {
  dimension: string; facetId?: string | null; label: string; query: string
  grounded: boolean; poolSize: number; k: number; error?: string | null; hits: SearchPresetsHit[]
}
export interface SearchPresetsDetail { items: SearchPresetsItem[] }
export interface SearchSimilarHit { intent: string; profile: string; dist: number }
export interface SearchSimilarDetail { hits: SearchSimilarHit[] }
export type ToolDetail = SearchPresetsDetail | SearchSimilarDetail
export function isPresetsDetail(name: string, d: ToolDetail | null | undefined): d is SearchPresetsDetail { return name === 'SearchPresets' && !!d && 'items' in d }
export function isSimilarDetail(name: string, d: ToolDetail | null | undefined): d is SearchSimilarDetail { return name === 'SearchSimilarPrompts' && !!d && 'hits' in d }
```

`AgentEvent` 的 `tool_result` 列改成：

```ts
  | { type: 'tool_result'; callId: string; name: string; summary: string; presets?: PresetRef[]; detail?: ToolDetail }
```

`SessionSnapshotDto` 加 `retrieval?: RetrievalMode`（舊後端沒有），並在它上面加：

```ts
/** POST /api/sessions */
export interface SessionCreated { sessionId: string; retrieval: RetrievalMode }
```

- [ ] **Step 4: reducer**

`lib/reducer.ts`：

- import 加 `RetrievalMode`、`ToolDetail`。
- `ToolEntry` 加 `detail?: ToolDetail | null`（放在 `presets` 之後）。
- `ChatState` 加 `retrieval: RetrievalMode`（放在 `askLimit` 之後），`initialState()` 加 `retrieval: 'on'`。
- `hydrate` 的回傳物件加 `retrieval: dto.retrieval ?? 'on'`。
- `tool_call` 建立 entry 時加 `detail: null`；`tool_result` 的 orphan 與配對更新都加 `detail: ev.detail ?? null`。

- [ ] **Step 5: API client 與 store**

`useApi.ts`：

```ts
  async function createSession(retrieval: RetrievalMode): Promise<SessionCreated> {
    const r = await fetch(`${base}/api/sessions`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ retrieval }),
    })
    if (!r.ok) throw new Error(`createSession HTTP ${r.status}`)
    const body = await r.json()
    // 舊後端沒有 retrieval 欄位：當成 on
    return { sessionId: body.sessionId as string, retrieval: body.retrieval === 'off' ? 'off' : 'on' }
  }
```

（import 加 `RetrievalMode`、`SessionCreated`。）

`stores/session.ts`：

- import 加 `import { loadPrefs, savePrefs, type Prefs } from '../lib/prefs'` 與 `type RetrievalMode`。
- `busy` 之後加：

```ts
  /** 跨對話的偏好。retrieval 只在 newSession 時送出；showTrace 純顯示。 */
  const prefs = ref<Prefs>(loadPrefs())
  function setRetrievalPref(v: RetrievalMode) { prefs.value = { ...prefs.value, retrieval: v }; savePrefs(prefs.value) }
  function setShowTrace(v: boolean) { prefs.value = { ...prefs.value, showTrace: v }; savePrefs(prefs.value) }
  /** 開關值與目前這段對話的模式不同：畫面要說「新對話後生效」。 */
  const retrievalMismatch = computed(() => prefs.value.retrieval !== state.value.retrieval)
```

- `newSession()` 前兩行改成：

```ts
    const created = await api.createSession(prefs.value.retrieval)
    state.value = { ...initialState(), sessionId: created.sessionId, retrieval: created.retrieval }
```

- return 物件加 `prefs, retrievalMismatch, setRetrievalPref, setShowTrace`。

- [ ] **Step 6: 跑測試與型別檢查**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/reducer.test.ts && npx nuxi typecheck`
Expected: reducer 測試全 PASS；typecheck 無錯誤。

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Frontend/types/api.ts src/PromptCopilot.Frontend/lib/reducer.ts src/PromptCopilot.Frontend/composables/useApi.ts src/PromptCopilot.Frontend/stores/session.ts src/PromptCopilot.Frontend/tests/reducer.test.ts
git commit -m "feat(frontend): session retrieval mode and tool_result detail flow through types, reducer and store"
```

---

### Task 9: TopBar 兩個開關

**Files:**
- Modify: `src/PromptCopilot.Frontend/components/TopBar.vue`

**Interfaces:**
- Consumes: store 的 `prefs`、`retrievalMismatch`、`setRetrievalPref`、`setShowTrace`（Task 8）。

- [ ] **Step 1: 實作**

`TopBar.vue` 的 template：把「新對話」按鈕包進一個右側容器，前面放兩個 switch：

```vue
    <div class="flex items-center gap-4">
      <label class="flex items-center gap-1.5 text-xs text-ink/80">
        <button type="button" role="switch" :aria-checked="s.prefs.retrieval === 'on'"
                class="relative h-4 w-7 rounded-full border transition-colors"
                :class="s.prefs.retrieval === 'on' ? 'border-ink bg-ink' : 'border-muted bg-paper'"
                @click="s.setRetrievalPref(s.prefs.retrieval === 'on' ? 'off' : 'on')">
          <span class="absolute top-0.5 h-2.5 w-2.5 rounded-full transition-[left]"
                :class="s.prefs.retrieval === 'on' ? 'left-[15px] bg-paper' : 'left-0.5 bg-muted'" aria-hidden="true" />
        </button>
        使用知識庫
        <span v-if="s.retrievalMismatch" class="text-[11px] text-muted">新對話後生效</span>
      </label>
      <label class="flex items-center gap-1.5 text-xs text-ink/80">
        <button type="button" role="switch" :aria-checked="s.prefs.showTrace"
                class="relative h-4 w-7 rounded-full border transition-colors"
                :class="s.prefs.showTrace ? 'border-ink bg-ink' : 'border-muted bg-paper'"
                @click="s.setShowTrace(!s.prefs.showTrace)">
          <span class="absolute top-0.5 h-2.5 w-2.5 rounded-full transition-[left]"
                :class="s.prefs.showTrace ? 'left-[15px] bg-paper' : 'left-0.5 bg-muted'" aria-hidden="true" />
        </button>
        顯示檢索細節
      </label>
      <button type="button" :disabled="s.busy || !!s.bootError"
              class="rounded-md border border-ink/80 px-3 py-1 text-sm font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
              @click="onNew">新對話</button>
    </div>
```

（原本的 `<button ...>新對話</button>` 移進這個容器。`<script setup>` 不用改。）

- [ ] **Step 2: 型別檢查與建置**

Run: `cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm run build`
Expected: 無錯誤。

- [ ] **Step 3: Commit**

```bash
git add src/PromptCopilot.Frontend/components/TopBar.vue
git commit -m "feat(frontend): TopBar switches for retrieval (next session) and trace display"
```

---

### Task 10: `lib/trace.ts` 純函式

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/trace.ts`
- Test: `src/PromptCopilot.Frontend/tests/trace.test.ts`

**Interfaces:**
- Consumes: `Entry`／`ToolEntry`／`FinalEntry`（`lib/reducer.ts`）、`TagSource`、`isPresetsDetail`（Task 8）。
- Produces:
  - `contributions(positive: TagSource[], negative: TagSource[]): ContributionSummary`，`ContributionSummary { counts: { rag; llm; base }; byPreset: Contribution[] }`，`Contribution { presetId; title; sourceRef; tags: string[] }`。
  - `retrievalSummary(transcript: Entry[]): RetrievalSummary`，`RetrievalSummary { searches; pools: { dimension; label; poolSize }[]; seen; borrowed }`。

- [ ] **Step 1: 寫失敗的測試**

`tests/trace.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { contributions, retrievalSummary } from '../lib/trace'
import type { Entry, ToolEntry } from '../lib/reducer'
import type { TagSource } from '../types/api'

const POS: TagSource[] = [
  { tag: 'neon lights', origin: 'rag', presetIds: [9, 3], presetTitle: '霓虹雨夜', sourceRef: 'civitai:1:0' },
  { tag: 'sandals', origin: 'rag', presetIds: [3], presetTitle: '夏日涼鞋' },
  { tag: '1girl', origin: 'llm', presetIds: [] },
  { tag: 'masterpiece', origin: 'base', presetIds: [] },
]
const NEG: TagSource[] = [
  { tag: 'lowres', origin: 'base', presetIds: [] },
  { tag: 'blurry', origin: 'rag', presetIds: [9], presetTitle: '霓虹雨夜' },
]

describe('contributions', () => {
  it('counts positive origins only and groups tags by preset, most tags first', () => {
    const c = contributions(POS, NEG)
    expect(c.counts).toEqual({ rag: 2, llm: 1, base: 1 })
    expect(c.byPreset).toEqual([
      { presetId: 3, title: '夏日涼鞋', sourceRef: null, tags: ['neon lights', 'sandals'] },
      { presetId: 9, title: '霓虹雨夜', sourceRef: 'civitai:1:0', tags: ['neon lights', '-blurry'] },
    ])
  })

  it('takes title and sourceRef from a source whose first presetId is that preset; a secondary mention only borrows the title', () => {
    const c = contributions([{ tag: 'a', origin: 'rag', presetIds: [5, 6], presetTitle: 'five', sourceRef: 'civitai:5:0' }], [])
    expect(c.byPreset).toEqual([
      { presetId: 5, title: 'five', sourceRef: 'civitai:5:0', tags: ['a'] },
      { presetId: 6, title: 'five', sourceRef: null, tags: ['a'] },
    ])
  })

  it('handles empty sources', () => {
    expect(contributions([], [])).toEqual({ counts: { rag: 0, llm: 0, base: 0 }, byPreset: [] })
  })
})

const tool = (callId: string, detail: unknown, name = 'SearchPresets', done = true): ToolEntry =>
  ({ kind: 'tool', callId, name, argsSummary: '', summary: 's', presets: [], detail: detail as ToolEntry['detail'], done })
const item = (dimension: string, label: string, poolSize: number, hitIds: number[], facetId: string | null = null) => ({
  dimension, facetId, label, query: 'q', grounded: true, poolSize, k: 5, error: null,
  hits: hitIds.map(id => ({ id, title: `t${id}`, band: '高', dist: 0.2, usable: true, facets: {} })),
})

describe('retrievalSummary', () => {
  it('returns searches 0 for a transcript without detail', () => {
    const t: Entry[] = [{ kind: 'user', text: 'x' }, tool('c1', null), tool('c2', undefined)]
    expect(retrievalSummary(t)).toEqual({ searches: 0, pools: [], seen: 0, borrowed: 0 })
  })

  it('counts searches, latest pool per dimension, distinct presets seen and presets borrowed in the latest final', () => {
    const t: Entry[] = [
      tool('c1', { items: [item('style', '風格', 4455, [1, 2]), item('clothing', '鞋履', 300, [3], 'clothing.footwear')] }),
      { kind: 'final', turnIndex: 1, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: '', intentSummary: '',
        positiveSources: [{ tag: 'a', origin: 'rag', presetIds: [1] }] } },
      tool('c2', { items: [item('style', '風格', 4460, [2, 4]), item('clothing', '鞋履', 19, [], 'clothing.footwear')] }),
      tool('c3', { hits: [] }, 'SearchSimilarPrompts'),
      tool('c4', { items: [item('style', '風格', 1, [])] }, 'SearchPresets', false),   // 進行中不算
      { kind: 'final', turnIndex: 2, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: '', intentSummary: '',
        positiveSources: [{ tag: 'a', origin: 'rag', presetIds: [2, 4] }, { tag: 'b', origin: 'llm', presetIds: [] }],
        negativeSources: [{ tag: 'c', origin: 'rag', presetIds: [4] }] } },
    ]
    expect(retrievalSummary(t)).toEqual({
      searches: 2,
      pools: [{ dimension: 'style', label: '風格', poolSize: 4460 }, { dimension: 'clothing', label: '鞋履', poolSize: 19 }],
      seen: 4,
      borrowed: 2,
    })
  })

  it('skips items with error when collecting pools', () => {
    const t: Entry[] = [tool('c1', { items: [{ ...item('style', '風格', 0, []), error: 'x' }] })]
    expect(retrievalSummary(t).pools).toEqual([])
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/trace.test.ts`
Expected: FAIL，找不到 `../lib/trace`。

- [ ] **Step 3: 實作**

`lib/trace.ts`：

```ts
import type { Entry, FinalEntry, ToolEntry } from './reducer'
import { isPresetsDetail, type TagSource } from '../types/api'

export interface Contribution { presetId: number; title: string; sourceRef: string | null; tags: string[] }
export interface ContributionSummary { counts: { rag: number; llm: number; base: number }; byPreset: Contribution[] }

/** 定稿卡的「檢索貢獻」：只靠伺服器標的 tag 來源。counts 只算正向（與 audit 的 tagOrigins 一致）；
 *  一個 tag 對到多個 preset 時每個都列；負向 tag 加 - 前綴列在同一組。
 *  TagSource 的 presetTitle／sourceRef 是 presetIds[0] 那筆片段的（後端「取第一個」），所以只有 i === 0 的提及才可信：
 *  它設定 title、補上還沒有的 sourceRef；其他提及只在建組時借標題當備用，sourceRef 留 null。 */
export function contributions(positive: TagSource[], negative: TagSource[]): ContributionSummary {
  const counts = { rag: 0, llm: 0, base: 0 }
  for (const t of positive) counts[t.origin] += 1
  const groups = new Map<number, Contribution>()
  const add = (t: TagSource, label: string) => {
    if (t.origin !== 'rag') return
    t.presetIds.forEach((id, i) => {
      const g = groups.get(id) ?? { presetId: id, title: t.presetTitle ?? String(id), sourceRef: null, tags: [] }
      if (i === 0) {
        if (t.presetTitle) g.title = t.presetTitle
        g.sourceRef = g.sourceRef ?? t.sourceRef ?? null
      }
      g.tags.push(label)
      groups.set(id, g)
    })
  }
  for (const t of positive) add(t, t.tag)
  for (const t of negative) add(t, `-${t.tag}`)
  const byPreset = [...groups.values()].sort((a, b) => b.tags.length - a.tags.length || a.presetId - b.presetId)
  return { counts, byPreset }
}

export interface RetrievalSummary {
  /** 完成的 SearchPresets 次數 */
  searches: number
  /** 每個維度最近一次查詢的候選池；facet 項目歸到所屬維度，同維度取最後一個項目 */
  pools: { dimension: string; label: string; poolSize: number }[]
  /** 命中過的不同 preset 數 */
  seen: number
  /** 最新一次定稿裡 rag 來源（正負向）引用的不同 preset 數 */
  borrowed: number
}

/** 儀表板的「本次對話檢索摘要」。transcript 沒有任何帶 detail 的 SearchPresets 卡時 searches 為 0，畫面整區隱藏。 */
export function retrievalSummary(transcript: Entry[]): RetrievalSummary {
  const pools = new Map<string, { dimension: string; label: string; poolSize: number }>()
  const seen = new Set<number>()
  let searches = 0
  let latestFinal: FinalEntry | null = null
  for (const e of transcript) {
    if (e.kind === 'final') { if (e.data.kind === 'finalized') latestFinal = e; continue }
    if (e.kind !== 'tool' || !e.done) continue
    const t = e as ToolEntry
    if (!isPresetsDetail(t.name, t.detail)) continue
    searches += 1
    for (const it of t.detail.items) {
      if (it.error) continue
      // 同維度後面的項目蓋掉前面的，但 label 用維度的：facet 項目的 label 是 facet 名，維度列要維度名
      const prev = pools.get(it.dimension)
      pools.set(it.dimension, { dimension: it.dimension, label: it.facetId ? (prev?.label ?? it.label) : it.label, poolSize: it.poolSize })
      for (const h of it.hits) seen.add(h.id)
    }
  }
  const borrowed = new Set<number>()
  if (latestFinal && latestFinal.data.kind === 'finalized') {
    for (const s of [...(latestFinal.data.positiveSources ?? []), ...(latestFinal.data.negativeSources ?? [])])
      if (s.origin === 'rag') s.presetIds.forEach(id => borrowed.add(id))
  }
  return { searches, pools: [...pools.values()], seen: seen.size, borrowed: borrowed.size }
}
```

注意測試 `counts searches, latest pool per dimension...` 期望 `clothing` 列的 label 是「鞋履」：那個測試裡 clothing 只有 facet 項目（label 已是「鞋履」），沒有維度項目可用，所以 `prev?.label ?? it.label` 會落到「鞋履」。這是接受的行為（沒有更好的名字時用 facet 名）。

- [ ] **Step 4: 跑測試確認通過**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/trace.test.ts`
Expected: 6 個 PASS。若 `contributions` 的第一個測試排序不符（3 與 9 都是 2 個 tag），是 `sort` 的 tie-break 用 presetId 升冪，3 在前，符合期望。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/lib/trace.ts src/PromptCopilot.Frontend/tests/trace.test.ts
git commit -m "feat(frontend): trace helpers — final contributions by preset and per-session retrieval summary"
```

---

### Task 11: `ToolCallCard` 依 detail 展開

**Files:**
- Modify: `src/PromptCopilot.Frontend/components/ToolCallCard.vue`

**Interfaces:**
- Consumes: `entry.detail`（Task 8）、`isPresetsDetail`／`isSimilarDetail`（Task 8）、`s.prefs.showTrace`（Task 8）、`s.openDrawer`。

- [ ] **Step 1: 實作**

`ToolCallCard.vue` 展開區（`<div v-if="open" ...>`）內，把 `<p v-if="entry.summary" ...>` 那行換成：

```vue
      <template v-if="presetsDetail">
        <ul class="mt-1 flex flex-col gap-1">
          <li v-for="(it, i) in presetsDetail.items" :key="i">
            <p v-if="it.error" class="text-magenta">{{ it.label }}｜{{ it.error }}</p>
            <template v-else>
              <button type="button" class="flex w-full items-baseline gap-2 text-left hover:bg-surface" :class="it.grounded ? 'text-ink' : 'text-muted'"
                      :aria-expanded="openItems.has(i)" @click="toggleItem(i)">
                <span class="shrink-0 font-medium">{{ it.label }}</span>
                <span class="truncate">{{ it.query }}</span>
                <span class="ml-auto shrink-0 tabular-nums">池 {{ it.poolSize }} → {{ it.hits.length }}</span>
                <span v-if="!it.grounded" class="shrink-0 text-[10px]">僅供建議</span>
              </button>
              <ul v-if="openItems.has(i)" class="ml-3 mt-0.5 flex flex-col gap-0.5 border-l border-rule pl-2 text-[11px]">
                <li v-for="h in it.hits" :key="h.id" class="flex items-baseline gap-2">
                  <button type="button" class="truncate text-left hover:text-cyan" @click="s.openDrawer(h.id)">{{ h.title }}</button>
                  <span class="ml-auto shrink-0 tabular-nums text-muted">{{ h.band }}・{{ h.dist.toFixed(3) }}・{{ h.usable ? '可借入' : '僅供建議' }}</span>
                </li>
                <li v-if="it.hits.length === 0" class="text-muted">沒有命中</li>
              </ul>
            </template>
          </li>
        </ul>
      </template>
      <ul v-else-if="similarDetail" class="mt-1 flex flex-col gap-0.5 text-[11px]">
        <li v-for="(h, i) in similarDetail.hits" :key="i" class="flex items-baseline gap-2">
          <span class="truncate">{{ h.intent }}</span>
          <span class="ml-auto shrink-0 tabular-nums text-muted">{{ h.profile }}・{{ h.dist.toFixed(3) }}</span>
        </li>
        <li v-if="similarDetail.hits.length === 0" class="text-muted">沒有相似作品</li>
      </ul>
      <p v-else-if="entry.summary" class="mt-1 text-ink">{{ entry.summary }}</p>
```

`<script setup>` 加：

```ts
import { isPresetsDetail, isSimilarDetail } from '../types/api'
const openItems = reactive(new Set<number>())
function toggleItem(i: number) { if (openItems.has(i)) openItems.delete(i); else openItems.add(i) }
/** 只有「顯示檢索細節」開啟且事件帶 detail 才展開逐項；否則退回一行摘要（舊的 sessionStorage 資料沒有 detail）。 */
const presetsDetail = computed(() => s.prefs.showTrace && isPresetsDetail(props.entry.name, props.entry.detail) ? props.entry.detail : null)
const similarDetail = computed(() => s.prefs.showTrace && isSimilarDetail(props.entry.name, props.entry.detail) ? props.entry.detail : null)
```

縮圖列與來源說明保持在展開區最下面，不動。

- [ ] **Step 2: 型別檢查**

Run: `cd src/PromptCopilot.Frontend && npx nuxi typecheck`
Expected: 無錯誤。若 `computed` 收窄型別失敗（回傳 `ToolDetail | null`），把 `isPresetsDetail(...) ? props.entry.detail : null` 改成 `isPresetsDetail(props.entry.name, props.entry.detail) ? (props.entry.detail as SearchPresetsDetail) : null` 並 import 該型別。

- [ ] **Step 3: Commit**

```bash
git add src/PromptCopilot.Frontend/components/ToolCallCard.vue
git commit -m "feat(frontend): ToolCallCard lists each retrieval item and its hits when trace display is on"
```

---

### Task 12: `FinalCard` 檢索貢獻與 `Dashboard` 檢索摘要，同步前端 spec

**Files:**
- Modify: `src/PromptCopilot.Frontend/components/FinalCard.vue`
- Modify: `src/PromptCopilot.Frontend/components/Dashboard.vue`
- Modify: `docs/superpowers/specs/2026-09-24-frontend-sse-design.md`（§3.2、§4、§10）

**Interfaces:**
- Consumes: `contributions`、`retrievalSummary`（Task 10）、`s.prefs.showTrace`（Task 8）。

- [ ] **Step 1: FinalCard**

`FinalCard.vue` 在 `生成建議` 那個 `<section>` 之後加：

```vue
    <section v-if="s.prefs.showTrace" class="mt-4" data-section="trace">
      <h4 class="text-xs font-bold">檢索貢獻</h4>
      <p class="mt-1.5 text-xs tabular-nums text-muted">rag {{ trace.counts.rag }}・llm {{ trace.counts.llm }}・base {{ trace.counts.base }}</p>
      <ul v-if="trace.byPreset.length" class="mt-1.5 flex flex-col gap-1 text-xs">
        <li v-for="p in trace.byPreset" :key="p.presetId" class="flex items-baseline gap-2">
          <button type="button" class="shrink-0 font-medium hover:text-cyan" @click="s.openDrawer(p.presetId)">{{ p.title }}</button>
          <span class="text-muted">→</span>
          <span class="font-mono text-[11px]">{{ p.tags.join(', ') }}</span>
        </li>
      </ul>
      <p v-else class="mt-1.5 text-xs text-muted">這次定稿沒有借用知識庫片段。</p>
    </section>
```

`<script setup>` 加：

```ts
import { contributions } from '../lib/trace'
const trace = computed(() => contributions(props.data.positiveSources ?? [], props.data.negativeSources ?? []))
```

（畫面上的「→」是 UI 文字，不是散文。）

- [ ] **Step 2: Dashboard**

`Dashboard.vue` 的 `<footer class="mt-auto pt-5">` 開頭（圖例 `<ul>` 之前）加：

```vue
      <section v-if="s.prefs.showTrace && trace.searches > 0" class="mb-4 border-t border-rule pt-4" data-section="trace">
        <h3 class="text-xs font-bold">本次對話檢索摘要</h3>
        <p class="mt-1.5 text-xs tabular-nums text-muted">
          查詢 {{ trace.searches }} 次・看過 {{ trace.seen }} 筆片段・借用 {{ trace.borrowed }} 筆
        </p>
        <div class="mt-1.5 flex flex-wrap gap-1">
          <span v-for="p in trace.pools" :key="p.dimension" class="rounded-[3px] border border-rule px-1.5 py-[3px] text-[11px] leading-4 tabular-nums">
            {{ p.label }} {{ p.poolSize }}
          </span>
        </div>
      </section>
```

`<script setup>` 加：

```ts
import { retrievalSummary } from '../lib/trace'
const trace = computed(() => retrievalSummary(s.state.transcript))
```

- [ ] **Step 3: 型別檢查、建置、整包前端測試**

Run: `cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm test && npm run build`
Expected: 全綠。

- [ ] **Step 4: 同步前端 spec**

`docs/superpowers/specs/2026-09-24-frontend-sse-design.md`：

- §3.2 的狀態清單在 `askCount, askLimit` 之後加一行 `retrieval                    # 'on' | 'off'，建立回應／GET session 拿；只影響畫面提示`，在 `drawer` 之後加 `prefs: { retrieval, showTrace }  # localStorage pc.prefs，跨對話`。`Entry` 表 `tool` 列改成 `{ callId, name, argsSummary, summary?, presets?, detail?, done }`。
- §4 元件表：`ToolCallCard` 列結尾加「；「顯示檢索細節」開啟且有 `detail` 時改列每個查詢項目（標籤｜查詢句｜池 → 命中），項目可展開命中清單（標題可開抽屜、分級、距離、可借入／僅供建議）」；`FinalCard` 列結尾加「；「顯示檢索細節」開啟時多一區「檢索貢獻」：rag／llm／base 計數與片段 → tag 清單」；`Dashboard`（若表裡沒有這一列就加一列）加「「顯示檢索細節」開啟且有查詢過時，圖例上方加「本次對話檢索摘要」」；加一列 `TopBar`：「兩個 switch：「使用知識庫」（存偏好，只影響新對話，與目前對話不同時標「新對話後生效」）、「顯示檢索細節」（即時）」。
- §10 表最後加一列：

```markdown
| （2026-09-25 知識庫開關與檢索細節） | `lib/prefs.ts`（localStorage）、`lib/trace.ts`（純函式）、`ToolEntry.detail`、`ChatState.retrieval`；三個元件在 `prefs.showTrace` 開時多畫一區，關時與原設計相同 | 設計見 `2026-09-25-retrieval-switch-and-trace-design.md` |
```

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/components/FinalCard.vue src/PromptCopilot.Frontend/components/Dashboard.vue docs/superpowers/specs/2026-09-24-frontend-sse-design.md
git commit -m "feat(frontend): final-card retrieval contributions and dashboard retrieval summary behind the trace switch"
```

---

### Task 13: 人工驗收案例與收尾

**Files:**
- Modify: `docs/eval-cases.md`（檔尾加一節）
- Modify: `docs/known-issues.md`（若有「RAG 過程不可見」相關項目，標已處理；沒有就不動）

- [ ] **Step 1: 整包測試**

Run（repo 根目錄）：

```bash
dotnet test src/PromptCopilot.Api.Tests
cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm test && npm run build
```

Expected: 全綠。

- [ ] **Step 2: 加人工驗收案例**

`docs/eval-cases.md` 檔尾加：

```markdown
## 2026-09-25 知識庫開關與檢索細節（待跑）

設計：`docs/superpowers/specs/2026-09-25-retrieval-switch-and-trace-design.md`。瀏覽器驗收，需要 API 與知識庫。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| R1 | 「顯示檢索細節」關，送「一個銀髮少女穿涼鞋站在雨夜街頭」 | 畫面與 2026-09-25 之前相同：工具卡一行摘要與縮圖、追問選項、定稿 chip | ⏳ |
| R2 | 開「顯示檢索細節」（不開新對話） | 同一張 `SearchPresets` 卡點開變成逐項（標籤｜查詢句｜池 → 命中），項目可展開命中清單，標題點了開抽屜；定稿卡多「檢索貢獻」；儀表板底部多「本次對話檢索摘要」 | ⏳ |
| R3 | 關「顯示檢索細節」 | 三處都回到 R1 的樣子 | ⏳ |
| R4 | 關「使用知識庫」 | 開關旁出現「新對話後生效」；目前對話照常 | ⏳ |
| R5 | 按「新對話」，送同一句 | 沒有「查知識庫」「找相似作品」卡；追問選項全是純文字；定稿 chip 只有 llm／base；「新對話後生效」消失 | ⏳ |
| R6 | 在 R5 的對話重新整理 | 對話流回來，「使用知識庫」開關仍是關、沒有「新對話後生效」 | ⏳ |
| R7 | 開「使用知識庫」再開新對話，跑到定稿，看 audit `Turn_Completed` | payload 有 `"retrieval":"on"`；R5 那段的是 `"off"`，且 `tagOrigins.rag` 為 0 | ⏳ |
```

- [ ] **Step 3: Commit**

```bash
git add docs/eval-cases.md docs/known-issues.md
git commit -m "docs: manual acceptance cases for the retrieval switch and trace display"
```

- [ ] **Step 4: 完成分支**

用 superpowers:finishing-a-development-branch 決定 merge 方式。
