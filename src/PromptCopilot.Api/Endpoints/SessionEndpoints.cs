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
public sealed record SessionCreated(string SessionId);
public sealed record SavedToShared(Guid Id);
public sealed record ErrorBody(string Error);

public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/sessions").WithTags("Sessions");

        g.MapPost("/", (SessionStore store) =>
        {
            var s = store.Create();
            return Results.Created($"/api/sessions/{s.Id}", new SessionCreated(s.Id));
        })
        .WithSummary("開一段新對話")
        .WithDescription("""
            不用帶 body。回 `201` 與 `{"sessionId": "..."}`，之後的呼叫都帶這個 id。

            session 只存在記憶體：API 重啟就消失；閒置超過 `Orchestrator:SessionSlidingExpirationMinutes`（預設 120 分鐘）也會過期，之後再用這個 id 會得到 404。
            """)
        .Produces<SessionCreated>(StatusCodes.Status201Created);

        g.MapPost("/{id}/messages", async (string id, MessageRequest req, SessionStore store, IPromptOrchestrator orchestrator, HttpContext http) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new ErrorBody("text 不可為空"));
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new ErrorBody("這個 session 還有一輪在跑"));
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
        })
        .WithSummary("送一句話，跑一輪對話（SSE 串流）")
        .WithDescription("""
            body：`{"text": "一個銀髮少女站在雨夜的霓虹街頭"}`。回應是 `text/event-stream`，每一筆是 `event: <名稱>` 加一行 `data: <JSON>`。

            | 事件 | 內容 |
            | :--- | :--- |
            | `session` | 一定是第一筆：第幾輪（`turnIndex`）、這輪開始時的狀態（`Collecting` 還在收集／`Finalized` 已定稿） |
            | `tool_call`／`tool_result` | 模型呼叫的工具與結果，一輪可能好幾次。`tool_result.presets` 是檢索到的 preset，可拿 id 去 `GET /api/presets/{id}` |
            | `dimensions` | 題材（`profile`）與每個 facet 的狀態：`covered`／`missing`／`waived`（使用者說不指定）／`notApplicable`。輪中有變動就送，成功的一輪最後會再送一次完整的 |
            | `final` | 這一輪的結果，看 `kind`：`ask` 追問（`preamble`、`asks`）、`message` 討論或回答問題（`message`、`options`）、`finalized` 定稿（`positive`、`negative`、`tips`）、`save_consent_requested` 使用者要求儲存（見 `save-to-shared`） |
            | `blocked` | 被攔下，`reason`：`Blocked_NSFW`、`Blocked_Celebrity`（輸入端，不會呼叫模型）、`Blocked_Output`（模型輸出被攔）、`Blocked_Upstream`（Gemini 拒絕生成）。session 狀態不變 |
            | `error` | 這一輪失敗，`code`：`timeout`、`protocol_violation`、`turn_failed`。session 已還原到送出前，可以直接重送同一句 |

            成功的一輪以 `final` + `dimensions` 收尾；失敗或被攔的一輪以 `blocked` 或 `error` 收尾，不會有 `final`。
            一輪最多 `Orchestrator:MaxToolCallsPerTurn` 次工具呼叫、`Orchestrator:TurnTimeoutSeconds` 秒。

            Swagger UI 會等整輪跑完才一次顯示所有事件（通常數秒到數十秒）。要逐筆看，用 `curl -N` 或 repo 的 `manual-tests/chat.py`。

            - `404`：session 不存在或已過期
            - `400`：`text` 是空白
            - `409`：同一個 session 上一輪還沒跑完（一個 session 同時只跑一輪）
            """)
        .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest)
        .Produces<ErrorBody>(StatusCodes.Status404NotFound)
        .Produces<ErrorBody>(StatusCodes.Status409Conflict);

        g.MapPost("/{id}/save-to-shared", async (string id, SaveRequest req, SessionStore store, IEmbeddingClient embed,
            HistoryRepository histories, IAuditSink audit, CancellationToken ct) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound();
            // 這裡會讀 FacetStates 與 LastFinal。那一輪還在跑（而且隨時可能被回滾）時讀到的是半途的狀態。
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new ErrorBody("這個 session 還有一輪在跑"));
            try
            {
                if (s.Status != SessionStatus.Finalized || s.LastFinal is null || s.Profile is null) return Results.Conflict(new ErrorBody("尚未定稿"));
                if (string.IsNullOrWhiteSpace(req.Intent)) return Results.BadRequest(new ErrorBody("intent 不可為空"));
                var vec = (await embed.EmbedAsync(new[] { req.Intent }, GeminiEmbeddingClient.RetrievalDocument, ct))[0];
                var scores = JsonSerializer.Serialize(s.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)));
                var newId = await histories.InsertAsync(new HistoryInsert(req.Intent.Trim(), s.LastFinal.Positive, s.LastFinal.Negative, s.Profile, scores, vec), ct);
                // 資料已經寫進去了：稽核再失敗也不能把回應變成 500，否則使用者一重試就多一筆重複的資料
                try { await audit.WriteAsync(new AuditEntry(s.Id, s.TurnIndex, "Saved_To_Shared", PayloadJson: JsonSerializer.Serialize(new { id = newId })), ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    app.Logger.LogWarning(e, "Saved_To_Shared audit write failed for session {SessionId}, row {RowId}", s.Id, newId);
                }
                return Results.Ok(new SavedToShared(newId));
            }
            finally { s.Lock.Release(); }
        })
        .WithSummary("把這個 session 的定稿存進共享庫")
        .WithDescription("""
            body：`{"intent": "一句話描述這張圖"}`。把這個 session 最後一次的定稿（positive、negative、題材、各 facet 狀態）連同 `intent` 的向量寫進 `shared_prompt_histories`，回新資料列的 `{"id": "..."}`。之後的對話呼叫 `SearchSimilarPrompts` 時會撈到它當參考。

            模型不會自己存：使用者說要存時，`messages` 只會回 `final.kind = save_consent_requested`，要由客戶端在使用者同意後呼叫這支。**這是真的寫入資料庫。**

            - `404`：session 不存在或已過期
            - `409`：還沒定稿，或這個 session 還有一輪在跑
            - `400`：`intent` 是空白
            """)
        .Produces<SavedToShared>(StatusCodes.Status200OK)
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorBody>(StatusCodes.Status409Conflict);
    }
}
