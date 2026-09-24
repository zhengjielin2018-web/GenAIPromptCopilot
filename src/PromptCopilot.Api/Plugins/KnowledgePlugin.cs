using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Plugins;

public sealed class KnowledgePlugin(TurnContext turn, FacetCatalog catalog, IEmbeddingClient embed, PresetRepository presets, HistoryRepository histories)
{
    // 與 scripts/pipeline/retrieval.py 一致
    public const int KCovered = 5;
    public const int KMissing = 3;
    public const double HighMax = 0.25;
    public const double MidMax = 0.30;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Band(double dist) => dist < HighMax ? "高" : dist < MidMax ? "中" : "低";

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

    [KernelFunction(ToolNames.SearchSimilarPrompts)]
    [Description("用整句需求找相似的既有作品，只供風格參考，不要照抄。")]
    public async Task<string> SearchSimilarPromptsAsync(
        [Description("使用者整句需求（繁中）")] string intent,
        [Description("幾筆，預設 3")] int topK = 3,
        CancellationToken ct = default)
    {
        var s = turn.Session;
        if (s.Profile is null) return "錯誤：請先呼叫 SetProfile";
        var vec = (await embed.EmbedAsync(new[] { intent }, GeminiEmbeddingClient.RetrievalQuery, ct))[0];
        var hits = await histories.SearchAsync(vec, s.Profile, Math.Clamp(topK, 1, 5), ct);
        turn.Emit(new ToolResultEvent(turn.CurrentCallId ?? Guid.NewGuid().ToString("N"), ToolNames.SearchSimilarPrompts, $"相似作品 {hits.Count}（{s.Profile}）", null));
        return JsonSerializer.Serialize(hits.Select(h => new { intent = h.UserIntent, positive = h.PositivePrompt, profile = h.SubjectProfile, dist = Math.Round(h.Dist, 3) }), Json);
    }
}
