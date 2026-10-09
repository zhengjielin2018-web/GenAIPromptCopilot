using System.Text.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Rendering;

public abstract record RenderAdmission
{
    public sealed record Accepted(RenderRecord Record) : RenderAdmission;
    public sealed record Rejected(int StatusCode, string Error) : RenderAdmission;
}

public static class RenderAudit
{
    /// <summary>camelCase、中文不跳脫（同 AgenticOrchestrator 的 audit payload），判定理由在 audit 裡讀得懂。</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>稽核是旁路：寫不進去只記 log，不改變回應或狀態（同推薦）。</summary>
    public static async Task TryWriteAsync(IAuditSink audit, AuditEntry entry, ILogger logger)
    {
        try { await audit.WriteAsync(entry, CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "audit write failed: {EventType} session {SessionId}", entry.EventType, entry.SessionId);
        }
    }
}

/// <summary>收件與建立工作的唯一入口（預覽設計 §4、§5.1、§12）。輸入是中性的 RenderRequest：不讀 LastFinal、不拿 session 鎖——
/// 那些是呼叫端的事；之後自主閉環的生圖工具在一輪對話裡呼叫它時，那一輪已經拿著鎖。
/// 「檢查上一張」到「建立紀錄」在 _gate 裡一次做完：同一個 session 兩個請求同時到，只有一個過得去。</summary>
public sealed class RenderService(RenderQueue queue, SafetyClassifier classifier, IAuditSink audit, IOptions<RenderOptions> options, ILogger<RenderService> logger)
{
    private readonly object _gate = new();

    private sealed record Refusal(string Reason, int Status, string Error, int SessionCount, int DailyCount, double Wait);

    public async Task<RenderAdmission> RequestAsync(Session session, RenderRequest request, CancellationToken ct)
    {
        var o = options.Value;
        RenderRecord? record = null;
        Refusal? refusal = null;
        lock (_gate)
        {
            if (session.Renders.Values.Any(r => !r.IsFinished)) return new RenderAdmission.Rejected(409, "上一張還在生");
            var used = session.Renders.Values.Count(r => r.CountsTowardLimit);
            var daily = queue.DailyCount;
            var wait = queue.EstimatedWaitSeconds(o.DefaultImageSeconds);
            // 先判斷、最後才保留額度：被 503 擋下的不能吃掉當日額度
            if (used >= o.PerSessionLimit) refusal = new("session_limit", 429, $"這段對話的預覽張數已達上限（{o.PerSessionLimit} 張）", used, daily, wait);
            else if (daily >= o.DailyLimit) refusal = new("daily_limit", 429, "今天的預覽張數已達上限，明天再試", used, daily, wait);
            else if (wait > o.MaxEstimatedWaitSeconds) refusal = new("busy", 503, "目前人多，稍後再試", used, daily, wait);
            else
            {
                record = new RenderRecord(Guid.NewGuid().ToString("N"), request, queue.TakeDaily());
                session.Renders[record.Id] = record;
            }
        }

        if (refusal is not null)
        {
            await RenderAudit.TryWriteAsync(audit, new AuditEntry(request.SessionId, request.TurnIndex, "Render_Rejected",
                PayloadJson: JsonSerializer.Serialize(new
                {
                    reason = refusal.Reason, sessionCount = refusal.SessionCount, dailyCount = refusal.DailyCount,
                    estimatedWaitSeconds = Math.Round(refusal.Wait, 1),
                }, RenderAudit.Json)), logger);
            return new RenderAdmission.Rejected(refusal.Status, refusal.Error);
        }

        // 定稿是在審查關著時產生的、這次又開著：先補審正向詞再花錢（預覽設計 §6）。審查關著時完全不跑分類器。
        // 同一份正向詞補審過了就不再審：同一張定稿卡再生不用每次多等一趟分類器。
        if (request.SafetyOn && !request.PromptReviewed && session.PreReviewedPositive != request.Positive)
        {
            if (await PromptBlockedBecauseAsync(record!, request, ct) is { } because)
            {
                record!.Block(RenderMessages.PromptBlocked, "prompt", because);
                queue.ReturnDaily(record.QuotaDay);
                await RenderAudit.TryWriteAsync(audit, new AuditEntry(request.SessionId, request.TurnIndex, "Render_Blocked",
                    PayloadJson: JsonSerializer.Serialize(new { renderId = record.Id, safety = "on", stage = "prompt", reason = because }, RenderAudit.Json)), logger);
                return new RenderAdmission.Accepted(record);
            }
            session.PreReviewedPositive = request.Positive;
        }

        queue.Enqueue(record!);
        return new RenderAdmission.Accepted(record!);
    }

    /// <summary>沒過回判定理由，過了回 null。判不出來就不放行（同 SafetyClassifier 的上層）。</summary>
    private async Task<string?> PromptBlockedBecauseAsync(RenderRecord record, RenderRequest request, CancellationToken ct)
    {
        try
        {
            var v = await classifier.ClassifyOutputAsync(request.Positive, ct);
            return v.Nsfw || v.RealPerson ? v.Reason : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 使用者關掉頁面：紀錄不能停在 queued 又不在佇列裡，否則這個 session 會一直「上一張還在生」。收件過的每一張都要有收尾的 audit
            record.Fail(RenderMessages.Failed, "cancelled", "請求在補審時被取消");
            queue.ReturnDaily(record.QuotaDay);
            await RenderAudit.TryWriteAsync(audit, new AuditEntry(request.SessionId, request.TurnIndex, "Render_Failed",
                PayloadJson: JsonSerializer.Serialize(new { renderId = record.Id, safety = "on", stage = "prompt", error = record.FailureKind, detail = record.Detail }, RenderAudit.Json)), logger);
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "prompt review failed for render {RenderId}", record.Id);
            return $"分類器失敗：{e.GetType().Name}";
        }
    }
}
