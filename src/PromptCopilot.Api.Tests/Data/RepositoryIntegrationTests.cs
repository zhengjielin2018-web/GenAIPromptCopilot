using Npgsql;
using Pgvector.Npgsql;
using PromptCopilot.Api.Data;
using Xunit.Abstractions;

namespace PromptCopilot.Api.Tests.Data;

[Trait("Category", "Integration")]
public class RepositoryIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private static readonly string Ref = $"test:{Guid.NewGuid():N}";
    private static float[] Unit(int hot) { var v = new float[768]; v[hot] = 1f; return v; }

    public async Task InitializeAsync()
    {
        var b = new NpgsqlDataSourceBuilder(TestEnv.Db); b.UseVector(); _ds = b.Build();
        await using var cmd = _ds.CreateCommand("""
            INSERT INTO prompt_knowledge_presets (source_ref, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, preset_embedding)
            VALUES (@r, '測試片段', 'Style', 'd', ARRAY['x'], ARRAY['style.genre'], 'photo realism', NULL, @e)
            """);
        cmd.Parameters.AddWithValue("r", Ref);
        cmd.Parameters.AddWithValue("e", new Pgvector.Vector(Unit(0)));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var cmd = _ds.CreateCommand("DELETE FROM prompt_knowledge_presets WHERE source_ref = @r");
        cmd.Parameters.AddWithValue("r", Ref);
        await cmd.ExecuteNonQueryAsync();
        await using var cmd2 = _ds.CreateCommand("DELETE FROM shared_prompt_histories WHERE user_intent = @i");
        cmd2.Parameters.AddWithValue("i", Ref);
        await cmd2.ExecuteNonQueryAsync();
        await _ds.DisposeAsync();
    }

    [IntegrationFact]
    public async Task Preset_search_filters_by_facets_and_orders_by_distance()
    {
        var repo = new PresetRepository(_ds);
        var hits = await repo.SearchAsync(Unit(0), new[] { "style.genre" }, 5, default);
        Assert.Contains(hits, h => h.Title == "測試片段" && h.Dist < 1e-6 && h.SourceRef == Ref);
        Assert.Empty(await repo.SearchAsync(Unit(0), new[] { "nope.facet" }, 5, default));
        Assert.True(await repo.PoolSizeAsync(new[] { "style.genre" }, default) >= 1);
    }

    [IntegrationFact]
    public async Task Preset_get_returns_detail_or_null()
    {
        var repo = new PresetRepository(_ds);
        var hit = (await repo.SearchAsync(Unit(0), new[] { "style.genre" }, 5, default)).First(h => h.Title == "測試片段");
        var d = await repo.GetAsync(hit.Id, default);
        Assert.NotNull(d); Assert.Equal("photo realism", d!.PromptSnippet);
        Assert.Null(await repo.GetAsync(-1, default));
    }

    [IntegrationFact]
    public async Task Database_turns_on_hnsw_iterative_scan_in_strict_order()
    {
        // HNSW 先取近鄰、之後才套 WHERE；沒開 iterative scan，候選池只佔全表一兩成時過濾搜尋常回 0 筆（docs/known-issues.md 已修正 #2）
        await using var conn = await _ds.OpenConnectionAsync();
        // 先呼叫一次 vector 的輸入函式把 pgvector 載進這個連線；沒載入時 SHOW 會說不認得 hnsw.*，而不是回預設值 off
        await using (var load = new NpgsqlCommand("SELECT '[1]'::vector IS NOT NULL", conn)) await load.ExecuteScalarAsync();
        await using var show = new NpgsqlCommand("SHOW hnsw.iterative_scan", conn);
        Assert.Equal("strict_order", (string?)await show.ExecuteScalarAsync());
    }

    [IntegrationFact]
    public async Task Preset_search_fills_k_from_the_style_pool_when_the_query_sits_among_scene_presets()
    {
        // 要真實知識庫（幾千筆）才重現得出來。xunit 2 沒有動態 Skip，沒種子就寫一行說明後直接結束。
        await using (var count = _ds.CreateCommand("SELECT count(*) FROM prompt_knowledge_presets WHERE source_ref IS DISTINCT FROM @r"))
        {
            count.Parameters.AddWithValue("r", Ref);
            if ((long)(await count.ExecuteScalarAsync())! == 0)
            {
                output.WriteLine("prompt_knowledge_presets 沒有種子資料，略過（先跑 docker compose 的 seed 或 scripts/seed_data.py）");
                return;
            }
        }

        // 查詢向量：id 最小、帶 scene facet 但不帶 style facet 的片段。帶 style facet 的話它自己就是 style 池裡距離 0 的第一名。
        // 它的近鄰幾乎都是 scene 片段，正是 HNSW 取完 ef_search 筆再過濾 style 會剩 0 筆的情況。
        await using var pick = _ds.CreateCommand("""
            SELECT preset_embedding FROM prompt_knowledge_presets
            WHERE EXISTS (SELECT 1 FROM unnest(facet_ids) f WHERE f LIKE 'scene.%')
              AND NOT EXISTS (SELECT 1 FROM unnest(facet_ids) f WHERE f LIKE 'style.%')
            ORDER BY id LIMIT 1
            """);
        if (await pick.ExecuteScalarAsync() is not Pgvector.Vector picked)
        {
            output.WriteLine("知識庫沒有帶 scene facet 又不帶 style facet 的片段，略過");
            return;
        }
        var query = picked.ToArray();
        await using var facets = _ds.CreateCommand("""
            SELECT array_agg(DISTINCT f ORDER BY f) FROM prompt_knowledge_presets, unnest(facet_ids) f WHERE f LIKE 'style.%'
            """);
        var style = (string[])(await facets.ExecuteScalarAsync())!;

        var hits = await new PresetRepository(_ds).SearchAsync(query, style, 3, default);

        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.True(h.FacetIds.Intersect(style).Any(), $"{h.Id} 不在 style 池：{string.Join(",", h.FacetIds)}"));
        Assert.True(hits.Zip(hits.Skip(1)).All(p => p.First.Dist <= p.Second.Dist), string.Join(", ", hits.Select(h => h.Dist)));
    }

    [IntegrationFact]
    public async Task History_insert_then_search_by_profile()
    {
        var repo = new HistoryRepository(_ds);
        var id = await repo.InsertAsync(new HistoryInsert(Ref, "1girl", "lowres", "portrait", """{"style":0}""", Unit(1)), default);
        Assert.NotEqual(Guid.Empty, id);
        var hits = await repo.SearchAsync(Unit(1), "portrait", 3, default);
        Assert.Contains(hits, h => h.Id == id && h.Dist < 1e-6);
        Assert.DoesNotContain(await repo.SearchAsync(Unit(1), "landscape", 3, default), h => h.Id == id);
    }

    [IntegrationFact]
    public async Task Audit_write_inserts_row()
    {
        var repo = new AuditRepository(_ds);
        await repo.WriteAsync(new AuditEntry(Ref, 0, "Turn_Completed", "abc", null, """{"k":1}""", 10, 20, 300), default);
        await using var cmd = _ds.CreateCommand("SELECT count(*) FROM audit_logs WHERE session_id = @s");
        cmd.Parameters.AddWithValue("s", Ref);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        await using var del = _ds.CreateCommand("DELETE FROM audit_logs WHERE session_id = @s");
        del.Parameters.AddWithValue("s", Ref);
        await del.ExecuteNonQueryAsync();
    }
}
