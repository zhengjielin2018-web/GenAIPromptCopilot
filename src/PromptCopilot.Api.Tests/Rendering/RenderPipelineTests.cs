using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class RenderPipelineTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47 };

    public sealed class FakeRunPod : IRunPodClient
    {
        public Func<RunPodJob> Result { get; set; } = () => Completed();
        public Exception? SubmitError { get; set; }
        public TimeSpan WaitFor { get; set; } = TimeSpan.Zero;
        public static RunPodJob Completed() => new("j1", "COMPLETED", 120, 4600,
            JsonSerializer.SerializeToElement(new { images = new[] { new { type = "base64", data = Convert.ToBase64String(Png) } } }), null);

        public Task<string> SubmitAsync(JsonObject workflow, CancellationToken ct) =>
            SubmitError is { } e ? Task.FromException<string>(e) : Task.FromResult("j1");

        public async Task<RunPodJob> WaitAsync(string jobId, TimeSpan timeout, CancellationToken ct)
        {
            if (WaitFor > TimeSpan.Zero) await Task.Delay(WaitFor, ct);
            return Result();
        }
    }

    public sealed class GatedReviewer : IImageReviewer
    {
        public TaskCompletionSource<ImageVerdict> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task<ImageVerdict> ReviewAsync(byte[] png, CancellationToken ct) { Interlocked.Increment(ref Calls); return Gate.Task; }
    }

    public sealed class GatedChecker : ISelfChecker
    {
        public TaskCompletionSource<IReadOnlyList<SelfCheckVerdict>> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<SelfCheckVerdict>> CheckAsync(byte[] png, IReadOnlyList<SelfCheckItem> items, CancellationToken ct) => Gate.Task;
    }

    private static readonly ImageVerdict Clean = new(false, false, null, "風景");
    private static readonly SelfCheckVerdict Hair = new("appearance.hair", "髮型", "silver hair", "present", "銀色長髮");

    private readonly FakeRunPod _runpod = new();
    private readonly GatedReviewer _reviewer = new();
    private readonly GatedChecker _checker = new();
    private readonly RecordingAudit _audit = new();
    private readonly RenderQueue _queue = new(TimeProvider.System);

    private RenderPipeline Pipeline() => new(_runpod, RenderWorkflow.Load(Path.Combine(AppContext.BaseDirectory, "Rendering", RenderWorkflow.FileName)),
        _reviewer, _checker, _queue, _audit, Options.Create(new RenderOptions()), NullLogger<RenderPipeline>.Instance, TimeProvider.System);

    /// <summary>從佇列拿出來，跟背景服務一樣。</summary>
    private async Task<RenderRecord> Dequeued(bool safetyOn = true)
    {
        var day = _queue.TakeDaily();
        var r = new RenderRecord("r1", new RenderRequest("s1", 3, "1girl", "lowres", 42, new[] { new SelfCheckItem("appearance.hair", "髮型", "silver hair") }, safetyOn, true), day);
        _queue.Enqueue(r);
        return await _queue.DequeueAsync(default);
    }

    private static RenderRecord Plain(string id, string session, DateOnly day) =>
        new(id, new RenderRequest(session, 1, "x", "", 1, Array.Empty<SelfCheckItem>(), true, true), day);

    private static async Task Eventually(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task Review_on_runs_generating_reviewing_self_checking_done()
    {
        var r = await Dequeued();
        var run = Pipeline().ProcessAsync(r, default);
        await Eventually(() => r.Status == RenderStatus.Reviewing);
        Assert.Null(r.Image);
        _reviewer.Gate.SetResult(Clean);
        await Eventually(() => r.Status == RenderStatus.SelfChecking);
        Assert.Equal(Png, r.Image);
        _checker.Gate.SetResult(new[] { Hair });
        await run;
        Assert.Equal((RenderStatus.Done, true, (int?)120, (int?)4600), (r.Status, r.Submitted, r.DelayMs, r.ExecutionMs));
        var e = Assert.Single(_audit.Entries);
        Assert.Equal(("Render_Completed", "s1", (int?)3), (e.EventType, e.SessionId, e.TurnIndex));
        Assert.Contains("\"present\":1", e.PayloadJson);
        Assert.Contains("\"jobId\":\"j1\"", e.PayloadJson);
    }

    [Fact]
    public async Task Self_check_finishing_first_waits_for_review()
    {
        var r = await Dequeued();
        var run = Pipeline().ProcessAsync(r, default);
        await Eventually(() => r.Status == RenderStatus.Reviewing);
        _checker.Gate.SetResult(new[] { Hair });
        await Task.Delay(50);
        Assert.Equal(RenderStatus.Reviewing, r.Status);
        _reviewer.Gate.SetResult(Clean);
        await run;
        Assert.Equal(RenderStatus.Done, r.Status);
    }

    [Fact]
    public async Task Review_off_never_calls_the_reviewer()
    {
        var r = await Dequeued(safetyOn: false);
        var run = Pipeline().ProcessAsync(r, default);
        await Eventually(() => r.Status == RenderStatus.SelfChecking);
        _checker.Gate.SetResult(new[] { Hair });
        await run;
        Assert.Equal((RenderStatus.Done, 0), (r.Status, _reviewer.Calls));
    }

    [Fact]
    public async Task Flagged_image_is_blocked_and_dropped()
    {
        var r = await Dequeued();
        var run = Pipeline().ProcessAsync(r, default);
        _reviewer.Gate.SetResult(new ImageVerdict(true, false, null, "裸露"));
        _checker.Gate.SetResult(new[] { Hair });
        await run;
        Assert.Equal((RenderStatus.Blocked, RenderMessages.ImageBlocked, "image"), (r.Status, r.Message, r.BlockStage));
        Assert.Null(r.Image);
        var e = Assert.Single(_audit.Entries);
        Assert.Equal("Render_Blocked", e.EventType);
        Assert.Contains("裸露", e.PayloadJson);
    }

    [Fact]
    public async Task Review_that_cannot_decide_or_is_refused_does_not_show_the_image()
    {
        var r = await Dequeued();
        var run = Pipeline().ProcessAsync(r, default);
        _reviewer.Gate.SetException(new UpstreamBlockedException("SAFETY"));
        _checker.Gate.SetResult(new[] { Hair });
        await run;
        Assert.Equal((RenderStatus.Blocked, RenderMessages.ReviewFailed), (r.Status, r.Message));
        Assert.Null(r.Image);
    }

    [Fact]
    public async Task Self_check_failure_still_shows_the_image()
    {
        var r = await Dequeued();
        var run = Pipeline().ProcessAsync(r, default);
        _reviewer.Gate.SetResult(Clean);
        _checker.Gate.SetException(new InvalidOperationException("bad json"));
        await run;
        Assert.Equal((RenderStatus.Done, SelfCheckStatus.Unavailable), (r.Status, r.SelfCheckState));
        Assert.Equal(Png, r.Image);
    }

    [Fact]
    public async Task Runpod_failure_counts_and_is_audited()
    {
        _runpod.Result = () => new RunPodJob("j1", "FAILED", 100, 200, null, "ckpt not found");
        var r = await Dequeued();
        await Pipeline().ProcessAsync(r, default);
        Assert.Equal((RenderStatus.Failed, RenderMessages.Failed, true), (r.Status, r.Message, r.CountsTowardLimit));
        Assert.Equal(1, _queue.DailyCount);
        Assert.Contains("ckpt not found", Assert.Single(_audit.Entries).PayloadJson);
    }

    [Fact]
    public async Task Timeout_has_its_own_message()
    {
        _runpod.Result = () => throw new TimeoutException("too slow");
        var r = await Dequeued();
        await Pipeline().ProcessAsync(r, default);
        Assert.Equal((RenderStatus.Failed, RenderMessages.Timeout), (r.Status, r.Message));
    }

    [Fact]
    public async Task Submit_failure_does_not_count_and_returns_the_quota()
    {
        _runpod.SubmitError = new HttpRequestException("402 balance");
        var r = await Dequeued();
        await Pipeline().ProcessAsync(r, default);
        Assert.Equal((RenderStatus.Failed, false), (r.Status, r.CountsTowardLimit));
        Assert.Equal(0, _queue.DailyCount);
        Assert.Equal(0, _queue.EstimatedWaitSeconds(10));   // 不再是處理中
    }

    [Fact]
    public async Task One_bad_render_does_not_stop_the_worker()
    {
        var calls = 0;
        _runpod.Result = () => ++calls == 1 ? throw new InvalidOperationException("boom") : FakeRunPod.Completed();
        _reviewer.Gate.SetResult(Clean);
        _checker.Gate.SetResult(new[] { Hair });
        var worker = new RenderWorker(_queue, new Lazy<RenderPipeline>(Pipeline), _audit, NullLogger<RenderWorker>.Instance);
        await worker.StartAsync(default);
        var a = Plain("a", "s1", _queue.TakeDaily());
        var b = Plain("b", "s2", _queue.TakeDaily());
        _queue.Enqueue(a); _queue.Enqueue(b);
        await Eventually(() => b.Status == RenderStatus.Done);
        Assert.Equal(RenderStatus.Failed, a.Status);
        await worker.StopAsync(default);
    }

    /// <summary>pipeline 建不起來（例如 workflow 範本壞掉）：那張要收掉（failed、退額度、audit），不能停在 queued 把 session 卡死；
    /// 下一張再試一次建 pipeline，不能被 Lazy 快取的例外永遠擋住。</summary>
    [Fact]
    public async Task A_pipeline_that_cannot_be_built_fails_that_render_and_the_next_one_retries()
    {
        _reviewer.Gate.SetResult(Clean);
        _checker.Gate.SetResult(new[] { Hair });
        var attempts = 0;
        var lazy = new Lazy<RenderPipeline>(() => ++attempts == 1 ? throw new InvalidOperationException("workflow 的節點 6 不對") : Pipeline(),
            LazyThreadSafetyMode.PublicationOnly);
        var worker = new RenderWorker(_queue, lazy, _audit, NullLogger<RenderWorker>.Instance);
        await worker.StartAsync(default);
        var a = Plain("a", "s1", _queue.TakeDaily());
        _queue.Enqueue(a);
        await Eventually(() => a.Status == RenderStatus.Failed);
        Assert.Equal(0, _queue.DailyCount);                        // 沒送 RunPod，額度退回
        Assert.Equal(0, _queue.EstimatedWaitSeconds(10));          // 不再是處理中
        Assert.Contains(_audit.Entries, e => e.EventType == "Render_Failed" && e.PayloadJson!.Contains("crash"));
        var b = Plain("b", "s2", _queue.TakeDaily());
        _queue.Enqueue(b);
        await Eventually(() => b.Status == RenderStatus.Done);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task Stopping_the_worker_fails_the_current_and_the_waiting_ones()
    {
        _runpod.WaitFor = TimeSpan.FromMinutes(5);
        var worker = new RenderWorker(_queue, new Lazy<RenderPipeline>(Pipeline), _audit, NullLogger<RenderWorker>.Instance);
        await worker.StartAsync(default);
        var a = Plain("a", "s1", _queue.TakeDaily());
        var b = Plain("b", "s2", _queue.TakeDaily());
        _queue.Enqueue(a); _queue.Enqueue(b);
        await Eventually(() => a.Status == RenderStatus.Generating);
        await worker.StopAsync(default);
        Assert.Equal((RenderStatus.Failed, RenderStatus.Failed), (a.Status, b.Status));
        Assert.Equal(2, _audit.Entries.Count(e => e.EventType == "Render_Failed"));
    }
}
