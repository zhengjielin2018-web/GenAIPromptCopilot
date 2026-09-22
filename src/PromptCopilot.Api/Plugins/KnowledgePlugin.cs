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

    [KernelFunction(ToolNames.SearchPresets)]
    [Description("分維度檢索知識庫片段。一次只查一個維度。使用者講過的維度：query 逐字用使用者原話。使用者沒講的維度：依整體畫面推想，且分兩次呼叫給對比方向（例：寫實攝影 vs 動漫插畫）。回傳候選池大小、每筆的相似度分級、可否借入提示詞、每個 facet 對本次使用者是 covered/missing。")]
    public async Task<string> SearchPresetsAsync(
        [Description("維度 key：style | scene | camera | appearance | pose | clothing")] string dimension,
        [Description("該維度專屬的繁中查詢語句")] string query,
        CancellationToken ct)
    {
        var s = turn.Session;
        if (s.Profile is null) return "錯誤：請先呼叫 SetProfile";
        var facetIds = catalog.FacetsOf(s.Profile, dimension);
        if (facetIds.Count == 0) return $"錯誤：維度 {dimension} 對 {s.Profile} 不適用或不存在";

        var grounded = s.GroundedDimensions(catalog).Contains(dimension);
        var k = grounded ? KCovered : KMissing;
        var vec = (await embed.EmbedAsync(new[] { query }, GeminiEmbeddingClient.RetrievalQuery, ct))[0];
        var pool = await presets.PoolSizeAsync(facetIds, ct);
        var hits = await presets.SearchAsync(vec, facetIds, k, ct);

        var rows = new List<object>();
        foreach (var h in hits)
        {
            s.Ledger.Record(new LedgerEntry { Id = h.Id, Title = h.Title, PromptSnippet = h.PromptSnippet, NegativeSnippet = h.NegativeSnippet, FacetIds = h.FacetIds, ImageUrl = h.ImageUrl },
                new LedgerHit(dimension, h.Dist, grounded));
            rows.Add(new
            {
                id = h.Id, title = h.Title, band = Band(h.Dist), dist = Math.Round(h.Dist, 3),
                usable = grounded ? "可借入提示詞" : "僅供建議",
                facets = h.FacetIds.ToDictionary(f => f, f => FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(f, FacetState.NotApplicable))),
                positive = h.PromptSnippet, negative = h.NegativeSnippet ?? "(無)",
            });
        }
        turn.Emit(new ToolResultEvent(turn.CurrentCallId ?? Guid.NewGuid().ToString("N"), ToolNames.SearchPresets,
            $"{catalog.DimensionLabel(dimension, s.Profile)} 池 {pool} → {hits.Count}", hits.Select(h => new PresetRef(h.Id, h.Title, h.ImageUrl)).ToList()));
        return JsonSerializer.Serialize(new { dimension, grounded, poolSize = pool, hits = rows }, Json);
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
