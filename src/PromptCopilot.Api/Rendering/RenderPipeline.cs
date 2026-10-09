using System.Text.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Safety;

namespace PromptCopilot.Api.Rendering;

/// <summary>一張預覽從出佇列到收尾（預覽設計 §5、§6、§7）：組 workflow → 送 RunPod → 等 → 取圖 → 審圖 ∥ 自評。
/// 審圖過了圖就放出來（self_checking），自評繼續跑；審查關著時不跑審圖。收尾只有 WrapUpAsync 一個地方。</summary>
public sealed class RenderPipeline(IRunPodClient runpod, RenderWorkflow workflow, IImageReviewer reviewer, ISelfChecker checker, RenderQueue queue,
    IAuditSink audit, IOptions<RenderOptions> options, ILogger<RenderPipeline> logger, TimeProvider time)
{
    public async Task ProcessAsync(RenderRecord r, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        double? imageSeconds = null;
        try
        {
            r.MarkGenerating(r.EnqueuedAt is { } at ? (int)(time.GetUtcNow() - at).TotalMilliseconds : 0);
            var wf = workflow.Build(r.Request.Positive, r.Request.Negative, r.Request.Seed);
            var submitted = time.GetTimestamp();
            r.MarkSubmitted(await runpod.SubmitAsync(wf, ct));
            var job = await runpod.WaitAsync(r.RunPodJobId!, TimeSpan.FromSeconds(options.Value.JobTimeoutSeconds), ct);
            if (job.Status != "COMPLETED") { r.Fail(RenderMessages.Failed, $"runpod_{job.Status.ToLowerInvariant()}", job.Error); return; }
            var png = RunPodClient.ExtractImage(job);
            imageSeconds = time.GetElapsedTime(submitted).TotalSeconds;
            r.ImageArrived(png, job.DelayTime, job.ExecutionTime);

            // 審圖與自評共用同一張縮圖；使用者拿到的仍是原圖（可行性 §9.4）
            var forGemini = ImageForGemini.Prepare(png);
            if (forGemini.Error is { } why) logger.LogWarning("render {RenderId}: downscale for Gemini failed, sending the original PNG ({Error})", r.Id, why);
            var selfCheck = SelfCheckAsync(r, forGemini, ct);
            if (r.Request.SafetyOn) await ReviewAsync(r, forGemini, ct);
            await selfCheck;
        }
        catch (TimeoutException e) { r.Fail(RenderMessages.Timeout, "timeout", e.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { r.Fail(RenderMessages.Failed, "shutdown", "服務停止"); }
        catch (Exception e)
        {
            logger.LogWarning(e, "render {RenderId} failed", r.Id);
            r.Fail(RenderMessages.Failed, r.Submitted ? "runpod" : "submit", $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            await WrapUpAsync(r, imageSeconds, (int)time.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>服務停止時佇列裡還沒輪到的。</summary>
    public Task AbandonAsync(RenderRecord r)
    {
        r.Fail(RenderMessages.Failed, "shutdown", "服務停止，還沒輪到");
        return WrapUpAsync(r, null, 0);
    }

    private async Task ReviewAsync(RenderRecord r, GeminiImage image, CancellationToken ct)
    {
        var t = time.GetTimestamp();
        try
        {
            var v = await reviewer.ReviewAsync(image, ct);
            var ms = (int)time.GetElapsedTime(t).TotalMilliseconds;
            if (v.Nsfw || v.RealPerson) r.Block(RenderMessages.ImageBlocked, "image", v.Reason, ms);
            else r.ReviewPassed(ms);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // 判不出來或 Gemini 拒收：不給看圖（同 SafetyClassifier 判不出來就不放行）
            r.Block(RenderMessages.ReviewFailed, "image", $"{e.GetType().Name}: {e.Message}", (int)time.GetElapsedTime(t).TotalMilliseconds);
        }
    }

    private async Task SelfCheckAsync(RenderRecord r, GeminiImage image, CancellationToken ct)
    {
        var t = time.GetTimestamp();
        try { r.SelfCheckFinished(await checker.CheckAsync(image, r.Request.SelfCheckItems, ct), (int)time.GetElapsedTime(t).TotalMilliseconds); }
        catch (Exception e)
        {
            // 自評只是顯示：失敗就標 unavailable，圖照給（審查關著時 Gemini 拒收也一樣）
            logger.LogInformation(e, "self-check unavailable for render {RenderId}", r.Id);
            r.SelfCheckFinished(null, (int)time.GetElapsedTime(t).TotalMilliseconds);
        }
    }

    /// <summary>收尾集中在這裡（預覽設計 §4、§12）：done／failed／blocked 都走它。之後自主閉環要在圖好了時觸發下一輪，就加在這裡。</summary>
    private async Task WrapUpAsync(RenderRecord r, double? imageSeconds, int latencyMs)
    {
        queue.Done(r, imageSeconds);
        if (!r.Submitted) queue.ReturnDaily(r.QuotaDay);   // 沒送 RunPod 的不算張數
        var eventType = r.Status switch { RenderStatus.Done => "Render_Completed", RenderStatus.Blocked => "Render_Blocked", _ => "Render_Failed" };
        var verdicts = r.SelfCheck;
        var payload = JsonSerializer.Serialize(new
        {
            renderId = r.Id, safety = r.Request.SafetyOn ? "on" : "off", jobId = r.RunPodJobId,
            queueMs = r.QueueMs, delayMs = r.DelayMs, executionMs = r.ExecutionMs, reviewMs = r.ReviewMs, selfCheckMs = r.SelfCheckMs,
            selfCheck = new
            {
                status = RenderWire.SelfCheck(r.SelfCheckState),
                present = verdicts.Count(v => v.Verdict == "present"), absent = verdicts.Count(v => v.Verdict == "absent"), unclear = verdicts.Count(v => v.Verdict == "unclear"),
            },
            stage = r.BlockStage, error = r.FailureKind, detail = r.Detail,
        }, RenderAudit.Json);
        await RenderAudit.TryWriteAsync(audit, new AuditEntry(r.Request.SessionId, r.Request.TurnIndex, eventType, PayloadJson: payload, LatencyMs: latencyMs), logger);
    }
}
