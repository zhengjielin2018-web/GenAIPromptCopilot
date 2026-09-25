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
    // 設計 §4.4：GIN 過濾該維度、至少涵蓋維度裡 2 個 facet（一個 facet 是單品不是組合）、只取已回填的列；
    // 有錨時先過濾再排序（涼鞋 20 筆、穿著 2,357 筆，先取向量前 N 再過濾會漏掉大半）。
    private const string RecommendSql = """
        SELECT id, title, facet_ids, facet_tags::text, image_url, source_ref, preset_embedding <=> @q AS dist
        FROM prompt_knowledge_presets
        WHERE facet_ids && @facets
          AND facet_tags IS NOT NULL
          AND cardinality(ARRAY(SELECT unnest(facet_ids) INTERSECT SELECT unnest(@facets))) >= 2
        """;
    // 錨：該維度任一 covered facet 底下有 tag 整段相等或以空白為界的字尾相符（與 TagAttribution.EndsWithWord 同義）。
    // 資料庫的 tag 可能帶底線，錨已正規化成空白，所以比對前 replace。
    private const string AnchorClause = """
          AND EXISTS (
            SELECT 1
            FROM jsonb_each(facet_tags) AS ft(facet_id, tags), jsonb_array_elements_text(ft.tags) AS t(tag)
            WHERE ft.facet_id = ANY(@anchorFacets)
              AND (replace(lower(t.tag), '_', ' ') = ANY(@anchorTags) OR replace(lower(t.tag), '_', ' ') LIKE ANY(@anchorSuffixes))
          )
        """;
    private const string RecommendTail = "\n        ORDER BY dist\n        LIMIT @take";

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
        await using var cmd = ds.CreateCommand(RecommendSql + (anchored ? AnchorClause : "") + RecommendTail);
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
