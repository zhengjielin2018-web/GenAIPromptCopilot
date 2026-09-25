using System.Text.Json;
using Npgsql;
using Pgvector;

namespace PromptCopilot.Api.Data;

/// <summary>SourceRef 是資料來源識別（<c>civitai:12345:0</c>、<c>kisegae:…</c>），隨 tool_result 事件給前端標圖片出處。</summary>
public sealed record PresetHit(long Id, string Title, string Category, IReadOnlyList<string> FacetIds,
    string PromptSnippet, string? NegativeSnippet, string? ImageUrl, double Dist, string? SourceRef);

/// <summary>FacetTags（2026-09-25）：tag → facet 拆分，回填腳本寫的；未回填為 null（線上照樣輸出 null）。</summary>
public sealed record PresetDetail(long Id, string Title, string Category, string Description, IReadOnlyList<string> Tags,
    IReadOnlyList<string> FacetIds, string PromptSnippet, string? NegativeSnippet, string? ImageUrl,
    string? SourceRef, string? SourceUrl, IReadOnlyDictionary<string, IReadOnlyList<string>>? FacetTags = null);

/// <summary>整套組合推薦的候選（設計 §4.4）。FacetTags 一定非 null：查詢只取已回填的列。</summary>
public sealed record PresetCandidate(long Id, string Title, IReadOnlyList<string> FacetIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FacetTags, string? ImageUrl, string? SourceRef, double Dist);

public class PresetRepository(NpgsqlDataSource ds)
{
    // 與 scripts/pipeline/retrieval.py 的 PRESETS_SQL 一致：GIN 過濾該維度 facet，HNSW 依「這一句自己的向量」排序，不設門檻
    private const string SearchSql = """
        SELECT id, title, category, facet_ids, prompt_snippet, negative_snippet, image_url,
               preset_embedding <=> @q AS dist, source_ref
        FROM prompt_knowledge_presets
        WHERE facet_ids && @facets
        ORDER BY dist
        LIMIT @k
        """;
    private const string PoolSql = "SELECT count(*) FROM prompt_knowledge_presets WHERE facet_ids && @facets";
    private const string GetSql = """
        SELECT id, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, image_url, source_ref, facet_tags::text
        FROM prompt_knowledge_presets WHERE id = @id
        """;
    // 設計 §4.4：facet_ids && 是 GIN 粗篩；組合要有該維度 2 個以上「採得到 tag」的 facet（一個 facet 是單品不是組合）。
    // 算 facet_tags 裡非空的鍵而不是 facet_ids：回填對歸不進 facet 的列寫 {}，也可能留空陣列，那些 facet 採用時拿不到東西。
    private const string SetFilter = """
        facet_ids && @facets
          AND facet_tags IS NOT NULL
          AND preset_embedding IS NOT NULL
          AND (SELECT count(*) FROM jsonb_each(facet_tags) AS ft(facet_id, tags)
               WHERE ft.facet_id = ANY(@facets) AND jsonb_array_length(ft.tags) > 0) >= 2
        """;
    // 沒錨：池子大，HNSW 依向量取前 N 正是要的。
    private const string RecommendSql = $"""
        SELECT id, title, facet_ids, facet_tags::text, image_url, source_ref, preset_embedding <=> @q AS dist
        FROM prompt_knowledge_presets
        WHERE {SetFilter}
        ORDER BY dist
        LIMIT @take
        """;
    // 有錨：先把符合的列整批撈出來再排序。MATERIALIZED 擋住規劃器把 ORDER BY／LIMIT 推進 HNSW：
    // iterative scan 掃到 hnsw.max_scan_tuples（預設 20,000，全表已近兩萬筆）就停，罕見的錨（涼鞋 20 筆）會被默默漏掉，錨的結果必須精確。
    // 錨本身：任一指定 facet 底下有 tag 整段相等或以空白為界的字尾相符（與 TagAttribution.EndsWithWord 同義）；
    // 資料庫的 tag 可能帶底線，錨已正規化成空白，所以比對前 replace。
    // 距離在 CTE 裡就算好：物化的暫存只帶一個 float，不帶 768 維向量。
    private const string AnchoredRecommendSql = $"""
        WITH c AS MATERIALIZED (
          SELECT id, title, facet_ids, facet_tags, image_url, source_ref, preset_embedding <=> @q AS dist
          FROM prompt_knowledge_presets
          WHERE {SetFilter}
            AND EXISTS (
              SELECT 1
              FROM jsonb_each(facet_tags) AS ft(facet_id, tags), jsonb_array_elements_text(ft.tags) AS t(tag)
              WHERE ft.facet_id = ANY(@anchorFacets)
                AND (replace(lower(t.tag), '_', ' ') = ANY(@anchorTags) OR replace(lower(t.tag), '_', ' ') LIKE ANY(@anchorSuffixes))
            )
        )
        SELECT id, title, facet_ids, facet_tags::text, image_url, source_ref, dist
        FROM c
        ORDER BY dist
        LIMIT @take
        """;

    public virtual async Task<IReadOnlyList<PresetHit>> SearchAsync(float[] query, IReadOnlyList<string> facetIds, int k, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(SearchSql);
        cmd.Parameters.AddWithValue("q", new Vector(query));
        cmd.Parameters.AddWithValue("facets", facetIds.ToArray());
        cmd.Parameters.AddWithValue("k", k);
        var list = new List<PresetHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PresetHit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    public virtual async Task<long> PoolSizeAsync(IReadOnlyList<string> facetIds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(PoolSql);
        cmd.Parameters.AddWithValue("facets", facetIds.ToArray());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public virtual async Task<IReadOnlyList<PresetCandidate>> RecommendAsync(float[] query, IReadOnlyList<string> dimensionFacets,
        IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take, CancellationToken ct)
    {
        var anchored = anchorFacets.Count > 0 && anchorTags.Count > 0;
        await using var cmd = ds.CreateCommand(anchored ? AnchoredRecommendSql : RecommendSql);
        cmd.Parameters.AddWithValue("q", new Vector(query));
        cmd.Parameters.AddWithValue("facets", dimensionFacets.ToArray());
        cmd.Parameters.AddWithValue("take", take);
        if (anchored)
        {
            cmd.Parameters.AddWithValue("anchorFacets", anchorFacets.ToArray());
            cmd.Parameters.AddWithValue("anchorTags", anchorTags.ToArray());
            cmd.Parameters.AddWithValue("anchorSuffixes", anchorTags.Select(t => "% " + EscapeLike(t)).ToArray());
        }
        var list = new List<PresetCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PresetCandidate(r.GetInt64(0), r.GetString(1), r.GetFieldValue<string[]>(2), ParseFacetTags(r.GetString(3))!,
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetDouble(6)));
        return list;
    }

    public virtual async Task<PresetDetail?> GetAsync(long id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(GetSql);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var sourceRef = r.IsDBNull(9) ? null : r.GetString(9);
        return new PresetDetail(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4),
            r.GetFieldValue<string[]>(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            sourceRef, SourceAttribution.UrlFor(sourceRef), ParseFacetTags(r.IsDBNull(10) ? null : r.GetString(10)));
    }

    /// <summary>jsonb 以 ::text 讀回來再自己解：Npgsql 對 jsonb 的動態型別對應在不同版本行為不一，字串最穩。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ParseFacetTags(string? json)
    {
        if (json is null) return null;
        var d = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new();
        return d.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>LIKE 的 % 與 _ 是萬用字元；PostgreSQL 預設的跳脫字元是反斜線。</summary>
    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
