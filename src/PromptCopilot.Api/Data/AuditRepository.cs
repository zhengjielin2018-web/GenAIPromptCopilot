using Npgsql;
using NpgsqlTypes;

namespace PromptCopilot.Api.Data;

public sealed record AuditEntry(string? SessionId, int? TurnIndex, string EventType, string? PromptVersion = null,
    string? RawInput = null, string? PayloadJson = null, int? PromptTokens = null, int? CompletionTokens = null, int? LatencyMs = null);

public interface IAuditSink
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

public sealed class AuditRepository(NpgsqlDataSource ds) : IAuditSink
{
    private const string Sql = """
        INSERT INTO audit_logs (session_id, turn_index, event_type, prompt_version, raw_input, payload, prompt_tokens, completion_tokens, latency_ms)
        VALUES (@s, @t, @e, @v, @raw, @p, @pt, @ct, @ms)
        """;

    public async Task WriteAsync(AuditEntry a, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(Sql);
        cmd.Parameters.AddWithValue("s", (object?)a.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", (object?)a.TurnIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("e", a.EventType);
        cmd.Parameters.AddWithValue("v", (object?)a.PromptVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("raw", (object?)a.RawInput ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = (object?)a.PayloadJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("pt", (object?)a.PromptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ct", (object?)a.CompletionTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ms", (object?)a.LatencyMs ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
