using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace PromptCopilot.Api.Data;

public sealed record HistoryHit(Guid Id, string UserIntent, string PositivePrompt, string SubjectProfile, double Dist);
public sealed record HistoryInsert(string UserIntent, string Positive, string Negative, string Profile, string? CompletenessJson, float[] IntentEmbedding);

/// <summary>非 sealed、InsertAsync 為 virtual：端點測試要在不碰資料庫的情況下走完 save-to-shared。</summary>
public class HistoryRepository(NpgsqlDataSource ds)
{
    private const string SearchSql = """
        SELECT id, user_intent, positive_prompt, subject_profile, intent_embedding <=> @q AS dist
        FROM shared_prompt_histories
        WHERE subject_profile = @profile
        ORDER BY dist
        LIMIT @k
        """;
    private const string InsertSql = """
        INSERT INTO shared_prompt_histories
            (user_intent, positive_prompt, negative_prompt, subject_profile, source, completeness_scores, intent_embedding)
        VALUES (@intent, @pos, @neg, @profile, 'user', @scores, @emb)
        RETURNING id
        """;

    public virtual async Task<IReadOnlyList<HistoryHit>> SearchAsync(float[] query, string profile, int k, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(SearchSql);
        cmd.Parameters.AddWithValue("q", new Vector(query));
        cmd.Parameters.AddWithValue("profile", profile);
        cmd.Parameters.AddWithValue("k", k);
        var list = new List<HistoryHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HistoryHit(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4)));
        return list;
    }

    public virtual async Task<Guid> InsertAsync(HistoryInsert h, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(InsertSql);
        cmd.Parameters.AddWithValue("intent", h.UserIntent);
        cmd.Parameters.AddWithValue("pos", h.Positive);
        cmd.Parameters.AddWithValue("neg", h.Negative);
        cmd.Parameters.AddWithValue("profile", h.Profile);
        cmd.Parameters.Add(new NpgsqlParameter("scores", NpgsqlDbType.Jsonb) { Value = (object?)h.CompletenessJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("emb", new Vector(h.IntentEmbedding));
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
