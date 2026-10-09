using System.Text.Json;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Rendering;

/// <summary>一次處理一張（對應 RunPod Max Workers 1）。服務停止時：手上那張由 ProcessAsync 取消並收尾，佇列裡的全部標失敗。
/// pipeline 用 Lazy：背景服務在啟動時就建立，直接注入會連帶提早建出 Gemini 的 chat service，沒設 Llm:ApiKey 時整個 API 起不來
/// （Program.cs：Gemini 的 service 在第一輪才建，key 沒設時只在啟動時警告）。第一張進來才建；Lazy 用 PublicationOnly，
/// 建失敗（例如 workflow 範本壞掉）不快取例外，下一張再試。</summary>
public sealed class RenderWorker(RenderQueue queue, Lazy<RenderPipeline> pipeline, IAuditSink audit, ILogger<RenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var r = await queue.DequeueAsync(stoppingToken);
                // ProcessAsync 自己會收尾；這裡是最後一道（包括 pipeline 建不起來），單張出什麼事都不能讓背景服務停掉，
                // 那張也不能停在 queued：前端一直輪詢會讓 session 永遠不過期、當日額度也永遠不退（預覽設計 §7）
                try { await pipeline.Value.ProcessAsync(r, stoppingToken); }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(e, "render {RenderId} crashed the pipeline", r.Id);
                    await CrashedAsync(r, e);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var r in queue.DrainWaiting())
            {
                try { await pipeline.Value.AbandonAsync(r); }
                catch (Exception e) { await CrashedAsync(r, e); }
            }
        }
    }

    /// <summary>pipeline 自己的收尾沒跑到時的替代收尾：標失敗、離開處理中、沒送 RunPod 就退額度、寫 audit。已經收過的不重複做（額度不能退兩次）。</summary>
    private async Task CrashedAsync(RenderRecord r, Exception e)
    {
        if (r.IsFinished) { queue.Done(r, null); return; }
        r.Fail(RenderMessages.Failed, "crash", $"{e.GetType().Name}: {e.Message}");
        queue.Done(r, null);
        if (!r.Submitted) queue.ReturnDaily(r.QuotaDay);
        await RenderAudit.TryWriteAsync(audit, new AuditEntry(r.Request.SessionId, r.Request.TurnIndex, "Render_Failed",
            PayloadJson: JsonSerializer.Serialize(new { renderId = r.Id, safety = r.Request.SafetyOn ? "on" : "off", jobId = r.RunPodJobId, error = "crash", detail = r.Detail }, RenderAudit.Json)), logger);
    }
}
