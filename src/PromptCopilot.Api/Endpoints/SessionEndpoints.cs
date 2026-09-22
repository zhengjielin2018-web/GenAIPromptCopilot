using System.Text.Json;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Endpoints;

public sealed record MessageRequest(string Text);
public sealed record SaveRequest(string Intent);

public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/sessions").WithTags("Sessions");

        g.MapPost("/", (SessionStore store) =>
        {
            var s = store.Create();
            return Results.Created($"/api/sessions/{s.Id}", new { sessionId = s.Id });
        });

        g.MapPost("/{id}/messages", async (string id, MessageRequest req, SessionStore store, IPromptOrchestrator orchestrator, HttpContext http) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new { error = "session 不存在或已過期" });
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "text 不可為空" });
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new { error = "這個 session 還有一輪在跑" });
            try
            {
                await SseWriter.WriteAsync(http.Response, orchestrator.RunTurnAsync(s, req.Text.Trim(), http.RequestAborted), http.RequestAborted);
                return Results.Empty;
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.Empty; }
            finally { s.Lock.Release(); }
        }).Produces(200, contentType: "text/event-stream");

        g.MapPost("/{id}/save-to-shared", async (string id, SaveRequest req, SessionStore store, IEmbeddingClient embed,
            HistoryRepository histories, IAuditSink audit, CancellationToken ct) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound();
            if (s.Status != SessionStatus.Finalized || s.LastFinal is null || s.Profile is null) return Results.Conflict(new { error = "尚未定稿" });
            if (string.IsNullOrWhiteSpace(req.Intent)) return Results.BadRequest(new { error = "intent 不可為空" });
            var vec = (await embed.EmbedAsync(new[] { req.Intent }, GeminiEmbeddingClient.RetrievalDocument, ct))[0];
            var scores = JsonSerializer.Serialize(s.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)));
            var newId = await histories.InsertAsync(new HistoryInsert(req.Intent.Trim(), s.LastFinal.Positive, s.LastFinal.Negative, s.Profile, scores, vec), ct);
            await audit.WriteAsync(new AuditEntry(s.Id, s.TurnIndex, "Saved_To_Shared", PayloadJson: JsonSerializer.Serialize(new { id = newId })), ct);
            return Results.Ok(new { id = newId });
        });
    }
}
