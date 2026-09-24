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
