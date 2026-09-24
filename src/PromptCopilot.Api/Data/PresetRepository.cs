using Npgsql;
using Pgvector;

namespace PromptCopilot.Api.Data;

/// <summary>SourceRef 是資料來源識別（<c>civitai:12345:0</c>、<c>kisegae:…</c>），隨 tool_result 事件給前端標圖片出處。</summary>
public sealed record PresetHit(long Id, string Title, string Category, IReadOnlyList<string> FacetIds,
    string PromptSnippet, string? NegativeSnippet, string? ImageUrl, double Dist, string? SourceRef);

public sealed record PresetDetail(long Id, string Title, string Category, string Description, IReadOnlyList<string> Tags,
    IReadOnlyList<string> FacetIds, string PromptSnippet, string? NegativeSnippet, string? ImageUrl,
    string? SourceRef, string? SourceUrl);

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
        SELECT id, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, image_url, source_ref
        FROM prompt_knowledge_presets WHERE id = @id
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

    public virtual async Task<PresetDetail?> GetAsync(long id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(GetSql);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var sourceRef = r.IsDBNull(9) ? null : r.GetString(9);
        return new PresetDetail(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4),
            r.GetFieldValue<string[]>(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            sourceRef, SourceAttribution.UrlFor(sourceRef));
    }
}
