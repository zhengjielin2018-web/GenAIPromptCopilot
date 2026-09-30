using System.Security.Cryptography;
using System.Text;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Orchestration;

/// <summary>定稿卡推薦組法的理由（推薦組法設計 §3.1、§5.1）。追問卡的組合不帶理由（null）。</summary>
public static class SlateReason
{
    public const string Anchored = "anchored";
    public const string Similar = "similar";
    public const string Query = "query";
    public const string Explore = "explore";
}

/// <summary>名單上的一筆。Rank：三層接成一條名單、合併後的原名次（0 起算）；Key：該維度的 tag 集合，看過次數用它記。</summary>
public sealed record SlateCandidate(PresetCandidate Preset, string Reason, IReadOnlyList<string> AnchorTags, int Rank, string Key);

/// <summary>挑中的一套。Rank：相關位是原名次，探索位是差異排名；Prob：被抽中的機率，第 1 位是取最高、不是抽的，記 1。</summary>
public sealed record SlatePick(SlateCandidate Candidate, int Rank, double Prob);

/// <summary>一層候選：理由、依距離排好的命中、每筆命中的錨。</summary>
public sealed record SlateTier(string Reason, IReadOnlyList<PresetCandidate> Hits, Func<PresetCandidate, IReadOnlyList<string>> AnchorsOf);

/// <summary>推薦組法的挑選（設計 §3.1、§4.1）。純函式：不碰資料庫與 session，隨機來源由呼叫端給（種子見 <see cref="Seed"/>）。</summary>
public static class SlateSelector
{
    public const int RelevantSlots = 2;

    /// <summary>該維度的 tag 集合：tag 正規化、帶 facet 前綴、去重、排序後串接。
    /// 知識庫很多片段在某維度的 tag 一模一樣；只認 presetId 的話，看過 A 之後一模一樣的 A' 權重仍是滿的（設計 §2）。</summary>
    public static string TagSetKey(IReadOnlyDictionary<string, IReadOnlyList<string>> facetTags, IReadOnlyList<string> dimensionFacets) =>
        string.Join("|", dimensionFacets
            .Where(facetTags.ContainsKey)
            .SelectMany(f => facetTags[f].Select(TagAttribution.Normalize).Where(t => t.Length > 0).Select(t => $"{f}={t}"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>三層依序接成一條名單：presetId 重複的留第一次出現的，tag 集合相同的只留名次最前的一筆。</summary>
    public static IReadOnlyList<SlateCandidate> Merge(IReadOnlyList<SlateTier> tiers, IReadOnlyList<string> dimensionFacets)
    {
        var ids = new HashSet<long>();
        var keys = new HashSet<string>();
        var list = new List<SlateCandidate>();
        foreach (var tier in tiers)
            foreach (var h in tier.Hits)
            {
                if (!ids.Add(h.Id)) continue;
                var key = TagSetKey(h.FacetTags, dimensionFacets);
                if (!keys.Add(key)) continue;
                list.Add(new SlateCandidate(h, tier.Reason, tier.AnchorsOf(h), list.Count, key));
            }
        return list;
    }

    /// <summary>相關位（設計 §3.1）：第 1 位取有效名次最小（同分取原名次小），第 2 位依 exp(−有效名次/τ) 抽。有效名次＝原名次＋P×看過次數。</summary>
    public static IReadOnlyList<SlatePick> PickRelevant(IReadOnlyList<SlateCandidate> candidates, IReadOnlyDictionary<string, int> seen,
        double penalty, double temperature, Random rng)
    {
        var picks = new List<SlatePick>();
        if (candidates.Count == 0) return picks;
        double Effective(SlateCandidate c) => c.Rank + penalty * seen.GetValueOrDefault(c.Key);
        var first = candidates.OrderBy(Effective).ThenBy(c => c.Rank).First();
        picks.Add(new SlatePick(first, first.Rank, 1.0));
        var rest = candidates.Where(c => !ReferenceEquals(c, first)).ToList();
        while (picks.Count < RelevantSlots && rest.Count > 0)
        {
            var (i, prob) = Draw(rest.Select(Effective).ToList(), temperature, rng);
            picks.Add(new SlatePick(rest[i], rest[i].Rank, prob));
            rest.RemoveAt(i);
        }
        return picks;
    }

    /// <summary>探索位（設計 §3.1）：不看錨的純向量候選裡，去掉跟相關位同 key 的、在比較用 facet 上一個 tag 都沒有的（它取代不了任何東西），
    /// 依「跟相關位的差異」由大到小排名（同分照原名次），再用同一個權重與看過延後抽 1 套。相關位一套都沒有時差異全當 1。</summary>
    public static SlatePick? PickExplore(IReadOnlyList<SlateCandidate> pool, IReadOnlyList<SlateCandidate> relevant, IReadOnlyList<string> compareFacets,
        IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors, IReadOnlyDictionary<string, int> seen,
        double penalty, double temperature, Random rng)
    {
        var taken = relevant.Select(r => r.Key).ToHashSet();
        var eligible = pool.Where(c => !taken.Contains(c.Key)
            && compareFacets.Any(f => (c.Preset.FacetTags.GetValueOrDefault(f)?.Count ?? 0) > 0)).ToList();
        if (eligible.Count == 0) return null;
        double Difference(SlateCandidate c) =>
            relevant.Count == 0 ? 1 : 1 - relevant.Max(r => Similarity(c.Preset.Id, r.Preset.Id, compareFacets, vectors));
        var ranked = eligible.Select(c => (c, diff: Difference(c))).OrderByDescending(x => x.diff).ThenBy(x => x.c.Rank).Select(x => x.c).ToList();
        var (i, prob) = Draw(ranked.Select((c, rank) => rank + penalty * seen.GetValueOrDefault(c.Key)).ToList(), temperature, rng);
        return new SlatePick(ranked[i], i, prob);
    }

    /// <summary>依 exp(−有效名次/τ) 抽一個，回索引與它的機率。先減掉最小值再取 exp：看過很多次的有效名次很大，直接算會全部下溢成 0。</summary>
    public static (int index, double prob) Draw(IReadOnlyList<double> effective, double temperature, Random rng)
    {
        var min = effective.Min();
        var w = effective.Select(e => Math.Exp(-(e - min) / temperature)).ToArray();
        var total = w.Sum();
        var x = rng.NextDouble() * total;
        for (var i = 0; i < w.Length; i++)
        {
            x -= w[i];
            if (x < 0) return (i, w[i] / total);
        }
        return (w.Length - 1, w[^1] / total);
    }

    /// <summary>兩套在比較用 facet 上的相似度：兩者都有 facet 向量的 facet 各算 cos 再平均；一個共有的都沒有記 0（視為完全不同）。</summary>
    public static double Similarity(long a, long b, IReadOnlyList<string> facets, IReadOnlyDictionary<(long PresetId, string FacetId), float[]> vectors)
    {
        var sims = facets.Where(f => vectors.ContainsKey((a, f)) && vectors.ContainsKey((b, f)))
            .Select(f => Cosine(vectors[(a, f)], vectors[(b, f)])).ToList();
        return sims.Count == 0 ? 0 : sims.Average();
    }

    public static double Cosine(float[] x, float[] y)
    {
        double dot = 0, nx = 0, ny = 0;
        for (var i = 0; i < x.Length; i++) { dot += x[i] * y[i]; nx += x[i] * x[i]; ny += y[i] * y[i]; }
        return nx == 0 || ny == 0 ? 0 : dot / Math.Sqrt(nx * ny);
    }

    /// <summary>(session, 定稿卡輪次, 維度, 批次) 的穩定種子：同樣狀態重播得到同一批（設計 §2）。
    /// 不用 string.GetHashCode：它每次程序啟動換隨機值，重啟後無法重現。</summary>
    public static int Seed(string sessionId, int turnIndex, string dimension, int batch) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}|{turnIndex}|{dimension}|{batch}")), 0);
}
