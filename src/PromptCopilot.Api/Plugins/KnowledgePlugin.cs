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

    /// <summary>使用者講到的每個 facet 各一項，加上沒講的維度各兩項。</summary>
    public const int MaxQueries = 24;

    private const string ItemsHelp = "每項：query（該項專屬的繁中查詢語句）加上 facetId（單一 facet，例如 clothing.footwear）或 dimension（整個維度，style | scene | camera | appearance | pose | clothing）。使用者講到的每個 facet 各一項用 facetId 與他的原話；使用者沒講的維度用 dimension 給兩個對比方向。最多 24 項。";

    [KernelFunction(ToolNames.SearchPresets)]
    [Description("檢索知識庫片段。一次呼叫帶上本輪所有要查的項目，不要一個項目一次呼叫。" + ItemsHelp + "每個項目各自回傳候選池大小、每筆的相似度分級、可否借入提示詞、每個 facet 對本次使用者是 covered/missing。")]
    public async Task<string> SearchPresetsAsync(
        [Description("要查的項目。" + ItemsHelp)] SearchQuery[]? queries,
        CancellationToken ct)
    {
        var s = turn.Session;
        if (s.Profile is null) return "錯誤：請先呼叫 SetProfile";
        if (queries is null || queries.Length == 0) return "錯誤：queries 不可為空，請一次帶上本輪所有要查的項目";
        if (queries.Length > MaxQueries) return $"錯誤：queries 最多 {MaxQueries} 個項目（使用者講到的每個 facet 各一項，加上沒講的維度各兩項），收到 {queries.Length} 個";

        var grounded = s.GroundedDimensions(catalog);
        var profileFacets = catalog.IdsForProfile(s.Profile);
        // 每個項目先驗證；合法的才進 embedding batch。index 對應回 queries 的位置，結果要照原順序回。
        // facetId 優先於 dimension：候選池縮到單一 facet，維度取 facet 所屬的（同時給了不一致的 dimension 也以 facetId 為準）。
        var valid = new List<(int index, string dimension, string? facetId, string query, IReadOnlyList<string> facetIds)>();
        var errors = new Dictionary<int, string>();
        for (var i = 0; i < queries.Length; i++)
        {
            var q = queries[i];
            string dimension;
            string? facetId = null;
            IReadOnlyList<string> facetIds;
            if (!string.IsNullOrWhiteSpace(q.FacetId))
            {
                facetId = q.FacetId;
                if (!catalog.Facets.ContainsKey(facetId) || !profileFacets.Contains(facetId)) { errors[i] = $"facet {facetId} 對 {s.Profile} 不適用或不存在"; continue; }
                facetIds = new[] { facetId };
                dimension = catalog.DimensionOf(facetId);
            }
            else if (!string.IsNullOrWhiteSpace(q.Dimension))
            {
                dimension = q.Dimension;
                facetIds = catalog.FacetsOf(s.Profile, dimension);
                if (facetIds.Count == 0) { errors[i] = $"維度 {dimension} 對 {s.Profile} 不適用或不存在"; continue; }
            }
            else { errors[i] = "每個項目要有 dimension 或 facetId"; continue; }
            if (string.IsNullOrWhiteSpace(q.Query)) { errors[i] = facetId is null ? $"維度 {dimension} 的 query 空白" : $"facet {facetId} 的 query 空白"; continue; }
            valid.Add((i, dimension, facetId, q.Query, facetIds));
        }

        var vectors = valid.Count == 0
            ? Array.Empty<float[]>()
            : await embed.EmbedAsync(valid.Select(v => v.query).ToList(), GeminiEmbeddingClient.RetrievalQuery, ct);

        // 候選池計數的快取鍵是實際用的 facetIds 集合：facet 項目與同維度的 dimension 項目池不同，各數一次。
        var pools = new Dictionary<string, long>();
        // 先組 detail，摘要與回給模型的 JSON 都從它投影，三邊同一份資料。detail 不放 snippet，原始命中另存給模型用。
        var items = new SearchPresetsItem[queries.Length];
        var rawHits = new IReadOnlyList<PresetHit>?[queries.Length];
        var presetsOut = new List<PresetRef>();
        var seen = new HashSet<long>();

        for (var vi = 0; vi < valid.Count; vi++)
        {
            var (index, dimension, facetId, query, facetIds) = valid[vi];
            var isGrounded = grounded.Contains(dimension);          // grounded 仍以維度判定
            var k = isGrounded ? KCovered : KMissing;
            var poolKey = string.Join(",", facetIds);
            if (!pools.TryGetValue(poolKey, out var pool))
                pools[poolKey] = pool = await presets.PoolSizeAsync(facetIds, ct);
            var hits = await presets.SearchAsync(vectors[vi], facetIds, k, ct);

            var detailHits = new List<SearchPresetsHit>();
            foreach (var h in hits)
            {
                s.Ledger.Record(new LedgerEntry { Id = h.Id, Title = h.Title, PromptSnippet = h.PromptSnippet, NegativeSnippet = h.NegativeSnippet, FacetIds = h.FacetIds, ImageUrl = h.ImageUrl, SourceRef = h.SourceRef },
                    new LedgerHit(dimension, h.Dist, isGrounded));
                detailHits.Add(new SearchPresetsHit(h.Id, h.Title, Band(h.Dist), Math.Round(h.Dist, 3), isGrounded,
                    h.FacetIds.ToDictionary(f => f, f => FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(f, FacetState.NotApplicable)))));
                if (seen.Add(h.Id)) presetsOut.Add(new PresetRef(h.Id, h.Title, h.ImageUrl, h.SourceRef));
            }
            var label = facetId is null ? catalog.DimensionLabel(dimension, s.Profile) : catalog.Facets[facetId].Label;
            items[index] = new SearchPresetsItem(dimension, facetId, label, query, isGrounded, pool, k, null, detailHits);
            rawHits[index] = hits;
        }
        foreach (var (i, message) in errors)
        {
            var q = queries[i];
            // 錯誤項目的 Label 用模型送的原始 facetId 或 dimension，摘要才會照舊顯示「hair 錯誤」
            var raw = string.IsNullOrWhiteSpace(q.FacetId) ? q.Dimension ?? "" : q.FacetId;
            items[i] = new SearchPresetsItem(q.Dimension ?? "", q.FacetId, raw, q.Query ?? "", false, 0, 0, message, Array.Empty<SearchPresetsHit>());
        }

        var summary = items.Select(it => it.Error is null ? $"{it.Label} 池 {it.PoolSize} → {it.Hits.Count}" : $"{it.Label} 錯誤".TrimStart());
        turn.Emit(new ToolResultEvent(turn.CurrentCallId ?? Guid.NewGuid().ToString("N"), ToolNames.SearchPresets, string.Join("・", summary), presetsOut,
            new SearchPresetsDetail(items)));

        var results = items.Select((it, i) => ModelResult(it, rawHits[i])).ToList();
        return JsonSerializer.Serialize(new { results }, Json);
    }

    /// <summary>detail 的一個項目投影成回給模型的形狀：欄位名、順序與內容與加 detail 之前逐字相同。
    /// usable 換回中文字串，並補上 detail 不放的 snippet（raw 與 it.Hits 在同一個迴圈裡組成，同序）。</summary>
    private static object ModelResult(SearchPresetsItem it, IReadOnlyList<PresetHit>? raw) => it.Error is not null
        ? new { dimension = it.Dimension, facetId = it.FacetId, query = it.Query, error = it.Error }
        : new
        {
            dimension = it.Dimension, facetId = it.FacetId, query = it.Query, grounded = it.Grounded, poolSize = it.PoolSize,
            hits = it.Hits.Zip(raw!, (h, r) => new
            {
                id = h.Id, title = h.Title, band = h.Band, dist = h.Dist,
                usable = h.Usable ? "可借入提示詞" : "僅供建議",
                facets = h.Facets,
                positive = r.PromptSnippet, negative = r.NegativeSnippet ?? "(無)",
            }).ToList(),
        };

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
        var detail = new SearchSimilarDetail(hits.Select(h => new SearchSimilarHit(
            h.UserIntent.Length > 40 ? h.UserIntent[..40] + "…" : h.UserIntent, h.SubjectProfile, Math.Round(h.Dist, 3))).ToList());
        turn.Emit(new ToolResultEvent(turn.CurrentCallId ?? Guid.NewGuid().ToString("N"), ToolNames.SearchSimilarPrompts, $"相似作品 {hits.Count}（{s.Profile}）", null, detail));
        return JsonSerializer.Serialize(hits.Select(h => new { intent = h.UserIntent, positive = h.PositivePrompt, profile = h.SubjectProfile, dist = Math.Round(h.Dist, 3) }), Json);
    }
}
