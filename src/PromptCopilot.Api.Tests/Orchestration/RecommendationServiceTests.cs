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
        public override Task<IReadOnlyList<PresetCandidate>> RecommendAsync(float[] query, IReadOnlyList<string> dimensionFacets,
            IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take, CancellationToken ct)
        {
            Calls.Add((dimensionFacets[0], anchorFacets, anchorTags, take));
            return Task.FromResult(Script.GetValueOrDefault((dimensionFacets[0], anchorFacets.Count > 0), Array.Empty<PresetCandidate>()));
        }
    }

    private static PresetCandidate Set(long id, string title, params (string facet, string tags)[] facetTags) =>
        new(id, title, facetTags.Select(f => f.facet).ToList(),
            facetTags.ToDictionary(f => f.facet, f => (IReadOnlyList<string>)f.tags.Split(", ")), "https://img", "civitai:1:0", 0.21);

    private static (RecommendationService svc, Session s, FakeEmbeddings embed, FakePresets presets) Make(params (string facet, string tags)[] covered)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddSystemMessage("sys");
        s.ChatHistory.AddUserMessage("一個少女穿涼鞋");
        s.ApplyFacetStates(covered.ToDictionary(c => c.facet, _ => FacetState.Covered), Catalog, covered.ToDictionary(c => c.facet, c => c.tags));
        var embed = new FakeEmbeddings(); var presets = new FakePresets();
        return (new RecommendationService(Catalog, embed, presets, new OrchestratorOptions()), s, embed, presets);
    }

    private static AskOutcome Ask(params string[] dims) =>
        new("p", dims.Select(d => new AskItem(d, "q", new[] { Catalog.FacetsOf("portrait", d)[0] }, new[] { new OptionItem("a", "t", null), new OptionItem("b", "t", null) })).ToList());
    private static FinalizedOutcome Finalized(string positive) => new(new FinalPrompt(positive, "lowres", "t", "i"));

    [Fact]
    public async Task Ask_outcome_queries_only_the_asked_dimensions_in_ask_order()
    {
        var (svc, s, _, presets) = Make();
        presets.Script[("scene.location", false)] = new[] { Set(1, "雨夜", ("scene.location", "city street"), ("scene.weather", "rain")) };
        var e = await svc.BuildAsync(s, Ask("scene", "style"), 3, default);
        Assert.Equal(new[] { "scene.location", "style.genre" }, presets.Calls.Select(c => c.firstFacet));
        Assert.Equal("scene", Assert.Single(e!.Dimensions).Dimension);     // style 沒命中就不列
        Assert.Equal(3, e.TurnIndex);
    }

    [Fact]
    public async Task Finalized_outcome_queries_every_dimension_of_the_profile()
    {
        var (svc, s, _, presets) = Make();
        await svc.BuildAsync(s, Finalized("1girl"), 2, default);
        Assert.Equal(6, presets.Calls.Count);
        Assert.Equal("clothing.head", presets.Calls[5].firstFacet);
    }

    /// <summary>設計 §5.3：錨＝該維度 covered facet 的 FacetTags，正規化、去重；沒 covered 的維度不帶錨。</summary>
    [Fact]
    public async Task Anchors_come_from_facet_tags_normalized()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "Sandals, platform_footwear"), ("clothing.upper", "(white shirt:1.2)"));
        await svc.BuildAsync(s, Ask("clothing", "style"), 1, default);
        var clothing = presets.Calls.First(c => c.firstFacet == "clothing.head");                   // 帶錨那次；沒命中會再退回一次純向量
        Assert.Equal(new[] { "clothing.upper", "clothing.footwear" }, clothing.anchorFacets);       // facets.yaml 順序
        Assert.Equal(new[] { "white shirt", "sandals", "platform footwear" }, clothing.anchorTags);
        Assert.Equal(3, clothing.take);
        Assert.Empty(presets.Calls.Single(c => c.firstFacet == "style.genre").anchorTags);
    }

    [Fact]
    public async Task Finalized_adds_positive_tags_to_the_anchors_of_dimensions_with_covered_facets()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals"));
        await svc.BuildAsync(s, Finalized("masterpiece, 1girl, sandals, white socks"), 1, default);
        var clothing = presets.Calls.First(c => c.firstFacet == "clothing.head");
        Assert.Equal(new[] { "sandals", "masterpiece", "1girl", "white socks" }, clothing.anchorTags);
        Assert.Empty(presets.Calls.First(c => c.firstFacet == "style.genre").anchorTags);       // style 沒有 covered
    }

    [Fact]
    public async Task Anchored_query_with_two_hits_is_reported_anchored_with_the_matched_anchor_tags_only()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals, socks"));
        presets.Script[("clothing.head", true)] = new[]
        {
            Set(1, "夏日", ("clothing.upper", "front-tie top"), ("clothing.footwear", "platform sandals")),
            Set(2, "海邊", ("clothing.lower", "short shorts"), ("clothing.footwear", "sandals")),
        };
        var e = await svc.BuildAsync(s, Ask("clothing"), 1, default);
        var d = Assert.Single(e!.Dimensions);
        Assert.True(d.Anchored);
        Assert.Equal(new[] { "sandals" }, d.AnchorTags);                                  // socks 沒有任何候選命中
        Assert.Equal("人物穿著", d.Label);
        Assert.Single(presets.Calls);                                                     // 沒退回第二次查詢
        var set = d.Sets[0];
        Assert.Equal(6, set.Facets.Count);                                                // 該維度全部 facet，順序照 yaml
        Assert.Equal(new[] { "clothing.head", "clothing.upper", "clothing.lower", "clothing.footwear", "clothing.material", "clothing.accessories" }, set.Facets.Select(f => f.FacetId));
        Assert.Equal("covered", set.Facets[3].State); Assert.Equal(new[] { "platform sandals" }, set.Facets[3].Tags);
        Assert.Equal("missing", set.Facets[0].State); Assert.Empty(set.Facets[0].Tags);
        Assert.Equal("鞋履", set.Facets[3].Label);
        Assert.Equal(0.21, set.Dist); Assert.Equal("https://img", set.ImageUrl); Assert.Equal("civitai:1:0", set.SourceRef);
    }

    [Fact]
    public async Task Anchored_query_with_fewer_than_two_hits_falls_back_to_an_unanchored_query()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = new[] { Set(1, "只有一套", ("clothing.footwear", "sandals"), ("clothing.upper", "x")) };
        presets.Script[("clothing.head", false)] = new[] { Set(2, "最接近", ("clothing.upper", "shirt"), ("clothing.lower", "jeans")), Set(3, "b", ("clothing.upper", "a"), ("clothing.lower", "b")) };
        var e = await svc.BuildAsync(s, Ask("clothing"), 1, default);
        var d = Assert.Single(e!.Dimensions);
        Assert.False(d.Anchored); Assert.Empty(d.AnchorTags);
        Assert.Equal(new long[] { 2, 3 }, d.Sets.Select(x => x.PresetId));
        Assert.Equal(2, presets.Calls.Count);
        Assert.Empty(presets.Calls[1].anchorFacets);
    }

    [Fact]
    public async Task No_covered_facets_means_one_unanchored_query()
    {
        var (svc, s, _, presets) = Make();
        presets.Script[("style.genre", false)] = new[] { Set(9, "油畫", ("style.genre", "oil painting"), ("style.palette", "muted")) };
        var e = await svc.BuildAsync(s, Ask("style"), 1, default);
        Assert.False(Assert.Single(e!.Dimensions).Anchored);
        Assert.Single(presets.Calls);
    }

    [Fact]
    public async Task Returns_null_when_no_dimension_has_candidates()
    {
        var (svc, s, _, _) = Make();
        Assert.Null(await svc.BuildAsync(s, Ask("style"), 1, default));
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
    public async Task Facet_tags_for_facets_outside_the_dimension_are_ignored()
    {
        var (svc, s, _, presets) = Make();
        presets.Script[("style.genre", false)] = new[] { Set(9, "x", ("style.genre", "oil painting"), ("style.palette", "muted"), ("scene.weather", "rain")) };
        var e = await svc.BuildAsync(s, Ask("style"), 1, default);
        var set = Assert.Single(Assert.Single(e!.Dimensions).Sets);
        Assert.DoesNotContain(set.Facets, f => f.FacetId == "scene.weather");
        Assert.Equal(4, set.Facets.Count);
    }

    [Fact]
    public async Task Without_profile_returns_null_and_touches_nothing()
    {
        var s = new Session("s");
        var embed = new FakeEmbeddings(); var presets = new FakePresets();
        Assert.Null(await new RecommendationService(Catalog, embed, presets, new OrchestratorOptions()).BuildAsync(s, Ask("style"), 1, default));
        Assert.Empty(embed.Texts); Assert.Empty(presets.Calls);
    }
}
