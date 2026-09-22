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
            // headers 已經送出去就改不了 status code，框架的 500 到不了客戶端，只會把連線砍掉、
            // 留下一段被截斷的串流。補發一個 error frame，客戶端才知道這一輪結束了、結束在哪。
            // 還沒開始寫就不接（走框架的 500 才是對的）。
            catch (Exception e) when (http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
            {
                // 例外訊息可能帶連線字串、路徑、上游原文：進 log，不進串流
                app.Logger.LogError(e, "turn failed mid-stream for session {SessionId}", id);
                await SseWriter.WriteOneAsync(http.Response,
                    new ErrorEvent("turn_failed", "這一輪失敗，已還原到送出前的狀態。可以直接再送一次。"),
                    http.RequestAborted);
                return Results.Empty;
            }
            finally { s.Lock.Release(); }
        }).Produces(200, contentType: "text/event-stream");

        g.MapPost("/{id}/save-to-shared", async (string id, SaveRequest req, SessionStore store, IEmbeddingClient embed,
            HistoryRepository histories, IAuditSink audit, CancellationToken ct) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound();
            // 這裡會讀 FacetStates 與 LastFinal。那一輪還在跑（而且隨時可能被回滾）時讀到的是半途的狀態。
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new { error = "這個 session 還有一輪在跑" });
            try
            {
                if (s.Status != SessionStatus.Finalized || s.LastFinal is null || s.Profile is null) return Results.Conflict(new { error = "尚未定稿" });
                if (string.IsNullOrWhiteSpace(req.Intent)) return Results.BadRequest(new { error = "intent 不可為空" });
                var vec = (await embed.EmbedAsync(new[] { req.Intent }, GeminiEmbeddingClient.RetrievalDocument, ct))[0];
                var scores = JsonSerializer.Serialize(s.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)));
                var newId = await histories.InsertAsync(new HistoryInsert(req.Intent.Trim(), s.LastFinal.Positive, s.LastFinal.Negative, s.Profile, scores, vec), ct);
                // 資料已經寫進去了：稽核再失敗也不能把回應變成 500，否則使用者一重試就多一筆重複的資料
                try { await audit.WriteAsync(new AuditEntry(s.Id, s.TurnIndex, "Saved_To_Shared", PayloadJson: JsonSerializer.Serialize(new { id = newId })), ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    app.Logger.LogWarning(e, "Saved_To_Shared audit write failed for session {SessionId}, row {RowId}", s.Id, newId);
                }
                return Results.Ok(new { id = newId });
            }
            finally { s.Lock.Release(); }
        });
    }
}
