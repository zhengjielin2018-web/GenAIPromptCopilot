# 先確認再動手 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 使用者說的任何會改動畫面的話，模型都先用確認卡跟他確認，他按下按鈕之後下一輪才動手；附帶把推薦限縮在定稿卡。

**Architecture:** 每一輪依輸入分成「確認輪」（使用者打字）與「動手輪」（按確認卡或採用）。`ToolSetBuilder` 依輪的種類給工具：確認輪只有新的終止型工具 `Confirm`、`Discuss` 與檢索；動手輪才有 `SetProfile`／`SetFacetStates`／`AskUser`／`FinalizePrompt`。`Confirm` 在 session 記一筆 `PendingConfirmation`（進快照），按鈕送 `{"confirm": {turnIndex, choice}}`，端點驗證後組出動手輪的輸入；動手輪不跑輸入分類器，system prompt 的流程段換成動手輪那一套並帶「使用者已確認」區塊。前端多一張確認卡，從對話流推算哪張可按。

**Tech Stack:** .NET 10 / ASP.NET Core minimal API、Semantic Kernel（Google connector）、xUnit、Nuxt 3 / Vue 3 / Pinia、Vitest、Python 3.12（manual-tests 只用標準函式庫）。

**Spec:** `docs/superpowers/specs/2026-10-05-confirm-before-act-design.md`

## Global Constraints

- **分支 `feat/confirm-before-act` 已經開好**（spec 與網頁範例文字已 commit），直接在主目錄上做，不要用 worktree：worktree 路徑下的 Debug 建置與 `nuxt prepare` 會被 Windows 應用程式控制擋下。
- 註解、文件以繁體中文為主，密度與風格照周圍程式（解釋「為什麼」、引用設計章節，寫「先確認再動手設計 §x」）。commit 標題英文，照 repo 慣例 `feat(api): …`／`test: …`／`docs: …`。
- 每個 commit 訊息結尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`（執行者若不是 Opus，改成當時 harness 給的那一行）。
- 固定字串（測試會比對）：接受句 `對，就這樣`；`沒有待確認的內容`；`只有最新一張確認卡可以按`；`定稿後才能採用組合`；`confirm 與 adopt 不能同時送`；`Discuss` 擋回字串 `錯誤：Discuss 不能改 facet 狀態；使用者要改畫面時，請用 Confirm 跟他確認`。
- `Confirm` 的 `choices`：清洗後 0 個或 2–4 個，每個 ≤ 40 字；`choices` 參數一定要有預設值 `null`。
- 工具清單：確認輪 = `Confirm`＋`SearchPresets`＋`SearchSimilarPrompts`，加 `Discuss`（`!wantsAutoComplete` 且（`Finalized` 或 `DiscussStreak < MaxDiscussStreak`）），`Finalized` 時加 `RequestSaveConsent`；動手輪 = `ToolNames.Always`，加 `AskUser`（`!wantsAutoComplete` 且 `Collecting` 且 `AskCount < MaxAskCount`）。`retrieval: off` 兩種輪都拿掉兩個檢索工具。
- `HistoryTurns` 預設 20。追問額度 `MaxAskCount = 2`、`MaxDiscussStreak = 8` 不動。
- SSE 用 `WhenWritingNull`；新欄位 `choices` 一律送陣列（沒有選項是 `[]`）。audit 的巢狀物件用 `AgenticOrchestrator.Fields` 省略 null。
- 不動 `scripts/demo.py` 與 `docs/單輪流程說明.md`（那是 Python 單輪 demo 的說明）。
- 測試指令：C# `dotnet test src/PromptCopilot.Api.Tests`（integration 測試要 `PC_INTEGRATION=1` 才跑）；前端在 `src/PromptCopilot.Frontend`，Node 不在工具的 shell PATH 上，先
  `export PATH="$LOCALAPPDATA/Microsoft/WinGet/Packages/OpenJS.NodeJS.22_Microsoft.Winget.Source_8wekyb3d8bbwe/node-v22.23.2-win-x64:$PATH"`，
  再 `npm test`、`npm run build`。

## Review Focus

spec 沒逐條寫、但使用者一定會碰到的情況；每一條都在對應任務加了測試：

1. **「對，就這樣」被當成畫面描述**：推薦的查詢向量是使用者訊息串接，這句會把查詢拉偏。預期：接受句不算描述；選了解讀的句子（「換掉飲料，改拿雨傘」）照算。→ Task 4 `Query_text_skips_the_plain_accept_sentence_but_keeps_a_chosen_interpretation`。
2. **確認內容裡剛好有 `{{TOOLS}}` 之類的字**：它被塞進 system prompt，若之後還有 placeholder 替換會被展開。預期：原樣保留。→ Task 5 `Confirmed_text_is_inserted_last_and_never_expanded`。
3. **按到過期的確認卡（另一個分頁、或已經動過手）**：預期 409 帶理由，前端顯示伺服器的理由，而且不能說「確認卡還在，可以再按一次」。→ Task 2 `Older_card_is_409`、Task 7 `an http rejection of a confirm turn does not claim the card is still there`。
4. **動手輪 Gemini 只回純文字**：動手輪沒有 `Discuss` 可包。預期 `protocol_violation`、整輪回滾、待確認回來，卡片可以再按。→ Task 4 `No_outcome_is_protocol_violation_and_rolls_back`（加斷言）。
5. **確認卡出現後在輸入框打「好」**：預期是新的確認輪，不動手；問問題不會讓卡片失效，新的確認卡取代舊的。→ Task 4 `Typed_ok_after_a_confirm_card_is_another_text_turn`。

---

## 檔案結構

| 檔案 | 責任 |
| :--- | :--- |
| `src/PromptCopilot.Api/Sessions/Confirmation.cs`（新） | `PendingConfirmation`、`ConfirmRequest`、`ConfirmedInput`、`ConfirmValidator`（含接受句常數）、`ConfirmValidationException` |
| `src/PromptCopilot.Api/Sessions/Session.cs` | 待確認的欄位、設定／清除、進快照 |
| `src/PromptCopilot.Api/Orchestration/IPromptOrchestrator.cs` | `TurnInput.Confirmed` |
| `src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs` | `ToolNames.Confirm`／`ProposeAlways`、`TurnKind`、依輪的種類組清單 |
| `src/PromptCopilot.Api/Plugins/DialogPlugin.cs`、`Plugins/Contracts.cs` | `Confirm` 工具、`ConfirmOutcome`、`Discuss` 閘門 |
| `src/PromptCopilot.Api/Streaming/AgentEvent.cs` | `FinalEvent.Choices` |
| `src/PromptCopilot.Api/Filters/OutputSafetyFilter.cs`、`Filters/TurnContextExtensions.cs` | 輸出審查涵蓋 `Confirm` |
| `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs` | 兩種輪、跳過分類器、清待確認、強制確認、補救提示、audit、只對定稿推薦 |
| `src/PromptCopilot.Api/Orchestration/RecommendationService.cs` | 只做定稿卡、查詢文字排除接受句 |
| `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs`、`Prompts/system.md`、`Prompts/flow-propose.md`（新）、`Prompts/flow-act.md`（新） | 流程段二選一、確認區塊 |
| `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs` | `confirm` body、採用定稿後才收、Swagger 描述 |
| `src/PromptCopilot.Api/Configuration/Options.cs`、`appsettings.json`、`PromptCopilot.Api.csproj`、`Program.cs` | `HistoryTurns = 20`、prompt 檔複製、建構子參數 |
| `src/PromptCopilot.Frontend/types/api.ts`、`composables/useApi.ts`、`lib/confirm.ts`（新）、`lib/reducer.ts`、`lib/adopt.ts`、`stores/session.ts` | 型別、可按的卡、失敗條目、按確認 |
| `src/PromptCopilot.Frontend/components/ConfirmCard.vue`（新）、`ChatStream.vue`、`Composer.vue`、`FailureNotice.vue`、`AskCard.vue`、`RecommendationStrip.vue` | 畫面 |
| `manual-tests/chat.py` | `/ok`、`/1`～`/4` |
| 文件 | 主規格、README、兩份推薦 spec、`docs/eval-cases.md` |

---

### Task 1: 追問卡不再推薦、採用只在定稿後

spec §8。獨立 commit，跟確認流程互不依賴。

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/RecommendationService.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`（`TryRecommendAsync`）
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`（採用的 409、Swagger 描述）
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs`（`LatestSlateTurn` 的註解）
- Modify: `src/PromptCopilot.Api/Prompts/system.md`（流程第 6 條）
- Modify: `src/PromptCopilot.Frontend/components/AskCard.vue`、`components/ChatStream.vue`、`components/RecommendationStrip.vue`、`lib/adopt.ts`、`stores/session.ts`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/RecommendationServiceTests.cs`、`Orchestration/AgenticOrchestratorTests.cs`、`Endpoints/EndpointTests.cs`、`Orchestration/SystemPromptBuilderTests.cs`、`src/PromptCopilot.Frontend/tests/adopt.test.ts`

**Interfaces:**
- Produces: `IRecommendationService.BuildAsync(Session s, FinalizedOutcome outcome, int turnIndex, CancellationToken ct)`（參數型別從 `TurnOutcome` 收窄）。
- Produces（測試 helper，`AgenticOrchestratorTests` 內）：`private static object FinalizeArgs()`、`private static void MakeFinalized(Session s)`，Task 4、5 會用。
- Produces（前端）：`lib/adopt.ts` 不再有 `latestRecommendableTurn`；store 不再 export `latestRecommendableTurn`，可採用的卡改看 `latestFinalizedTurn`。

- [ ] **Step 1: 寫失敗的測試**

`AgenticOrchestratorTests.cs`，在 `SomeRecommendations` 定義之後加兩個 helper 與一個測試：

```csharp
    private static object FinalizeArgs() => new
    {
        positivePrompt = "masterpiece, 1girl", negativePrompt = "lowres", tips = "t", intentSummary = "一個女生", facetStates = Array.Empty<object>(),
    };

    /// <summary>已定稿的 session：定稿後 AskUser 不在清單上，FinalizePrompt 過得了定稿閘門。</summary>
    private static void MakeFinalized(Session s)
    {
        s.ApplyProfile("portrait", Catalog);
        s.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));
    }

    /// <summary>先確認再動手設計 §8：未定稿前不推薦。追問卡不呼叫推薦服務，也不發事件。</summary>
    [Fact]
    public async Task Ask_turn_never_calls_recommendations()
    {
        var h = new Harness();
        var stub = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(SomeRecommendations(1)));
        h.Recommendations = stub;
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");
        Assert.Equal("ask", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal(0, stub.Calls);
        Assert.Empty(events.OfType<RecommendationsEvent>());
    }
```

`EndpointTests.cs`，在 `Adopt_is_409_when_retrieval_is_off_or_profile_is_unset` 之後加：

```csharp
    /// <summary>先確認再動手設計 §8：只有定稿卡推薦，採用只在定稿後收。</summary>
    [Fact]
    public async Task Adopt_is_409_before_finalize()
    {
        var s = PortraitSession();
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/messages", new { adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("定稿後才能採用組合", (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }
```

- [ ] **Step 2: 跑測試，確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~Ask_turn_never_calls_recommendations|FullyQualifiedName~Adopt_is_409_before_finalize"`
Expected: 兩個都 FAIL（推薦被呼叫 1 次；採用回 200）。

- [ ] **Step 3: 後端實作**

`RecommendationService.cs`：介面與 `BuildAsync` 換成只收定稿；刪掉 `AskRowAsync` 與 `SimilarAsync`（只有追問卡用它們）。類別註解改寫。

```csharp
public interface IRecommendationService
{
    /// <summary>定稿卡的推薦（先確認再動手設計 §8：2026-10-05 起只有定稿卡推薦）。沒有任何維度有候選（或 profile 未設）時回 null，不發事件。</summary>
    Task<RecommendationsEvent?> BuildAsync(Session s, FinalizedOutcome outcome, int turnIndex, CancellationToken ct);

    /// <summary>換一批（推薦組法設計 §4.5）：該維度的下一批。沒有候選時回 Sets 為空的一排（批次不前進、不記看過）。
    /// 呼叫端要拿著 session 鎖，並先確認 turnIndex 等於 LatestSlateTurn。</summary>
    Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct);
}

/// <summary>整套組合推薦（設計 §5）。由伺服器產生、模型不知道：推薦系統要「每次都在、每次一樣」。
/// 只在定稿時推薦（先確認再動手設計 §8），查本 profile 全部維度：2 相關＋1 探索、看過加權延後、可換一批（2026-09-30 推薦組法設計）。
/// 相關位的候選分三層：字面錨（covered facet 的 FacetTags＋定稿 positive）、近似錨（facet 向量離錨 ≤ RecommendationSimilarMaxDist）、純向量。</summary>
public sealed class RecommendationService(FacetCatalog catalog, IEmbeddingClient embed, PresetRepository presets, OrchestratorOptions options) : IRecommendationService
{
    public const int QueryChars = 500;
    public const int MinAnchoredHits = 2;
    /// <summary>伺服器組的採用句開頭；直接引用 AdoptionComposer 的常數，兩處不會分岔。</summary>
    public const string AdoptionPrefix = AdoptionComposer.Prefix;

    public async Task<RecommendationsEvent?> BuildAsync(Session s, FinalizedOutcome outcome, int turnIndex, CancellationToken ct)
    {
        if (s.Profile is null) return null;
        // 舊的定稿卡不再是最新的：先收掉換一批，下面再重開（Review Focus 3）
        s.EndSlate();
        var dimensions = catalog.DimensionsOf(s.Profile);
        if (dimensions.Count == 0) return null;
        var query = QueryText(s.ChatHistory);
        if (query.Length == 0) return null;
        var (vec, anchorVec) = await EmbedAsync(s, dimensions, query, ct);
        // 基礎畫質詞不當錨：每次定稿都有，只會把推薦拉向剛好也寫了 masterpiece 的片段
        var finalTags = TagAttribution.Split(outcome.Final.Positive).Select(TagAttribution.Normalize).Where(t => t.Length > 0 && !TagAttribution.IsBase(t)).ToList();

        s.BeginSlate(turnIndex, finalTags);
        var result = new List<RecommendedDimension>();
        // 全部維度都成功才記看過：中途失敗時事件不會送出，使用者沒看到的不能被往後推（Review Focus 2）
        var seen = new List<(string dim, IReadOnlyList<string> keys)>();
        foreach (var dim in dimensions)
            if (await SlateAsync(s, dim, 1, vec, anchorVec, finalTags, ct) is { } built)
            {
                result.Add(built.row);
                seen.Add((dim, built.keys));
            }
        foreach (var (dim, keys) in seen) s.RecordSlate(dim, 1, keys);
        return result.Count == 0 ? null : new RecommendationsEvent(turnIndex, result);
    }
```

`NextAsync`、`EmbedAsync`、`JoinedUserText`、`QueryText`、`AnchorTags`、`MatchedAnchors`、`SlateAsync`、`ToSet`、`NormalizedFacetTags`、`SimilarHitsAsync`、`SimilarAnchorText` 原樣保留；整段刪掉 `/// <summary>追問卡的一排…` 開頭的 `AskRowAsync` 與 `/// <summary>近似錨（設計 §6.1）：每個 covered…` 開頭的 `SimilarAsync`。

`AgenticOrchestrator.cs` 的 `TryRecommendAsync`：

```csharp
    /// <summary>推薦是附加的（設計 §5.1、§9）：final 已宣告出去，推薦失敗或逾時只記 audit，不回滾、不發 error。
    /// 用自己的逾時，不掛在整輪的 token 上——整輪的 token 取消會走回滾路徑。只有定稿卡推薦（先確認再動手設計 §8）。</summary>
    private async Task<RecommendationsEvent?> TryRecommendAsync(Session session, TurnOutcome outcome, int turnIndex, string version, string text)
    {
        if (!session.RetrievalEnabled || outcome is not FinalizedOutcome finalized) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.RecommendationTimeoutSeconds));
        try { return await recommendations.BuildAsync(session, finalized, turnIndex, cts.Token); }
```

（`catch` 區塊原樣保留。）

`Session.cs`：`LatestSlateTurn` 註解裡的「出了新的追問卡就清掉」改成「下一張定稿卡產生推薦前先清掉」。

`SessionEndpoints.cs` 的 `messages` 端點，採用分支：

```csharp
                if (req.Adopt is { } adopt)
                {
                    // 拿著鎖再讀 session：沒鎖時讀到的可能是上一輪回滾中的半途狀態
                    if (!s.RetrievalEnabled) return Results.Conflict(new ErrorBody("這段對話沒有知識庫，沒有組合可以採用"));
                    // 只有定稿卡推薦（先確認再動手設計 §8）；定稿必然已有題材
                    if (s.Status != SessionStatus.Finalized) return Results.Conflict(new ErrorBody("定稿後才能採用組合"));
```

（刪掉原本 `if (s.Profile is null) return Results.Conflict(new ErrorBody("尚未判定題材，還不能採用組合"));` 那一行。）

同一個端點的 `.WithDescription`：
- `recommendations` 那一列的內容開頭「整套組合推薦（2026-09-25）：追問時只有被問的維度、定稿時全部維度，跟在 `final`＋`dimensions` 之後；掛在該輪的追問卡／定稿卡下方。」改成「整套組合推薦（2026-09-25）：只有定稿那一輪有（2026-10-05 起追問卡不推薦），全部維度，跟在 `final`＋`dimensions` 之後；掛在定稿卡下方。」
- 同一列「`batch`、每套的 `reason`／`anchorTags`／`rank`／`prob`（2026-09-30，換一批）只有定稿卡有，追問卡是 `null` 而省略。」改成「`batch`、每套的 `reason`／`anchorTags`／`rank`／`prob`：2026-09-30 換一批加的。」
- 409 那一行「`adopt` 但這段對話 `retrieval: off` 或尚未判定題材」改成「`adopt` 但這段對話 `retrieval: off` 或還沒定稿」。

`Prompts/system.md` 流程第 6 條整條換成：

```markdown
6. 使用者訊息以「採用〈」開頭時，那是他從定稿卡的推薦裡挑了一套。「照它的」是**取代**：定稿時該 facet 只留括號內的 tag（原字，不改寫），原本的 tag 全部拿掉——從使用者先前的描述翻的、上一版定稿裡屬於這個 facet 的、括號內「取代原本的」後面列的都算；狀態設 `covered`、`tags` 填留下的那些字、note 記「採用知識庫 #編號」。「保留我的」facet 維持原狀。然後直接 `FinalizePrompt` 重新定稿（採用只會發生在定稿之後）。
```

- [ ] **Step 4: 改既有的後端測試**

`RecommendationServiceTests.cs`：
- 刪掉 `Ask(...)` helper，以及這幾個只測追問卡的測試：`Ask_outcome_queries_only_the_asked_dimensions_in_ask_order`、`Anchored_query_with_two_hits_is_reported_anchored_with_the_matched_anchor_tags_only`、`Anchored_query_with_fewer_than_two_hits_falls_back_to_an_unanchored_query`、`No_covered_facets_means_one_unanchored_query`、`Literal_anchor_short_of_two_hits_tries_the_similar_anchor_and_reports_it_as_similar`、`Similar_anchor_short_of_two_hits_falls_back_to_unanchored`、`Anchored_result_never_runs_the_similar_query`、`Similar_results_from_several_facets_merge_by_min_distance_and_credit_only_contributing_facets`、`Ask_outcome_ends_the_slate`。
- 下面這些改成定稿卡（整段替換）：

```csharp
    /// <summary>設計 §5.3：錨＝該維度 covered facet 的 FacetTags，正規化、去重；沒 covered 的維度不帶錨。基礎畫質詞不進錨。</summary>
    [Fact]
    public async Task Anchors_come_from_facet_tags_normalized()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "Sandals, platform_footwear"), ("clothing.upper", "(white shirt:1.2)"));
        await svc.BuildAsync(s, Finalized("masterpiece"), 1, default);
        var clothing = presets.Calls.First(c => c.firstFacet == "clothing.head");                   // 字面錨那一層
        Assert.Equal(new[] { "clothing.upper", "clothing.footwear" }, clothing.anchorFacets);       // facets.yaml 順序
        Assert.Equal(new[] { "white shirt", "sandals", "platform footwear" }, clothing.anchorTags);
        Assert.Equal(30, clothing.take);                                                            // 定稿卡每層取 PoolSize
        Assert.All(presets.Calls.Where(c => c.firstFacet == "style.genre"), c => Assert.Empty(c.anchorTags));
    }

    [Fact]
    public async Task Final_card_set_lists_every_dimension_facet_with_state_and_tags()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = new[]
        {
            Set(1, "夏日", ("clothing.upper", "front-tie top"), ("clothing.footwear", "platform sandals")),
            Set(2, "海邊", ("clothing.lower", "short shorts"), ("clothing.footwear", "sandals")),
        };
        var d = (await svc.BuildAsync(s, Finalized("masterpiece"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal("人物穿著", d.Label);
        var set = d.Sets.Single(x => x.PresetId == 1);
        Assert.Equal(new[] { "clothing.head", "clothing.upper", "clothing.lower", "clothing.footwear", "clothing.material", "clothing.accessories" }, set.Facets.Select(f => f.FacetId));
        Assert.Equal("covered", set.Facets[3].State); Assert.Equal(new[] { "platform sandals" }, set.Facets[3].Tags);
        Assert.Equal("missing", set.Facets[0].State); Assert.Empty(set.Facets[0].Tags);
        Assert.Equal("鞋履", set.Facets[3].Label);
        Assert.Equal(0.21, set.Dist); Assert.Equal("https://img", set.ImageUrl); Assert.Equal("civitai:1:0", set.SourceRef);
    }

    /// <summary>每套的 AnchorTags 要等於 SQL 過濾實際比中的錨：DB tag 等於錨、或以「空白＋錨」結尾。反方向（錨以 DB tag 結尾）SQL 不比，這裡也不能算。</summary>
    [Fact]
    public async Task Matched_anchors_mirror_the_sql_filter_and_ignore_anchors_that_only_end_with_a_db_tag()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals, white socks"));
        presets.Script[("clothing.head", true)] = new[]
        {
            Set(1, "a", ("clothing.footwear", "sandals, socks"), ("clothing.upper", "x")),
            Set(2, "b", ("clothing.footwear", "socks"), ("clothing.lower", "y")),
        };
        var d = (await svc.BuildAsync(s, Finalized("masterpiece"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(new[] { "sandals" }, d.Sets.Single(x => x.PresetId == 1).AnchorTags);     // white socks 不因 DB 的 socks 而算命中
        Assert.Empty(d.Sets.Single(x => x.PresetId == 2).AnchorTags!);
    }

    [Fact]
    public async Task Returns_null_when_no_dimension_has_candidates()
    {
        var (svc, s, _, _) = Make();
        Assert.Null(await svc.BuildAsync(s, Finalized("1girl"), 1, default));
    }

    [Fact]
    public async Task Facet_tags_for_facets_outside_the_dimension_are_ignored()
    {
        var (svc, s, _, presets) = MakeWith(Greedy());
        presets.Script[("style.genre", false)] = new[] { Set(9, "x", ("style.genre", "oil painting"), ("style.palette", "muted"), ("scene.weather", "rain")) };
        var d = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "style");
        var set = Assert.Single(d.Sets);
        Assert.DoesNotContain(set.Facets, f => f.FacetId == "scene.weather");
        Assert.Equal(4, set.Facets.Count);
    }

    [Fact]
    public async Task Without_profile_returns_null_and_touches_nothing()
    {
        var s = new Session("s");
        var embed = new FakeEmbeddings(); var presets = new FakePresets();
        Assert.Null(await new RecommendationService(Catalog, embed, presets, new OrchestratorOptions()).BuildAsync(s, Finalized("1girl"), 1, default));
        Assert.Empty(embed.Texts); Assert.Empty(presets.Calls);
    }

    [Fact]
    public async Task Similar_anchor_is_skipped_when_the_subtable_has_no_rows_for_the_facet()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "slippers"));                     // FacetPools 沒設 → 0
        await svc.BuildAsync(s, Finalized("masterpiece"), 1, default);
        Assert.Empty(presets.SimilarCalls);
    }

    /// <summary>近似錨：同一片段被兩個 facet 撈到時取較小的距離，記在那個 facet 名下（設計 §6.1）。</summary>
    [Fact]
    public async Task Similar_results_from_several_facets_merge_by_min_distance_and_credit_the_closer_facet()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "slippers"), ("clothing.head", "beret"));
        presets.FacetPools["clothing.footwear"] = 338; presets.FacetPools["clothing.head"] = 662;
        presets.SimilarScript["clothing.footwear"] = new[] { Set(2, "x", 0.20, ("clothing.footwear", "sandals"), ("clothing.upper", "a")), Set(3, "y", 0.22, ("clothing.footwear", "flip flops"), ("clothing.upper", "c")) };
        presets.SimilarScript["clothing.head"] = new[] { Set(2, "x", 0.10, ("clothing.head", "cap"), ("clothing.upper", "a")), Set(4, "z", 0.15, ("clothing.head", "hat"), ("clothing.upper", "b")) };
        var d = (await svc.BuildAsync(s, Finalized("masterpiece"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(new long[] { 2, 4 }, d.Sets.Select(x => x.PresetId));                      // 2 取兩邊較小的 0.10，排第一
        Assert.Equal(0.10, d.Sets[0].Dist);
        Assert.Equal(new[] { "beret" }, d.Sets[0].AnchorTags);                                    // 記在距離較小的 clothing.head 名下
        Assert.All(d.Sets, x => Assert.Equal("similar", x.Reason));
    }

    [Fact]
    public async Task Facet_whose_tags_normalize_to_nothing_is_not_embedded_as_an_anchor()
    {
        // Review Focus 4
        var (svc, s, embed, presets) = Make(("clothing.footwear", "( :1.2)"));
        presets.FacetPools["clothing.footwear"] = 338;
        await svc.BuildAsync(s, Finalized("masterpiece"), 1, default);
        Assert.Equal(new[] { "一個少女穿涼鞋" }, embed.Texts);
        Assert.Empty(presets.SimilarCalls);
        Assert.Null(RecommendationService.SimilarAnchorText(s, "clothing.footwear"));
        Assert.Null(RecommendationService.SimilarAnchorText(s, "clothing.head"));                 // 沒有 FacetTags
    }
```

- `Final_card_uses_the_similar_tier_when_the_literal_anchor_has_no_hits`：把 `var (svc, s, _, presets)` 改成 `var (svc, s, embed, presets)`，最後加一行 `Assert.Equal(new[] { "一個少女穿涼鞋", "slippers" }, embed.Texts);                        // 錨向量與查詢向量同一次 embed`（原本在被刪掉的追問卡測試裡）。

`AgenticOrchestratorTests.cs`：
- `StubRecommendations.BuildAsync` 與 `ObservingRecommendations.BuildAsync` 的第二個參數型別改成 `FinalizedOutcome outcome`（`StubRecommendations` 的 `impl(outcome)` 照舊）。
- 刪掉 `Adoption_turn_while_collecting_can_end_in_ask`（採用只會在定稿後發生）。
- 下面五個測試原本跑「`SetProfile`＋`AskUser`」，改成在已定稿的 session 上重新定稿。每個都把
  ```csharp
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");
  ```
  換成
  ```csharp
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        var events = await h.RunAsync("背景改成黃昏");
  ```
  適用：`Recommendations_event_follows_final_and_is_audited`、`Slate_recommendations_audit_batch_and_each_set`（它沒有 `var events =`，就寫 `await h.RunAsync("背景改成黃昏");`）、`Recommendation_failure_does_not_roll_back_the_turn`、`Recommendation_timeout_is_audited_as_timeout`。
  `Recommendation_failure_does_not_roll_back_the_turn` 裡的 `Assert.Equal(1, h.Session.AskCount);` 改成 `Assert.Equal("masterpiece, 1girl", h.Session.LastFinal!.Positive);   // 新的定稿已成立`。
- `Retrieval_off_never_calls_recommendations` 改成：

```csharp
    [Fact]
    public async Task Retrieval_off_never_calls_recommendations()
    {
        var h = new Harness();
        var stub = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(SomeRecommendations(1)));
        h.Recommendations = stub;
        var off = new Session("off", retrievalEnabled: false);
        MakeFinalized(off);
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        h.GuardChat.Then(FakeChatCompletion.Text(OkVerdict)); h.ClassifierChat.Then(FakeChatCompletion.Text(OkVerdict));
        var events = new List<AgentEvent>();
        await foreach (var e in h.Build().RunTurnAsync(off, new TurnInput("背景改成黃昏"), default)) events.Add(e);
        Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal(0, stub.Calls);
        Assert.Empty(events.OfType<RecommendationsEvent>());
    }
```

`EndpointTests.cs`：
- `StubRecommendations.BuildAsync` 第二個參數型別改成 `FinalizedOutcome outcome`。
- `Adopt_composes_the_user_sentence_and_streams_it`、`Adopt_with_text_runs_the_adoption_and_ignores_the_text`、`Adopt_rejects_bad_requests` 裡的 `var s = PortraitSession();` 改成 `var s = FinalizedSession();`（採用要定稿後；`FinalizedSession` 也是 portrait、facet 全 missing，組出的採用句不變）。
- `Adopt_is_409_when_retrieval_is_off_or_profile_is_unset` 改名 `Adopt_is_409_when_retrieval_is_off_or_not_finalized`，最後一行斷言改成 `Assert.Contains("定稿後才能採用組合", (await r2.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);`。

`SystemPromptBuilderTests.cs` 的 `Flow_rule_tells_the_model_how_to_handle_an_adoption_message`：刪掉這三行斷言與它們上面那行註解
```csharp
        // 在追問卡上採用時 session 還在收集：照第 1 條走，定稿閘門才不會擋（全分支審查 #1）
        Assert.Contains("然後照第 1 條判斷", prompt);
        Assert.Contains("不要再問剛採用的那些 facet", prompt);
        Assert.Contains("否則直接 `FinalizePrompt`", prompt);
```
換成
```csharp
        // 2026-10-05 起只有定稿卡推薦：採用一定在定稿之後，直接重新定稿（先確認再動手設計 §8）
        Assert.Contains("然後直接 `FinalizePrompt` 重新定稿", prompt);
```

- [ ] **Step 5: 跑後端測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS（integration 測試 Skip）。

- [ ] **Step 6: 前端**

`components/AskCard.vue` 整檔換成（拿掉推薦條與用不到的 props）：

```vue
<template>
  <div data-card="ask" class="relative rounded-lg border border-rule bg-surface py-4 pl-5 pr-4">
    <span class="absolute inset-y-3 left-0 w-[3px] rounded-full bg-yellow" aria-hidden="true" />
    <p class="text-sm leading-6">{{ data.preamble }}</p>
    <section v-for="a in data.asks" :key="a.dimension" class="mt-4">
      <h3 class="flex items-center gap-1.5 text-xs font-bold">
        <span class="inline-block h-2 w-2 rounded-[1px] bg-yellow" aria-hidden="true" />{{ s.dimensionLabels[a.dimension] ?? a.dimension }}
      </h3>
      <p class="mt-1 text-sm leading-6">{{ a.question }}</p>
      <OptionChips class="mt-2" :options="a.options" :dimension="a.dimension" />
    </section>
    <p class="mt-4 text-[11px] text-muted">選項會填進下面的輸入框，可以多選、可以再改，按送出才會送。</p>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
// 2026-10-05 起追問卡不推薦（先確認再動手設計 §8）
defineProps<{ data: Extract<FinalData, { kind: 'ask' }> }>()
const s = useSessionStore()
</script>
```

`components/ChatStream.vue`：`<AskCard v-if="e.data.kind === 'ask'" :data="e.data" :turn-index="e.turnIndex" :recommendations="e.recommendations ?? null" />` 改成 `<AskCard v-if="e.data.kind === 'ask'" :data="e.data" />`。

`lib/adopt.ts`：刪掉 `latestRecommendableTurn` 整個函式與它的註解，以及只有它用到的 `import type { Entry } from './reducer'`。

`stores/session.ts`：
- `import { latestRecommendableTurn as latestRecommendableTurnOf, adoptPlaceholder } from '../lib/adopt'` 改成 `import { adoptPlaceholder } from '../lib/adopt'`。
- 刪掉 `latestRecommendableTurn` 的 computed 與它的註解。
- `openAdopt` 與 `nextBatch` 裡的 `turnIndex !== latestRecommendableTurn.value` 改成 `turnIndex !== latestFinalizedTurn.value`。
- `return { … }` 裡的 `adoptTarget, latestRecommendableTurn, openAdopt, closeAdopt, adopt,` 改成 `adoptTarget, openAdopt, closeAdopt, adopt,`。

`components/RecommendationStrip.vue`：
- `const adoptable = computed(() => s.latestRecommendableTurn === props.turnIndex && !s.busy)` 改成 `const adoptable = computed(() => s.latestFinalizedTurn === props.turnIndex && !s.busy)`。
- props 註解「只有最新一張追問卡／定稿卡可以採用與換一批」改成「只有最新一張定稿卡可以採用與換一批（2026-10-05 起只有定稿卡推薦）」。

`tests/adopt.test.ts`：import 拿掉 `latestRecommendableTurn`，刪掉整個 `describe('latestRecommendableTurn', …)`；檔案若因此不再用 `Entry`，一併拿掉 `import type { Entry } from '../lib/reducer'`。

- [ ] **Step 7: 跑前端測試與建置**

Run（在 `src/PromptCopilot.Frontend`，先照 Global Constraints export PATH）：`npm test && npm run build`
Expected: 測試全過，build 成功。

- [ ] **Step 8: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests src/PromptCopilot.Frontend
git commit -m "feat: recommend only on final cards and accept adoptions only after finalize" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: 待確認的資料與驗證

spec §3.4、§3.5。

**Files:**
- Create: `src/PromptCopilot.Api/Sessions/Confirmation.cs`
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/IPromptOrchestrator.cs`
- Test: Create `src/PromptCopilot.Api.Tests/Sessions/ConfirmationTests.cs`；Modify `src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs`

**Interfaces:**
- Produces:
  - `record PendingConfirmation(int TurnIndex, string Message, IReadOnlyList<string> Choices, bool AutoComplete)`
  - `record ConfirmRequest(int TurnIndex, int? Choice = null)`
  - `record ConfirmedInput(PendingConfirmation Pending, int? Choice)`，屬性 `string? ChosenText`、`string Text`
  - `static class ConfirmValidator { const string AcceptText = "對，就這樣"; static ConfirmedInput Validate(Session s, ConfirmRequest req) }`
  - `class ConfirmValidationException(int status, string message)`，屬性 `int Status`
  - `Session.PendingConfirmation { get; }`、`Session.SetPendingConfirmation(PendingConfirmation p)`、`Session.ClearPendingConfirmation()`
  - `SessionSnapshot` 最後多一個 `PendingConfirmation? PendingConfirmation = null`
  - `TurnInput(string Text, Adoption? Adoption = null, LedgerEntry? AdoptedPreset = null, bool SafetyOn = true, ConfirmedInput? Confirmed = null)`

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Sessions/ConfirmationTests.cs`：

```csharp
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Sessions;

/// <summary>先確認再動手設計 §3.5：按確認的請求怎麼驗。</summary>
public class ConfirmationTests
{
    private static Session WithPending(int turn, params string[] choices)
    {
        var s = new Session("s");
        s.SetPendingConfirmation(new PendingConfirmation(turn, "我理解的畫面：一位女士站在雨夜街頭。", choices, false));
        return s;
    }

    [Fact]
    public void Proposal_without_choices_takes_a_null_choice_and_says_ok()
    {
        var c = ConfirmValidator.Validate(WithPending(3), new ConfirmRequest(3, null));
        Assert.Null(c.Choice);
        Assert.Null(c.ChosenText);
        Assert.Equal("對，就這樣", c.Text);
        Assert.Equal(ConfirmValidator.AcceptText, c.Text);
    }

    [Fact]
    public void Choice_picks_the_interpretation_text()
    {
        var c = ConfirmValidator.Validate(WithPending(3, "換掉飲料，改拿雨傘", "換掉相機，改拿雨傘"), new ConfirmRequest(3, 1));
        Assert.Equal(1, c.Choice);
        Assert.Equal("換掉相機，改拿雨傘", c.ChosenText);
        Assert.Equal("換掉相機，改拿雨傘", c.Text);
    }

    [Fact]
    public void No_pending_is_409()
    {
        var e = Assert.Throws<ConfirmValidationException>(() => ConfirmValidator.Validate(new Session("s"), new ConfirmRequest(1, null)));
        Assert.Equal(409, e.Status);
        Assert.Equal("沒有待確認的內容", e.Message);
    }

    /// <summary>Review Focus 3：另一個分頁、或已經動過手的舊卡。</summary>
    [Fact]
    public void Older_card_is_409()
    {
        var e = Assert.Throws<ConfirmValidationException>(() => ConfirmValidator.Validate(WithPending(5), new ConfirmRequest(3, null)));
        Assert.Equal(409, e.Status);
        Assert.Equal("只有最新一張確認卡可以按", e.Message);
    }

    [Theory]
    [InlineData(new string[0], 0)]                // 沒有選項卻帶 choice
    [InlineData(new[] { "a", "b" }, null)]        // 有選項卻沒選
    [InlineData(new[] { "a", "b" }, 2)]
    [InlineData(new[] { "a", "b" }, -1)]
    public void Choice_that_does_not_fit_the_card_is_400(string[] choices, int? choice)
    {
        var e = Assert.Throws<ConfirmValidationException>(() => ConfirmValidator.Validate(WithPending(3, choices), new ConfirmRequest(3, choice)));
        Assert.Equal(400, e.Status);
    }
}
```

`SessionTests.cs`，在 `Snapshot_restore_reverts_everything_including_history_and_ledger` 之後加：

```csharp
    /// <summary>先確認再動手設計 §3.4：待確認進快照。確認輪失敗不留半張卡；動手輪失敗時卡片回來可以再按。</summary>
    [Fact]
    public void Pending_confirmation_is_part_of_the_snapshot()
    {
        var s = New();
        var p = new PendingConfirmation(2, "m", new[] { "a", "b" }, true);
        s.SetPendingConfirmation(p);
        var snap = s.Snapshot();
        s.ClearPendingConfirmation();
        Assert.Null(s.PendingConfirmation);
        s.Restore(snap);
        Assert.Same(p, s.PendingConfirmation);

        var empty = New().Snapshot();
        s.Restore(empty);
        Assert.Null(s.PendingConfirmation);
    }
```

- [ ] **Step 2: 跑測試，確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~ConfirmationTests|FullyQualifiedName~Pending_confirmation_is_part_of_the_snapshot"`
Expected: 編譯失敗（`PendingConfirmation` 等型別不存在）。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Sessions/Confirmation.cs`：

```csharp
namespace PromptCopilot.Api.Sessions;

/// <summary>確認輪留下的待確認（先確認再動手設計 §3.4）。只有最新一筆可以按；動手輪（含採用輪）開始時清掉。
/// AutoComplete：確認輪的輸入分類器判定「隨便／直接給我」；按下確認後才打開 AutoFill。</summary>
public sealed record PendingConfirmation(int TurnIndex, string Message, IReadOnlyList<string> Choices, bool AutoComplete);

/// <summary>POST /messages 的 confirm 欄位。Choice：沒有選項的提案卡是 null。</summary>
public sealed record ConfirmRequest(int TurnIndex, int? Choice = null);

/// <summary>按下的那張卡與選的解讀。Text 是使用者泡泡與 history 裡的那句：沒有選項是接受句，有選項是那個選項的原文。</summary>
public sealed record ConfirmedInput(PendingConfirmation Pending, int? Choice)
{
    public string? ChosenText => Choice is { } c ? Pending.Choices[c] : null;
    public string Text => ChosenText ?? ConfirmValidator.AcceptText;
}

/// <summary>Status：端點要回的 HTTP 狀態碼（409 或 400）。</summary>
public sealed class ConfirmValidationException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public static class ConfirmValidator
{
    public const string AcceptText = "對，就這樣";

    /// <summary>設計 §3.5：沒有待確認、不是最新一張 → 409；choice 跟卡片對不上 → 400。純函式，不動 session。</summary>
    public static ConfirmedInput Validate(Session s, ConfirmRequest req)
    {
        var p = s.PendingConfirmation ?? throw new ConfirmValidationException(409, "沒有待確認的內容");
        if (p.TurnIndex != req.TurnIndex) throw new ConfirmValidationException(409, "只有最新一張確認卡可以按");
        if (p.Choices.Count == 0)
        {
            if (req.Choice is not null) throw new ConfirmValidationException(400, "這張確認卡沒有選項，choice 要是 null");
        }
        else if (req.Choice is not { } c || c < 0 || c >= p.Choices.Count)
            throw new ConfirmValidationException(400, $"choice 要是 0 到 {p.Choices.Count - 1}");
        return new ConfirmedInput(p, req.Choice);
    }
}
```

`Session.cs`：
- `SessionSnapshot` 的參數列最後加 `, PendingConfirmation? PendingConfirmation = null`（加預設值，既有呼叫端不用改）。
- 在 `public FinalPrompt? LastFinal { get; private set; }` 之後加：

```csharp
    /// <summary>確認輪留下的待確認（先確認再動手設計 §3.4）。進快照：確認輪失敗不留半張卡，動手輪失敗時卡片回來可以再按。</summary>
    public PendingConfirmation? PendingConfirmation { get; private set; }

    public void SetPendingConfirmation(PendingConfirmation p) => PendingConfirmation = p;
    public void ClearPendingConfirmation() => PendingConfirmation = null;
```

- `Snapshot()` 的最後一個引數 `new List<Adoption>(Adoptions))` 改成 `new List<Adoption>(Adoptions), PendingConfirmation)`。
- `Restore` 的 `Ledger = s.Ledger.Clone(); LastFinal = s.LastFinal; TurnIndex = s.TurnIndex;` 之後加 `PendingConfirmation = s.PendingConfirmation;`。

`IPromptOrchestrator.cs` 的 `TurnInput`：

```csharp
/// <summary>一輪的輸入：使用者原文；或伺服器組好的採用句加上要記帳的採用與片段（設計 §6）；
/// 或按下確認卡（Confirmed，先確認再動手設計 §3.5：Text 是「對，就這樣」或選的那句）。
/// SafetyOn=false：這一輪關掉程式端審查（測試用；端點只在 Safety:AllowDisable 開著時收）。Gemini 自己的攔截不受影響。</summary>
public sealed record TurnInput(string Text, Adoption? Adoption = null, LedgerEntry? AdoptedPreset = null, bool SafetyOn = true, ConfirmedInput? Confirmed = null);
```

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Sessions src/PromptCopilot.Api/Orchestration/IPromptOrchestrator.cs src/PromptCopilot.Api.Tests/Sessions
git commit -m "feat(api): pending confirmation on the session and confirm request validation" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `Confirm` 工具、`Discuss` 不再改 facet、輸出審查

spec §3.2、§3.3、§4。這一步還不改工具清單，`Confirm` 只是存在、測得到。

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs`（`ToolNames`）
- Modify: `src/PromptCopilot.Api/Plugins/Contracts.cs`、`Plugins/DialogPlugin.cs`
- Modify: `src/PromptCopilot.Api/Streaming/AgentEvent.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`（`ToFinal`）
- Modify: `src/PromptCopilot.Api/Filters/OutputSafetyFilter.cs`、`Filters/TurnContextExtensions.cs`
- Test: `src/PromptCopilot.Api.Tests/Plugins/DialogPluginTests.cs`、`Filters/FiltersTests.cs`、`Llm/GeminiToolDeclarationTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `PendingConfirmation`、`Session.SetPendingConfirmation`。
- Produces:
  - `ToolNames.Confirm = "Confirm"`；`ToolNames.Terminal` 含它；`ToolNames.ProposeAlways = { SearchSimilarPrompts, SearchPresets, Confirm }`
  - `DialogPlugin.Confirm(string message, string[]? choices = null)`、`DialogPlugin.MaxChoiceChars = 40`
  - `record ConfirmOutcome(string Message, IReadOnlyList<string> Choices) : TurnOutcome`
  - `FinalEvent` 最後多 `IReadOnlyList<string>? Choices = null`；`ToFinal(ConfirmOutcome)` → `kind = "confirm"`

- [ ] **Step 1: 寫失敗的測試**

`DialogPluginTests.cs` 加：

```csharp
    // ---- Confirm（先確認再動手設計 §3.2）----

    [Fact]
    public void Confirm_without_choices_records_the_pending_proposal_and_changes_nothing()
    {
        var (p, turn, s) = Make();
        var before = new Dictionary<string, FacetState>(s.FacetStates);
        var r = p.Confirm("  我理解的畫面：一位金色短髮的中年女士站在雨夜的霓虹街頭。  ");
        Assert.Equal("ok", r);
        var o = Assert.IsType<ConfirmOutcome>(turn.Outcome);
        Assert.Equal("我理解的畫面：一位金色短髮的中年女士站在雨夜的霓虹街頭。", o.Message);
        Assert.Empty(o.Choices);
        var pending = s.PendingConfirmation!;
        Assert.Equal(1, pending.TurnIndex);
        Assert.Equal(o.Message, pending.Message);
        Assert.Empty(pending.Choices);
        Assert.False(pending.AutoComplete);
        Assert.Equal(before, s.FacetStates);                                         // 確認不改任何 facet
    }

    [Fact]
    public void Confirm_keeps_two_to_four_trimmed_distinct_choices()
    {
        var (p, turn, s) = Make();
        var r = p.Confirm("她兩手已經拿著相機和飲料，再拿雨傘會拿不下。你想要哪一種？",
            new[] { " 換掉飲料，改拿雨傘 ", "換掉相機，改拿雨傘", "", "換掉飲料，改拿雨傘", "三樣都拿（可能不自然）" });
        Assert.Equal("ok", r);
        Assert.Equal(new[] { "換掉飲料，改拿雨傘", "換掉相機，改拿雨傘", "三樣都拿（可能不自然）" }, Assert.IsType<ConfirmOutcome>(turn.Outcome).Choices);
        Assert.Equal(3, s.PendingConfirmation!.Choices.Count);
    }

    [Theory]
    [InlineData("   ", null)]                                     // 空 message
    [InlineData("m", new[] { "只有一個" })]
    [InlineData("m", new[] { "a", " a " })]                        // 去重後剩 1 個
    [InlineData("m", new[] { "a", "b", "c", "d", "e" })]
    public void Confirm_rejects_bad_arguments_without_an_outcome(string message, string[]? choices)
    {
        var (p, turn, s) = Make();
        Assert.StartsWith("錯誤", p.Confirm(message, choices));
        Assert.Null(turn.Outcome);
        Assert.Null(s.PendingConfirmation);
        Assert.NotEmpty(turn.Rejections);
    }

    [Fact]
    public void Confirm_rejects_a_choice_longer_than_40_chars()
    {
        var (p, turn, _) = Make();
        var r = p.Confirm("m", new[] { new string('長', 41), "短" });
        Assert.StartsWith("錯誤", r);
        Assert.Contains("40", r);
        Assert.Null(turn.Outcome);
    }

    [Fact]
    public void Confirm_remembers_that_the_user_asked_to_auto_complete()
    {
        var s = new Session("s");
        var turn = new TurnContext(s, 4, GuardResult.Ok(true), ToolNames.ProposeAlways, Channel.CreateUnbounded<AgentEvent>().Writer);
        Assert.Equal("ok", new DialogPlugin(turn, Catalog, O).Confirm("我會直接定稿，風格補成寫實攝影。"));
        Assert.True(s.PendingConfirmation!.AutoComplete);
        Assert.Equal(4, s.PendingConfirmation.TurnIndex);
    }

    [Fact]
    public void Confirm_works_before_a_profile_is_set()
    {
        var (p, turn, _) = Make(profile: false);
        Assert.Equal("ok", p.Confirm("我理解的畫面：一隻貓。"));
        Assert.IsType<ConfirmOutcome>(turn.Outcome);
    }

    [Fact]
    public void Confirm_outcome_becomes_a_confirm_final_event()
    {
        var ev = AgenticOrchestrator.ToFinal(new ConfirmOutcome("m", new[] { "a", "b" }));
        Assert.Equal("confirm", ev.Kind);
        Assert.Equal("m", ev.Message);
        Assert.Equal(new[] { "a", "b" }, ev.Choices);
    }

    /// <summary>設計 §3.3：Discuss 不分狀態都不能改 facet，否則它是繞過確認的後門。</summary>
    [Fact]
    public void Discuss_while_collecting_rejects_changed_facets()
    {
        var (p, turn, s) = Make();
        var r = p.Discuss("好的", States(("pose.gaze", "covered")));
        Assert.Equal("錯誤：Discuss 不能改 facet 狀態；使用者要改畫面時，請用 Confirm 跟他確認", r);
        Assert.Null(turn.Outcome);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.Equal(0, s.DiscussStreak);
    }
```

同檔既有的 `Discuss_when_finalized_rejects_changed_facets` 與 `Discuss_when_finalized_rejects_facets_changed_earlier_in_the_same_turn`：`Assert.Contains("FinalizePrompt", r);` 改成 `Assert.Contains("Confirm", r);`。

`FiltersTests.cs` 加：

```csharp
    /// <summary>先確認再動手設計 §3.2：確認卡的正文與每個選項都會送到使用者眼前。</summary>
    [Fact]
    public void OutputTextFor_confirm_takes_message_and_every_choice()
    {
        var text = TurnContextExtensions.OutputTextFor("Confirm", new KernelArguments
        {
            ["message"] = "她兩手已經拿著相機和飲料，你想要哪一種？",
            ["choices"] = Escaped(new[] { "換掉飲料，改拿雨傘", "三樣都拿（可能不自然）" }),
        });
        Assert.Contains("相機和飲料", text);
        Assert.Contains("換掉飲料，改拿雨傘", text);
        Assert.Contains("三樣都拿", text);
        Assert.DoesNotContain("\\u", text);
    }

    [Fact]
    public async Task OutputSafety_checks_confirm()
    {
        var (k, turn, _) = Kernel();
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"露骨"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        await f.OnAutoFunctionInvocationAsync(Ctx(k, "Confirm", ("message", "一段露骨的確認")), Next(() => called = true));
        Assert.False(called);
        Assert.IsType<BlockedOutcome>(turn.Outcome);
    }
```

`GeminiToolDeclarationTests.cs` 加：

```csharp
    /// <summary>先確認再動手設計 §3.2：Confirm 的 choices 有預設值，不能在 required 裡（Discuss.options 踩過：沒預設值時 Gemini 照描述省略會丟 KernelException）。
    /// Gemini 回的 functionCall 不帶 choices 也要綁得起來。</summary>
    [Fact]
    public async Task Confirm_declares_choices_as_optional_and_binds_without_them()
    {
        var catalog = FacetCatalogTests.Real();
        var session = new Session("p");
        var tools = ToolNames.ProposeAlways;
        var turn = new TurnContext(session, 1, GuardResult.Ok(false), tools, Channel.CreateUnbounded<AgentEvent>().Writer);
        var kernel = new Kernel();
        AgentKernelFactory.AddFiltered(kernel, "Dialog", new DialogPlugin(turn, catalog, new OrchestratorOptions()), tools);

        var bodies = new List<string>();
        var reply = Call("Dialog_Confirm", """{"message":"我理解的畫面：一位女士站在雨夜街頭。"}""");
        var chat = new GoogleAIGeminiChatCompletionService("gemini-x", "fake", GoogleAIVersion.V1_Beta,
            new HttpClient(new GeminiRoleFixHandler(new Canned(new Queue<string>(new[] { reply, Text }), bodies))));
        var history = new ChatHistory("sys");
        history.AddUserMessage("u");
        await chat.GetChatMessageContentsAsync(history, new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() }, kernel);

        var decl = JsonDocument.Parse(bodies[0]).RootElement
            .GetProperty("tools")[0].GetProperty("functionDeclarations")
            .EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Dialog_Confirm");
        var parameters = decl.GetProperty("parameters");
        Assert.Equal(new[] { "message" }, parameters.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("array", parameters.GetProperty("properties").GetProperty("choices").GetProperty("type").GetString());
        Assert.Empty(Assert.IsType<ConfirmOutcome>(turn.Outcome).Choices);
    }
```

（這個檔案已經有 `using PromptCopilot.Api.Plugins;` 等；缺 `using PromptCopilot.Api.Configuration;` 就補上，`OrchestratorOptions` 在那裡。）

- [ ] **Step 2: 跑測試，確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 編譯失敗（`Confirm`、`ConfirmOutcome`、`ToolNames.ProposeAlways`、`FinalEvent.Choices` 不存在）。

- [ ] **Step 3: 實作**

`ToolSetBuilder.cs` 的 `ToolNames`：

```csharp
public static class ToolNames
{
    public const string SearchSimilarPrompts = "SearchSimilarPrompts";
    public const string SearchPresets = "SearchPresets";
    public const string SetProfile = "SetProfile";
    public const string SetFacetStates = "SetFacetStates";
    public const string AskUser = "AskUser";
    public const string Discuss = "Discuss";
    public const string FinalizePrompt = "FinalizePrompt";
    public const string RequestSaveConsent = "RequestSaveConsent";
    public const string Confirm = "Confirm";

    /// <summary>動手輪一定有的：會改畫面的工具與檢索（先確認再動手設計 §3.1）。</summary>
    public static readonly IReadOnlySet<string> Always = new HashSet<string>
        { SearchSimilarPrompts, SearchPresets, SetProfile, SetFacetStates, FinalizePrompt };
    /// <summary>確認輪一定有的：只能確認與檢索，沒有任何會改畫面的工具。</summary>
    public static readonly IReadOnlySet<string> ProposeAlways = new HashSet<string>
        { SearchSimilarPrompts, SearchPresets, Confirm };
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>
        { AskUser, Discuss, FinalizePrompt, RequestSaveConsent, Confirm };
}
```

`Contracts.cs`，在 `MessageOutcome` 之後加：

```csharp
/// <summary>確認卡（先確認再動手設計 §3.2）：Choices 沒有歧義時是空的。</summary>
public sealed record ConfirmOutcome(string Message, IReadOnlyList<string> Choices) : TurnOutcome;
```

`DialogPlugin.cs`，類別開頭加常數，在 `AskUser` 之前加 `Confirm`：

```csharp
    /// <summary>prompt 要求 20 字；伺服器放寬到 40，模型稍微超過不必白燒一次重叫（設計 §3.2）。</summary>
    public const int MaxChoiceChars = 40;

    [KernelFunction(ToolNames.Confirm)]
    [Description("確認：使用者說了會改動畫面的話（描述題材、回答追問、要求修改、說隨便），先用這個跟他確認；他按下確認卡的按鈕後，下一輪你才能動手。message 寫你理解的畫面或打算怎麼改；要求跟現有內容衝突或有多種解讀時，choices 給 2–4 個解讀讓他選，沒有歧義就省略。")]
    // choices 排在最後而且有預設值：SK 只看「有沒有預設值」決定必填與否（同 Discuss.options）
    public string Confirm(
        [Description("繁中 1–3 句：你理解的畫面，或打算怎麼改；有 choices 時寫成問題")] string message,
        [Description("0 個或 2–4 個解讀，繁中陳述句、20 字以內、彼此互斥；沒有歧義就省略")] string[]? choices = null)
    {
        var text = message?.Trim() ?? "";
        if (text.Length == 0) return Reject("message 不可為空");
        var kept = (choices ?? Array.Empty<string>()).Select(c => c?.Trim() ?? "").Where(c => c.Length > 0).Distinct().ToList();
        if (kept.Count == 1) return Reject("choices 只有 1 個；沒有歧義就省略 choices，有歧義就給 2–4 個");
        if (kept.Count > AskCleaner.MaxOptions) return Reject($"choices 有 {kept.Count} 個，最多 {AskCleaner.MaxOptions} 個");
        if (kept.FirstOrDefault(c => c.Length > MaxChoiceChars) is { } tooLong) return Reject($"choices「{tooLong}」超過 {MaxChoiceChars} 字");
        S.SetPendingConfirmation(new PendingConfirmation(turn.TurnIndex, text, kept, turn.Guard.WantsAutoComplete));
        turn.Outcome = new ConfirmOutcome(text, kept);
        return "ok";
    }

    private string Reject(string reason)
    {
        turn.Rejections.Add($"Confirm：{reason}");
        return $"錯誤：{reason}";
    }
```

`Discuss` 的閘門（原本只在 `Finalized` 擋）：

```csharp
        // 不分狀態：確認之前不能改畫面，Discuss 帶著改過的狀態等於繞過確認（先確認再動手設計 §3.3）
        if (S.Profile is not null && StatesDiffer(facetStates))
            return "錯誤：Discuss 不能改 facet 狀態；使用者要改畫面時，請用 Confirm 跟他確認";
```

（`Discuss` 的 `[Description]` 句尾加「不能改 facet 狀態。」）

`AgentEvent.cs` 的 `FinalEvent` 參數列最後加 `, IReadOnlyList<string>? Choices = null`，並在 record 上方的註解補一句「Choices（2026-10-05）：確認卡的解讀，沒有歧義時是空陣列。」

`AgenticOrchestrator.ToFinal` 加一個分支：

```csharp
        ConfirmOutcome c => new FinalEvent("confirm", Message: c.Message, Choices: c.Choices),
```

`OutputSafetyFilter.cs`：`Guarded` 加 `ToolNames.Confirm`，類別註解改成「四個會把文字送到使用者眼前的 tool 都檢（Confirm 是 2026-10-05 加的）」。

`TurnContextExtensions.OutputTextFor` 的 `switch` 加：

```csharp
            case ToolNames.Confirm:
                parts.Add(Field(args, "message"));
                foreach (var c in Node(args, "choices") as JsonArray ?? new JsonArray()) parts.Add(Text(c));
                break;
```

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests
git commit -m "feat(api): Confirm tool, Discuss never changes facets, output review covers Confirm" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 兩種輪：確認輪與動手輪

spec §3.1、§3.4、§3.5、§6、§9。這一步之後「打字就直接改畫面」在程式上不可能發生。

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/RecommendationService.cs`（`JoinedUserText`）
- Modify: `src/PromptCopilot.Api/Configuration/Options.cs`、`src/PromptCopilot.Api/appsettings.json`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/ToolSetBuilderTests.cs`（整檔換）、`Orchestration/AgenticOrchestratorTests.cs`、`Orchestration/RecommendationServiceTests.cs`、`Configuration/OptionsTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `PendingConfirmation`、`ConfirmedInput`、`ConfirmValidator.AcceptText`、`TurnInput.Confirmed`、`Session.ClearPendingConfirmation`；Task 3 的 `ToolNames.Confirm`、`ToolNames.ProposeAlways`、`ConfirmOutcome`；Task 1 的 `FinalizeArgs()`、`MakeFinalized(Session)`。
- Produces:
  - `enum TurnKind { Propose, Act }`（在 `ToolSetBuilder.cs`）
  - `ToolSetBuilder.Build(Session s, TurnKind kind, bool wantsAutoComplete, OrchestratorOptions o)`
  - 測試 harness：`TurnInput Harness.ConfirmInput(string message = …, IReadOnlyList<string>? choices = null, int? choice = null, bool autoComplete = false, Session? session = null)`、`Task<List<AgentEvent>> Harness.ActAsync(string message = …, IReadOnlyList<string>? choices = null, int? choice = null, bool autoComplete = false)`

- [ ] **Step 1: 重寫 `ToolSetBuilderTests.cs`（先寫測試）**

整檔換成：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Orchestration;

public class ToolSetBuilderTests
{
    private static readonly OrchestratorOptions O = new() { MaxAskCount = 2, MaxDiscussStreak = 8 };
    private static Session S(int asks = 0, int streak = 0, bool finalized = false)
    {
        var s = new Session("s");
        for (var i = 0; i < asks; i++) s.RecordAsk();
        for (var i = 0; i < streak; i++) s.RecordDiscuss();
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
        return s;
    }
    private static IReadOnlySet<string> Propose(Session s, bool auto = false) => ToolSetBuilder.Build(s, TurnKind.Propose, auto, O);
    private static IReadOnlySet<string> Act(Session s, bool auto = false) => ToolSetBuilder.Build(s, TurnKind.Act, auto, O);
    private static readonly string[] PictureTools = { ToolNames.SetProfile, ToolNames.SetFacetStates, ToolNames.AskUser, ToolNames.FinalizePrompt };

    /// <summary>先確認再動手設計 §3.1：打字的那一輪只能確認、討論、檢索。</summary>
    [Fact]
    public void Propose_turn_has_confirm_discuss_and_search_but_nothing_that_changes_the_picture()
    {
        var t = Propose(S());
        Assert.Equal(new HashSet<string> { ToolNames.Confirm, ToolNames.Discuss, ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }, t);
        Assert.Empty(t.Intersect(PictureTools));
    }

    [Fact]
    public void Act_turn_has_the_picture_tools_and_no_confirm_or_discuss()
    {
        var t = Act(S());
        Assert.True(ToolNames.Always.IsSubsetOf(t));
        Assert.Contains(ToolNames.AskUser, t);
        Assert.DoesNotContain(ToolNames.Confirm, t);
        Assert.DoesNotContain(ToolNames.Discuss, t);
        Assert.DoesNotContain(ToolNames.RequestSaveConsent, t);
    }

    [Fact]
    public void Ask_disappears_at_max_ask_count() => Assert.DoesNotContain(ToolNames.AskUser, Act(S(asks: 2)));

    [Fact]
    public void Discuss_disappears_at_max_streak_while_collecting() => Assert.DoesNotContain(ToolNames.Discuss, Propose(S(streak: 8)));

    [Fact]
    public void Finalized_propose_turn_keeps_discuss_and_offers_save_consent()
    {
        var s = S(streak: 8); s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
        var t = Propose(s);
        Assert.Contains(ToolNames.Discuss, t);
        Assert.Contains(ToolNames.RequestSaveConsent, t);
        Assert.Contains(ToolNames.Confirm, t);
    }

    [Fact]
    public void Finalized_act_turn_has_no_ask_and_no_save_consent()
    {
        var t = Act(S(finalized: true));
        Assert.DoesNotContain(ToolNames.AskUser, t);
        Assert.DoesNotContain(ToolNames.RequestSaveConsent, t);
        Assert.Contains(ToolNames.FinalizePrompt, t);
    }

    /// <summary>釘住 Build 的 `Status == Finalized ||` 左分支：RecordFinalize 會把 streak 歸零，
    /// 所以只有用 Restore 造出「已定稿且 streak 未歸零」的狀態才咬得到這個分支。</summary>
    [Fact]
    public void Discuss_stays_when_finalized_with_nonzero_streak_via_restore()
    {
        var s = new Session("s");
        s.Restore(new SessionSnapshot(SessionStatus.Finalized, null, 0, 8, false,
            new(), new(), 0, new PresetLedger(), new FinalPrompt("p", "n", "t", "i"), 0, new(), new()));
        Assert.Contains(ToolNames.Discuss, Propose(s));
    }

    /// <summary>「隨便」的確認輪連 Discuss 都沒有：只能確認要補什麼（設計 §6.3）。</summary>
    [Fact]
    public void Auto_complete_propose_turn_leaves_only_confirm_and_search() =>
        Assert.Equal(new HashSet<string> { ToolNames.Confirm, ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }, Propose(S(), auto: true));

    [Fact]
    public void Auto_complete_act_turn_drops_ask()
    {
        var t = Act(S(), auto: true);
        Assert.DoesNotContain(ToolNames.AskUser, t);
        Assert.Contains(ToolNames.FinalizePrompt, t);
    }

    [Fact]
    public void Exhausted_collecting_act_turn_has_only_always_tools() => Assert.Equal(ToolNames.Always, Act(S(asks: 2, streak: 8)));

    /// <summary>計畫 §4.1：off 的 session 是量測用的對照組。兩種輪都只拿掉兩個檢索工具，其餘規則照舊。</summary>
    [Fact]
    public void Retrieval_off_removes_both_search_tools_in_both_kinds_and_nothing_else()
    {
        foreach (var kind in new[] { TurnKind.Propose, TurnKind.Act })
        {
            var on = ToolSetBuilder.Build(S(), kind, false, O);
            var off = ToolSetBuilder.Build(new Session("s", retrievalEnabled: false), kind, false, O);
            Assert.Equal(on.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet(), off);
        }
        Assert.True(ToolNames.Always.IsSubsetOf(Act(S())));   // Always 本身不動
    }

    [Fact]
    public void Retrieval_off_with_auto_complete_act_turn_leaves_only_state_tools_and_finalize() =>
        Assert.Equal(new HashSet<string> { ToolNames.SetProfile, ToolNames.SetFacetStates, ToolNames.FinalizePrompt },
            Act(new Session("s", retrievalEnabled: false), auto: true));

    [Fact]
    public void Retrieval_mode_defaults_on_and_survives_restore()
    {
        var s = new Session("s");
        Assert.True(s.RetrievalEnabled); Assert.Equal("on", s.RetrievalMode);
        var off = new Session("s", retrievalEnabled: false);
        off.Restore(s.Snapshot());
        Assert.False(off.RetrievalEnabled); Assert.Equal("off", off.RetrievalMode);
    }
}
```

- [ ] **Step 2: orchestrator harness 與新測試**

`AgenticOrchestratorTests.cs` 的 `Harness`，在 `RunAsync(TurnInput input, …)` 之後加：

```csharp
        /// <summary>動手輪的輸入：先在 session 放一筆待確認（確認輪會留下的樣子），再組按下按鈕的那一輪。
        /// choices 有給時 choice 預設選第 0 個。</summary>
        public TurnInput ConfirmInput(string message = "我理解的畫面：一個銀髮少女。", IReadOnlyList<string>? choices = null, int? choice = null,
            bool autoComplete = false, Session? session = null)
        {
            var s = session ?? Session;
            var c = choices ?? Array.Empty<string>();
            var pending = new PendingConfirmation(s.TurnIndex, message, c, autoComplete);
            s.SetPendingConfirmation(pending);
            var confirmed = new ConfirmedInput(pending, c.Count == 0 ? null : choice ?? 0);
            return new TurnInput(confirmed.Text, Confirmed: confirmed);
        }

        /// <summary>跑一輪動手輪。RunAsync 照樣排一個分類器判定：動手輪用不到，留在佇列裡無妨；要驗「沒呼叫分類器」看 GuardChat.Calls。</summary>
        public Task<List<AgentEvent>> ActAsync(string message = "我理解的畫面：一個銀髮少女。", IReadOnlyList<string>? choices = null, int? choice = null,
            bool autoComplete = false) =>
            RunAsync(ConfirmInput(message, choices, choice, autoComplete));
```

檔尾（`Summary_log_covers_guard_blocks_and_failures_too` 之後）加：

```csharp
    // ---- 先確認再動手（2026-10-05）----

    /// <summary>設計 §3.1：打字的那一輪只拿得到確認、討論與檢索；想直接改畫面也沒有工具可叫。</summary>
    [Fact]
    public async Task Text_turn_only_offers_confirm_discuss_and_search()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal(new[] { "Confirm", "Discuss" }, k!.Plugins["Dialog"].Select(f => f.Name).Order());
            Assert.False(k.Plugins.Contains("Session"));
            return new[] { await Invoke(hist, k, "Dialog", "Confirm", new { message = "我理解的畫面：一位金色短髮的中年女士站在雨夜的霓虹街頭。" }) };
        });
        var events = await h.RunAsync("一位金色短髮的中年女士站在雨夜的霓虹街頭");

        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("confirm", final.Kind);
        Assert.Empty(final.Choices!);
        Assert.Null(Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.Null(h.Session.Profile);                                                // 確認前什麼都沒動
        Assert.Equal(1, h.Session.PendingConfirmation!.TurnIndex);
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"outcome\":\"ConfirmOutcome\"", completed.PayloadJson!);
        Assert.Contains("\"confirmChoices\":0", completed.PayloadJson!);
        Assert.DoesNotContain("\"confirmed\"", completed.PayloadJson!);
    }

    /// <summary>設計 §3.5：按下確認的那一輪不跑輸入分類器、session 事件帶「對，就這樣」，只拿得到動手的工具，待確認被消耗掉。</summary>
    [Fact]
    public async Task Confirmed_turn_skips_the_guard_and_gets_the_tools_that_change_the_picture()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal("對，就這樣", hist.Last(m => m.Role == AuthorRole.User).Content);
            Assert.Contains("SetProfile", k!.Plugins["Session"].Select(f => f.Name));
            Assert.DoesNotContain("Confirm", k.Plugins["Dialog"].Select(f => f.Name));
            Assert.DoesNotContain("Discuss", k.Plugins["Dialog"].Select(f => f.Name));
            await Invoke(hist, k, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.ActAsync();

        Assert.Empty(h.GuardChat.Calls);
        Assert.Equal("對，就這樣", Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.Equal("ask", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Null(h.Session.PendingConfirmation);
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"confirmed\":{\"turnIndex\":0}", completed.PayloadJson!);      // 沒有選項：choice 省略
        Assert.Equal("對，就這樣", completed.RawInput);
    }

    [Fact]
    public async Task Choosing_an_interpretation_sends_that_sentence_and_audits_the_choice()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal("換掉飲料，改拿雨傘", hist.Last(m => m.Role == AuthorRole.User).Content);
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        var events = await h.ActAsync("她兩手已經拿著相機和飲料，你想要哪一種？", new[] { "換掉相機，改拿雨傘", "換掉飲料，改拿雨傘" }, choice: 1);

        Assert.Equal("換掉飲料，改拿雨傘", Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Contains("\"confirmed\":{\"turnIndex\":0,\"choice\":1}", Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed").PayloadJson!);
    }

    /// <summary>設計 §3.4：「隨便」在確認輪只記在待確認裡；按下確認才打開 AutoFill，動手輪也不給 AskUser。</summary>
    [Fact]
    public async Task Auto_complete_is_switched_on_only_by_the_confirmed_turn()
    {
        var h = new Harness();
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":true,"reason":"ok"}"""));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal(new[] { "Confirm" }, k!.Plugins["Dialog"].Select(f => f.Name));
            return new[] { await Invoke(hist, k, "Dialog", "Confirm", new { message = "我會直接定稿，風格補成寫實攝影。" }) };
        });
        await h.RunAsync("隨便，直接給我");
        Assert.False(h.Session.AutoFill);
        Assert.True(h.Session.PendingConfirmation!.AutoComplete);

        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.DoesNotContain("AskUser", k!.Plugins["Dialog"].Select(f => f.Name));
            await Invoke(hist, k, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        var events = await h.RunAsync(new TurnInput(ConfirmValidator.AcceptText, Confirmed: new ConfirmedInput(h.Session.PendingConfirmation!, null)));
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.True(h.Session.AutoFill);
    }

    /// <summary>設計 §6.1：確認輪預算用完只能強制確認，不能強制定稿（那等於跳過確認）。</summary>
    [Fact]
    public async Task Budget_exhausted_in_a_text_turn_forces_confirm_with_only_that_tool()
    {
        var h = new Harness();
        h.Chat.ThenAsync((hist, k) =>
        {
            k!.Turn().Outcome = new BudgetExhaustedOutcome();
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { FakeChatCompletion.Text("") });
        })
        .ThenAsync(async (hist, k) =>
        {
            Assert.Equal(AuthorRole.System, hist.Last().Role);
            Assert.Contains("Confirm", hist.Last().Content!);
            Assert.Single(k!.Plugins);
            Assert.Equal("Confirm", Assert.Single(k.Plugins["Dialog"]).Name);
            return new[] { await Invoke(hist, k, "Dialog", "Confirm", new { message = "我理解的畫面：一個女生。" }) };
        });
        var events = await h.RunAsync("一個女生");
        Assert.Equal("confirm", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Null(h.Session.Profile);
        Assert.Equal(SessionStatus.Collecting, h.Session.Status);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Tool_Budget_Exhausted");
    }

    /// <summary>Review Focus 5：確認卡出現後打「好」是新的一輪確認，不是動手；討論不清掉待確認，新的確認卡取代舊的。</summary>
    [Fact]
    public async Task Typed_ok_after_a_confirm_card_is_another_text_turn()
    {
        var h = new Harness();
        h.Session.SetPendingConfirmation(new PendingConfirmation(0, "我理解的畫面：一個女生。", Array.Empty<string>(), false));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.False(k!.Plugins.Contains("Session"));
            return new[] { await Invoke(hist, k, "Dialog", "Discuss", DiscussArgs("要套用的話請按確認卡上的按鈕。")) };
        });
        await h.RunAsync("好");
        Assert.Equal("我理解的畫面：一個女生。", h.Session.PendingConfirmation!.Message);

        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：一個穿紅衣的女生。" }) });
        await h.RunAsync("衣服要紅色");
        Assert.Equal(2, h.Session.PendingConfirmation!.TurnIndex);
        Assert.Equal("我理解的畫面：一個穿紅衣的女生。", h.Session.PendingConfirmation.Message);
    }

    /// <summary>設計 §3.4：採用輪也是動手輪，清掉待確認。</summary>
    [Fact]
    public async Task Adoption_clears_a_pending_confirmation()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Session.SetPendingConfirmation(new PendingConfirmation(0, "m", Array.Empty<string>(), false));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        await h.RunAsync(AdoptInput());
        Assert.Null(h.Session.PendingConfirmation);
    }

    /// <summary>補救提示只列這一輪實際有的收尾工具（設計 §5.3）。</summary>
    [Fact]
    public async Task Retry_reminder_names_only_this_turns_terminal_tools()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("好的。"))
              .ThenAsync(async (hist, k) =>
              {
                  Assert.Contains("你必須呼叫 Confirm、Discuss 之一", hist.Last().Content!);
                  Assert.DoesNotContain("FinalizePrompt", hist.Last().Content!);
                  return new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) };
              });
        await h.RunAsync("寫實跟動漫差在哪");
    }
```

（`AdoptInput()` 是同檔已有的 helper；它定義在檔案中段，C# 裡順序無妨。）

`RecommendationServiceTests.cs` 加（Review Focus 1）：

```csharp
    /// <summary>Review Focus 1：「對，就這樣」不是畫面描述，不能進查詢向量；選了解讀的句子是使用者選定的內容，照算。</summary>
    [Fact]
    public void Query_text_skips_the_plain_accept_sentence_but_keeps_a_chosen_interpretation()
    {
        var h = new ChatHistory();
        h.AddUserMessage("一位女士拿著相機和飲料");
        h.AddUserMessage("對，就這樣");
        h.AddUserMessage("讓她拿雨傘");
        h.AddUserMessage("換掉飲料，改拿雨傘");
        Assert.Equal("一位女士拿著相機和飲料\n讓她拿雨傘\n換掉飲料，改拿雨傘", RecommendationService.JoinedUserText(h));
    }
```

`OptionsTests.cs`：`Assert.Equal(10, o.HistoryTurns);` 改成 `Assert.Equal(20, o.HistoryTurns);`。

- [ ] **Step 3: 既有 orchestrator 測試改走動手輪**

下面這些測試原本「打字就 `SetProfile`／`AskUser`／`FinalizePrompt`」，閘門上線後那些工具只在動手輪。逐一改：

| 測試 | 改法 |
| :--- | :--- |
| `Happy_path_ask_emits_final_ask_and_commits_session` | `await h.RunAsync("一個銀髮少女")` → `await h.ActAsync()` |
| `Llm_exception_rolls_back_everything_and_emits_error` | `await h.RunAsync("一個少女")` → `await h.ActAsync()`；最後加 `Assert.NotNull(h.Session.PendingConfirmation);                    // 動手輪失敗：待確認回來，卡片可以再按` |
| `Enumerator_disposal_waits_for_the_turn_to_finish_unwinding` | `h.Build().RunTurnAsync(h.Session, new TurnInput("一個少女"), default)` → `h.Build().RunTurnAsync(h.Session, h.ConfirmInput(), default)` |
| `No_outcome_is_protocol_violation_and_rolls_back` | `await h.RunAsync("一個少女")` → `await h.ActAsync()`；最後加 `Assert.NotNull(h.Session.PendingConfirmation);                    // Review Focus 4` |
| `Turn_timeout_rolls_back_and_emits_timeout_error` | `await h.RunAsync("一個少女")` → `await h.ActAsync()` |
| `Second_turn_replaces_system_message_instead_of_stacking` | 第一個 `await h.RunAsync("一個銀髮少女");` → `await h.ActAsync();` |
| `Retry_request_after_a_tool_call_ends_with_the_tool_result` | `await h.RunAsync("一個少女")` → `await h.ActAsync()` |
| `Forced_finalize_reminder_does_not_outlive_the_turn` | `await h.RunAsync("一個少女")` → `await h.ActAsync()` |
| `Budget_exhausted_forces_finalize_with_only_that_tool` | `await h.RunAsync("一個少女")` → `await h.ActAsync()` |
| `Ask_turn_never_calls_recommendations`（Task 1） | `await h.RunAsync("一個銀髮少女")` → `await h.ActAsync()` |
| `Recommendations_event_follows_final_and_is_audited`、`Slate_recommendations_audit_batch_and_each_set`、`Recommendation_failure_does_not_roll_back_the_turn`、`Recommendation_timeout_is_audited_as_timeout`（Task 1 改過） | `h.RunAsync("背景改成黃昏")` → `h.ActAsync("我會把背景改成黃昏。")` |
| `Retrieval_off_never_calls_recommendations` | `new TurnInput("背景改成黃昏")` → `h.ConfirmInput("我會把背景改成黃昏。", session: off)` |
| `Each_turn_ends_with_one_summary_log_line` | 第一個 `await h.RunAsync("一個銀髮少女");` → `await h.ActAsync();` |

這兩個測試要測的就是確認輪，改成以 `Confirm` 收尾：

`Safety_off_runs_the_turn_despite_an_nsfw_input_verdict_and_audits_it`：`h.Chat.ThenAsync(...)` 換成

```csharp
        h.Chat.ThenAsync(async (hist, k) =>
        {
            seen = k!.Turn().SafetyOn;
            return new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：穿 bikini 的女生。" }) };
        });
```

並把 `Assert.Equal("ask", Assert.Single(events.OfType<FinalEvent>()).Kind);` 改成 `Assert.Equal("confirm", Assert.Single(events.OfType<FinalEvent>()).Kind);`。

`Ordinary_turn_session_event_has_no_text_and_no_adoption_in_audit`：`h.Chat.ThenAsync(...)` 換成

```csharp
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：一個銀髮少女。" }) });
```

其餘測試（`Discuss` 類、純文字補救、guard、上游攔截、逾時診斷、採用）不用改：它們本來就是確認輪能做的事，或是採用（動手輪）。

- [ ] **Step 4: 跑測試，確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 編譯失敗（`TurnKind`、新的 `ToolSetBuilder.Build` 簽名不存在）。

- [ ] **Step 5: 實作**

`ToolSetBuilder.cs`，`ToolNames` 之後：

```csharp
/// <summary>先確認再動手設計 §3.1：使用者打字是確認輪；按確認卡或採用是動手輪。</summary>
public enum TurnKind { Propose, Act }

/// <summary>主規格 §4.3 + 多輪 §3.3 + 先確認再動手設計 §3.1。LLM 不需要「遵守」規則：違規的選項根本不在清單裡。
/// 確認輪沒有任何會改畫面的工具；動手輪沒有 Confirm 與 Discuss，只能照確認的內容動手。</summary>
public static class ToolSetBuilder
{
    public static IReadOnlySet<string> Build(Session s, TurnKind kind, bool wantsAutoComplete, OrchestratorOptions o)
    {
        var tools = new HashSet<string>(kind == TurnKind.Act ? ToolNames.Always : ToolNames.ProposeAlways);
        if (!s.RetrievalEnabled)
        {
            // 對照組：模型拿不到檢索工具，就不會「自稱」借用。Always／ProposeAlways 是一般情況的宣告，這裡減，不改它。
            tools.Remove(ToolNames.SearchPresets);
            tools.Remove(ToolNames.SearchSimilarPrompts);
        }
        if (kind == TurnKind.Act)
        {
            if (!wantsAutoComplete && s.Status == SessionStatus.Collecting && s.AskCount < o.MaxAskCount)
                tools.Add(ToolNames.AskUser);
            return tools;
        }
        if (!wantsAutoComplete && (s.Status == SessionStatus.Finalized || s.DiscussStreak < o.MaxDiscussStreak))
            tools.Add(ToolNames.Discuss);
        if (s.Status == SessionStatus.Finalized)
            tools.Add(ToolNames.RequestSaveConsent);
        return tools;
    }
}
```

`AgenticOrchestrator.ExecuteTurnAsync`：

開頭：

```csharp
        var text = input.Text;
        var turnIndex = session.TurnIndex + 1;
        // 先確認再動手（設計 §3.1）：使用者打字是確認輪；按確認卡或採用是動手輪
        var kind = input.Confirmed is null && input.Adoption is null ? TurnKind.Propose : TurnKind.Act;
        // 動手輪的使用者句是伺服器組的（採用句、確認句）：session 事件帶回去，前端拿它換掉泡泡的暫代字
        writer.TryWrite(new SessionEvent(session.Id, turnIndex, session.Status.ToString(), kind == TurnKind.Act ? text : null));
```

guard：

```csharp
            // ① 輸入側：不進 kernel、不計任何東西。按確認那一輪不再檢查（設計 §3.5）：內容是上一輪模型的確認文字，
            //    已過輸出審查；原話在確認輪已過輸入審查。「隨便」沿用確認輪當時的判斷。
            var g = input.Confirmed is { } confirmed
                ? GuardResult.Ok(confirmed.Pending.AutoComplete)
                : await guard.CheckAsync(text, input.SafetyOn, ct);
```

setup（取代原本的 `var tools = …` 與 `if (g.WantsAutoComplete) session.AutoFill = true;` 兩行）：

```csharp
            // 動手輪消耗待確認；快照已取，這一輪失敗時會跟著回來，卡片可以再按（設計 §3.4）
            if (kind == TurnKind.Act) session.ClearPendingConfirmation();
            var tools = ToolSetBuilder.Build(session, kind, g.WantsAutoComplete, options);
            // 「隨便」在確認輪只記進待確認，按下確認的動手輪才打開（設計 §3.4）
            if (kind == TurnKind.Act && g.WantsAutoComplete) session.AutoFill = true;
```

純文字補救的提示（取代寫死四個工具名的那句）：

```csharp
                var retryText = await CallWithReminderAsync(turn, kernel,
                    $"你必須呼叫 {string.Join("、", ToolNames.Terminal.Where(tools.Contains).Order())} 之一來結束這一輪，不要只回純文字。", tct);
```

預算用盡：

```csharp
            if (turn.Outcome is BudgetExhaustedOutcome)
            {
                await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Tool_Budget_Exhausted", version, text, JsonSerializer.Serialize(new { turn.ToolCalls }, Json)));
                // 確認輪強制收尾也只能確認：強制定稿等於跳過確認（設計 §6.1）
                if (kind == TurnKind.Act) await ForcedFinishAsync(turn, ToolNames.FinalizePrompt, ForcedFinalizeReminder, tct);
                else await ForcedFinishAsync(turn, ToolNames.Confirm, ForcedConfirmReminder, tct);
            }
```

`if (turn.Outcome is null or BudgetExhaustedOutcome) throw new ProtocolViolationException("強制定稿後仍無定稿");` 的訊息改成 `"強制收尾後仍沒有結果"`。

`Turn_Completed` 的 payload，在 `("outcome", turn.Outcome.GetType().Name),` 之後加 `("confirmChoices", turn.Outcome is ConfirmOutcome co ? (object)co.Choices.Count : null),`；在 `("adoption", …)` 之前加：

```csharp
                    ("confirmed", input.Confirmed is null ? null : (object)Fields(("turnIndex", input.Confirmed.Pending.TurnIndex), ("choice", input.Confirmed.Choice))),
```

`ForcedFinalizeAsync` 換成：

```csharp
    private const string ForcedFinalizeReminder = "tool 呼叫預算已用盡。請立即以現有資訊呼叫 FinalizePrompt 定稿，不要再檢索。facetStates 依使用者原話標記：使用者講過的 facet 標 covered，真的沒講的才是 missing，其餘 missing 的 facet 留白。";
    private const string ForcedConfirmReminder = "tool 呼叫預算已用盡。請立即以目前的理解呼叫 Confirm 跟使用者確認，不要再檢索。";

    /// <summary>主規格 §4.6：預算耗盡後只掛一個收尾工具再跑一次；kernel 不掛 budget filter，否則第一個 call 又被擋。
    /// 動手輪掛 FinalizePrompt，確認輪掛 Confirm（先確認再動手設計 §6.1）。</summary>
    private async Task ForcedFinishAsync(TurnContext turn, string tool, string reminder, CancellationToken ct)
    {
        turn.Outcome = null;
        turn.ForcedFinalize = tool == ToolNames.FinalizePrompt;   // 定稿閘門放行：只剩 FinalizePrompt，擋下去這一輪就沒有出口
        var kernel = kernelFactory(turn, new HashSet<string> { tool }, false);
        await CallWithReminderAsync(turn, kernel, reminder, ct);
    }
```

`RecommendationService.JoinedUserText`：

```csharp
    /// <summary>本 session 使用者講過的原話依序串接；伺服器組的採用句與接受句「對，就這樣」不算（那不是描述）。
    /// 選了解讀的確認句（「換掉飲料，改拿雨傘」）照算：那是使用者選定的內容（先確認再動手設計 Review Focus 1）。</summary>
    public static string JoinedUserText(ChatHistory history) =>
        string.Join("\n", history
            .Where(m => m.Role == AuthorRole.User && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content!.Trim())
            .Where(t => !t.StartsWith(AdoptionPrefix, StringComparison.Ordinal) && t != ConfirmValidator.AcceptText));
```

`Options.cs`：`public int HistoryTurns { get; set; } = 10;` 改成

```csharp
    /// <summary>保留最近幾則使用者訊息的輪次。2026-10-05 由 10 改 20：每個要求多一則「對，就這樣」，維持原本記得的要求數（先確認再動手設計 §2）。</summary>
    public int HistoryTurns { get; set; } = 20;
```

`appsettings.json`：`"HistoryTurns": 10` 改成 `"HistoryTurns": 20`。

- [ ] **Step 6: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS。（`AgenticOrchestratorGeminiTests` 的「你必須呼叫」照樣在補救提示裡。）

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests
git commit -m "feat(api): split turns into confirm and act; only a pressed confirm card unlocks the picture tools" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: 流程 prompt 拆成確認輪與動手輪

spec §5。

**Files:**
- Create: `src/PromptCopilot.Api/Prompts/flow-propose.md`、`src/PromptCopilot.Api/Prompts/flow-act.md`
- Modify: `src/PromptCopilot.Api/Prompts/system.md`
- Modify: `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`（呼叫 `prompts.Build`）
- Modify: `src/PromptCopilot.Api/Program.cs`、`src/PromptCopilot.Api/PromptCopilot.Api.csproj`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`、`Orchestration/AgenticOrchestratorTests.cs`（harness 建構子與兩個新測試）、`Llm/GeminiContractTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `TurnKind`；Task 2 的 `ConfirmedInput`。
- Produces:
  - `SystemPromptBuilder(FacetCatalog catalog, OrchestratorOptions options, string promptsDir)`（第三個參數從 system.md 的路徑改成資料夾）
  - 常數 `SystemPromptBuilder.TemplateFile = "system.md"`、`ProposeFlowFile = "flow-propose.md"`、`ActFlowFile = "flow-act.md"`
  - `(string Prompt, string Version) Build(Session s, IReadOnlySet<string> tools, TurnKind kind, ConfirmedInput? confirmed = null)`

- [ ] **Step 1: 寫失敗的測試**

`SystemPromptBuilderTests.cs`：
- `Make` 改成 `new(Catalog, new OrchestratorOptions { OfferedOptionsLimit = offeredLimit }, Path.Combine(AppContext.BaseDirectory, "Prompts"));`
- 檔內其餘每個 `.Build(<session>, <tools>)` 呼叫都在第二個引數後加 `, TurnKind.Act`（既有斷言都是動手輪的流程或共用段落）。
- `Flow_rule_tells_the_model_how_to_handle_an_adoption_message` 的兩處 `"6. 使用者訊息以「採用〈」開頭時"` 改成 `"5. 使用者訊息以「採用〈」開頭時"`。
- `Version_is_stable_across_line_ending_styles` 整個換成：

```csharp
    /// <summary>版本 hash 是 eval 對得上 prompt 的鑰匙。樣板的換行在別台機器上可能被 git 轉成
    /// CRLF，組出來的 Facts 也用 Environment.NewLine——同一份 prompt 就會有兩個 hash。三個樣板檔都要顧到。</summary>
    [Fact]
    public void Version_is_stable_across_line_ending_styles()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Prompts");
        var crlfDir = Path.Combine(Path.GetTempPath(), $"prompts-crlf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(crlfDir);
        try
        {
            foreach (var f in new[] { SystemPromptBuilder.TemplateFile, SystemPromptBuilder.ProposeFlowFile, SystemPromptBuilder.ActFlowFile })
                File.WriteAllText(Path.Combine(crlfDir, f), File.ReadAllText(Path.Combine(src, f)).Replace("\r\n", "\n").Replace("\n", "\r\n"));
            var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
            foreach (var kind in new[] { TurnKind.Propose, TurnKind.Act })
            {
                var lfBuilt = Make().Build(s, ToolNames.Always, kind);
                var crlfBuilt = new SystemPromptBuilder(Catalog, new OrchestratorOptions(), crlfDir).Build(s, ToolNames.Always, kind);
                Assert.Equal(lfBuilt.Version, crlfBuilt.Version);
                Assert.DoesNotContain("\r\n", crlfBuilt.Prompt);
            }
        }
        finally { Directory.Delete(crlfDir, recursive: true); }
    }
```

- 新增：

```csharp
    // ---- 先確認再動手（2026-10-05）----

    [Fact]
    public void Propose_prompt_tells_the_model_to_confirm_and_gives_the_umbrella_example()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose);
        Assert.Contains("確認輪", prompt);
        Assert.Contains("「換掉飲料，改拿雨傘」", prompt);
        Assert.Contains("不要列「算了不改」", prompt);
        Assert.Contains("他沒有按按鈕，這句不算確認", prompt);
        Assert.DoesNotContain("先 `SetFacetStates`", prompt);              // 動手輪的流程不在確認輪出現
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void Propose_and_act_prompts_have_different_versions()
    {
        var s = new Session("s");
        Assert.NotEqual(Make().Build(s, ToolNames.Always, TurnKind.Propose).Version, Make().Build(s, ToolNames.Always, TurnKind.Act).Version);
    }

    [Fact]
    public void Act_prompt_starts_the_flow_with_the_confirmed_block()
    {
        var pending = new PendingConfirmation(1, "她兩手已經拿著相機和飲料，你想要哪一種？", new[] { "換掉相機，改拿雨傘", "換掉飲料，改拿雨傘" }, false);
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act, new ConfirmedInput(pending, 1));
        var flow = prompt[prompt.IndexOf("## 流程", StringComparison.Ordinal)..prompt.IndexOf("## Facet 四態", StringComparison.Ordinal)];
        Assert.Contains("### 使用者已確認", flow);
        Assert.Contains("她兩手已經拿著相機和飲料，你想要哪一種？", flow);
        Assert.Contains("使用者選的是：換掉飲料，改拿雨傘", flow);
        Assert.Contains("不要加入確認以外的改動", flow);
        Assert.Contains("動手輪", flow);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void Act_prompt_without_a_confirmation_has_no_confirmed_block()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.DoesNotContain("### 使用者已確認", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    /// <summary>Review Focus 2：確認內容是模型與使用者的文字，最後才放進來，裡面的 {{…}} 不能再被展開。</summary>
    [Fact]
    public void Confirmed_text_is_inserted_last_and_never_expanded()
    {
        var pending = new PendingConfirmation(1, "我會把背景改成 {{TOOLS}} 與 {{FACETS}}", new[] { "選 {{SESSION_FACTS}}", "b" }, false);
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act, new ConfirmedInput(pending, 0));
        Assert.Contains("我會把背景改成 {{TOOLS}} 與 {{FACETS}}", prompt);
        Assert.Contains("使用者選的是：選 {{SESSION_FACTS}}", prompt);
    }
```

`AgenticOrchestratorTests.cs`：harness 的 `new SystemPromptBuilder(Catalog, Options, Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md"))` 改成 `new SystemPromptBuilder(Catalog, Options, Path.Combine(AppContext.BaseDirectory, "Prompts"))`；檔尾加：

```csharp
    /// <summary>設計 §5.2：動手輪的 system prompt 帶使用者確認的內容與選的解讀。</summary>
    [Fact]
    public async Task Confirmed_turn_prompt_carries_what_the_user_confirmed()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) =>
        {
            var sys = hist[0].Content!;
            Assert.Contains("### 使用者已確認", sys);
            Assert.Contains("使用者選的是：換掉飲料，改拿雨傘", sys);
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        await h.ActAsync("她兩手已經拿著相機和飲料，你想要哪一種？", new[] { "換掉相機，改拿雨傘", "換掉飲料，改拿雨傘" }, choice: 1);
    }

    [Fact]
    public async Task Text_turn_prompt_is_the_propose_flow()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Contains("確認輪", hist[0].Content!);
            Assert.DoesNotContain("### 使用者已確認", hist[0].Content!);
            return new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：一個女生。" }) };
        });
        await h.RunAsync("一個女生");
    }
```

`GeminiContractTests.cs`（integration，CI 不跑，但要能編譯、語意要對）：`Agent` 裡的 `new SystemPromptBuilder(catalog, new OrchestratorOptions(), Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")).Build(session, tools)` 改成 `new SystemPromptBuilder(catalog, new OrchestratorOptions(), Path.Combine(AppContext.BaseDirectory, "Prompts")).Build(session, tools, TurnKind.Propose)`；兩個測試裡的 `var tools = ToolNames.Always.Union(new[] { ToolNames.AskUser, ToolNames.Discuss }).ToHashSet();` 改成 `var tools = ToolNames.ProposeAlways.Union(new[] { ToolNames.Discuss }).ToHashSet();          // 使用者第一句是確認輪`。

- [ ] **Step 2: 跑測試，確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 編譯失敗（`Build` 沒有 `TurnKind` 參數、常數不存在）。

- [ ] **Step 3: 寫 prompt 檔**

`src/PromptCopilot.Api/Prompts/flow-propose.md`（新）：

```markdown
這一輪是**確認輪**：使用者剛打了一段話。你手上沒有任何會改畫面的工具；會改畫面的話一律先用 `Confirm` 跟他確認，他按下確認卡的按鈕之後，下一輪你才會拿到 `SetProfile`、`SetFacetStates`、`AskUser`、`FinalizePrompt`。

1. **先判斷這句話會不會改畫面。**描述題材、回答追問、要求修改（加東西、拿掉、換風格、換背景）、說「隨便／你決定／直接給我」都會改畫面 → `Confirm`。純提問或討論（「差在哪」「還有別的方向嗎」「為什麼有這個詞」「再多講一點」）不改畫面 → `Discuss`，可以附 0–4 個參考方向，不消耗追問額度。問題裡夾著修改 → `Confirm`，正文順便回答問題。要把定稿存進共享庫 → `RequestSaveConsent`。
2. **`Confirm` 的正文**：繁中 1–3 句，用使用者的說法，不寫英文 tag。
   - 第一次描述題材：複述你理解的畫面——主體，以及他在各面向講到的東西；再用一句話帶過還沒講的面向（例：「風格、鏡頭還沒講」）。不要補他沒講的。
   - 回答追問：講你會怎麼設（例：「我會把風格設成寫實攝影、鏡頭設成低角度」），再說確認之後是「接著問 X、Y」還是「直接定稿，沒講的留白」。
   - 定稿後要改：對照「Session 事實」裡目前的定稿，講清楚改哪裡、從什麼改成什麼。
   - 說隨便／你決定／直接給我：列出你打算補上的具體內容，說明確認後直接定稿。
   - 只有一個提案時寫成陳述句，不用自己問「對嗎」，確認卡的按鈕會幫你問。
3. **什麼時候給 `choices`**：要求跟現有內容衝突，或有多種解讀時，給 2–4 個解讀讓他選，正文寫成問題；沒有歧義就省略 `choices`。
   - 搶同一個位置：雙手已經拿著東西又要拿別的、地點換成另一個地點、時間或光源互斥（正午與霓虹夜景）。
   - 前後矛盾：晴天又下雨、拿掉雨卻還撐著傘。
   - 太模糊：「更有氣質」「換個感覺」→ 2–4 個具體方向。
   - 例：她兩手拿著相機和飲料，使用者說「讓她拿雨傘」→ 正文「她兩手已經拿著相機和飲料，再拿雨傘會拿不下。你想要哪一種？」，`choices`：「換掉飲料，改拿雨傘」「換掉相機，改拿雨傘」「三樣都拿（可能不自然）」。
   - 選項寫成陳述句、20 字以內、彼此互斥；做得到的「全都要」也列出來，註明代價；不要列「算了不改」——不想改的話他不按就好。
4. **使用者打「好」「對」「OK」**：他沒有按按鈕，這句不算確認。再用 `Confirm` 確認一次同樣的內容，正文開頭說「要套用的話請按下面的按鈕」。
5. **每一輪都必須以本輪工具清單裡的 `Confirm`、`Discuss` 或 `RequestSaveConsent` 之一結束。**不要只回純文字。
```

`src/PromptCopilot.Api/Prompts/flow-act.md`（新）：

```markdown
{{CONFIRMED}}這一輪是**動手輪**：使用者按下了確認卡的按鈕，或從定稿卡的推薦裡採用了一套組合。這一輪沒有 `Confirm` 與 `Discuss`，照已確認的內容動手，不要加入確認以外的改動。使用者的原話在先前的對話裡，確認的內容在上面。

1. **第一次描述題材確認後**：先 `SetProfile`（動物歸 object）。接著**先 `SetFacetStates`**，把使用者已經描述到的 facet 標 `covered`，並在 `tags` 附上那一項的英文 tag（例：涼鞋 → `sandals`）；沒講的維持 `missing`，不要猜。{{RETRIEVAL_STEP}}然後：**只要還有 missing 的維度就 `AskUser`**，一次問滿，最多 3 個維度，槓桿大的先問；問不完的，等使用者回答後的下一輪照同樣的判斷再問。missing 的維度指底下還有任何 facet 是 missing 的維度（waived 與有委託 note 的 facet 不算）；使用者只講了一部分的維度也要問剩下的 facet，`missingFacetIds` 只填還缺的那些。**只有**三種情況直接 `FinalizePrompt`：沒有 missing 的維度、使用者說隨便／你決定、或本輪工具清單裡沒有 `AskUser`（追問額度用完）。
2. **回答追問確認後**：用 `SetFacetStates` 套用確認的內容，然後照第 1 條判斷：還有 missing 的維度而且工具清單有 `AskUser` 就 `AskUser`，否則 `FinalizePrompt`。
3. **定稿後的修改確認後**：`FinalizePrompt` 重新定稿，facet 狀態照確認的內容改；確認時選了哪個解讀，就照那個改（例：選了「換掉飲料，改拿雨傘」，就拿掉飲料、加上雨傘、相機留著）。
4. 使用者說「隨便／你決定／直接給我」並確認後，本輪不會有 `AskUser`，直接 `FinalizePrompt` 並補齊所有 missing（就是確認卡上列的那些）。
5. 使用者訊息以「採用〈」開頭時，那是他從定稿卡的推薦裡挑了一套。「照它的」是**取代**：定稿時該 facet 只留括號內的 tag（原字，不改寫），原本的 tag 全部拿掉——從使用者先前的描述翻的、上一版定稿裡屬於這個 facet 的、括號內「取代原本的」後面列的都算；狀態設 `covered`、`tags` 填留下的那些字、note 記「採用知識庫 #編號」。「保留我的」facet 維持原狀。然後直接 `FinalizePrompt` 重新定稿（採用只會發生在定稿之後）。
6. **每一輪都必須以本輪工具清單裡的 `AskUser` 或 `FinalizePrompt` 之一結束。**不要只回純文字。
```

`src/PromptCopilot.Api/Prompts/system.md`：
- 第一段之後加一段：

```markdown
會改動畫面的事，一律先跟使用者確認，他按下確認卡的按鈕之後才動手：使用者打字的那一輪是**確認輪**，按下按鈕（或從推薦裡採用一套組合）的那一輪是**動手輪**。兩種輪拿到的工具不同，以「本輪可用的工具」為準。
```

- `## 流程` 底下第 1–6 條整段刪掉，換成單獨一行 `{{FLOW}}`。
- `## AskUser 與 Discuss 的用法` 改成：

```markdown
## Confirm、AskUser 與 Discuss 的用法

- `Confirm` 是**確認**：使用者說了會改畫面的話，你先講你的理解或打算，他按下按鈕才算數。有歧義時用 `choices` 給解讀讓他選。它不改任何 facet 狀態。
- `AskUser` 是**索取**：我需要你回答才能繼續。一次把目前 missing 的維度問滿，最多 3 個，槓桿大的先問（風格 > 鏡頭 > 場景 > 樣貌 > 動作 > 穿著）；還有剩的下一輪再問。每則 2–4 個**不同方向**的選項（寫實／動漫是不同方向，寫實的兩種說法不是）。`missingFacetIds` 只能填該維度目前 missing 的 facet。
- `Discuss` 是**回應**：這是我對你問題的回答，你可以無視它繼續講別的。`options` 是參考方向，可以是知識庫沒有的方向（`presetId` 留空）。`Discuss` 不能改 facet 狀態。
- `AskUser` 與 `Discuss` 的 `facetStates` 都要帶目前每個 facet 的狀態——那是儀表板同步的唯一來源；`Discuss` 帶的值必須等於現值。
```

（`## Facet 四態`、`## 提示詞規則`、`## 本輪可用的工具`、`## Facet 清單`、`## Session 事實`、`{{OFFERED}}` 不動。）

- [ ] **Step 4: `SystemPromptBuilder` 與接線**

`SystemPromptBuilder.cs`：

```csharp
/// <summary>主規格 §4.9 + 多輪 §6.2。每輪重組；hash 進 audit 讓 eval 對得上 prompt 版本。
/// 流程段依這一輪的種類二選一（先確認再動手設計 §5）：確認輪 flow-propose.md、動手輪 flow-act.md。</summary>
public sealed class SystemPromptBuilder(FacetCatalog catalog, OrchestratorOptions options, string promptsDir)
{
    public const string TemplateFile = "system.md";
    public const string ProposeFlowFile = "flow-propose.md";
    public const string ActFlowFile = "flow-act.md";

    // RetrievalStepOn／RetrievalStepOff／RetrievalRuleOn／RetrievalRuleOff 原樣保留

    private readonly string _template = File.ReadAllText(Path.Combine(promptsDir, TemplateFile));
    private readonly string _proposeFlow = File.ReadAllText(Path.Combine(promptsDir, ProposeFlowFile));
    private readonly string _actFlow = File.ReadAllText(Path.Combine(promptsDir, ActFlowFile));

    public (string Prompt, string Version) Build(Session s, IReadOnlySet<string> tools, TurnKind kind, ConfirmedInput? confirmed = null)
    {
        var prompt = _template
            // 流程段最先換進來：它自己也有 {{RETRIEVAL_STEP}}
            .Replace("{{FLOW}}", kind == TurnKind.Act ? _actFlow : _proposeFlow)
            // 樣板自己的段落先換：之後才塞進來的 session 內容（定稿、選項）就不會誤中這兩個 placeholder。
            .Replace("{{RETRIEVAL_STEP}}", s.RetrievalEnabled ? RetrievalStepOn : RetrievalStepOff)
            .Replace("{{RETRIEVAL_RULE}}", s.RetrievalEnabled ? RetrievalRuleOn : RetrievalRuleOff)
            .Replace("{{TOOLS}}", string.Join("\n", tools.Order().Select(t => $"- `{t}`")))
            .Replace("{{FACETS}}", s.Profile is null ? catalog.PromptListing() : catalog.ProfileListing(s.Profile))
            .Replace("{{SESSION_FACTS}}", Facts(s))
            .Replace("{{OFFERED}}", Offered(s))
            // 確認內容最後才放：它是模型與使用者的文字，放進來之後不能再被任何 placeholder 替換掃到（Review Focus 2）
            .Replace("{{CONFIRMED}}", Confirmed(confirmed))
            // 樣板的換行在別台機器上可能被 git 轉成 CRLF，Facts/Offered 又是用 Environment.NewLine 接的：
            // 同一份 prompt 會算出兩個 hash，eval 就對不回 prompt 版本。統一成 \n 再算。
            .Replace("\r\n", "\n");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return (prompt, Convert.ToHexString(hash)[..12].ToLowerInvariant());
    }

    /// <summary>動手輪流程段開頭的「使用者已確認」區塊（設計 §5.2）；採用輪沒有確認內容，回空字串。</summary>
    private static string Confirmed(ConfirmedInput? c)
    {
        if (c is null) return "";
        var sb = new StringBuilder("### 使用者已確認\n\n");
        sb.Append(c.Pending.Message.Trim()).Append('\n');
        if (c.ChosenText is { } chosen) sb.Append("使用者選的是：").Append(chosen).Append('\n');
        sb.Append("\n這一輪照上面的內容動手，不要加入確認以外的改動。\n\n");
        return sb.ToString();
    }
```

（`Facts`、`Offered` 原樣保留；檔頭補 `using PromptCopilot.Api.Sessions;` 若還沒有。）

`AgenticOrchestrator.cs`：`(var systemPrompt, version) = prompts.Build(session, tools);` 改成 `(var systemPrompt, version) = prompts.Build(session, tools, kind, input.Confirmed);`。

`Program.cs`：`Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")` 改成 `Path.Combine(AppContext.BaseDirectory, "Prompts")`。

`PromptCopilot.Api.csproj`：`<None Update="Prompts\system.md" CopyToOutputDirectory="PreserveNewest" />` 改成 `<None Update="Prompts\*.md" CopyToOutputDirectory="PreserveNewest" />`（三個樣板都要複製到輸出與發佈目錄；測試專案透過專案參考拿到同一批檔案）。

- [ ] **Step 5: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS。另外確認 `src/PromptCopilot.Api.Tests/bin/Debug/net10.0/Prompts/` 底下有三個 `.md`。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests
git commit -m "feat(api): propose and act flows in the system prompt with the confirmed block" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: 端點收 `confirm`

spec §3.5、§4。

**Files:**
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `ConfirmRequest`、`ConfirmValidator`、`ConfirmValidationException`、`TurnInput.Confirmed`。
- Produces: `MessageRequest(string? Text, AdoptRequest? Adopt = null, string? Safety = null, ConfirmRequest? Confirm = null)`；body `{"confirm": {"turnIndex": n, "choice": k | null}}`。

- [ ] **Step 1: 寫失敗的測試**

`EndpointTests.cs`：
- `FakeOrchestrator.RunTurnAsync` 的第一行改成 `yield return new SessionEvent(session.Id, 1, "Collecting", input.Adoption is null && input.Confirmed is null ? null : input.Text);`
- 加 helper 與測試：

```csharp
    /// <summary>直接放一筆待確認：走 HTTP 的話得先跑完一輪真的確認輪。</summary>
    private Session PendingSession(params string[] choices)
    {
        var s = PortraitSession();
        s.TurnIndex = 3;
        s.SetPendingConfirmation(new PendingConfirmation(3, "她兩手已經拿著相機和飲料，你想要哪一種？", choices, false));
        return s;
    }

    /// <summary>先確認再動手設計 §3.5：按下解讀，串流第一個事件與模型看到的都是那一句。</summary>
    [Fact]
    public async Task Confirm_runs_the_turn_with_the_chosen_sentence()
    {
        var s = PendingSession("換掉飲料，改拿雨傘", "換掉相機，改拿雨傘");
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/messages", new { confirm = new { turnIndex = 3, choice = 0 } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("\"text\":\"換掉飲料，改拿雨傘\"", SessionFrameData(body));
        Assert.Contains("echo: 換掉飲料，改拿雨傘", body);
    }

    [Fact]
    public async Task Confirm_without_choices_says_ok_and_ignores_text()
    {
        var s = PendingSession();
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/messages", new { text = "別理我這句", confirm = new { turnIndex = 3 } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("echo: 對，就這樣", body);
        Assert.DoesNotContain("別理我這句", body);
    }

    [Fact]
    public async Task Confirm_is_409_without_a_pending_card_or_for_an_older_one()
    {
        var none = PortraitSession();
        var r1 = await _client.PostAsJsonAsync($"/api/sessions/{none.Id}/messages", new { confirm = new { turnIndex = 0 } });
        Assert.Equal(HttpStatusCode.Conflict, r1.StatusCode);
        Assert.Equal("沒有待確認的內容", (await r1.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);

        var s = PendingSession();
        var r2 = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/messages", new { confirm = new { turnIndex = 2 } });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Equal("只有最新一張確認卡可以按", (await r2.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }

    [Theory]
    [InlineData("""{"confirm":{"turnIndex":3,"choice":0}}""", false)]      // 沒有選項卻帶 choice
    [InlineData("""{"confirm":{"turnIndex":3}}""", true)]                   // 有選項卻沒選
    [InlineData("""{"confirm":{"turnIndex":3,"choice":2}}""", true)]
    [InlineData("""{"confirm":{"turnIndex":3},"adopt":{"presetId":1,"dimension":"scene","take":["scene.location"]}}""", false)]
    public async Task Confirm_is_400_when_the_choice_does_not_fit_or_comes_with_adopt(string json, bool withChoices)
    {
        var s = withChoices ? PendingSession("a", "b") : PendingSession();
        var r = await _client.PostAsync($"/api/sessions/{s.Id}/messages", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
```

- [ ] **Step 2: 跑測試，確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~EndpointTests"`
Expected: 新測試 FAIL（`confirm` 被忽略，拿到 400「text 不可為空」或 200 echo 原文）。

- [ ] **Step 3: 實作**

`SessionEndpoints.cs`：

```csharp
/// <summary>Adopt（2026-09-25）：採用推薦的一套組合；有它時 Text 忽略（設計 §6.1）。
/// Confirm（2026-10-05）：按確認卡（先確認再動手設計 §3.5）；有它時 Text 忽略，不能跟 Adopt 一起送。
/// Safety：on（預設）／off，off 是測試用的審查開關，後端 Safety:AllowDisable 開著才收。</summary>
public sealed record MessageRequest(string? Text, AdoptRequest? Adopt = null, string? Safety = null, ConfirmRequest? Confirm = null);
```

`messages` 端點：

```csharp
            if (req.Adopt is null && req.Confirm is null && string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new ErrorBody("text 不可為空"));
            if (req.Adopt is not null && req.Confirm is not null) return Results.BadRequest(new ErrorBody("confirm 與 adopt 不能同時送"));
```

鎖裡面組 `input` 的地方，在 `if (req.Adopt is { } adopt)` 之前插一個分支（原本的 `if` 變成 `else if`）：

```csharp
                TurnInput input;
                if (req.Confirm is { } confirm)
                {
                    // 拿著鎖再讀待確認：同一張卡連按兩次，第二次看到的已經是被動手輪清掉的狀態
                    try
                    {
                        var c = ConfirmValidator.Validate(s, confirm);
                        input = new TurnInput(c.Text, SafetyOn: safetyOn.Value, Confirmed: c);
                    }
                    catch (ConfirmValidationException e) { return Results.Json(new ErrorBody(e.Message), statusCode: e.Status); }
                }
                else if (req.Adopt is { } adopt)
                {
```

`.WithDescription`：
- body 那段開頭「body：`{"text": "一個銀髮少女站在雨夜的霓虹街頭"}`；或採用推薦的一套組合 …」中，在採用說明的右括號之後、「回應是 `text/event-stream`」之前插入：「；或按確認卡 `{"confirm": {"turnIndex": 5, "choice": 1}}`（2026-10-05，先確認再動手：`turnIndex` 是那張確認卡的輪次，必須是最新一筆待確認；沒有選項的卡 `choice` 給 `null` 或省略，伺服器以「對，就這樣」當使用者訊息，有選項時以那個選項的原文當使用者訊息，`session` 事件的 `text` 帶回這句；有 `confirm` 時 `text` 忽略）」。
- 表格前加一段：「使用者打字的那一輪只會以 `confirm`（確認卡）、`message`（討論）或 `save_consent_requested` 收尾，不會改任何 facet；按下確認卡或採用的那一輪才會追問或定稿。」
- `final` 那一列的 kind 清單加「`confirm` 確認卡（`message`、`choices`：0 或 2–4 個解讀，沒有歧義時是空陣列）」。
- `session` 那一列「`text?` 採用輪才有」改成「`text?` 採用輪與按確認的那一輪才有」。
- 錯誤清單：`400` 加「`confirm` 的 `choice` 跟卡片對不上（有選項沒選、沒選項卻帶、超出範圍）；`confirm` 與 `adopt` 同時送」；`409` 加「`confirm` 但沒有待確認，或不是最新一張確認卡」。

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Endpoints src/PromptCopilot.Api.Tests/Endpoints
git commit -m "feat(api): messages endpoint accepts a pressed confirm card" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: 前端確認卡

spec §7。

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/confirm.ts`、`src/PromptCopilot.Frontend/components/ConfirmCard.vue`、`src/PromptCopilot.Frontend/tests/confirm.test.ts`
- Modify: `src/PromptCopilot.Frontend/types/api.ts`、`composables/useApi.ts`、`lib/reducer.ts`、`stores/session.ts`、`components/ChatStream.vue`、`components/Composer.vue`、`components/FailureNotice.vue`
- Test: `src/PromptCopilot.Frontend/tests/reducer.test.ts`、`tests/safety.test.ts`

**Interfaces:**
- Consumes: 後端 `final.kind = "confirm"`（`message`、`choices`）、`POST messages` 的 `{"confirm": {turnIndex, choice}}`、`session.text`。
- Produces:
  - `types/api.ts`：`FinalData` 多 `{ kind: 'confirm'; message: string; choices: string[] }`；`interface ConfirmRequest { turnIndex: number; choice: number | null }`；`TERMINAL_TOOLS` 含 `'Confirm'`
  - `useApi.ts`：`TurnBody = { text: string } | { adopt: AdoptRequest } | { confirm: ConfirmRequest }`
  - `lib/confirm.ts`：`ACCEPT_TEXT`、`pendingConfirmTurn(transcript: Entry[]): number | null`、`confirmDisplay(data, choice): string`
  - `lib/reducer.ts`：`beginTurn(state, text, opts?: { confirm?: boolean })`；`FailureEntry.confirm?: boolean`；`ChatState.pending.confirm?: boolean`
  - store：`pendingConfirm`（computed）、`confirm(turnIndex, choice)`

- [ ] **Step 1: 寫失敗的測試**

`tests/confirm.test.ts`（新）：

```ts
import { describe, it, expect } from 'vitest'
import { pendingConfirmTurn, confirmDisplay, ACCEPT_TEXT } from '../lib/confirm'
import type { Entry } from '../lib/reducer'

const confirm = (t: number, choices: string[] = []): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'confirm', message: 'm', choices } })
const ask = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'ask', preamble: 'p', asks: [] } })
const fin = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: 't', intentSummary: 'i' } })
const msg = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'message', message: 'm' } })
const consent = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'save_consent_requested' } })
const user: Entry = { kind: 'user', text: 'x' }
const failure: Entry = { kind: 'failure', source: 'error', code: 'turn_failed', message: 'm', originalText: '' }

describe('pendingConfirmTurn', () => {
  it('is the latest confirm card', () => {
    expect(pendingConfirmTurn([user, confirm(1)])).toBe(1)
    expect(pendingConfirmTurn([confirm(1), user, confirm(2)])).toBe(2)
  })

  // 後端同一條規則（設計 §3.4）：討論、存檔確認、失敗都不清掉待確認
  it('survives a discussion, a save consent card and a failed turn', () => {
    expect(pendingConfirmTurn([confirm(1), user, msg(2)])).toBe(1)
    expect(pendingConfirmTurn([confirm(1), user, consent(2)])).toBe(1)
    expect(pendingConfirmTurn([confirm(1), user, failure])).toBe(1)
  })

  it('is gone once an ask or finalized card follows (the turn acted)', () => {
    expect(pendingConfirmTurn([confirm(1), user, ask(2)])).toBeNull()
    expect(pendingConfirmTurn([confirm(1), user, fin(2)])).toBeNull()
    expect(pendingConfirmTurn([fin(1)])).toBeNull()
    expect(pendingConfirmTurn([])).toBeNull()
  })
})

describe('confirmDisplay', () => {
  it('is the accept sentence without choices and the picked choice otherwise', () => {
    expect(ACCEPT_TEXT).toBe('對，就這樣')
    expect(confirmDisplay({ kind: 'confirm', message: 'm', choices: [] }, null)).toBe(ACCEPT_TEXT)
    expect(confirmDisplay({ kind: 'confirm', message: 'm', choices: ['換掉飲料，改拿雨傘', '換掉相機，改拿雨傘'] }, 1)).toBe('換掉相機，改拿雨傘')
  })
})
```

`tests/reducer.test.ts` 檔尾加：

```ts
describe('confirm turns (2026-10-05)', () => {
  const confirmFinal: AgentEvent = { type: 'final', kind: 'confirm', message: '她兩手已經拿著相機和飲料，你想要哪一種？', choices: ['換掉飲料，改拿雨傘', '換掉相機，改拿雨傘'] }

  it('final confirm pushes a confirm entry and settles without touching askCount', () => {
    const s = applyEvent(started(), confirmFinal)
    expect(s.transcript.at(-1)).toEqual({ kind: 'final', turnIndex: 1, data: { kind: 'confirm', message: '她兩手已經拿著相機和飲料，你想要哪一種？', choices: ['換掉飲料，改拿雨傘', '換掉相機，改拿雨傘'] } })
    expect(s.pending?.settled).toBe(true)
    expect(s.askCount).toBe(0)
  })

  // 放回輸入框送出會變成新的意見；卡片還在，再按一次就好
  it('a failed confirm turn keeps no original text and says the card is still there', () => {
    let s = beginTurn({ ...initialState(), sessionId: 's1' }, '對，就這樣', { confirm: true })
    s = applyEvent(s, session(2))
    s = applyEvent(s, { type: 'error', code: 'turn_failed', message: '這一輪失敗' })
    expect(s.transcript.at(-1)).toEqual({ kind: 'failure', source: 'error', code: 'turn_failed', message: '這一輪失敗', originalText: '', confirm: true })
  })

  // Review Focus 3：被 409 擋下的多半是過期的卡，不能說「卡片還在」
  it('an http rejection of a confirm turn does not claim the card is still there', () => {
    const s = failHttp(beginTurn({ ...initialState(), sessionId: 's1' }, '對，就這樣', { confirm: true }), 'http_409', '只有最新一張確認卡可以按')
    expect(s.transcript.at(-1)).toEqual({ kind: 'failure', source: 'http', code: 'http_409', message: '只有最新一張確認卡可以按', originalText: '' })
  })

  it('an ordinary failed turn still keeps the original text', () => {
    let s = beginTurn({ ...initialState(), sessionId: 's1' }, '一個女生')
    s = applyEvent(s, { type: 'error', code: 'turn_failed', message: 'x' })
    expect(s.transcript.at(-1)).toEqual({ kind: 'failure', source: 'error', code: 'turn_failed', message: 'x', originalText: '一個女生' })
  })
})
```

`tests/safety.test.ts` 的 `describe('messageBody', …)` 裡加：

```ts
  it('passes a confirm body through and adds safety off the same way', () => {
    const confirm = { turnIndex: 3, choice: null }
    expect(messageBody({ confirm }, { canDisable: true, off: false })).toEqual({ confirm })
    expect(messageBody({ confirm }, { canDisable: true, off: true })).toEqual({ confirm, safety: 'off' })
  })
```

- [ ] **Step 2: 跑測試，確認失敗**

Run（在 `src/PromptCopilot.Frontend`）：`npm test`
Expected: FAIL（`lib/confirm` 不存在、`beginTurn` 不收第三個參數、型別不認 `confirm`）。

- [ ] **Step 3: 實作**

`types/api.ts`：
- `FinalData` 聯集加一個成員 `| { kind: 'confirm'; message: string; choices: string[] }`，聯集上方加註解 `/** confirm（2026-10-05，先確認再動手設計 §4）：確認卡；choices 沒有歧義時是空陣列。 */`。
- `AdoptRequest` 之後加：

```ts
/** POST /api/sessions/{id}/messages 的 confirm（先確認再動手設計 §3.5）：按下確認卡。沒有選項的卡 choice 是 null。 */
export interface ConfirmRequest { turnIndex: number; choice: number | null }
```

- `TERMINAL_TOOLS` 改成 `new Set(['AskUser', 'Discuss', 'FinalizePrompt', 'RequestSaveConsent', 'Confirm'])`。

`composables/useApi.ts`：import 加 `ConfirmRequest`；`export type TurnBody = { text: string } | { adopt: AdoptRequest } | { confirm: ConfirmRequest }`；`openStream` 的註解「body 是一般訊息或採用」改成「body 是一般訊息、採用或按確認」。

`lib/confirm.ts`（新）：

```ts
import type { Entry } from './reducer'
import type { FinalData } from '../types/api'

/** 沒有選項的確認卡按下去時，伺服器當使用者訊息的那一句（後端 ConfirmValidator.AcceptText）。 */
export const ACCEPT_TEXT = '對，就這樣'

/** 可以按的確認卡（先確認再動手設計 §7）：從尾端往回找，先碰到確認卡就是它；先碰到追問卡或定稿卡代表已經動過手，沒有可按的。
 *  討論泡泡、存檔確認、失敗條目、使用者泡泡、工具卡都不改變待確認——跟後端 §3.4 同一條規則；不一致時伺服器的 409 會說明。 */
export function pendingConfirmTurn(transcript: Entry[]): number | null {
  for (let i = transcript.length - 1; i >= 0; i--) {
    const e = transcript[i]
    if (e.kind !== 'final') continue
    if (e.data.kind === 'confirm') return e.turnIndex
    if (e.data.kind === 'ask' || e.data.kind === 'finalized') return null
  }
  return null
}

/** 按下按鈕時泡泡先顯示的字；伺服器的 session 事件帶回同一句。 */
export function confirmDisplay(data: Extract<FinalData, { kind: 'confirm' }>, choice: number | null): string {
  return choice === null ? ACCEPT_TEXT : data.choices[choice] ?? ACCEPT_TEXT
}
```

`lib/reducer.ts`：
- `FailureEntry` 改成 `export interface FailureEntry { kind: 'failure'; source: 'error' | 'blocked' | 'stream_ended' | 'http'; code: string; message: string; originalText: string; confirm?: boolean }`，上方加註解 `/** confirm（2026-10-05）：按確認的那一輪連線層失敗，確認卡還在、可以再按；畫面不給「放回輸入框」。 */`。
- `ChatState.pending` 的型別改成 `{ text: string; settled: boolean; snapshot: ChatState; confirm?: boolean } | null`。
- `beginTurn`：

```ts
/** 送出當下：先快照（不含 pending），再推 user 條目。斷線可能發生在第一個事件之前，所以不能等 session 事件才快照。
 *  用 JSON 複製而不是 structuredClone：store 傳進來的是 Vue 的 reactive proxy，structuredClone 會丟 DataCloneError；
 *  狀態全是純資料（沒有 undefined／Date／函式），JSON 來回不失真。
 *  opts.confirm：按確認卡的那一輪（先確認再動手設計 §7），失敗時不帶原文。 */
export function beginTurn(state: ChatState, text: string, opts: { confirm?: boolean } = {}): ChatState {
  const snapshot: ChatState = JSON.parse(JSON.stringify({ ...state, pending: null }))
  const pending = opts.confirm ? { text, settled: false, snapshot, confirm: true } : { text, settled: false, snapshot }
  return { ...state, transcript: [...state.transcript, { kind: 'user', text }], pending }
}
```

- `fail`：

```ts
function fail(state: ChatState, source: FailureEntry['source'], code: string, message: string): ChatState {
  const p = state.pending
  const base = p ? p.snapshot : state
  // 按確認的那一輪：原文放回輸入框送出會變成新的意見，所以不帶原文。連線層的失敗後端已回滾、待確認還在，提示再按一次；
  // HTTP 被拒多半是過期的卡，不能這樣說（Review Focus 3）。不寫 confirm: undefined：狀態要能 JSON 來回。
  const failure: FailureEntry = p?.confirm
    ? { kind: 'failure', source, code, message, originalText: '', ...(source === 'http' ? {} : { confirm: true }) }
    : { kind: 'failure', source, code, message, originalText: p?.text ?? '' }
  return { ...base, transcript: [...base.transcript, failure], pending: p ? { ...p, settled: true } : null }
}
```

`stores/session.ts`：
- import 加 `import { pendingConfirmTurn, confirmDisplay } from '../lib/confirm'`；reducer 的 import 加 `type FinalEntry`。
- 在 `latestFinalizedTurn` computed 之後加：

```ts
  /** 可以按的確認卡（先確認再動手設計 §7）；null 表示沒有。輸入框的提示與確認卡的按鈕看它。 */
  const pendingConfirm = computed<number | null>(() => pendingConfirmTurn(state.value.transcript))
```

- `runTurn`：`state.value = beginTurn(state.value, display)` 改成 `state.value = beginTurn(state.value, display, { confirm: 'confirm' in body })`；`if ('adopt' in body || r.status === 403)` 改成 `if ('adopt' in body || 'confirm' in body || r.status === 403)`，上一行註解改成「採用、按確認被拒（400／409）與審查開關被拒（403：後端中途關掉了開放）帶有理由：直接顯示」。
- 在 `adopt` 之後加：

```ts
  /** 按確認卡（先確認再動手設計 §3.5）：泡泡先顯示「對，就這樣」或選的那句，session 事件帶回同一句。只有可按的那張、沒在跑時能按（伺服器也會擋）。 */
  async function confirm(turnIndex: number, choice: number | null) {
    if (busy.value || turnIndex !== pendingConfirm.value) return
    const entry = state.value.transcript.findLast((e): e is FinalEntry => e.kind === 'final' && e.turnIndex === turnIndex)
    if (!entry || entry.data.kind !== 'confirm') return
    await runTurn(confirmDisplay(entry.data, choice), { confirm: { turnIndex, choice } })
  }
```

- `return { … }` 的 `dimensionLabels, latestFinalizedTurn,` 那行加 `pendingConfirm, confirm,`。

`components/ConfirmCard.vue`（新）：

```vue
<template>
  <div data-card="confirm" class="relative rounded-lg border border-rule bg-surface py-4 pl-5 pr-4">
    <span class="absolute inset-y-3 left-0 w-[3px] rounded-full bg-cyan" aria-hidden="true" />
    <p class="whitespace-pre-wrap text-sm leading-6">{{ data.message }}</p>
    <div class="mt-3 flex flex-wrap gap-2">
      <button v-for="(label, i) in buttons" :key="i" type="button" :disabled="!active" data-action="confirm"
              :title="stale ? '已經有新的進展，這張卡不能再按' : undefined"
              class="rounded-md border border-ink/80 px-3 py-1.5 text-sm font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
              @click="s.confirm(turnIndex, data.choices.length ? i : null)">{{ label }}</button>
    </div>
    <p class="mt-3 text-[11px] text-muted">不對的話，直接在下面打字修正。</p>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
import { ACCEPT_TEXT } from '../lib/confirm'
/** 先確認再動手設計 §7：沒有選項時一顆「對，就這樣」，有選項時每個解讀一顆。只有最新一張、之後還沒動手的卡可以按。 */
const props = defineProps<{ data: Extract<FinalData, { kind: 'confirm' }>; turnIndex: number }>()
const s = useSessionStore()
const buttons = computed(() => (props.data.choices.length ? props.data.choices : [ACCEPT_TEXT]))
const stale = computed(() => s.pendingConfirm !== props.turnIndex)
const active = computed(() => !stale.value && !s.busy)
</script>
```

`components/ChatStream.vue`：`<MessageBubble …/>` 那行之前加 `<ConfirmCard v-else-if="e.data.kind === 'confirm'" :data="e.data" :turn-index="e.turnIndex" />`（放在 `AskCard` 之後、`MessageBubble` 之前）。

`components/Composer.vue`：textarea 的 `placeholder="描述你想要的畫面（Enter 送出，Shift+Enter 換行）"` 改成 `:placeholder="s.pendingConfirm !== null ? '按上面的按鈕套用；在這裡打字會當成修正' : '描述你想要的畫面（Enter 送出，Shift+Enter 換行）'"`。

`components/FailureNotice.vue`：`<blockquote …>` 之前加 `<p v-if="entry.confirm" class="mt-1 text-xs text-muted">確認卡還在，可以再按一次。</p>`。

- [ ] **Step 4: 跑測試與建置**

Run（在 `src/PromptCopilot.Frontend`）：`npm test && npm run build`
Expected: 測試全過，build 成功。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend
git commit -m "feat(frontend): confirm card with accept or interpretation buttons" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: `manual-tests/chat.py` 支援確認卡

spec §7 末段。

**Files:**
- Modify: `manual-tests/chat.py`

**Interfaces:**
- Consumes: SSE `final.kind = "confirm"`、`session.turnIndex`、`session.text`；`POST messages` 的 `confirm` body。

- [ ] **Step 1: 實作**

模組 docstring 的指令行改成：

```python
指令：/new 開新 session　/ok 按確認卡的「對，就這樣」　/1～/4 選確認卡的第幾個解讀　/save <一句話描述> 存到共享庫　/raw 切換原始事件　/quit 離開
```

`Chat.__init__` 加兩個欄位：

```python
        self.turn = 0
        self.pending: tuple[int, int] | None = None  # 可以按的確認卡：(輪次, 選項數)
```

`new_session` 的 `self.sid, self.last_states, self.last_profile = …` 那行之後加 `self.pending = None`。

`send` 換成兩個方法：

```python
    def send(self, text: str):
        self.post({"text": text})

    def confirm(self, choice: int | None):
        """按確認卡（先確認再動手設計 §3.5）。choice：沒有選項的卡是 None，否則 0 起算。"""
        if self.pending is None:
            print(self.ui.yellow("  現在沒有可以按的確認卡。"))
            return
        turn, n = self.pending
        if n == 0 and choice is not None:
            print(self.ui.yellow("  這張確認卡沒有選項，用 /ok。"))
            return
        if n > 0 and (choice is None or not 0 <= choice < n):
            print(self.ui.yellow(f"  這張確認卡有 {n} 個選項，用 /1～/{n}。"))
            return
        self.post({"confirm": {"turnIndex": turn, "choice": choice}})

    def post(self, body: dict):
        try:
            for name, ev in sse_events(f"{self.base}/api/sessions/{self.sid}/messages", body):
                if self.raw:
                    print(self.ui.dim(f"  [{name}] {json.dumps(ev, ensure_ascii=False)}"))
                getattr(self, f"on_{name}", self.on_unknown)(ev)
        except urllib.error.HTTPError as e:
            msg = e.read().decode("utf-8", "replace")
            print(self.ui.red(f"  HTTP {e.code}：{msg}"))
            if e.code == 404:
                print(self.ui.dim("  session 過期了，自動開新的；請再送一次。"))
                self.new_session()
```

`on_session`：

```python
    def on_session(self, ev):
        self.turn = ev["turnIndex"]
        print(self.ui.dim(f"  第 {ev['turnIndex']} 輪（開始時狀態 {ev['status']}）"))
        if ev.get("text"):
            print(self.ui.dim(f"  你：{ev['text']}"))
```

`on_final` 改三處，其餘分支內容不動：
1. 原本的 `if kind == "ask":` 改成 `elif kind == "ask":`，並在它前面（`kind = ev["kind"]` 之後）插入 `confirm` 分支：

```python
        if kind == "confirm":
            choices = ev.get("choices") or []
            print(self.ui.bold(f"\n助手（確認）：{ev.get('message', '')}"))
            for n, c in enumerate(choices, 1):
                print(f"     /{n}) {c}")
            print(self.ui.dim("  按 /ok 套用；打字會當成修正。" if not choices else "  選一個 /1～/%d；打字會當成修正。" % len(choices)))
            self.pending = (self.turn, len(choices))
```

2. `elif kind == "ask":` 分支的第一行加 `self.pending = None`（追問卡出來代表已經動過手）。
3. `elif kind == "finalized":` 分支的第一行加 `self.pending = None`。

`main` 的提示行改成 `print(chat.ui.dim("指令：/new 開新 session　/ok 按確認　/1～/4 選解讀　/save <描述> 存到共享庫　/raw 切換原始事件　/quit 離開\n"))`；指令判斷在 `elif text.startswith("/save"):` 之前加：

```python
        elif text == "/ok":
            chat.confirm(None)
        elif len(text) == 2 and text[0] == "/" and text[1] in "1234":
            chat.confirm(int(text[1]) - 1)
```

- [ ] **Step 2: 語法檢查**

Run: `python -m py_compile manual-tests/chat.py`（或 `scripts/.venv/Scripts/python.exe -m py_compile manual-tests/chat.py`）
Expected: 沒有輸出。

- [ ] **Step 3: Commit**

```bash
git add manual-tests/chat.py
git commit -m "feat(manual-tests): chat.py can press confirm cards with /ok and /1-/4" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: 文件同步

spec §11。

**Files:**
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-09-25-set-recommendations-design.md`、`docs/superpowers/specs/2026-09-30-recommendation-slate-design.md`
- Modify: `docs/eval-cases.md`

- [ ] **Step 1: 主規格**

`2026-09-21-genai-prompt-copilot-design.md`，下列位置各加一段（連結一律寫 `[先確認再動手設計](2026-10-05-confirm-before-act-design.md)`）：

1. `### 4.2 Plugins 與 Tools` 的工具表，在 `DialogPlugin.RequestSaveConsent` 那列之後加一列：
   `| `DialogPlugin.Confirm` | `message: string, choices: string[]? = null` | **終止型**（2026-10-05）；只在確認輪；`choices` 0 或 2–4 個；不改 facet，只在 session 記一筆待確認（[先確認再動手設計](2026-10-05-confirm-before-act-design.md) §3.2） |`
2. `### 4.3 工具清單組裝規則` 的規則程式碼區塊之後加：
   > **2026-10-05 起每一輪先分種類**（[先確認再動手設計](2026-10-05-confirm-before-act-design.md) §3.1）：使用者打字是**確認輪**，只註冊 `Confirm`、`Discuss`（規則同上）與兩個檢索工具，`Finalized` 時加 `RequestSaveConsent`；按確認卡或採用是**動手輪**，註冊上面「永遠註冊」那組，加 `AskUser`（規則同上），沒有 `Confirm`、`Discuss`、`RequestSaveConsent`。`wantsAutoComplete` 在確認輪只記進待確認，按下確認後的動手輪才拿掉 `AskUser`、設 `AutoFill`。
3. `### 4.4 Session 狀態機` 的 `Session { … }` 程式碼區塊之後加：
   > 2026-10-05 加 `PendingConfirmation: { TurnIndex, Message, Choices, AutoComplete }?`：確認輪的 `Confirm` 寫入，動手輪（含採用）開始時清掉，進快照（[先確認再動手設計](2026-10-05-confirm-before-act-design.md) §3.4）。

   同一節「`Finalized` 後使用者要求修改（「把背景改成黃昏」）→ …」那個項目之後加：
   > 2026-10-05 起修改要先過確認輪：模型用 `Confirm` 講要改哪裡（有歧義時給解讀），使用者按下確認後的動手輪才 `FinalizePrompt`。
4. `### 4.5 Filters` 的「**`Finalized` 之下 `Discuss` 不得變更 facet 狀態。**」那段之後加：
   > 2026-10-05 起不分狀態：`Discuss` 任何時候帶著跟本輪開始時不同的 facet 狀態都擋回，錯誤字串要模型改用 `Confirm`；`OutputSafetyFilter` 也檢 `Confirm` 的 `message` 與 `choices`。
5. `### 4.6 失敗模式處理` 小節最後加：
   > 2026-10-05：確認輪的 tool 預算用盡時強制的是 `Confirm`（只掛它），不是 `FinalizePrompt`；純文字補救的提示改列本輪實際有的終止型工具。動手輪只回純文字時沒有 `Discuss` 可包，走 `protocol_violation` 回滾，待確認跟著回來。
6. `### 4.7 Chat history 修剪與截斷` 小節最後加：
   > 2026-10-05：`HistoryTurns` 預設由 10 改 20：每個要求多一則「對，就這樣」使用者訊息，維持原本記得的要求數。
7. `### 4.9 System prompt` 小節最後加：
   > 2026-10-05：`system.md` 的流程段換成 `{{FLOW}}`，依這一輪的種類換進 `Prompts/flow-propose.md` 或 `flow-act.md`；動手輪的流程開頭是伺服器組的「使用者已確認」區塊（`{{CONFIRMED}}`，最後才替換，裡面的文字不會再被展開）。
8. `### 10.1 端點` 小節最後加：
   > 2026-10-05：`messages` 的 body 多一種 `{"confirm": {"turnIndex": n, "choice": k | null}}`，按確認卡；沒有待確認或不是最新一張回 409，`choice` 跟卡片對不上、或與 `adopt` 同時送回 400。`adopt` 改成定稿後才收（未定稿 409）。
9. `### 10.2 SSE 事件` 小節最後加：
   > 2026-10-05：`final.kind` 多 `confirm`（`message`、`choices`）；`session.text` 在動手輪都會帶（採用句或確認句）；`recommendations` 只跟在定稿之後。
10. `### 11.1 版面` 小節最後加：
    > 2026-10-05：對話流多一種確認卡：只有最新一張、而且之後還沒動手時按鈕可按；有可按的確認卡時，輸入框提示「在這裡打字會當成修正」。追問卡不再有推薦條。
11. `### 12.1 單元測試` 小節最後加：
    > 2026-10-05 先確認再動手的測試見該設計 §10。

- [ ] **Step 2: README**

- 第 5 行「…主動追問缺的細節、從知識庫推薦可用片段，最後產出 SD／SDXL tag 風格的英文正／負向提示詞，逐個 tag 標示來源（知識庫片段、採用的組合或模型生成）。每次追問與定稿另外推薦知識庫裡真實存在、有圖的整套組合，使用者可逐項採用。」改成「…每個會改動畫面的要求都先跟你確認理解、你按下確認後才動手，主動追問缺的細節、從知識庫推薦可用片段，最後產出 SD／SDXL tag 風格的英文正／負向提示詞，逐個 tag 標示來源（知識庫片段、採用的組合或模型生成）。每次定稿另外推薦知識庫裡真實存在、有圖的整套組合，使用者可逐項採用。」（句首「用繁體中文描述想要的畫面，系統以六個維度判斷資訊夠不夠、」與句尾「求職作品集專案：…」不動。）
- 第 45 行「一輪對話：…該輪以一個終止型工具收尾。」之後、「任何一步失敗整輪回滾」之前插入：「會改動畫面的要求一律分兩輪：打字的那一輪模型只拿得到確認、討論與檢索，使用者按下確認卡的按鈕後，下一輪才拿得到改狀態的工具（[先確認再動手設計](docs/superpowers/specs/2026-10-05-confirm-before-act-design.md)）。」
- 第 98 行「子專案設計：…」清單最後加 `、[先確認再動手](docs/superpowers/specs/2026-10-05-confirm-before-act-design.md)`。

- [ ] **Step 3: 推薦相關的兩份 spec**

`2026-09-25-set-recommendations-design.md` 與 `2026-09-30-recommendation-slate-design.md`：各在檔頭的日期／狀態／來源那幾行之後加一行：

> 2026-10-05：追問卡不再推薦，採用改成定稿後才收（[先確認再動手設計](2026-10-05-confirm-before-act-design.md) §8）。本文講到追問卡推薦的地方以那裡為準。

- [ ] **Step 4: `docs/eval-cases.md`**

- `## 2026-09-25 整套組合推薦與採用…` 標題下第一段之後加：`> 2026-10-05 起追問卡不再推薦（先確認再動手設計 §8）：S1 的「追問卡底下參考組合」與 S7 的「在追問卡採用」已不適用。`
- `## 2026-09-30 推薦組法（看過延後、探索位、換一批）` 標題下第一段之後加：`> 2026-10-05 起追問卡不再推薦，G5 已不適用。`
- 檔尾加新的一節：

```markdown
## 2026-10-05 先確認再動手（含追問卡不再推薦）

設計：`docs/superpowers/specs/2026-10-05-confirm-before-act-design.md`。Claude 用 Playwright（`playwright-core` 驅動系統的 Edge，headless）跑，`docker compose up -d --build api frontend` 重建後執行，實際呼叫 Gemini。腳本放 scratchpad，不進 repo。

| # | 操作 | 預期 | 結果 |
| :--- | :--- | :--- | :--- |
| C1 | 送「一位金色短髮的中年女士拿著相機和飲料站在雨夜的霓虹街頭」 | 確認卡複述畫面；按下前儀表板不變；按「對，就這樣」後出追問卡，沒有推薦條 | |
| C2 | 用選項回答追問 | 先出確認卡（「我會把風格設成…」），按下後才追問或定稿 | |
| C3 | 定稿後送「讓她拿雨傘」，跑 3 次 | 3 次都是有選項的確認卡；選「換掉飲料，改拿雨傘」後定稿有雨傘與相機、沒有飲料 | |
| C4 | 定稿後送「鞋子換成靴子」 | 單一提案卡；按下後重新定稿 | |
| C5 | 確認卡沒按時問「寫實跟動漫差在哪」 | `Discuss` 回答；之後舊確認卡仍可按且有效 | |
| C6 | 確認卡出現後在輸入框打「好」 | 出新的確認卡，不動手 | |
| C7 | 動手後看舊確認卡；curl 帶舊輪次送確認 | 舊卡停用；curl 409「只有最新一張確認卡可以按」 | |
| C8 | 送「直接給我」 | 確認卡列出打算補的內容；按下後定稿 | |
| C9 | 確認卡沒按時重新整理 | 卡片回來，照樣能按 | |
| C10 | 從定稿卡採用一套；curl 在追問階段送採用 | 採用直接動手、不出確認卡；curl 409「定稿後才能採用組合」 | |

耗時（從 `docker compose logs api` 的 `Turn …` 摘要行算中位數）：確認輪 ＿ ms、動手輪 ＿ ms。
```

（「結果」欄與耗時在 Task 10 填。）

- [ ] **Step 5: Commit**

```bash
git add docs README.md
git commit -m "docs: confirm-before-act in the main spec, README, recommendation specs and eval cases" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: 全套測試與瀏覽器驗收

spec §10.3。實際呼叫 Gemini。

**Files:**
- Modify: `docs/eval-cases.md`（填結果與耗時）

- [ ] **Step 1: 全套測試**

Run: `dotnet build src/PromptCopilot.sln -c Release && dotnet test src/PromptCopilot.sln -c Release --no-build`
Run（`src/PromptCopilot.Frontend`）：`npm test && npm run build`
Run（`scripts`）：`./.venv/Scripts/python.exe -m ruff check . && ./.venv/Scripts/python.exe -m pytest`
Expected: 全過。

- [ ] **Step 2: 重建並起服務**

Run: `docker compose up -d --build api frontend`
Expected: `api`、`frontend` healthy；`curl -s http://localhost:5000/health` 回 200。

- [ ] **Step 3: 跑 C1–C10**

用 Playwright（`playwright-core` 驅動系統 Edge，headless；腳本放 scratchpad）開 `http://localhost:8080`，照 `docs/eval-cases.md` 新那一節逐條操作。判斷依據：
- 確認卡：`[data-card="confirm"]`，按鈕 `[data-action="confirm"]`；可按與否看 `disabled`。
- 追問卡沒有推薦：`[data-card="ask"] [data-section="recommendations"]` 不存在。
- 儀表板不變：按下確認前後各取一次 `GET /api/sessions/{id}` 的 `facetStates`／`profile` 比對。
- C3：同一句跑 3 次（每次新對話、先定稿出「拿著相機和飲料」的畫面）；選第一個含「飲料」的解讀後，看新定稿 `positive` 有 `umbrella`、有 `camera`、沒有 `drink`／`cup`／`bottle` 類 tag。
- C7、C10 的 curl：`curl -s -X POST http://localhost:5000/api/sessions/<id>/messages -H "content-type: application/json" --data-binary @body.json`（中文 body 用 UTF-8 檔案，Git Bash 直接 `-d` 會被轉碼壞）。

若 C3 有任何一次沒給選項：調 `src/PromptCopilot.Api/Prompts/flow-propose.md` 第 3 條的說明（不放寬標準），`docker compose up -d --build api` 後重跑 C3，並在結果欄記下改了什麼。

- [ ] **Step 4: 記耗時**

Run: `docker compose logs api --since 2h | grep "Turn "`
從摘要行（`Turn <session>#<n> Turn_Completed <Outcome> … <ms> ms`）分出確認輪（`ConfirmOutcome`、`MessageOutcome`）與動手輪（`AskOutcome`、`FinalizedOutcome`），各算中位數，填進 eval-cases 那一節的耗時行。

- [ ] **Step 5: 填結果並 commit**

每條在「結果」欄寫 ✅／⚠️／❌ 與觀察（session id、實際的確認文字、按鈕數），照前面各節的寫法。

```bash
git add docs/eval-cases.md src/PromptCopilot.Api/Prompts
git commit -m "docs: browser acceptance C1-C10 for confirm-before-act" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## 完成後

用 superpowers:finishing-a-development-branch 決定 merge 方式。session 持久化的商品性（記憶裡的待辦）跟這次無關，不在這個分支處理。
