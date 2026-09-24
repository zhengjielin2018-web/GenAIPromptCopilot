# 批次 `SearchPresets` 與工具預算 16 — 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `SearchPresets` 一次呼叫帶多組 `(dimension, query)`，一輪的檢索只花一次工具呼叫；預算調到 16；強制定稿時 facet 狀態依使用者原話標記。修掉 known-issues #1「人像題材第一輪常被強制定稿」。

**Architecture:** `KnowledgePlugin.SearchPresetsAsync` 改收 `SearchQuery[]`，先驗證、再一次 embedding batch、每個項目各一次 SQL、每個維度各一次候選池計數，回傳 `{ results: [...] }`。`HistoryTrimmer` 跟著改壓縮形狀。`system.md` 與工具描述改成批次語意。`MaxToolCallsPerTurn` 8 → 16。強制定稿提示多一句 facetStates 規則。前端與 `OutputSafetyFilter` 不動。

**Tech Stack:** .NET 10、Semantic Kernel（`[KernelFunction]` + `[Description]` 產 Gemini function declaration）、xunit 2.9.3、Npgsql + Pgvector。

**Spec:** [docs/superpowers/specs/2026-09-24-batch-search-presets-design.md](../specs/2026-09-24-batch-search-presets-design.md)

## Global Constraints

- 分支：`fix/batch-search-presets`（已從 `fix/hnsw-iterative-scan` 分出，spec 已在上面）。所有 commit 都在這裡，不 push、不 merge。
- Commit 訊息結尾一律加 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`。標題沿用 `git log` 既有的 conventional 英文格式；正文可用繁中。
- 測試指令：`dotnet test src/PromptCopilot.Api.Tests`（不設 `PC_INTEGRATION` 時整合測試會 skip，這是預期）。Node 不在 shell PATH 上，本計畫不碰前端也不需要 npm。
- `dimension` 合法值：`style | scene | camera | appearance | pose | clothing`。`facetIds` 與 `k` 由伺服器導出，不從參數收。
- `k`：維度 grounded 取 `KnowledgePlugin.KCovered = 5`，否則 `KMissing = 3`。grounded 由 `Session.GroundedDimensions(catalog)` 算，不信 LLM。
- 批次上限 12 個項目。空清單、超過 12、未設 profile 整包回錯誤字串（`錯誤：` 開頭，同現行寫法）；單一項目維度不合法或 query 空白只在該項目標 `error`。
- 回傳 JSON 用既有的 `KnowledgePlugin.Json`（camelCase）。每筆命中的形狀（`id, title, band, dist, usable, facets, positive, negative`）不變。
- 事件摘要分隔符是全形中點「・」。
- 舊的單維度簽名移除，不並存。
- 不改：前端、`OutputSafetyFilter`、`SearchSimilarPrompts`、預算用盡的處理方式、`docs/單輪流程說明.md`。

## Review Focus

1. **同一維度重複兩次（對比方向）**：兩個項目都要各自回結果、各自帶同一個 `poolSize`，`PoolSizeAsync` 只查一次。Task 2 的 `Batch_embeds_once_and_counts_each_dimension_pool_once` 涵蓋。
2. **項目全部不合法**（例如 12 個項目維度都拼錯）：不能拋例外，也不能打 embedding；`results` 全是 `error`。Task 2 的 `All_items_invalid_skips_embedding_and_reports_each_error` 涵蓋。
3. **Gemini 傳來 `queries` 為 null**（模型漏參數時 SK 反序列化可能給 null 而不是空陣列）：要當空清單處理，回錯誤字串而不是 `NullReferenceException`。Task 2 的 `Empty_or_null_queries_is_an_error_without_embedding` 涵蓋。
4. **preset 在兩個維度都命中**：`ToolResultEvent.Presets` 依 id 去重，但 ledger 兩筆 `LedgerHit` 都要記（歸屬到定稿時才算，主規格 §9）。Task 2 的 `Same_preset_from_two_dimensions_is_deduped_in_event_but_recorded_twice_in_ledger` 涵蓋。
5. **舊形狀的 tool result 留在 history 裡**（session 跨版本、或測試資料）：`HistoryTrimmer` 遇到單維度舊形狀要整段跳過、不丟例外。Task 3 的 `CompressTurn_leaves_legacy_single_dimension_shape_untouched` 涵蓋。

---

### Task 1: `SearchQuery` 契約與 repository 可覆寫

**Files:**
- Modify: `src/PromptCopilot.Api/Plugins/Contracts.cs`
- Modify: `src/PromptCopilot.Api/Data/PresetRepository.cs:30,44`
- Test: `src/PromptCopilot.Api.Tests/Plugins/ContractsTests.cs`（新檔）

**Interfaces:**
- Produces: `public sealed record SearchQuery([property: JsonPropertyName("dimension")] string Dimension, [property: JsonPropertyName("query")] string Query);`（namespace `PromptCopilot.Api.Plugins`）
- Produces: `PresetRepository.SearchAsync` 與 `PoolSizeAsync` 變成 `virtual`，Task 2 的 fake 要覆寫。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Plugins/ContractsTests.cs`：

```csharp
using System.Text.Json;
using PromptCopilot.Api.Plugins;

namespace PromptCopilot.Api.Tests.Plugins;

public class ContractsTests
{
    [Fact]
    public void SearchQuery_round_trips_camelCase_json()
    {
        var json = """[{"dimension":"style","query":"寫實攝影"},{"dimension":"scene","query":"稻田"}]""";
        var parsed = JsonSerializer.Deserialize<SearchQuery[]>(json)!;
        Assert.Equal(2, parsed.Length);
        Assert.Equal("style", parsed[0].Dimension);
        Assert.Equal("稻田", parsed[1].Query);
        // 序列化用 ASCII 句子：預設 encoder 會把非 ASCII 轉成 \uXXXX，這裡只驗欄位名是 camelCase
        Assert.Equal("""{"dimension":"style","query":"photo"}""", JsonSerializer.Serialize(new SearchQuery("style", "photo")));
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~ContractsTests"`
Expected: 編譯錯誤 `SearchQuery` 不存在。

- [ ] **Step 3: 加 record、把 repository 兩個方法改 virtual**

`Contracts.cs` 在 `AskItem` 之後加：

```csharp
/// <summary>SearchPresets 的一個項目。同一維度可重複出現（使用者沒講的維度給兩個對比方向）。</summary>
public sealed record SearchQuery(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("query")] string Query);
```

`PresetRepository.cs`：

```csharp
public virtual async Task<IReadOnlyList<PresetHit>> SearchAsync(float[] query, IReadOnlyList<string> facetIds, int k, CancellationToken ct)
// ...
public virtual async Task<long> PoolSizeAsync(IReadOnlyList<string> facetIds, CancellationToken ct)
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~ContractsTests"`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Plugins/Contracts.cs src/PromptCopilot.Api/Data/PresetRepository.cs src/PromptCopilot.Api.Tests/Plugins/ContractsTests.cs
git commit -m "feat(plugins): SearchQuery contract; preset search overridable for fakes"
```

---

### Task 2: `KnowledgePlugin.SearchPresetsAsync` 批次版

**Files:**
- Modify: `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs:24-58`
- Test: `src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs`（新檔）

**Interfaces:**
- Consumes: `SearchQuery`（Task 1）、`PresetRepository.SearchAsync/PoolSizeAsync` virtual（Task 1）、`TurnContext.Emit`、`Session.Ledger.Record`、`FacetCatalog.FacetsOf/DimensionLabel`、`IEmbeddingClient.EmbedAsync(IReadOnlyList<string>, string, CancellationToken)`。
- Produces: `public async Task<string> SearchPresetsAsync(SearchQuery[]? queries, CancellationToken ct)`，回傳 `{"results":[{dimension, query, grounded, poolSize, hits:[...]} | {dimension, query, error}]}`。
- Produces: `public const int MaxQueries = 12;`。
- Produces: 一則 `ToolResultEvent(callId, "SearchPresets", "風格 池 4455 → 3・場景 池 6752 → 5", presets 依 id 去重)`。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs`：

```csharp
using System.Text.Json;
using System.Threading.Channels;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Plugins;

public class KnowledgePluginTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    /// <summary>記下每次呼叫的句子；向量的第 0 維寫入句子在 batch 裡的序號，讓 FakePresets 認得出是哪一句。</summary>
    private sealed class FakeEmbeddings : IEmbeddingClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct)
        {
            Calls.Add(texts);
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select((_, i) => { var v = new float[768]; v[0] = i; return v; }).ToList());
        }
    }

    /// <summary>記下每次 Search／PoolSize 的參數。命中由 <see cref="Hits"/> 決定：key 是 facetIds 的第一個 id。</summary>
    private sealed class FakePresets() : PresetRepository(null!)
    {
        public List<(string firstFacet, int k, float queryIndex)> Searches { get; } = new();
        public List<string> PoolCalls { get; } = new();
        public Dictionary<string, IReadOnlyList<PresetHit>> Hits { get; } = new();
        public Dictionary<string, long> Pools { get; } = new();

        public override Task<IReadOnlyList<PresetHit>> SearchAsync(float[] query, IReadOnlyList<string> facetIds, int k, CancellationToken ct)
        {
            Searches.Add((facetIds[0], k, query[0]));
            return Task.FromResult(Hits.GetValueOrDefault(facetIds[0], Array.Empty<PresetHit>()));
        }
        public override Task<long> PoolSizeAsync(IReadOnlyList<string> facetIds, CancellationToken ct)
        {
            PoolCalls.Add(facetIds[0]);
            return Task.FromResult(Pools.GetValueOrDefault(facetIds[0], 0L));
        }
    }

    private sealed class FakeHistories() : HistoryRepository(null!);

    private static PresetHit Hit(long id, string title, string facet, double dist) =>
        new(id, title, "Cat", new[] { facet }, $"tags for {title}", null, null, dist);

    private static (KnowledgePlugin plugin, TurnContext turn, Session s, FakeEmbeddings embed, FakePresets presets, ChannelReader<AgentEvent> events)
        Make(string[]? covered = null, bool profile = true)
    {
        var s = new Session("s");
        if (profile)
        {
            s.ApplyProfile("portrait", Catalog);
            s.ApplyFacetStates((covered ?? Array.Empty<string>()).ToDictionary(f => f, _ => FacetState.Covered), Catalog);
        }
        var ch = Channel.CreateUnbounded<AgentEvent>();
        var turn = new TurnContext(s, 1, GuardResult.Ok(false), ToolNames.Always, ch.Writer) { CurrentCallId = "c1" };
        var embed = new FakeEmbeddings();
        var presets = new FakePresets();
        return (new KnowledgePlugin(turn, Catalog, embed, presets, new FakeHistories()), turn, s, embed, presets, ch.Reader);
    }

    private static SearchQuery[] Q(params (string d, string q)[] xs) => xs.Select(x => new SearchQuery(x.d, x.q)).ToArray();

    private static List<AgentEvent> Drain(ChannelReader<AgentEvent> r)
    {
        var list = new List<AgentEvent>();
        while (r.TryRead(out var e)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Requires_profile()
    {
        var (p, _, _, embed, _, _) = Make(profile: false);
        var r = await p.SearchPresetsAsync(Q(("style", "x")), default);
        Assert.StartsWith("錯誤", r); Assert.Contains("SetProfile", r); Assert.Empty(embed.Calls);
    }

    [Fact]
    public async Task Empty_or_null_queries_is_an_error_without_embedding()
    {
        var (p, _, _, embed, _, _) = Make();
        Assert.StartsWith("錯誤", await p.SearchPresetsAsync(Array.Empty<SearchQuery>(), default));
        Assert.StartsWith("錯誤", await p.SearchPresetsAsync(null, default));
        Assert.Empty(embed.Calls);
    }

    [Fact]
    public async Task More_than_twelve_items_is_an_error_without_embedding()
    {
        var (p, _, _, embed, _, _) = Make();
        var thirteen = Enumerable.Range(0, 13).Select(i => new SearchQuery("style", $"q{i}")).ToArray();
        var r = await p.SearchPresetsAsync(thirteen, default);
        Assert.StartsWith("錯誤", r); Assert.Contains("12", r); Assert.Empty(embed.Calls);
    }

    [Fact]
    public async Task Batch_embeds_once_and_counts_each_dimension_pool_once()
    {
        var (p, _, _, embed, presets, _) = Make(covered: new[] { "scene.location" });
        presets.Pools["style.genre"] = 4455; presets.Pools["scene.location"] = 6752;
        presets.Hits["style.genre"] = new[] { Hit(1, "寫實", "style.genre", 0.2) };
        presets.Hits["scene.location"] = new[] { Hit(2, "稻田", "scene.location", 0.15) };

        var r = await p.SearchPresetsAsync(Q(("style", "寫實攝影"), ("style", "日系動漫插畫"), ("scene", "稻田裡面喝茶")), default);

        var call = Assert.Single(embed.Calls);
        Assert.Equal(new[] { "寫實攝影", "日系動漫插畫", "稻田裡面喝茶" }, call);
        Assert.Equal(3, presets.Searches.Count);
        Assert.Equal(new[] { 0f, 1f, 2f }, presets.Searches.Select(x => x.queryIndex));   // 每個項目用自己那句的向量
        Assert.Equal(new[] { "style.genre", "scene.location" }, presets.PoolCalls);          // 每個維度只數一次

        var results = JsonDocument.Parse(r).RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal("style", results[0].GetProperty("dimension").GetString());
        Assert.Equal("寫實攝影", results[0].GetProperty("query").GetString());
        Assert.Equal(4455, results[0].GetProperty("poolSize").GetInt64());
        Assert.Equal(4455, results[1].GetProperty("poolSize").GetInt64());
        Assert.Equal(6752, results[2].GetProperty("poolSize").GetInt64());
        Assert.Equal(1, results[0].GetProperty("hits").GetArrayLength());
        Assert.Equal("可借入提示詞", results[2].GetProperty("hits")[0].GetProperty("usable").GetString());
        Assert.Equal("僅供建議", results[0].GetProperty("hits")[0].GetProperty("usable").GetString());
    }

    [Fact]
    public async Task K_follows_grounded_per_dimension()
    {
        var (p, _, _, _, presets, _) = Make(covered: new[] { "scene.location" });
        await p.SearchPresetsAsync(Q(("scene", "稻田"), ("style", "寫實")), default);
        Assert.Equal(KnowledgePlugin.KCovered, presets.Searches.Single(x => x.firstFacet == "scene.location").k);
        Assert.Equal(KnowledgePlugin.KMissing, presets.Searches.Single(x => x.firstFacet == "style.genre").k);
        var r = await p.SearchPresetsAsync(Q(("scene", "稻田")), default);
        Assert.True(JsonDocument.Parse(r).RootElement.GetProperty("results")[0].GetProperty("grounded").GetBoolean());
    }

    [Fact]
    public async Task One_invalid_item_is_reported_alone_and_the_rest_run()
    {
        var (p, _, _, embed, presets, _) = Make();
        var r = await p.SearchPresetsAsync(Q(("hair", "捲髮"), ("style", "寫實"), ("scene", "   ")), default);

        Assert.StartsWith("{", r);                                                   // 不是整包錯誤字串
        var results = JsonDocument.Parse(r).RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.Contains("hair", results[0].GetProperty("error").GetString());
        Assert.False(results[1].TryGetProperty("error", out _));
        Assert.True(results[2].TryGetProperty("error", out _));                      // query 空白
        Assert.Equal(new[] { "寫實" }, Assert.Single(embed.Calls));                  // 只 embed 合法的那句
        Assert.Single(presets.Searches);
    }

    [Fact]
    public async Task All_items_invalid_skips_embedding_and_reports_each_error()
    {
        var (p, _, _, embed, presets, events) = Make();
        var r = await p.SearchPresetsAsync(Q(("hair", "a"), ("nope", "b")), default);
        var results = JsonDocument.Parse(r).RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.All(results, x => Assert.True(x.TryGetProperty("error", out _)));
        Assert.Empty(embed.Calls); Assert.Empty(presets.Searches);
        var ev = Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events)));
        Assert.Empty(ev.Presets!);
    }

    [Fact]
    public async Task Event_summary_lists_each_item_and_dedupes_presets()
    {
        var (p, _, _, _, presets, events) = Make();
        presets.Pools["style.genre"] = 4455; presets.Pools["camera.shot"] = 2147;
        presets.Hits["style.genre"] = new[] { Hit(1, "寫實", "style.genre", 0.2), Hit(2, "動漫", "style.genre", 0.22) };
        presets.Hits["camera.shot"] = new[] { Hit(2, "動漫", "camera.shot", 0.28) };

        await p.SearchPresetsAsync(Q(("style", "寫實攝影"), ("camera", "低角度"), ("hair", "x")), default);

        var ev = Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events)));
        Assert.Equal("c1", ev.CallId);
        Assert.Equal(ToolNames.SearchPresets, ev.Name);
        Assert.Equal("風格 池 4455 → 2・鏡頭 池 2147 → 1・hair 錯誤", ev.Summary);
        Assert.Equal(new long[] { 1, 2 }, ev.Presets!.Select(x => x.Id));
    }

    [Fact]
    public async Task Same_preset_from_two_dimensions_is_deduped_in_event_but_recorded_twice_in_ledger()
    {
        var (p, _, s, _, presets, _) = Make(covered: new[] { "scene.location" });
        presets.Hits["style.genre"] = new[] { Hit(9, "x", "style.genre", 0.3) };
        presets.Hits["scene.location"] = new[] { Hit(9, "x", "scene.location", 0.1) };

        await p.SearchPresetsAsync(Q(("style", "a"), ("scene", "b")), default);

        var entry = s.Ledger.Get(9)!;
        Assert.Equal(2, entry.Hits.Count);
        Assert.Contains(entry.Hits, h => h.Dimension == "style" && !h.Grounded);
        Assert.Contains(entry.Hits, h => h.Dimension == "scene" && h.Grounded);
    }
}
```

注意：`Catalog.DimensionLabel("style", "portrait")` 回「風格」、`camera` 回「鏡頭」，跟現行的 `SearchPresets` 摘要一致（eval-cases 裡有 `風格 池 4455 → 3` 的實例）。若 facets.yaml 的 portrait 標籤不同，以 yaml 為準改測試字串。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~KnowledgePluginTests"`
Expected: 編譯錯誤（`SearchPresetsAsync` 簽名不符）。

- [ ] **Step 3: 改寫 `SearchPresetsAsync`**

把 `KnowledgePlugin.cs` 第 24–58 行整段換成：

```csharp
public const int MaxQueries = 12;

[KernelFunction(ToolNames.SearchPresets)]
[Description("分維度檢索知識庫片段。一次呼叫帶上本輪所有要查的維度，不要一個維度一次。使用者講過的維度：一個項目，query 逐字用使用者原話。使用者沒講的維度：兩個項目，依整體畫面推想兩個對比方向（例：寫實攝影 vs 動漫插畫）。每個維度各自回傳候選池大小、每筆的相似度分級、可否借入提示詞、每個 facet 對本次使用者是 covered/missing。")]
public async Task<string> SearchPresetsAsync(
    [Description("要查的項目，最多 12 個。每項：dimension（style | scene | camera | appearance | pose | clothing）與 query（該維度專屬的繁中查詢語句）。同一維度可重複。")] SearchQuery[]? queries,
    CancellationToken ct)
{
    var s = turn.Session;
    if (s.Profile is null) return "錯誤：請先呼叫 SetProfile";
    if (queries is null || queries.Length == 0) return "錯誤：queries 不可為空，請一次帶上本輪所有要查的維度";
    if (queries.Length > MaxQueries) return $"錯誤：queries 最多 {MaxQueries} 個項目（6 個維度 × 2 個對比方向），收到 {queries.Length} 個";

    var grounded = s.GroundedDimensions(catalog);
    // 每個項目先驗證；合法的才進 embedding batch。index 對應回 queries 的位置，結果要照原順序回。
    var valid = new List<(int index, string dimension, string query, IReadOnlyList<string> facetIds)>();
    var errors = new Dictionary<int, string>();
    for (var i = 0; i < queries.Length; i++)
    {
        var q = queries[i];
        var dimension = q.Dimension ?? "";
        var facetIds = catalog.FacetsOf(s.Profile, dimension);
        if (facetIds.Count == 0) { errors[i] = $"維度 {dimension} 對 {s.Profile} 不適用或不存在"; continue; }
        if (string.IsNullOrWhiteSpace(q.Query)) { errors[i] = $"維度 {dimension} 的 query 空白"; continue; }
        valid.Add((i, dimension, q.Query, facetIds));
    }

    var vectors = valid.Count == 0
        ? Array.Empty<float[]>()
        : await embed.EmbedAsync(valid.Select(v => v.query).ToList(), GeminiEmbeddingClient.RetrievalQuery, ct);

    var pools = new Dictionary<string, long>();
    var results = new object[queries.Length];
    var counts = new int[queries.Length];
    var summary = new List<string>();
    var presetsOut = new List<PresetRef>();
    var seen = new HashSet<long>();

    for (var vi = 0; vi < valid.Count; vi++)
    {
        var (index, dimension, query, facetIds) = valid[vi];
        var isGrounded = grounded.Contains(dimension);
        var k = isGrounded ? KCovered : KMissing;
        if (!pools.TryGetValue(dimension, out var pool))
            pools[dimension] = pool = await presets.PoolSizeAsync(facetIds, ct);
        var hits = await presets.SearchAsync(vectors[vi], facetIds, k, ct);

        var rows = new List<object>();
        foreach (var h in hits)
        {
            s.Ledger.Record(new LedgerEntry { Id = h.Id, Title = h.Title, PromptSnippet = h.PromptSnippet, NegativeSnippet = h.NegativeSnippet, FacetIds = h.FacetIds, ImageUrl = h.ImageUrl },
                new LedgerHit(dimension, h.Dist, isGrounded));
            rows.Add(new
            {
                id = h.Id, title = h.Title, band = Band(h.Dist), dist = Math.Round(h.Dist, 3),
                usable = isGrounded ? "可借入提示詞" : "僅供建議",
                facets = h.FacetIds.ToDictionary(f => f, f => FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(f, FacetState.NotApplicable))),
                positive = h.PromptSnippet, negative = h.NegativeSnippet ?? "(無)",
            });
            if (seen.Add(h.Id)) presetsOut.Add(new PresetRef(h.Id, h.Title, h.ImageUrl));
        }
        results[index] = new { dimension, query, grounded = isGrounded, poolSize = pool, hits = rows };
        counts[index] = rows.Count;
    }
    foreach (var (i, message) in errors)
        results[i] = new { dimension = queries[i].Dimension ?? "", query = queries[i].Query ?? "", error = message };

    for (var i = 0; i < queries.Length; i++)
    {
        var d = queries[i].Dimension ?? "";
        summary.Add(errors.ContainsKey(i)
            ? $"{d} 錯誤"
            : $"{catalog.DimensionLabel(d, s.Profile)} 池 {pools[d]} → {counts[i]}");
    }
    turn.Emit(new ToolResultEvent(turn.CurrentCallId ?? Guid.NewGuid().ToString("N"), ToolNames.SearchPresets, string.Join("・", summary), presetsOut));
    return JsonSerializer.Serialize(new { results }, Json);
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~KnowledgePluginTests"`
Expected: 9 條全 PASS。

- [ ] **Step 5: 跑整個測試專案，找出因簽名改變而壞掉的既有測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: `HistoryTrimmerTests` 裡兩條用舊形狀的案例可能仍過（壓縮吃到舊形狀時回 null 而跳過，那正是既有的行為），其餘全過。若有 `AgenticOrchestratorTests` 或 `EndpointTests` 以 `Invoke(hist, k, "Knowledge", "SearchPresets", new { dimension = ..., query = ... })` 呼叫，改成 `new { queries = new[] { new { dimension = "...", query = "..." } } }`。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs src/PromptCopilot.Api.Tests
git commit -m "feat(plugins): SearchPresets takes a batch of (dimension, query); one embedding call per turn"
```

---

### Task 3: `HistoryTrimmer` 壓縮新形狀

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/HistoryTrimmer.cs:40-45`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/HistoryTrimmerTests.cs:32,70-78`

**Interfaces:**
- Consumes: Task 2 的回傳形狀 `{"results":[{dimension, query, grounded, poolSize, hits} | {dimension, query, error}]}`。
- Produces: 壓縮後 `{"results":[{dimension, poolSize, hits:[{id,title}]} | {dimension, error}]}`；形狀不對回 `null`（整段跳過）。

- [ ] **Step 1: 改既有案例、加兩條新案例**

`HistoryTrimmerTests.cs` 第 32 行的 `ToolResult("SearchPresets", ...)` 改成新形狀：

```csharp
h.Add(ToolResult("SearchPresets", """{"results":[{"dimension":"style","query":"寫實","grounded":false,"poolSize":4455,"hits":[{"id":1,"title":"a","positive":"long text"},{"id":2,"title":"b","positive":"x"}]}]}"""));
```

該測試的斷言 `Assert.Contains("\"title\":\"a\"", result); Assert.DoesNotContain("long text", result);` 不變，另加：

```csharp
Assert.Contains("\"poolSize\":4455", result); Assert.DoesNotContain("\"query\"", result);
```

新增兩條：

```csharp
[Fact]
public void CompressTurn_keeps_error_items_and_drops_hit_bodies_per_result()
{
    var h = new ChatHistory();
    h.Add(ToolResult("SearchPresets", """{"results":[{"dimension":"hair","query":"x","error":"維度 hair 不存在"},{"dimension":"scene","query":"稻田","grounded":true,"poolSize":6752,"hits":[{"id":3,"title":"c","positive":"very long"}]}]}"""));

    HistoryTrimmer.CompressTurn(h, 0);

    var result = h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
    Assert.Contains("\"error\":\"維度 hair 不存在\"", result);
    Assert.Contains("\"title\":\"c\"", result);
    Assert.DoesNotContain("very long", result);
}

[Fact]
public void CompressTurn_leaves_legacy_single_dimension_shape_untouched()
{
    const string legacy = """{"dimension":"style","poolSize":4455,"hits":[{"id":1,"title":"a","positive":"long text"}]}""";
    var h = new ChatHistory();
    h.Add(ToolResult("SearchPresets", legacy));

    HistoryTrimmer.CompressTurn(h, 0);

    Assert.Equal(legacy, h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString());
}
```

第 70–78 行既有的「形狀不對就原樣保留」案例（`[{"dimension":"style","hits":[]}]`）不用動。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~HistoryTrimmerTests"`
Expected: 三條都 FAIL。改過的案例與 `keeps_error_items`：新形狀沒有頂層 `hits`，舊程式當成形狀不對而原樣保留，`long text`／`very long` 還在。`legacy` 案例：舊程式會壓縮單維度形狀、丟掉 `positive`，所以 `Assert.Equal(legacy, …)` 不成立。

- [ ] **Step 3: 改 `CompressResult` 的 `SearchPresets` 分支**

```csharp
case ToolNames.SearchPresets:
{
    if (JsonNode.Parse(json) is not JsonObject root || root["results"] is not JsonArray results) return null;
    var slim = new JsonArray(results.OfType<JsonObject>().Select(r =>
    {
        if (r["error"] is not null)
            return (JsonNode)new JsonObject { ["dimension"] = r["dimension"]?.DeepClone(), ["error"] = r["error"]!.DeepClone() };
        var hits = r["hits"] as JsonArray ?? new JsonArray();
        var ids = new JsonArray(hits.OfType<JsonObject>().Select(x => (JsonNode)new JsonObject { ["id"] = x["id"]?.DeepClone(), ["title"] = x["title"]?.DeepClone() }).ToArray());
        return new JsonObject { ["dimension"] = r["dimension"]?.DeepClone(), ["poolSize"] = r["poolSize"]?.DeepClone(), ["hits"] = ids };
    }).ToArray());
    return new JsonObject { ["results"] = slim }.ToJsonString(Json);
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~HistoryTrimmerTests"`
Expected: 全 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Orchestration/HistoryTrimmer.cs src/PromptCopilot.Api.Tests/Orchestration/HistoryTrimmerTests.cs
git commit -m "feat(orchestration): compress the batched SearchPresets result shape"
```

---

### Task 4: 預算 16 與強制定稿提示

**Files:**
- Modify: `src/PromptCopilot.Api/Configuration/Options.cs:30`
- Modify: `src/PromptCopilot.Api/appsettings.json:5`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs:230`
- Test: `src/PromptCopilot.Api.Tests/Configuration/OptionsTests.cs:17`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs:434-455`

**Interfaces:**
- Produces: `OrchestratorOptions.MaxToolCallsPerTurn` 預設 16。
- Produces: 強制定稿的系統提示含「covered」。

- [ ] **Step 1: 改測試**

`OptionsTests.cs` 第 17 行：`Assert.Equal(16, o.MaxToolCallsPerTurn);`

`AgenticOrchestratorTests.cs` 的 `Budget_exhausted_forces_finalize_with_only_that_tool` 第二個 `ThenAsync` 裡，`Assert.Contains("定稿", hist.Last().Content!);` 之後加：

```csharp
Assert.Contains("covered", hist.Last().Content!);
Assert.Equal(AuthorRole.System, hist.Last().Role);
```

（檔頭若沒有 `using Microsoft.SemanticKernel.ChatCompletion;` 就加上。）

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~OptionsTests|FullyQualifiedName~Budget_exhausted"`
Expected: 兩條 FAIL（8 ≠ 16；提示沒有 covered）。

- [ ] **Step 3: 改三個檔案**

`Options.cs` 第 30 行：`public int MaxToolCallsPerTurn { get; set; } = 16;`

`appsettings.json` 第 5 行的 `"MaxToolCallsPerTurn": 8` 改 `16`。

`AgenticOrchestrator.cs` 第 230 行：

```csharp
turn.Session.ChatHistory.AddSystemMessage("tool 呼叫預算已用盡。請立即以現有資訊呼叫 FinalizePrompt 定稿，不要再檢索。facetStates 依使用者原話標記：使用者講過的 facet 標 covered，真的沒講的才是 missing。");
```

- [ ] **Step 4: 跑整個測試專案**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全 PASS（整合測試 skip）。`bin/Debug` 下的 appsettings 副本是建置產物，不用手改。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Configuration/Options.cs src/PromptCopilot.Api/appsettings.json src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs src/PromptCopilot.Api.Tests/Configuration/OptionsTests.cs src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs
git commit -m "feat(orchestration): tool budget 16; forced finalize marks user-stated facets covered"
```

---

### Task 5: `system.md` 批次語意

**Files:**
- Modify: `src/PromptCopilot.Api/Prompts/system.md`（「## 流程」第 1 條）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`（若存在；不存在就在 `AgenticOrchestratorTests` 旁新增一個小檔）

**Interfaces:**
- Consumes: `SystemPromptBuilder.Build(session, tools)` 回 `(string prompt, string version)`（見 `AgenticOrchestrator.cs:87`）。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs` 已有 `private static SystemPromptBuilder Make(int offeredLimit = 24)`（讀 `AppContext.BaseDirectory/Prompts/system.md`）。在該檔加一條：

```csharp
[Fact]
public void Flow_rule_asks_for_one_batched_SearchPresets_call()
{
    var s = new Session("s");
    var (prompt, _) = Make().Build(s, ToolNames.Always);
    Assert.Contains("用一次 `SearchPresets` 帶上所有適用的維度", prompt);
    Assert.DoesNotContain("分兩次呼叫", prompt);
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~Flow_rule_asks_for_one_batched"`
Expected: FAIL（現行文字是「對每個適用的維度呼叫 `SearchPresets`」）。

- [ ] **Step 3: 改 system.md 第 1 條**

把：

> 1. **使用者第一次描述題材**：先 `SetProfile`（動物歸 object）。接著對每個適用的維度呼叫 `SearchPresets`——使用者講過的維度，query 逐字用他的原話；沒講的維度，依整體畫面推想，並且**分兩次呼叫給對比的方向**（例：「寫實攝影」與「日系動漫插畫」）。需要風格參考時呼叫 `SearchSimilarPrompts`。然後判斷：資訊足夠就 `FinalizePrompt`；真的缺了沒有就無法定稿的關鍵資訊，才 `AskUser`。**使用者在描述題材時不要用 `Discuss`**，要推進流程。

改成：

> 1. **使用者第一次描述題材**：先 `SetProfile`（動物歸 object）。接著**用一次 `SearchPresets` 帶上所有適用的維度**——使用者講過的維度一個項目，query 逐字用他的原話；沒講的維度兩個項目，依整體畫面推想兩個對比方向（例：「寫實攝影」與「日系動漫插畫」）。不要一個維度一次呼叫。需要風格參考時呼叫 `SearchSimilarPrompts`。然後判斷：資訊足夠就 `FinalizePrompt`；真的缺了沒有就無法定稿的關鍵資訊，才 `AskUser`。**使用者在描述題材時不要用 `Discuss`**，要推進流程。

「## 提示詞規則」裡「`SearchPresets` 回的片段標了…」那條不用改（片段的標記方式沒變）。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全 PASS。`prompt_version` 會變（它是 system.md 的雜湊），這是預期。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Prompts/system.md src/PromptCopilot.Api.Tests
git commit -m "feat(prompts): one batched SearchPresets call per turn"
```

---

### Task 6: 文件同步

**Files:**
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md:100`（§4.2 工具表）、`:286`（§4.5 預算列）、`:697-712`（§9）、`:1016` 附近（§15 決定紀錄）
- Modify: `docs/known-issues.md:8`（表格列）、`:17-50`（#1 段落）、`:119` 之後（已修正）
- Modify: `docs/eval-cases.md:30` 之後（新案例）、`:84`（P3 那列的備註）
- Modify: `docs/superpowers/specs/2026-09-24-batch-search-presets-design.md:4`（狀態）

**Interfaces:** 無程式介面。內容以 spec §5.3、§5.4 為準。

- [ ] **Step 1: 主規格**

§4.2 第 100 行改成：

```markdown
| `KnowledgePlugin.SearchPresets` | `queries: {dimension, query}[]`（≤ 12） | RAG 2，**分維度檢索** `prompt_knowledge_presets`：一次呼叫帶本輪所有要查的維度，每項一個維度專屬語句，同維度可重複（對比方向），見 §9 |
```

§4.5 第 286 行 `ToolBudgetFilter` 那列的「預設 8」改「預設 16」。

§9（第 700 行起「因此 agent 呼叫 `SearchPresets` 時」的清單）：

- 第一點「一次呼叫一個維度，`facetIds` 給該維度在當前 profile 下的 facet 集合。」改成「一次呼叫帶本輪所有要查的維度（`queries[]`，每項一個維度），`facetIds` 由伺服器依 profile 導出。」
- 第二點結尾「分兩次呼叫」改「在同一次呼叫裡放兩個項目」。
- 「落地方式：session ledger」那點裡「`SearchPresets` 每次呼叫照實回傳自己維度的結果，不做跨維度去重（它本來也看不到別的維度）」改成「`SearchPresets` 每個項目照實回傳自己維度的結果，不做跨維度去重」。
- 最後一段「多個維度的查詢語句合併為單次 `embed_batch` 呼叫。」改成「多個維度的查詢語句合併為單次 `embed_batch` 呼叫（2026-09-24 起 C# 端也是：批次簽名的緣由見 [批次 SearchPresets 設計](2026-09-24-batch-search-presets-design.md)）。」

§15 決定紀錄，`SearchPresets` 參數那列之後加兩列：

```markdown
| `SearchPresets` 批次簽名 | `queries: {dimension, query}[]`，一輪一次呼叫 | 逐維度呼叫的規則跟預算 8 在算術上不相容（人像 6 維 + 對比方向 > 8），第一輪就強制定稿；Python 管線本來就是一次 `embed_batch`（known-issues #1、[批次設計](2026-09-24-batch-search-presets-design.md)） |
| `MaxToolCallsPerTurn` | 16（原 8） | 批次後預期一輪 4–5 次；16 是模型仍逐維度呼叫時的保險，不是設計目標 |
```

- [ ] **Step 2: known-issues**

表格第 8 行的 #1 整列刪除。第 17–50 行的「## 1. …」整段搬到「## 已修正」之下、「### 2.」之前，標題改 `### 1. 人像題材第一輪常被強制定稿，整個 session 不再追問`，「修正方向」小節換成：

```markdown
**修正**（分支 `fix/batch-search-presets`，commit hash 待 merge 後補；設計見 [批次 SearchPresets 設計](superpowers/specs/2026-09-24-batch-search-presets-design.md)）：採原本列的方向 2 + 3，方向 1 當保險。

- `SearchPresets` 改收 `queries: {dimension, query}[]`，一輪的檢索只花一次工具呼叫，embedding 走一次 batch；`system.md` 第 1 條與工具描述同步。
- `MaxToolCallsPerTurn` 8 → 16。批次後第一輪預期 4–5 次呼叫，16 只是模型仍拆開呼叫時的餘裕。
- 強制定稿提示要求 `facetStates` 依使用者原話標 covered；`FinalizePrompt` 本來就收 `facetStates`，不需要多一次 `SetFacetStates`。

**驗收**：待 merge 後依設計 §7 跑，結果記到 `docs/eval-cases.md`。
```

「驗收」原本那段（「用上表兩句重跑…」）刪掉，由上面這段取代。文首說明「子專案 2 的效果調整…」那句保留。

- [ ] **Step 3: eval-cases**

第 30 行（#23）之後加一列：

```markdown
| 24 | 有細節但缺風格與鏡頭的人像描述：「一個老爺爺在稻田裡面喝茶，遠處是房子，太陽很大，老爺爺有著白色捲髮，穿著白色短衣」 | `final.kind = ask`；audit 無 `Tool_Budget_Exhausted`；`Turn_Completed.toolCalls` ≤ 5；只有一張 `SearchPresets` 工具卡，摘要列出每個維度的候選池筆數；儀表板場景／樣貌／穿著為 covered | | | |
```

第 84 行 P3 那列末尾加：「→ known-issues #1 已在 `fix/batch-search-presets` 修正，merge 後解除延後。」

- [ ] **Step 4: spec 狀態**

`2026-09-24-batch-search-presets-design.md` 第 4 行「狀態：設計已確認，待實作」改「狀態：已實作（分支 `fix/batch-search-presets`），待瀏覽器驗收（§7）」。

- [ ] **Step 5: 檢查沒有漏掉的舊說法**

Run: `grep -rn "一次呼叫一個維度\|一次只查一個維度\|分兩次呼叫\|預設 8" docs src/PromptCopilot.Api --include=*.md --include=*.cs`
Expected: 只剩 `docs/superpowers/plans/` 與 `docs/superpowers/specs/2026-09-22-*` 的歷史文件命中（歷史計畫不改）。若 `src/` 或主規格還有命中，回頭改。

- [ ] **Step 6: Commit**

```bash
git add docs
git commit -m "docs: batched SearchPresets in the main spec, known-issues #1 fixed, eval case 24"
```

---

### Task 7: 全分支驗證與交接

**Files:** 無新檔。

- [ ] **Step 1: 全部測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全 PASS，整合測試 skip。

若開發機 db 容器在跑（`docker compose ps` 看 `db` 為 healthy）：
Run（PowerShell）：`$env:PC_INTEGRATION="1"; $env:PC_TEST_DB="Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=change_me"; dotnet test src/PromptCopilot.Api.Tests`
Expected: `RepositoryIntegrationTests` 全 PASS；`GeminiContractTests` 沒有 key 會失敗，那組本來就這樣，回報時說明即可。

- [ ] **Step 2: 建置 API 專案確認沒有警告變錯誤**

Run: `dotnet build src/PromptCopilot.Api -warnaserror`
Expected: 成功。若專案本來就有既有警告導致失敗，改跑 `dotnet build src/PromptCopilot.Api` 並回報警告清單。

- [ ] **Step 3: 交接**

回報：commit 清單、測試輸出摘要、瀏覽器驗收（spec §7）尚未跑且需要 `fix/hnsw-iterative-scan` 先合併、API 重啟。
