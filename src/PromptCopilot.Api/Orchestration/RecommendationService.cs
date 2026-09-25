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
    /// <summary>沒有任何維度有候選（或 profile 未設）時回 null，不發事件。</summary>
    Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct);
}

/// <summary>整套組合推薦（設計 §5）。由伺服器產生、模型不知道：推薦系統要「每次都在、每次一樣」。
/// 追問時只查被問的維度，定稿時查本 profile 全部維度；每個維度：錨（covered facet 的 FacetTags＋定稿 positive）
/// 有就先過濾再向量排序，命中不到 2 筆退回純向量。</summary>
public sealed class RecommendationService(FacetCatalog catalog, IEmbeddingClient embed, PresetRepository presets, OrchestratorOptions options) : IRecommendationService
{
    public const int QueryChars = 500;
    public const int MinAnchoredHits = 2;
    /// <summary>伺服器組的採用句開頭（AdoptionComposer 也用這個字串）。</summary>
    public const string AdoptionPrefix = "採用〈";

    public async Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct)
    {
        if (s.Profile is null) return null;
        var profile = s.Profile;
        IReadOnlyList<string> dimensions = outcome switch
        {
            AskOutcome a => a.Asks.Select(x => x.Dimension).Distinct().Where(d => catalog.FacetsOf(profile, d).Count > 0).ToList(),
            FinalizedOutcome => catalog.DimensionsOf(profile),
            _ => Array.Empty<string>(),
        };
        if (dimensions.Count == 0) return null;
        var query = QueryText(s.ChatHistory);
        if (query.Length == 0) return null;
        var vec = (await embed.EmbedAsync(new[] { query }, GeminiEmbeddingClient.RetrievalQuery, ct))[0];
        var finalTags = outcome is FinalizedOutcome f
            ? TagAttribution.Split(f.Final.Positive).Select(TagAttribution.Normalize).Where(t => t.Length > 0).ToList()
            : new List<string>();

        var result = new List<RecommendedDimension>();
        foreach (var dim in dimensions)
        {
            var facets = catalog.FacetsOf(profile, dim);
            // 預設給 Missing：FacetState 的 default 是 Covered，缺鍵時不能被當成已涵蓋
            var covered = facets.Where(x => s.FacetStates.GetValueOrDefault(x, FacetState.Missing) == FacetState.Covered).ToList();
            var anchors = AnchorTags(s, covered, finalTags);
            IReadOnlyList<PresetCandidate> hits = Array.Empty<PresetCandidate>();
            var anchored = false;
            if (anchors.Count > 0)
            {
                hits = await presets.RecommendAsync(vec, facets, covered, anchors, options.RecommendationTake, ct);
                anchored = hits.Count >= MinAnchoredHits;
            }
            if (!anchored) hits = await presets.RecommendAsync(vec, facets, Array.Empty<string>(), Array.Empty<string>(), options.RecommendationTake, ct);
            if (hits.Count == 0) continue;
            var matched = anchored ? MatchedAnchors(hits, covered, anchors) : Array.Empty<string>();
            var sets = hits.Select(h => new RecommendedSet(h.Id, h.Title, h.ImageUrl, h.SourceRef, Math.Round(h.Dist, 3),
                facets.Select(x => new RecommendedFacet(x, catalog.Facets[x].Label,
                    FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(x, FacetState.Missing)),
                    h.FacetTags.GetValueOrDefault(x) ?? Array.Empty<string>())).ToList())).ToList();
            result.Add(new RecommendedDimension(dim, catalog.DimensionLabel(dim, profile), anchored, matched, sets));
        }
        return result.Count == 0 ? null : new RecommendationsEvent(turnIndex, result);
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

    /// <summary>該維度 covered facet 的錨：模型給的 FacetTags，定稿時再加 positive 的全部 tag。全部正規化、去重、保序。</summary>
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
}
