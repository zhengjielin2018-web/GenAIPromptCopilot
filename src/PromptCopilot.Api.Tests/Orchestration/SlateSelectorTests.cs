using PromptCopilot.Api.Data;
using PromptCopilot.Api.Orchestration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class SlateSelectorTests
{
    private static readonly string[] Clothing = { "clothing.upper", "clothing.lower", "clothing.footwear" };
    private static readonly IReadOnlyDictionary<string, int> NoneSeen = new Dictionary<string, int>();
    private static readonly IReadOnlyDictionary<(long PresetId, string FacetId), float[]> NoVectors = new Dictionary<(long, string), float[]>();

    private static PresetCandidate C(long id, params (string facet, string tags)[] ft) =>
        new(id, $"#{id}", ft.Select(f => f.facet).ToList(),
            ft.ToDictionary(f => f.facet, f => (IReadOnlyList<string>)f.tags.Split(", ")), null, null, 0.2);

    private static SlateTier Tier(string reason, params PresetCandidate[] hits) => new(reason, hits, _ => Array.Empty<string>());
    private static float[] Unit(int hot) { var v = new float[8]; v[hot] = 1f; return v; }

    [Fact]
    public void Tag_set_key_normalizes_sorts_and_ignores_other_dimensions()
    {
        var a = C(1, ("clothing.upper", "White_Shirt, (skirt:1.2)"), ("scene.weather", "rain")).FacetTags;
        var b = C(2, ("clothing.upper", "skirt, white shirt")).FacetTags;
        Assert.Equal(SlateSelector.TagSetKey(a, Clothing), SlateSelector.TagSetKey(b, Clothing));
        Assert.Equal("clothing.upper=skirt|clothing.upper=white shirt", SlateSelector.TagSetKey(a, Clothing));
    }

    [Fact]
    public void Merge_keeps_tier_order_dedups_ids_and_collapses_identical_tag_sets()
    {
        var merged = SlateSelector.Merge(new[]
        {
            Tier(SlateReason.Anchored, C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "shirt")), C(2, ("clothing.footwear", "sandals"), ("clothing.lower", "skirt"))),
            Tier(SlateReason.Similar, C(2, ("clothing.footwear", "sandals"), ("clothing.lower", "skirt")), C(3, ("clothing.upper", "Shirt"), ("clothing.footwear", "sandals"))),
            Tier(SlateReason.Query, C(4, ("clothing.upper", "hoodie"), ("clothing.lower", "jeans"))),
        }, Clothing);
        Assert.Equal(new long[] { 1, 2, 4 }, merged.Select(c => c.Preset.Id));          // 2 重複 id、3 跟 1 的 tag 集合相同
        Assert.Equal(new[] { 0, 1, 2 }, merged.Select(c => c.Rank));
        Assert.Equal(new[] { SlateReason.Anchored, SlateReason.Anchored, SlateReason.Query }, merged.Select(c => c.Reason));
    }

    [Fact]
    public void Merge_takes_anchor_tags_from_the_tier()
    {
        var tier = new SlateTier(SlateReason.Anchored, new[] { C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "x")) }, _ => new[] { "sandals" });
        Assert.Equal(new[] { "sandals" }, Assert.Single(SlateSelector.Merge(new[] { tier }, Clothing)).AnchorTags);
    }

    private static IReadOnlyList<SlateCandidate> Four() => SlateSelector.Merge(new[]
    {
        Tier(SlateReason.Anchored, C(1, ("clothing.upper", "a"), ("clothing.lower", "a")), C(2, ("clothing.upper", "b"), ("clothing.lower", "b")),
             C(3, ("clothing.upper", "c"), ("clothing.lower", "c")), C(4, ("clothing.upper", "d"), ("clothing.lower", "d"))),
    }, Clothing);

    [Fact]
    public void First_relevant_slot_is_the_best_effective_rank_with_probability_one()
    {
        var picks = SlateSelector.PickRelevant(Four(), NoneSeen, 10, 5, new Random(1));
        Assert.Equal(2, picks.Count);
        Assert.Equal(1, picks[0].Candidate.Preset.Id);
        Assert.Equal(1.0, picks[0].Prob);
        Assert.NotEqual(1, picks[1].Candidate.Preset.Id);
        Assert.InRange(picks[1].Prob, 0.0, 1.0);
    }

    [Fact]
    public void Seen_candidates_are_pushed_back_by_the_penalty()
    {
        var four = Four();
        var seen = new Dictionary<string, int> { [four[0].Key] = 1, [four[1].Key] = 1 };
        var picks = SlateSelector.PickRelevant(four, seen, 10, 0.01, new Random(1));   // τ 很小：第 2 位幾乎必取有效名次次小的
        Assert.Equal(new long[] { 3, 4 }, picks.Select(p => p.Candidate.Preset.Id));
        Assert.Equal(new[] { 2, 3 }, picks.Select(p => p.Rank));                         // Rank 記原名次，不是有效名次
    }

    [Fact]
    public void Same_seed_gives_the_same_picks()
    {
        var seed = SlateSelector.Seed("s1", 3, "clothing", 1);
        var a = SlateSelector.PickRelevant(Four(), NoneSeen, 10, 5, new Random(seed)).Select(p => p.Candidate.Preset.Id);
        var b = SlateSelector.PickRelevant(Four(), NoneSeen, 10, 5, new Random(seed)).Select(p => p.Candidate.Preset.Id);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Pick_relevant_handles_one_and_zero_candidates()
    {
        Assert.Empty(SlateSelector.PickRelevant(Array.Empty<SlateCandidate>(), NoneSeen, 10, 5, new Random(1)));
        Assert.Single(SlateSelector.PickRelevant(Four().Take(1).ToList(), NoneSeen, 10, 5, new Random(1)));
    }

    [Fact]
    public void Draw_frequency_matches_the_weights()
    {
        var rng = new Random(42);
        var counts = new int[3];
        for (var i = 0; i < 10_000; i++) counts[SlateSelector.Draw(new double[] { 0, 1, 2 }, 1, rng).index]++;
        var z = 1 + Math.Exp(-1) + Math.Exp(-2);
        Assert.InRange(counts[0] / 10_000.0, 1 / z - 0.02, 1 / z + 0.02);
        Assert.InRange(counts[2] / 10_000.0, Math.Exp(-2) / z - 0.02, Math.Exp(-2) / z + 0.02);
        var first = SlateSelector.Draw(new double[] { 0, 1, 2 }, 1, new Random(0));
        Assert.Equal(new[] { 1 / z, Math.Exp(-1) / z, Math.Exp(-2) / z }[first.index], first.prob, 6);   // 回報的機率就是它的權重占比
    }

    [Fact]
    public void Draw_survives_huge_effective_ranks()
    {
        // Review Focus 4
        var (index, prob) = SlateSelector.Draw(new double[] { 500, 510 }, 5, new Random(1));
        Assert.InRange(index, 0, 1);
        Assert.False(double.IsNaN(prob));
        Assert.InRange(prob, 0.0, 1.0);
    }

    [Fact]
    public void Explore_picks_the_candidate_most_different_on_the_compare_facets()
    {
        var relevant = SlateSelector.Merge(new[] { Tier(SlateReason.Anchored, C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "x"))) }, Clothing);
        var pool = SlateSelector.Merge(new[]
        {
            Tier(SlateReason.Explore, C(2, ("clothing.footwear", "flip flops"), ("clothing.upper", "y")), C(3, ("clothing.footwear", "boots"), ("clothing.lower", "long skirt"))),
        }, Clothing);
        var vectors = new Dictionary<(long, string), float[]>
        {
            [(1, "clothing.footwear")] = Unit(0), [(2, "clothing.footwear")] = Unit(0), [(3, "clothing.footwear")] = Unit(1),
        };
        var pick = SlateSelector.PickExplore(pool, relevant, new[] { "clothing.footwear" }, vectors, NoneSeen, 10, 0.01, new Random(1));
        Assert.Equal(3, pick!.Candidate.Preset.Id);                                    // flip flops 跟 sandals 同向量，差異 0
        Assert.Equal(0, pick.Rank);
        Assert.Equal(SlateReason.Explore, pick.Candidate.Reason);
    }

    [Fact]
    public void Explore_skips_candidates_without_tags_on_compare_facets_and_those_sharing_a_relevant_key()
    {
        var relevant = SlateSelector.Merge(new[] { Tier(SlateReason.Anchored, C(1, ("clothing.footwear", "sandals"), ("clothing.upper", "x"))) }, Clothing);
        var pool = SlateSelector.Merge(new[]
        {
            Tier(SlateReason.Explore, C(5, ("clothing.footwear", "sandals"), ("clothing.upper", "x")), C(6, ("clothing.upper", "hoodie"), ("clothing.lower", "jeans"))),
        }, Clothing);
        Assert.Null(SlateSelector.PickExplore(pool, relevant, new[] { "clothing.footwear" }, NoVectors, NoneSeen, 10, 5, new Random(1)));
    }

    [Fact]
    public void Explore_without_relevant_falls_back_to_the_original_rank()
    {
        var pool = SlateSelector.Merge(new[] { Tier(SlateReason.Explore, C(7, ("clothing.upper", "a"), ("clothing.lower", "a")), C(8, ("clothing.upper", "b"), ("clothing.lower", "b"))) }, Clothing);
        var pick = SlateSelector.PickExplore(pool, Array.Empty<SlateCandidate>(), Clothing, NoVectors, NoneSeen, 10, 0.01, new Random(1));
        Assert.Equal(7, pick!.Candidate.Preset.Id);
    }

    [Fact]
    public void Similarity_averages_shared_facets_and_is_zero_without_any()
    {
        var v = new Dictionary<(long, string), float[]>
        {
            [(1, "a")] = Unit(0), [(2, "a")] = Unit(0), [(1, "b")] = Unit(0), [(2, "b")] = Unit(1), [(3, "c")] = Unit(0),
        };
        Assert.Equal(0.5, SlateSelector.Similarity(1, 2, new[] { "a", "b" }, v), 6);
        Assert.Equal(0, SlateSelector.Similarity(1, 3, new[] { "a", "b", "c" }, v));
    }

    [Fact]
    public void Seed_is_stable_and_changes_with_every_field()
    {
        var s = SlateSelector.Seed("s1", 3, "clothing", 1);
        Assert.Equal(s, SlateSelector.Seed("s1", 3, "clothing", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s2", 3, "clothing", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s1", 4, "clothing", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s1", 3, "style", 1));
        Assert.NotEqual(s, SlateSelector.Seed("s1", 3, "clothing", 2));
    }
}
