using System.Text.Json;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
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

    /// <summary>記下每次 Search／PoolSize 的參數。命中由 <see cref="Hits"/> 決定：key 是 facetIds 的第一個 id。
    /// <see cref="SearchFacetSets"/>／<see cref="PoolFacetSets"/> 記完整的 facetIds，分得出單一 facet 與整個維度。</summary>
    private sealed class FakePresets() : PresetRepository(null!)
    {
        public List<(string firstFacet, int k, float queryIndex)> Searches { get; } = new();
        public List<string> PoolCalls { get; } = new();
        public List<IReadOnlyList<string>> SearchFacetSets { get; } = new();
        public List<IReadOnlyList<string>> PoolFacetSets { get; } = new();
        public Dictionary<string, IReadOnlyList<PresetHit>> Hits { get; } = new();
        public Dictionary<string, long> Pools { get; } = new();

        public override Task<IReadOnlyList<PresetHit>> SearchAsync(float[] query, IReadOnlyList<string> facetIds, int k, CancellationToken ct)
        {
            Searches.Add((facetIds[0], k, query[0]));
            SearchFacetSets.Add(facetIds.ToArray());
            return Task.FromResult(Hits.GetValueOrDefault(facetIds[0], Array.Empty<PresetHit>()));
        }
        public override Task<long> PoolSizeAsync(IReadOnlyList<string> facetIds, CancellationToken ct)
        {
            PoolCalls.Add(facetIds[0]);
            PoolFacetSets.Add(facetIds.ToArray());
            return Task.FromResult(Pools.GetValueOrDefault(facetIds[0], 0L));
        }
    }

    private sealed class FakeHistories() : HistoryRepository(null!)
    {
        public IReadOnlyList<HistoryHit> Hits { get; set; } = Array.Empty<HistoryHit>();
        public override Task<IReadOnlyList<HistoryHit>> SearchAsync(float[] query, string profile, int k, CancellationToken ct) => Task.FromResult(Hits);
    }

    private static PresetHit Hit(long id, string title, string facet, double dist, string? sourceRef = null) =>
        new(id, title, "Cat", new[] { facet }, $"tags for {title}", null, null, dist, sourceRef);

    private static (KnowledgePlugin plugin, TurnContext turn, Session s, FakeEmbeddings embed, FakePresets presets, ChannelReader<AgentEvent> events)
        Make(string[]? covered = null, bool profile = true, FakeHistories? histories = null)
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
        return (new KnowledgePlugin(turn, Catalog, embed, presets, histories ?? new FakeHistories()), turn, s, embed, presets, ch.Reader);
    }

    private static SearchQuery[] Q(params (string d, string q)[] xs) => xs.Select(x => new SearchQuery(x.d, x.q)).ToArray();

    /// <summary>facet 項目：不帶 dimension。</summary>
    private static SearchQuery F(string facetId, string query) => new(null, query, facetId);

    private static List<JsonElement> Results(string json) => JsonDocument.Parse(json).RootElement.GetProperty("results").EnumerateArray().ToList();

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
    public async Task Up_to_24_items_run_and_25_is_an_error_without_embedding()
    {
        var (p, _, _, embed, _, _) = Make();
        var twentyFour = Enumerable.Range(0, 24).Select(i => new SearchQuery("style", $"q{i}")).ToArray();
        Assert.Equal(24, Results(await p.SearchPresetsAsync(twentyFour, default)).Count);
        Assert.Equal(24, Assert.Single(embed.Calls).Count);

        var twentyFive = Enumerable.Range(0, 25).Select(i => new SearchQuery("style", $"q{i}")).ToArray();
        var r = await p.SearchPresetsAsync(twentyFive, default);
        Assert.StartsWith("錯誤", r); Assert.Contains("24", r);
        Assert.Single(embed.Calls);                                                  // 25 項沒打 embedding
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

    /// <summary>圖片屬於原作者（docs/資料來源.md「署名機制」）：tool_result 的 preset 帶 sourceRef，前端縮圖據此標來源；
    /// ledger 也記下，定稿 tag 來源用得到。沒有 source_ref 的片段就是 null。</summary>
    [Fact]
    public async Task Event_presets_and_ledger_carry_the_source_ref()
    {
        var (p, _, s, _, presets, events) = Make();
        presets.Hits["style.genre"] = new[] { Hit(1, "寫實", "style.genre", 0.2, "civitai:12345:0"), Hit(2, "動漫", "style.genre", 0.22) };

        await p.SearchPresetsAsync(Q(("style", "寫實攝影")), default);

        var ev = Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events)));
        Assert.Equal(new string?[] { "civitai:12345:0", null }, ev.Presets!.Select(x => x.SourceRef));
        Assert.Equal("civitai:12345:0", s.Ledger.Get(1)!.SourceRef);
        Assert.Null(s.Ledger.Get(2)!.SourceRef);
    }

    [Fact]
    public async Task Kernel_binds_json_queries_argument_to_SearchQuery_array()
    {
        var (p, _, _, embed, presets, _) = Make();
        presets.Pools["style.genre"] = 10;
        var kernel = new Kernel();
        AgentKernelFactory.AddFiltered(kernel, "Knowledge", p, ToolNames.Always);
        var ka = new KernelArguments();
        ka["queries"] = JsonSerializer.SerializeToElement(new[] { new { dimension = "style", query = "寫實攝影" }, new { dimension = "style", query = "動漫插畫" } });

        var r = (await kernel.Plugins["Knowledge"][ToolNames.SearchPresets].InvokeAsync(kernel, ka)).ToString();

        var call = Assert.Single(embed.Calls);
        Assert.Equal(new[] { "寫實攝影", "動漫插畫" }, call);
        Assert.Equal(2, JsonDocument.Parse(r).RootElement.GetProperty("results").GetArrayLength());
    }

    /// <summary>2026-09-25 根因 1：一句「泳裝上衣與短褲與拖鞋」查整個 clothing 池撈到整套穿搭，單品進不了 ledger。
    /// facet 項目把候選池縮到單一 facet。</summary>
    [Fact]
    public async Task FacetId_item_narrows_the_pool_to_that_single_facet()
    {
        var (p, _, s, embed, presets, _) = Make();
        presets.Pools["clothing.footwear"] = 19;
        presets.Hits["clothing.footwear"] = new[] { Hit(5, "拖鞋", "clothing.footwear", 0.2) };

        var r = await p.SearchPresetsAsync(new[] { F("clothing.footwear", "拖鞋") }, default);

        Assert.Equal(new[] { "拖鞋" }, Assert.Single(embed.Calls));
        Assert.Equal(new[] { "clothing.footwear" }, Assert.Single(presets.SearchFacetSets));
        Assert.Equal(new[] { "clothing.footwear" }, Assert.Single(presets.PoolFacetSets));
        var item = Assert.Single(Results(r));
        Assert.Equal("clothing", item.GetProperty("dimension").GetString());
        Assert.Equal("clothing.footwear", item.GetProperty("facetId").GetString());
        Assert.Equal("拖鞋", item.GetProperty("query").GetString());
        Assert.Equal(19, item.GetProperty("poolSize").GetInt64());
        Assert.Equal(1, item.GetProperty("hits").GetArrayLength());
        Assert.Equal("clothing", Assert.Single(s.Ledger.Get(5)!.Hits).Dimension);  // ledger 仍記維度
    }

    [Fact]
    public async Task Dimension_item_has_no_facetId()
    {
        var (p, _, _, _, _, _) = Make();
        var item = Assert.Single(Results(await p.SearchPresetsAsync(Q(("style", "寫實")), default)));
        Assert.Equal("style", item.GetProperty("dimension").GetString());
        Assert.True(!item.TryGetProperty("facetId", out var f) || f.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("scene.season")]       // 存在，但只有 landscape 有；portrait 不適用
    [InlineData("clothing.shoes")]     // 不存在
    public async Task FacetId_outside_the_profile_is_an_item_error_and_the_rest_run(string facetId)
    {
        var (p, _, _, embed, presets, _) = Make();

        var results = Results(await p.SearchPresetsAsync(new[] { F(facetId, "秋天"), new SearchQuery("style", "寫實") }, default));

        Assert.Equal(2, results.Count);
        var error = results[0].GetProperty("error").GetString()!;
        Assert.Contains(facetId, error); Assert.Contains("portrait", error);
        Assert.False(results[1].TryGetProperty("error", out _));
        Assert.Equal(new[] { "寫實" }, Assert.Single(embed.Calls));
        Assert.Single(presets.Searches);
    }

    [Fact]
    public async Task FacetId_wins_over_a_mismatched_dimension()
    {
        var (p, _, _, _, presets, _) = Make();

        var item = Assert.Single(Results(await p.SearchPresetsAsync(new[] { new SearchQuery("style", "拖鞋", "clothing.footwear") }, default)));

        Assert.False(item.TryGetProperty("error", out _));
        Assert.Equal("clothing", item.GetProperty("dimension").GetString());
        Assert.Equal("clothing.footwear", item.GetProperty("facetId").GetString());
        Assert.Equal(new[] { "clothing.footwear" }, Assert.Single(presets.SearchFacetSets));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    public async Task Item_without_dimension_or_facetId_is_an_item_error(string? dimension, string? facetId)
    {
        var (p, _, _, embed, _, _) = Make();

        var results = Results(await p.SearchPresetsAsync(new[] { new SearchQuery(dimension, "拖鞋", facetId), new SearchQuery("style", "寫實") }, default));

        Assert.Contains("dimension 或 facetId", results[0].GetProperty("error").GetString());
        Assert.False(results[1].TryGetProperty("error", out _));
        Assert.Equal(new[] { "寫實" }, Assert.Single(embed.Calls));
    }

    [Fact]
    public async Task FacetId_item_with_blank_query_is_an_item_error()
    {
        var (p, _, _, embed, _, _) = Make();
        var item = Assert.Single(Results(await p.SearchPresetsAsync(new[] { F("clothing.footwear", "  ") }, default)));
        Assert.Contains("clothing.footwear", item.GetProperty("error").GetString());
        Assert.Empty(embed.Calls);
    }

    /// <summary>grounded 仍以維度判定：同維度任何一個 facet covered，這個維度的 facet 項目就能借入。</summary>
    [Fact]
    public async Task FacetId_item_is_grounded_by_its_dimension()
    {
        var (p, _, s, _, presets, _) = Make(covered: new[] { "clothing.upper" });
        presets.Hits["clothing.footwear"] = new[] { Hit(5, "拖鞋", "clothing.footwear", 0.2) };

        var item = Assert.Single(Results(await p.SearchPresetsAsync(new[] { F("clothing.footwear", "拖鞋") }, default)));

        Assert.Equal(KnowledgePlugin.KCovered, Assert.Single(presets.Searches).k);
        Assert.True(item.GetProperty("grounded").GetBoolean());
        Assert.Equal("可借入提示詞", item.GetProperty("hits")[0].GetProperty("usable").GetString());
        var hit = Assert.Single(s.Ledger.Get(5)!.Hits);
        Assert.Equal("clothing", hit.Dimension); Assert.True(hit.Grounded);
    }

    [Fact]
    public async Task Event_summary_uses_the_facet_label_for_facet_items()
    {
        var (p, _, _, _, presets, events) = Make();
        presets.Pools["clothing.footwear"] = 19; presets.Pools["style.genre"] = 4455;
        presets.Hits["clothing.footwear"] = new[] { Hit(5, "拖鞋", "clothing.footwear", 0.2) };

        await p.SearchPresetsAsync(new[] { F("clothing.footwear", "拖鞋"), new SearchQuery("style", "寫實"), F("scene.season", "秋天") }, default);

        var ev = Assert.IsType<ToolResultEvent>(Assert.Single(Drain(events)));
        Assert.Equal("鞋履 池 19 → 1・風格 池 4455 → 0・scene.season 錯誤", ev.Summary);   // 鞋履：facets.yaml 的 label
    }

    /// <summary>候選池計數的快取鍵是實際用的 facetIds 集合：同一 facet 兩項只數一次；
    /// facet 項目與同維度的 dimension 項目池不同，各數一次。</summary>
    [Fact]
    public async Task Pool_is_counted_once_per_facet_set()
    {
        var (p, _, _, _, presets, _) = Make();
        presets.Pools["clothing.footwear"] = 19; presets.Pools["clothing.head"] = 3000;   // 整個 clothing 維度的第一個 facet 是 clothing.head

        var results = Results(await p.SearchPresetsAsync(new[]
        {
            F("clothing.footwear", "拖鞋"), F("clothing.footwear", "夾腳拖"),
            new SearchQuery("clothing", "泳裝"), new SearchQuery("clothing", "運動服"),
        }, default));

        Assert.Equal(2, presets.PoolFacetSets.Count);
        Assert.Equal(new[] { "clothing.footwear" }, presets.PoolFacetSets[0]);
        Assert.Equal(Catalog.FacetsOf("portrait", "clothing"), presets.PoolFacetSets[1]);
        Assert.Equal(new long[] { 19, 19, 3000, 3000 }, results.Select(x => x.GetProperty("poolSize").GetInt64()));
        Assert.Equal(4, presets.Searches.Count);
    }

    /// <summary>2026-09-25 根因 3：模型自發用 <c>{"facetId":"appearance.hair","query":"銀色雙馬尾"}</c> 的形狀呼叫，沒有 dimension。</summary>
    [Fact]
    public async Task Kernel_binds_facetId_items_without_dimension()
    {
        var (p, _, _, embed, presets, _) = Make();
        var kernel = new Kernel();
        AgentKernelFactory.AddFiltered(kernel, "Knowledge", p, ToolNames.Always);
        var ka = new KernelArguments();
        ka["queries"] = JsonSerializer.SerializeToElement(new object[] { new { facetId = "appearance.hair", query = "銀色雙馬尾" }, new { dimension = "style", query = "寫實攝影" } });

        var r = (await kernel.Plugins["Knowledge"][ToolNames.SearchPresets].InvokeAsync(kernel, ka)).ToString();

        Assert.Equal(new[] { "銀色雙馬尾", "寫實攝影" }, Assert.Single(embed.Calls));
        Assert.Equal(new[] { "appearance.hair" }, presets.SearchFacetSets[0]);
        var results = Results(r);
        Assert.Equal("appearance", results[0].GetProperty("dimension").GetString());
        Assert.False(results[0].TryGetProperty("error", out _));
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
}
