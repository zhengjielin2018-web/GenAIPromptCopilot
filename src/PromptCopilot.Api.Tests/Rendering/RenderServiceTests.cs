using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class RenderServiceTests
{
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 9, 4, 0, 0, TimeSpan.Zero));
    private readonly RecordingAudit _audit = new();
    private readonly FakeChatCompletion _chat = new();
    private RenderQueue _queue = null!;

    private RenderService Service(RenderOptions? o = null)
    {
        _queue = new RenderQueue(_clock);
        return new RenderService(_queue, new SafetyClassifier(_chat, Options.Create(new LlmOptions())), _audit,
            Options.Create(o ?? new RenderOptions()), NullLogger<RenderService>.Instance);
    }

    private static RenderRequest Req(string session = "s1", bool safetyOn = true, bool reviewed = true) =>
        new(session, 3, "1girl", "lowres", 42, Array.Empty<SelfCheckItem>(), safetyOn, reviewed);

    private static RenderRecord Accepted(RenderAdmission a) => Assert.IsType<RenderAdmission.Accepted>(a).Record;
    private static RenderAdmission.Rejected Rejected(RenderAdmission a) => Assert.IsType<RenderAdmission.Rejected>(a);

    [Fact]
    public async Task Accepts_and_enqueues_without_a_lock_or_last_final()
    {
        var svc = Service();
        var s = new Session("s1");   // 沒定稿、沒拿鎖：那些是端點的事（預覽設計 §5.1、§12）
        var r = Accepted(await svc.RequestAsync(s, Req(), default));
        Assert.Same(r, s.Renders[r.Id]);
        Assert.Equal((RenderStatus.Queued, (int?)1, 1), (r.Status, _queue.PositionOf(r), _queue.DailyCount));
        Assert.Empty(_chat.Calls);
    }

    [Fact]
    public async Task Rejects_while_the_previous_one_is_unfinished()
    {
        var svc = Service();
        var s = new Session("s1");
        Accepted(await svc.RequestAsync(s, Req(), default));
        var no = Rejected(await svc.RequestAsync(s, Req(), default));
        Assert.Equal((409, "上一張還在生"), (no.StatusCode, no.Error));
    }

    [Fact]
    public async Task Two_requests_at_once_only_one_gets_through()
    {
        var svc = Service();
        var s = new Session("s1");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => svc.RequestAsync(s, Req(), default))));
        Assert.Single(results.OfType<RenderAdmission.Accepted>());
        Assert.Single(s.Renders);
    }

    [Fact]
    public async Task Session_limit_counts_only_what_reached_runpod()
    {
        var svc = Service(new RenderOptions { PerSessionLimit = 1 });
        var s = new Session("s1");
        var first = Accepted(await svc.RequestAsync(s, Req(), default));
        first.Block(RenderMessages.PromptBlocked, "prompt", null);           // 沒送 RunPod：不算
        var second = Accepted(await svc.RequestAsync(s, Req(), default));
        second.MarkSubmitted("j"); second.Fail(RenderMessages.Failed, "runpod", null);   // 送了：算
        var no = Rejected(await svc.RequestAsync(s, Req(), default));
        Assert.Equal((429, "這段對話的預覽張數已達上限（1 張）"), (no.StatusCode, no.Error));
        Assert.Contains(_audit.Entries, e => e.EventType == "Render_Rejected" && e.PayloadJson!.Contains("session_limit"));
    }

    [Fact]
    public async Task Daily_limit_rejects_and_is_audited()
    {
        var svc = Service(new RenderOptions { DailyLimit = 1 });
        Accepted(await svc.RequestAsync(new Session("a"), Req("a"), default));
        var no = Rejected(await svc.RequestAsync(new Session("b"), Req("b"), default));
        Assert.Equal((429, "今天的預覽張數已達上限，明天再試"), (no.StatusCode, no.Error));
        Assert.Contains(_audit.Entries, e => e.EventType == "Render_Rejected" && e.PayloadJson!.Contains("daily_limit"));
    }

    [Fact]
    public async Task Long_estimated_wait_is_503_and_takes_no_quota()
    {
        var svc = Service(new RenderOptions { MaxEstimatedWaitSeconds = 5, DefaultImageSeconds = 10 });
        Accepted(await svc.RequestAsync(new Session("a"), Req("a"), default));
        var no = Rejected(await svc.RequestAsync(new Session("b"), Req("b"), default));
        Assert.Equal((503, "目前人多，稍後再試"), (no.StatusCode, no.Error));
        Assert.Equal(1, _queue.DailyCount);
        Assert.Contains(_audit.Entries, e => e.EventType == "Render_Rejected" && e.PayloadJson!.Contains("busy"));
    }

    [Fact]
    public async Task Unreviewed_final_is_reviewed_before_spending_and_blocked_when_it_fails()
    {
        _chat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"裸露"}"""));
        var svc = Service();
        var s = new Session("s1");
        var r = Accepted(await svc.RequestAsync(s, Req(reviewed: false), default));
        Assert.Equal((RenderStatus.Blocked, "prompt", RenderMessages.PromptBlocked), (r.Status, r.BlockStage, r.Message));
        Assert.Null(_queue.PositionOf(r));
        Assert.Equal(0, _queue.DailyCount);   // 額度退回
        Assert.Contains(_audit.Entries, e => e.EventType == "Render_Blocked" && e.PayloadJson!.Contains("\"stage\":\"prompt\""));
        Assert.Contains("1girl", _chat.Calls[0][0].Content);
    }

    [Fact]
    public async Task Classifier_error_blocks_the_unreviewed_final()
    {
        _chat.Throw(new HttpRequestException("gemini down"));
        var r = Accepted(await Service().RequestAsync(new Session("s1"), Req(reviewed: false), default));
        Assert.Equal(RenderStatus.Blocked, r.Status);
    }

    [Theory]
    [InlineData(true, true)]     // 定稿審過：不再審
    [InlineData(false, false)]   // 審查關著：完全不跑分類器
    [InlineData(false, true)]
    public async Task No_prompt_review_when_reviewed_or_safety_off(bool safetyOn, bool reviewed)
    {
        var r = Accepted(await Service().RequestAsync(new Session("s1"), Req(safetyOn: safetyOn, reviewed: reviewed), default));
        Assert.Equal(RenderStatus.Queued, r.Status);
        Assert.Empty(_chat.Calls);
    }

    [Fact]
    public async Task Cancelled_pre_review_does_not_leave_the_session_stuck()
    {
        using var cts = new CancellationTokenSource();
        _chat.ThenAsync(async (_, _, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Array.Empty<Microsoft.SemanticKernel.ChatMessageContent>(); });
        var svc = Service();
        var s = new Session("s1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RequestAsync(s, Req(reviewed: false), cts.Token));
        var r = Assert.Single(s.Renders.Values);
        Assert.Equal(RenderStatus.Failed, r.Status);
        Assert.Equal(0, _queue.DailyCount);
        Accepted(await svc.RequestAsync(s, Req(), default));   // 下一張收得進來
    }
}
