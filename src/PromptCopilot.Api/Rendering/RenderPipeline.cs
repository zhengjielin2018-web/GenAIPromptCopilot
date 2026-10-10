using System.Text.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Safety;

namespace PromptCopilot.Api.Rendering;

/// <summary>一張預覽從出佇列到收尾（預覽設計 §5、§6、§7；符合度設計 §6）：文字步（整理要求清單）一開始就啟動、跟生圖同時跑；
/// 組 workflow → 送 RunPod → 等 → 取圖 → 審圖 ∥（等文字步 → 看圖步 → 計分）。
/// 審圖過了圖就放出來（self_checking），評分繼續跑；審查關著時不跑審圖；審圖擋下、生圖失敗就取消評分。
/// 審圖、文字步、看圖步各有 GeminiTimeoutSeconds 上限。收尾只有 WrapUpAsync 一個地方。</summary>
public sealed class RenderPipeline(IRunPodClient runpod, RenderWorkflow workflow, IImageReviewer reviewer, IRequirementExtractor extractor, ISelfChecker checker,
    RenderQueue queue, IAuditSink audit, IOptions<RenderOptions> options, ILogger<RenderPipeline> logger, TimeProvider time)
{
    private sealed record RequirementsOutcome(IReadOnlyList<RequirementMatch> Items, bool Reused);

    public async Task ProcessAsync(RenderRecord r, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var gotImage = false;
        // 文字步與看圖步共用一個取消來源：審圖擋下、生圖失敗、出例外時一起停
        using var evaluation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<RequirementsOutcome?>? requirements = null;
        try
        {
            r.MarkGenerating(r.EnqueuedAt is { } at ? (int)(time.GetUtcNow() - at).TotalMilliseconds : 0);
            // 文字步不需要圖：跟送 RunPod、等圖同時跑，使用者感覺到的等待不變（符合度設計 §6）
            requirements = RequirementsAsync(r, evaluation.Token);
            var wf = workflow.Build(r.Request.Positive, r.Request.Negative, r.Request.Seed);
            r.MarkSubmitted(await runpod.SubmitAsync(wf, ct));
            var job = await runpod.WaitAsync(r.RunPodJobId!, TimeSpan.FromSeconds(options.Value.JobTimeoutSeconds), ct);
            if (job.Status != "COMPLETED") { r.Fail(RenderMessages.Failed, $"runpod_{job.Status.ToLowerInvariant()}", job.Error); return; }
            var png = RunPodClient.ExtractImage(job);
            gotImage = true;
            r.ImageArrived(png, job.DelayTime, job.ExecutionTime);

            // 審圖與看圖步共用同一張縮圖；使用者拿到的仍是原圖（可行性 §9.4）
            var forGemini = ImageForGemini.Prepare(png);
            if (forGemini.Error is { } why) logger.LogWarning("render {RenderId}: downscale for Gemini failed, sending the original PNG ({Error})", r.Id, why);
            var selfCheck = SelfCheckAsync(r, requirements, forGemini, evaluation.Token);
            if (r.Request.SafetyOn) await ReviewAsync(r, forGemini, ct);
            // 審圖擋下：評分結果反正會丟掉，不用再等它佔著佇列
            if (r.Status == RenderStatus.Blocked) evaluation.Cancel();
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
            // 沒走到看圖步就結束（生圖失敗、逾時、例外）：文字步不用再跑。RequirementsAsync 不丟例外；等它是為了不留下還在跑的工作
            evaluation.Cancel();
            if (requirements is not null) await requirements;
            // 佇列的預估等待要算整張佔住佇列的時間（含審圖、評分），不只生圖；沒拿到圖的很快就結束，不進平均
            var elapsed = time.GetElapsedTime(started);
            await WrapUpAsync(r, gotImage ? elapsed.TotalSeconds : null, (int)elapsed.TotalMilliseconds);
        }
    }

    /// <summary>服務停止時佇列裡還沒輪到的。</summary>
    public Task AbandonAsync(RenderRecord r)
    {
        r.Fail(RenderMessages.Failed, "shutdown", "服務停止，還沒輪到");
        return WrapUpAsync(r, null, 0);
    }

    private TimeSpan GeminiTimeout => TimeSpan.FromSeconds(options.Value.GeminiTimeoutSeconds);

    private int Ms(long since) => (int)time.GetElapsedTime(since).TotalMilliseconds;

    private async Task ReviewAsync(RenderRecord r, GeminiImage image, CancellationToken ct)
    {
        var t = time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GeminiTimeout);
        try
        {
            var v = await reviewer.ReviewAsync(image, timeout.Token);
            if (v.Nsfw || v.RealPerson) r.Block(RenderMessages.ImageBlocked, "image", v.Reason, Ms(t));
            else r.ReviewPassed(Ms(t));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // 逾時也是判不出來：不給看圖
            r.Block(RenderMessages.ReviewFailed, "image", $"審圖逾時（{options.Value.GeminiTimeoutSeconds} 秒）", Ms(t));
        }
        catch (Exception e)
        {
            // 判不出來或 Gemini 拒收：不給看圖（同 SafetyClassifier 判不出來就不放行）
            r.Block(RenderMessages.ReviewFailed, "image", $"{e.GetType().Name}: {e.Message}", Ms(t));
        }
    }

    /// <summary>文字步（符合度設計 §4.2、§4.3）：鍵相同就重用清單、只對 tag；否則重新整理，成功後寫回 session 的快照。
    /// 不丟例外：失敗、逾時、被取消都回 null，看圖步據此標 unavailable。</summary>
    private async Task<RequirementsOutcome?> RequirementsAsync(RenderRecord r, CancellationToken ct)
    {
        var t = time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GeminiTimeout);
        var q = r.Request;
        try
        {
            if (q.ReusedRequirements is { } fixedList)
            {
                var matched = await extractor.MatchAsync(fixedList, q.Positive, q.Negative, timeout.Token);
                r.RequirementsFinished(Ms(t));
                return new(matched, true);
            }
            var items = await extractor.ExtractAsync(q.Intent.Transcript, q.Positive, q.Negative, timeout.Token);
            r.RequirementsFinished(Ms(t));
            // 快照只存清單本身：tag 每次照當下的 prompt 重新對
            if (r.Owner is { } s) s.Requirements = new RequirementSnapshot(q.Intent.Key, items.Select(i => new Requirement(i.Id, i.Text, i.Source)).ToList());
            return new(items, false);
        }
        catch (Exception e)
        {
            r.RequirementsFinished(Ms(t));
            // 被取消（審圖擋下、生圖失敗）不用記；其餘是 Gemini 出錯、逾時、拒收或重用時漏答
            if (!ct.IsCancellationRequested) logger.LogInformation(e, "requirements unavailable for render {RenderId}", r.Id);
            return null;
        }
    }

    private async Task SelfCheckAsync(RenderRecord r, Task<RequirementsOutcome?> requirements, GeminiImage image, CancellationToken ct)
    {
        // 沒有清單就判不了圖：標 unavailable，圖照給（符合度設計 §11）
        if (await requirements is not { } outcome) { r.SelfCheckFinished(null, null); return; }
        var t = time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GeminiTimeout);   // 從看圖步開始算，不含等文字步的時間
        try
        {
            var verdicts = await checker.CheckAsync(image, outcome.Items, timeout.Token);
            var result = SelfCheckScore.Build(verdicts, r.Request.Intent.Key, outcome.Reused);
            r.SelfCheckFinished(result with { Suggestion = Advise(r, result) }, Ms(t));
        }
        catch (Exception e)
        {
            // 評分只是顯示：失敗或逾時就標 unavailable，圖照給（審查關著時 Gemini 拒收也一樣）。審圖擋下而取消的不用記
            if (r.Status != RenderStatus.Blocked) logger.LogInformation(e, "self-check unavailable for render {RenderId}", r.Id);
            r.SelfCheckFinished(null, Ms(t));
        }
    }

    /// <summary>修正建議（修正建議設計 §4、§7）：用同一段對話其他評分 ok 的圖算「換了幾個 seed」。只是建議：算不出來就沒有，分數與圖照給。</summary>
    private FixSuggestion? Advise(RenderRecord r, SelfCheckResult result)
    {
        try
        {
            var others = (r.Owner?.Renders.Values ?? Enumerable.Empty<RenderRecord>())
                .Where(o => o.Id != r.Id)
                .Select(o => (o, sc: o.SelfCheck))
                .Where(x => x.sc is not null)
                .Select(x => new PastRender(x.o.Request.Seed, x.o.Request.Positive, x.o.Request.Negative, x.sc!))
                .ToList();
            return FixAdvisor.Advise(result, r.Request.Seed, r.Request.Positive, r.Request.Negative, others, options.Value.SeedsBeforeRewrite);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "fix advice failed for render {RenderId}", r.Id);
            return null;
        }
    }

    /// <summary>收尾集中在這裡（預覽設計 §4、§12）：done／failed／blocked 都走它。之後自主閉環要在圖好了時觸發下一輪，就加在這裡。</summary>
    private async Task WrapUpAsync(RenderRecord r, double? busySeconds, int latencyMs)
    {
        queue.Done(r, busySeconds);
        if (!r.Submitted) queue.ReturnDaily(r.QuotaDay);   // 沒送 RunPod 的不算張數
        var eventType = r.Status switch { RenderStatus.Done => "Render_Completed", RenderStatus.Blocked => "Render_Blocked", _ => "Render_Failed" };
        var payload = JsonSerializer.Serialize(new
        {
            renderId = r.Id, safety = r.Request.SafetyOn ? "on" : "off", jobId = r.RunPodJobId, seed = r.Request.Seed, reroll = r.Request.Reroll,
            queueMs = r.QueueMs, delayMs = r.DelayMs, executionMs = r.ExecutionMs, reviewMs = r.ReviewMs,
            requirementsMs = r.RequirementsMs, selfCheckMs = r.SelfCheckMs,
            selfCheck = SelfCheckScore.Audit(RenderWire.SelfCheck(r.SelfCheckState), r.SelfCheck),
            suggestion = r.SelfCheck?.Suggestion?.Kind, suggestionItems = r.SelfCheck?.Suggestion?.ItemIds.Count,
            stage = r.BlockStage, error = r.FailureKind, detail = r.Detail,
        }, RenderAudit.Json);
        await RenderAudit.TryWriteAsync(audit, new AuditEntry(r.Request.SessionId, r.Request.TurnIndex, eventType, PayloadJson: payload, LatencyMs: latencyMs), logger);
    }
}
