using Npgsql;
using Pgvector.Npgsql;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Tests.Data;

[Trait("Category", "Integration")]
public class RepositoryIntegrationTests : IAsyncLifetime
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
        Assert.Contains(hits, h => h.Title == "測試片段" && h.Dist < 1e-6);
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
