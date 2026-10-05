using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class RecommendationServiceTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private sealed class FakeEmbeddings : IEmbeddingClient
    {
        public List<string> Texts { get; } = new();
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct)
        {
            Texts.AddRange(texts);
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[768]).ToList());
        }
    }

    /// <summary>記每次呼叫的參數；回什麼由 Script 決定（key：維度第一個 facet + 是否帶錨）。</summary>
    private sealed class FakePresets() : PresetRepository(null!)
    {
        public List<(string firstFacet, IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take)> Calls { get; } = new();
        public Dictionary<(string firstFacet, bool anchored), IReadOnlyList<PresetCandidate>> Script { get; } = new();
        public List<(string facet, double maxDist, int take)> SimilarCalls { get; } = new();
        public Dictionary<string, IReadOnlyList<PresetCandidate>> SimilarScript { get; } = new();     // key：facetId
        public Dictionary<string, long> FacetPools { get; } = new();                                    // 沒設 → 0 → 跳過近似
        public string? ThrowOn { get; set; }                                                               // 維度第一個 facet 等於它就丟例外
        public Dictionary<(long, string), float[]> Vectors { get; } = new();
        public List<(IReadOnlyList<long> ids, IReadOnlyList<string> facets)> VectorCalls { get; } = new();
        public override Task<IReadOnlyList<PresetCandidate>> RecommendAsync(float[] query, IReadOnlyList<string> dimensionFacets,
            IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take, CancellationToken ct)
        {
            if (ThrowOn == dimensionFacets[0]) throw new InvalidOperationException("db down");
            Calls.Add((dimensionFacets[0], anchorFacets, anchorTags, take));
            return Task.FromResult(Script.GetValueOrDefault((dimensionFacets[0], anchorFacets.Count > 0), Array.Empty<PresetCandidate>()));
        }
        public override Task<long> FacetPoolSizeAsync(string facetId, CancellationToken ct) => Task.FromResult(FacetPools.GetValueOrDefault(facetId, 0L));
        public override Task<IReadOnlyList<PresetCandidate>> RecommendSimilarAsync(float[] anchor, string facetId, IReadOnlyList<string> dimensionFacets, double maxDist, int take, CancellationToken ct)
        {
            SimilarCalls.Add((facetId, maxDist, take));
            return Task.FromResult(SimilarScript.GetValueOrDefault(facetId, Array.Empty<PresetCandidate>()));
        }
        public override Task<IReadOnlyDictionary<(long PresetId, string FacetId), float[]>> FacetVectorsAsync(IReadOnlyList<long> presetIds, IReadOnlyList<string> facetIds, CancellationToken ct)
        {
            VectorCalls.Add((presetIds, facetIds));
            IReadOnlyDictionary<(long PresetId, string FacetId), float[]> r = Vectors.Where(kv => presetIds.Contains(kv.Key.Item1) && facetIds.Contains(kv.Key.Item2))
                .ToDictionary(kv => (kv.Key.Item1, kv.Key.Item2), kv => kv.Value);
            return Task.FromResult(r);
        }
    }

    private static PresetCandidate Set(long id, string title, params (string facet, string tags)[] facetTags) => Set(id, title, 0.21, facetTags);
    private static PresetCandidate Set(long id, string title, double dist, params (string facet, string tags)[] facetTags) =>
        new(id, title, facetTags.Select(f => f.facet).ToList(),
            facetTags.ToDictionary(f => f.facet, f => (IReadOnlyList<string>)f.tags.Split(", ")), "https://img", "civitai:1:0", dist);

    private static (RecommendationService svc, Session s, FakeEmbeddings embed, FakePresets presets) Make(params (string facet, string tags)[] covered) =>
        MakeWith(new OrchestratorOptions(), covered);

    private static (RecommendationService svc, Session s, FakeEmbeddings embed, FakePresets presets) MakeWith(OrchestratorOptions o, params (string facet, string tags)[] covered)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddSystemMessage("sys");
        s.ChatHistory.AddUserMessage("一個少女穿涼鞋");
        s.ApplyFacetStates(covered.ToDictionary(c => c.facet, _ => FacetState.Covered), Catalog, covered.ToDictionary(c => c.facet, c => c.tags));
        var embed = new FakeEmbeddings(); var presets = new FakePresets();
        return (new RecommendationService(Catalog, embed, presets, o), s, embed, presets);
    }

    /// <summary>τ 很小：第 2 位幾乎必取有效名次次小的，測試結果不受抽樣影響。</summary>
    private static OrchestratorOptions Greedy() => new() { RecommendationTemperature = 0.01 };

    private static readonly PresetCandidate[] FourSandals =
    {
        Set(1, "A", ("clothing.footwear", "sandals"), ("clothing.upper", "shirt")),
        Set(2, "B", ("clothing.footwear", "sandals"), ("clothing.lower", "skirt")),
        Set(3, "C", ("clothing.footwear", "sandals"), ("clothing.upper", "tank top")),
        Set(4, "D", ("clothing.footwear", "sandals"), ("clothing.lower", "shorts")),
    };

    private static FinalizedOutcome Finalized(string positive) => new(new FinalPrompt(positive, "lowres", "t", "i"));

    [Fact]
    public async Task Finalized_outcome_queries_every_dimension_of_the_profile()
    {
        var (svc, s, _, presets) = Make();
        await svc.BuildAsync(s, Finalized("1girl"), 2, default);
        Assert.Equal(6, presets.Calls.Count);
        Assert.Equal("clothing.head", presets.Calls[5].firstFacet);
    }

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

    [Fact]
    public async Task Finalized_adds_positive_tags_to_the_anchors_of_dimensions_with_covered_facets()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals"));
        await svc.BuildAsync(s, Finalized("masterpiece, (best quality:1.2), 1girl, sandals, white socks"), 1, default);
        var clothing = presets.Calls.First(c => c.firstFacet == "clothing.head");
        Assert.Equal(new[] { "sandals", "1girl", "white socks" }, clothing.anchorTags);          // 基礎畫質詞（含加權寫法）不當錨
        Assert.Empty(presets.Calls.First(c => c.firstFacet == "style.genre").anchorTags);       // style 沒有 covered
    }

    /// <summary>設計 §5.3：查詢向量＝使用者原話串接的最後 500 字，採用句不算；一輪只嵌入一次。</summary>
    [Fact]
    public async Task Query_text_joins_user_messages_skips_adoption_sentences_and_embeds_once()
    {
        var (svc, s, embed, presets) = Make();
        s.ChatHistory.AddAssistantMessage("好的");
        s.ChatHistory.AddUserMessage("採用〈和風女僕〉（知識庫 #41720）：上半身照它的（purple kimono）。");
        s.ChatHistory.AddUserMessage(new string('黃', 600) + "昏街頭");
        presets.Script[("style.genre", false)] = new[] { Set(9, "x", ("style.genre", "a"), ("style.palette", "b")) };
        await svc.BuildAsync(s, Finalized("1girl"), 1, default);
        var q = Assert.Single(embed.Texts);
        Assert.Equal(500, q.Length);
        Assert.EndsWith("昏街頭", q);
        Assert.DoesNotContain("採用〈", q);
        Assert.Equal("一個少女穿涼鞋\n" + new string('黃', 600) + "昏街頭", RecommendationService.JoinedUserText(s.ChatHistory));
    }

    [Fact]
    public void Similar_anchor_text_normalizes_dedups_and_joins_the_facet_tags()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Covered }, Catalog,
            new Dictionary<string, string> { ["clothing.footwear"] = "(Slippers:1.2), flip_flops, slippers" });
        Assert.Equal("slippers, flip flops", RecommendationService.SimilarAnchorText(s, "clothing.footwear"));
    }

    [Fact]
    public async Task Final_card_row_has_two_relevant_sets_and_one_explore_set_with_reasons_and_batch_one()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = FourSandals.Take(2).ToList();
        presets.Script[("clothing.head", false)] = new[]
        {
            Set(5, "靴子長裙", ("clothing.footwear", "boots"), ("clothing.lower", "long skirt")),
            Set(6, "帽T牛仔褲", ("clothing.upper", "hoodie"), ("clothing.lower", "jeans")),         // 沒有鞋履 tag：取代不了使用者講的，不能當探索位
        };
        var e = await svc.BuildAsync(s, Finalized("1girl, sandals"), 4, default);
        var d = e!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(1, d.Batch);
        Assert.False(d.Anchored); Assert.False(d.Similar); Assert.Empty(d.AnchorTags);
        Assert.Equal(new long[] { 1, 2, 5 }, d.Sets.Select(x => x.PresetId));
        Assert.Equal(new[] { "anchored", "anchored", "explore" }, d.Sets.Select(x => x.Reason));
        Assert.Equal(new[] { "sandals" }, d.Sets[0].AnchorTags);
        Assert.Equal(1.0, d.Sets[0].Prob); Assert.Equal(0, d.Sets[0].Rank);
        Assert.Equal(0, d.Sets[2].Rank);
        Assert.Equal(4, s.LatestSlateTurn);
        Assert.Equal(1, s.SlateBatch("clothing"));
        Assert.Equal(3, s.SeenFor("clothing").Count);
        Assert.All(presets.Calls, c => Assert.Equal(30, c.take));                                     // 每層取 PoolSize
        Assert.Equal(new[] { "clothing.footwear" }, presets.VectorCalls.Last().facets);                 // 比較用 facet = covered
    }

    /// <summary>final-review finding #8：定稿卡也要走近似錨那條路，不是只有追問卡（Literal_anchor_short_of_two_hits...）測過。
    /// 字面錨沒有命中（沒設 `("clothing.head", true)` 的 Script），近似錨找到兩套涼鞋，該套要標 similar、AnchorTags 是有貢獻的錨。</summary>
    [Fact]
    public async Task Final_card_uses_the_similar_tier_when_the_literal_anchor_has_no_hits()
    {
        var (svc, s, embed, presets) = MakeWith(Greedy(), ("clothing.footwear", "slippers"));
        presets.FacetPools["clothing.footwear"] = 338;
        presets.SimilarScript["clothing.footwear"] = new[]
        {
            Set(2, "涼鞋一", 0.18, ("clothing.footwear", "sandals"), ("clothing.upper", "a")),
            Set(3, "涼鞋二", 0.21, ("clothing.footwear", "sandals"), ("clothing.lower", "b")),
        };
        var d = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        var set = d.Sets.First(x => x.Reason == "similar");    // 只有近似錨兩套候選，兩個相關位都會是 similar
        Assert.Equal(new[] { "slippers" }, set.AnchorTags);
        Assert.Equal(new[] { "一個少女穿涼鞋", "slippers" }, embed.Texts);                        // 錨向量與查詢向量同一次 embed
    }

    [Fact]
    public async Task Final_card_queries_every_tier_even_when_the_literal_anchor_has_enough_hits()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.FacetPools["clothing.footwear"] = 338;
        presets.Script[("clothing.head", true)] = FourSandals;
        await svc.BuildAsync(s, Finalized("1girl"), 1, default);
        Assert.Equal(("clothing.footwear", 0.30, 30), Assert.Single(presets.SimilarCalls));
        Assert.Contains(presets.Calls, c => c.firstFacet == "clothing.head" && c.anchorTags.Count > 0);
        Assert.Contains(presets.Calls, c => c.firstFacet == "clothing.head" && c.anchorTags.Count == 0);
    }

    [Fact]
    public async Task Seen_sets_are_pushed_back_on_the_next_final_card()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = FourSandals;
        var first = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        var second = (await svc.BuildAsync(s, Finalized("1girl"), 3, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(new long[] { 1, 2 }, first.Sets.Select(x => x.PresetId));
        Assert.Equal(new long[] { 3, 4 }, second.Sets.Select(x => x.PresetId));
        Assert.Equal(1, second.Batch);
    }

    [Fact]
    public async Task Identical_tag_sets_collapse_into_one_candidate()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = new[] { FourSandals[0], Set(9, "A 的雙胞胎", ("clothing.upper", "Shirt"), ("clothing.footwear", "sandals")), FourSandals[1] };
        var d = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "clothing");
        Assert.Equal(new long[] { 1, 2 }, d.Sets.Select(x => x.PresetId));
    }

    [Fact]
    public async Task Explore_compares_every_dimension_facet_when_nothing_is_covered()
    {
        var (svc, s, _, presets) = MakeWith(Greedy());
        presets.Script[("style.genre", false)] = new[]
        {
            Set(11, "油畫", ("style.genre", "oil painting"), ("style.palette", "muted")),
            Set(12, "動漫", ("style.genre", "anime"), ("style.palette", "vivid")),
            Set(13, "水彩", ("style.genre", "watercolor"), ("style.palette", "pastel")),
        };
        var d = (await svc.BuildAsync(s, Finalized("1girl"), 1, default))!.Dimensions.Single(x => x.Dimension == "style");
        Assert.Equal(new[] { "query", "query", "explore" }, d.Sets.Select(x => x.Reason));
        Assert.Equal(13, d.Sets[2].PresetId);
        Assert.Equal(Catalog.FacetsOf("portrait", "style"), presets.VectorCalls.First(c => c.ids.Contains(13)).facets);
    }

    [Fact]
    public async Task Failed_final_card_records_nothing_as_seen()
    {
        // Review Focus 2
        var (svc, s, _, presets) = MakeWith(Greedy());
        presets.Script[("style.genre", false)] = new[] { Set(11, "油畫", ("style.genre", "oil painting"), ("style.palette", "muted")) };
        presets.ThrowOn = "clothing.head";                                                            // 最後一個維度才炸
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BuildAsync(s, Finalized("1girl"), 1, default));
        Assert.Empty(s.SeenFor("style"));
    }

    [Fact]
    public async Task Next_returns_batch_two_avoids_the_first_batch_and_reuses_the_final_tags_as_anchors()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = FourSandals;
        await svc.BuildAsync(s, Finalized("masterpiece, 1girl, sandals, white socks"), 1, default);
        var next = await svc.NextAsync(s, "clothing", default);
        Assert.Equal(2, next.Batch);
        Assert.Equal(new long[] { 3, 4 }, next.Sets.Select(x => x.PresetId));
        Assert.Equal(2, s.SlateBatch("clothing"));
        Assert.Contains("white socks", presets.Calls.Last(c => c.anchorTags.Count > 0).anchorTags);
        Assert.DoesNotContain("masterpiece", presets.Calls.Last(c => c.anchorTags.Count > 0).anchorTags);
    }

    /// <summary>final-review finding #8：換一批查資料庫失敗時不能悄悄把批次往前推或多記看過——使用者根本沒看到這批。</summary>
    [Fact]
    public async Task Next_failure_does_not_advance_the_batch_or_record_anything_as_seen()
    {
        var (svc, s, _, presets) = MakeWith(Greedy(), ("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = FourSandals.Take(2).ToList();
        presets.Script[("clothing.head", false)] = new[]
        {
            Set(5, "靴子長裙", ("clothing.footwear", "boots"), ("clothing.lower", "long skirt")),
            Set(6, "帽T牛仔褲", ("clothing.upper", "hoodie"), ("clothing.lower", "jeans")),
        };
        await svc.BuildAsync(s, Finalized("1girl, sandals"), 4, default);   // 2 相關 + 1 探索，SeenFor 記 3 筆
        presets.ThrowOn = "clothing.head";
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NextAsync(s, "clothing", default));
        Assert.Equal(1, s.SlateBatch("clothing"));
        Assert.Equal(3, s.SeenFor("clothing").Count);
    }

    [Fact]
    public async Task Next_without_candidates_returns_an_empty_row_and_does_not_advance()
    {
        var (svc, s, _, _) = MakeWith(Greedy());
        s.BeginSlate(1, Array.Empty<string>());
        var next = await svc.NextAsync(s, "style", default);
        Assert.Empty(next.Sets);
        Assert.Equal(1, next.Batch);
        Assert.Equal("風格", next.Label);
        Assert.Equal(0, s.SlateBatch("style"));
        Assert.Empty(s.SeenFor("style"));
    }

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
}
