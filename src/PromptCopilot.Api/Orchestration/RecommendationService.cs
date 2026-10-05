using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

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

    public async Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct)
    {
        var profile = s.Profile ?? throw new InvalidOperationException("尚未判定題材");
        var batch = s.SlateBatch(dimension) + 1;
        var empty = new RecommendedDimension(dimension, catalog.DimensionLabel(dimension, profile), false, Array.Empty<string>(), Array.Empty<RecommendedSet>(), Batch: batch);
        var query = QueryText(s.ChatHistory);
        if (query.Length == 0) return empty;
        var (vec, anchorVec) = await EmbedAsync(s, new[] { dimension }, query, ct);
        if (await SlateAsync(s, dimension, batch, vec, anchorVec, s.LastFinalTags, ct) is not { } built) return empty;
        s.RecordSlate(dimension, batch, built.keys);
        return built.row;
    }

    /// <summary>查詢向量與近似錨的向量一次 embed（設計 §6.2）：哪個維度走到近似錨都不會多一次呼叫；沒用到的向量丟掉無妨。</summary>
    private async Task<(float[] vec, Dictionary<(string dim, string facet), float[]> anchorVec)> EmbedAsync(Session s, IReadOnlyList<string> dimensions,
        string query, CancellationToken ct)
    {
        var anchorTexts = new List<(string dim, string facet, string text)>();
        foreach (var dim in dimensions)
            foreach (var facetId in catalog.FacetsOf(s.Profile!, dim))
                if (s.FacetStates.GetValueOrDefault(facetId, FacetState.Missing) == FacetState.Covered && SimilarAnchorText(s, facetId) is { } t)
                    anchorTexts.Add((dim, facetId, t));
        var vectors = await embed.EmbedAsync(new[] { query }.Concat(anchorTexts.Select(a => a.text)).ToList(), GeminiEmbeddingClient.RetrievalQuery, ct);
        return (vectors[0], anchorTexts.Select((a, i) => (a, v: vectors[i + 1])).ToDictionary(x => (x.a.dim, x.a.facet), x => x.v));
    }

    /// <summary>本 session 使用者講過的原話依序串接；伺服器組的採用句不算（那不是描述）。</summary>
    public static string JoinedUserText(ChatHistory history) =>
        string.Join("\n", history
            .Where(m => m.Role == AuthorRole.User && !string.IsNullOrWhiteSpace(m.Content) && !m.Content!.TrimStart().StartsWith(AdoptionPrefix, StringComparison.Ordinal))
            .Select(m => m.Content!.Trim()));

    /// <summary>查詢向量的來源：最後 500 字。定稿前後同一個查法，跟片段的中文 embedding 同語言。</summary>
    public static string QueryText(ChatHistory history)
    {
        var joined = JoinedUserText(history);
        return joined.Length <= QueryChars ? joined : joined[^QueryChars..];
    }

    /// <summary>該維度 covered facet 的錨：模型給的 FacetTags，定稿時再加 positive 的 tag（基礎詞已由呼叫端排除）。全部正規化、去重、保序。</summary>
    public static IReadOnlyList<string> AnchorTags(Session s, IReadOnlyList<string> covered, IReadOnlyList<string> finalTags)
    {
        if (covered.Count == 0) return Array.Empty<string>();
        var set = new List<string>();
        foreach (var f in covered)
            if (s.FacetTags.TryGetValue(f, out var raw))
                foreach (var t in TagAttribution.Split(raw).Select(TagAttribution.Normalize))
                    if (t.Length > 0 && !set.Contains(t)) set.Add(t);
        foreach (var t in finalTags) if (!set.Contains(t)) set.Add(t);
        return set;
    }

    /// <summary>實際命中的錨，規則照 SQL 錨過濾：DB tag 等於錨，或以「空白＋錨」結尾。
    /// 不算反方向（錨以 DB tag 結尾）：SQL 不比那個，列出來就是在報過濾沒用到的錨。</summary>
    private static IReadOnlyList<string> MatchedAnchors(IReadOnlyList<PresetCandidate> hits, IReadOnlyList<string> covered, IReadOnlyList<string> anchors) =>
        anchors.Where(a => hits.Any(h => covered.Any(f => (h.FacetTags.GetValueOrDefault(f) ?? Array.Empty<string>())
            .Select(TagAttribution.Normalize).Any(t => t == a || TagAttribution.EndsWithWord(t, a))))).ToList();

    /// <summary>定稿卡的一排（推薦組法設計 §3.1）：三層（字面錨、近似錨、純向量）各取 PoolSize 筆、都查，接成一條名單並依 tag 集合合併；
    /// 2 套相關位＋最多 1 套探索位。回傳這一排與要記成看過的 key；由呼叫端決定何時記（整張卡成功才記）。沒有任何候選回 null。</summary>
    private async Task<(RecommendedDimension row, IReadOnlyList<string> keys)?> SlateAsync(Session s, string dim, int batch, float[] vec,
        IReadOnlyDictionary<(string, string), float[]> anchorVec, IReadOnlyList<string> finalTags, CancellationToken ct)
    {
        var facets = catalog.FacetsOf(s.Profile!, dim);
        var covered = facets.Where(x => s.FacetStates.GetValueOrDefault(x, FacetState.Missing) == FacetState.Covered).ToList();
        var anchors = AnchorTags(s, covered, finalTags);
        var pool = options.RecommendationPoolSize;
        var none = Array.Empty<string>();
        var literal = anchors.Count > 0 ? await presets.RecommendAsync(vec, facets, covered, anchors, pool, ct) : Array.Empty<PresetCandidate>();
        var similar = await SimilarHitsAsync(dim, covered, facets, anchorVec, pool, ct);
        var similarFacet = similar.ToDictionary(x => x.c.Id, x => x.facet);
        var plain = await presets.RecommendAsync(vec, facets, none, none, pool, ct);
        var candidates = SlateSelector.Merge(new SlateTier[]
        {
            new(SlateReason.Anchored, literal, h => MatchedAnchors(new[] { h }, covered, anchors)),
            new(SlateReason.Similar, similar.Select(x => x.c).ToList(), h => NormalizedFacetTags(s, similarFacet[h.Id])),
            new(SlateReason.Query, plain, _ => none),
        }, facets);
        if (candidates.Count == 0) return null;

        var seen = s.SeenFor(dim);
        var (penalty, temperature) = (options.RecommendationSeenPenalty, options.RecommendationTemperature);
        var rng = new Random(SlateSelector.Seed(s.Id, s.LatestSlateTurn ?? 0, dim, batch));
        var relevant = SlateSelector.PickRelevant(candidates, seen, penalty, temperature, rng);
        // 探索位只比使用者講過的 facet：比整個維度會挑到「只在沒講的地方不同」的，那不會發生取代（設計 §2）
        var compare = covered.Count > 0 ? covered : facets;
        var explorePool = SlateSelector.Merge(new[] { new SlateTier(SlateReason.Explore, plain, _ => none) }, facets);
        var relevantCandidates = relevant.Select(p => p.Candidate).ToList();
        IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors = explorePool.Count == 0
            ? new Dictionary<(long PresetId, string FacetId), float[]>()
            : await presets.FacetVectorsAsync(explorePool.Select(c => c.Preset.Id).Concat(relevantCandidates.Select(c => c.Preset.Id)).Distinct().ToList(), compare, ct);
        var explore = SlateSelector.PickExplore(explorePool, relevantCandidates, compare, vectors, seen, penalty, temperature, rng);
        var picks = explore is null ? relevant : relevant.Append(explore).ToList();

        var sets = picks.Select(p => ToSet(s, facets, p.Candidate.Preset) with
        {
            // Prob 不四捨五入：抽中機率低的套（例如探索位深名次）捨到小數 4 位常常變 0，
            // 之後拿 prob 做逆傾向分析（inverse propensity）會被這個 0 除以，不能存捨入後的值（review finding #4）。
            Reason = p.Candidate.Reason, AnchorTags = p.Candidate.AnchorTags, Rank = p.Rank, Prob = p.Prob,
        }).ToList();
        var row = new RecommendedDimension(dim, catalog.DimensionLabel(dim, s.Profile!), false, Array.Empty<string>(), sets, Batch: batch);
        return (row, picks.Select(p => p.Candidate.Key).ToList());
    }

    private RecommendedSet ToSet(Session s, IReadOnlyList<string> facets, PresetCandidate h) =>
        new(h.Id, h.Title, h.ImageUrl, h.SourceRef, Math.Round(h.Dist, 3),
            facets.Select(x => new RecommendedFacet(x, catalog.Facets[x].Label,
                FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(x, FacetState.Missing)),
                h.FacetTags.GetValueOrDefault(x) ?? Array.Empty<string>())).ToList());

    private static IReadOnlyList<string> NormalizedFacetTags(Session s, string facetId) =>
        TagAttribution.Split(s.FacetTags.GetValueOrDefault(facetId)).Select(TagAttribution.Normalize).Where(t => t.Length > 0).Distinct().ToList();

    /// <summary>近似錨的命中與它來自哪個 facet，依距離排好、取前 take。</summary>
    private async Task<List<(PresetCandidate c, string facet)>> SimilarHitsAsync(string dim, IReadOnlyList<string> covered, IReadOnlyList<string> facets,
        IReadOnlyDictionary<(string, string), float[]> anchorVec, int take, CancellationToken ct)
    {
        var best = new Dictionary<long, (PresetCandidate c, string facet)>();
        foreach (var f in covered)
        {
            if (!anchorVec.TryGetValue((dim, f), out var av)) continue;
            if (await presets.FacetPoolSizeAsync(f, ct) == 0) continue;
            foreach (var h in await presets.RecommendSimilarAsync(av, f, facets, options.RecommendationSimilarMaxDist, take, ct))
                if (!best.TryGetValue(h.Id, out var cur) || h.Dist < cur.c.Dist) best[h.Id] = (h, f);
        }
        return best.Values.OrderBy(x => x.c.Dist).Take(take).ToList();
    }

    /// <summary>該 facet 的錨文字：FacetTags 正規化、丟空、去重、保序、", " 串接；沒有可用的 tag 回 null（不拿空字串去 embed）。</summary>
    public static string? SimilarAnchorText(Session s, string facetId)
    {
        if (!s.FacetTags.TryGetValue(facetId, out var raw)) return null;
        var parts = TagAttribution.Split(raw).Select(TagAttribution.Normalize).Where(t => t.Length > 0).Distinct().ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
