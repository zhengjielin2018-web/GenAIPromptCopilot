using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Endpoints;

public sealed record RenderPostRequest(int TurnIndex, string? Safety = null);
public sealed record RenderCreated(string RenderId);

/// <summary>定稿後生成預覽（docs/superpowers/specs/2026-10-09-render-preview-design.md §5）。
/// 端點只做跟 session 狀態有關的檢查與快照；其餘交給 RenderService（設計 §5.1「誰檢查哪幾列」）。</summary>
public static class RenderEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/sessions").WithTags("Render");

        g.MapPost("/{id}/renders", async (string id, RenderPostRequest req, SessionStore store, RenderService renders, FacetCatalog catalog,
            IOptions<RenderOptions> render, IOptions<SafetyOptions> safety, CancellationToken ct) =>
        {
            if (!render.Value.Enabled) return Results.NotFound(new ErrorBody("生圖沒有開啟"));
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
            bool? safetyOn = req.Safety?.Trim().ToLowerInvariant() switch { null or "" or "on" => true, "off" => false, _ => null };
            if (safetyOn is null) return Results.BadRequest(new ErrorBody("safety 只能是 on 或 off"));
            if (safetyOn is false && !safety.Value.AllowDisable)
                return Results.Json(new ErrorBody("後端沒開放關閉審查：.env 設 SAFETY_ALLOW_DISABLE=true 後重建 api（本機開發設 Safety:AllowDisable）"),
                    statusCode: StatusCodes.Status403Forbidden);
            // 讀定稿、facet 狀態與對話：那一輪還在跑（而且隨時可能回滾）時讀到的是半途的狀態（同存進共享庫）
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new ErrorBody("這個 session 還有一輪在跑"));
            RenderRequest request;
            try
            {
                if (s.Status != SessionStatus.Finalized || s.LastFinal is null) return Results.Conflict(new ErrorBody("尚未定稿"));
                if (s.LastFinal.TurnIndex != req.TurnIndex) return Results.Conflict(new ErrorBody("只有最新一張定稿卡可以生成預覽"));
                // 對話整理在鎖裡組：背景的 pipeline 不碰 ChatHistory。使用者沒再開口（鍵相同）就沿用上一份清單（符合度設計 §4.3）
                var intent = IntentTranscript.Build(s, catalog);
                var reused = s.Requirements is { } snap && snap.Key == intent.Key ? snap.Items : null;
                request = new RenderRequest(s.Id, req.TurnIndex, s.LastFinal.Positive, s.LastFinal.Negative, s.RenderSeed,
                    intent, safetyOn.Value, s.LastFinal.Reviewed, reused);
            }
            finally { s.Lock.Release(); }

            return await renders.RequestAsync(s, request, ct) switch
            {
                RenderAdmission.Accepted a => Results.Accepted($"/api/sessions/{s.Id}/renders/{a.Record.Id}", new RenderCreated(a.Record.Id)),
                RenderAdmission.Rejected r => Results.Json(new ErrorBody(r.Error), statusCode: r.StatusCode),
                _ => throw new InvalidOperationException("unknown admission"),
            };
        })
        .WithSummary("最新的定稿卡生成預覽圖")
        .WithDescription("""
            body：`{"turnIndex": 4, "safety": "on" | "off"}`。`turnIndex` 必須是最後一次定稿的那輪（`GET /api/sessions/{id}` 的 `lastFinal.turnIndex`）；`safety` 可省略（預設 `on`），`off` 要後端開 `Safety:AllowDisable`。

            收下就回 `202 {"renderId": "..."}`，之後用 `GET /api/sessions/{id}/renders/{renderId}` 輪詢。不經過模型、不算一輪；圖只存在 session 裡，跟 session 一起過期。

            - `404`：生圖沒開（`Render:EndpointId`／`Render:ApiKey` 沒設），或 session 不存在
            - `400`：`safety` 不是 `on`／`off`
            - `403`：`safety: off` 但後端沒開放
            - `409`：這個 session 還有一輪在跑；還沒定稿；`turnIndex` 不是最新那張定稿卡；上一張還沒結束
            - `429`：這段對話或全站今天的張數到上限
            - `503`：預估等待超過 `Render:MaxEstimatedWaitSeconds`
            """)
        .Produces<RenderCreated>(StatusCodes.Status202Accepted)
        .Produces<ErrorBody>(StatusCodes.Status400BadRequest)
        .Produces<ErrorBody>(StatusCodes.Status403Forbidden)
        .Produces<ErrorBody>(StatusCodes.Status404NotFound)
        .Produces<ErrorBody>(StatusCodes.Status409Conflict)
        .Produces<ErrorBody>(StatusCodes.Status429TooManyRequests)
        .Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);

        g.MapGet("/{id}/renders/{renderId}", (string id, string renderId, SessionStore store, RenderQueue queue) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
            return s.Renders.TryGetValue(renderId, out var r) ? Results.Ok(r.View(queue.PositionOf(r))) : Results.NotFound(new ErrorBody("找不到這張預覽"));
        })
        .WithSummary("查一張預覽的狀態")
        .WithDescription("""
            `status`：`queued`（`position` 是排第幾，1 是下一張）→ `generating`（已送 RunPod）→ `reviewing`（看圖審查中，圖還不給；審查關著時跳過）→ `self_checking`（圖拿得到，評分還在跑）→ `done`；或 `failed`／`blocked`（`message` 是給使用者看的一句話）。
            `selfCheck`：使用者想法符合度評分（docs/superpowers/specs/2026-10-10-intent-fit-scoring-design.md §7）。`{status: pending | ok | unavailable, score, summary, items: [{id, text, source: user | delegated, tags, negativeTags, verdict: met | unmet | unclear, issue: none | prompt_missing | not_rendered | unclear, reason}]}`。`score` 是 0–100（只算 `source: user`、`unclear` 不計），沒有判得出來的要求時是 `null`；`score`、`summary`、`items` 只在 `done` 時有。`tags` 與 `negativeTags` 都空表示 prompt 沒寫這條。
            `timings` 是各段毫秒數：`queueMs`、`delayMs`、`executionMs`、`reviewMs`、`requirementsMs`（整理要求清單，跟生圖同時跑）、`selfCheckMs`（看圖步）。
            """)
        .Produces<RenderView>(StatusCodes.Status200OK)
        .Produces<ErrorBody>(StatusCodes.Status404NotFound);

        g.MapGet("/{id}/renders/{renderId}/image", (string id, string renderId, SessionStore store) =>
        {
            var s = store.TryGet(id);
            if (s is not null && s.Renders.TryGetValue(renderId, out var r) && r.Image is { } png) return Results.File(png, "image/png");
            return Results.NotFound();
        })
        .WithSummary("拿預覽圖")
        .WithDescription("`self_checking` 或 `done` 才回 `image/png`；審查開著時那表示看圖審查過了。其他狀態與被擋的圖一律 `404`。")
        .Produces(StatusCodes.Status200OK, contentType: "image/png")
        .Produces(StatusCodes.Status404NotFound);
    }
}
