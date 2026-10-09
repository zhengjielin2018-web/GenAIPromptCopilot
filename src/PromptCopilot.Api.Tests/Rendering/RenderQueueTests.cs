using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class RenderQueueTests
{
    // 2026-10-09 23:59 台灣時間
    private static ManualTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 9, 15, 59, 0, TimeSpan.Zero));

    [Fact]
    public async Task Position_counts_from_one_and_the_dequeued_one_is_current()
    {
        var q = new RenderQueue(Clock());
        var a = RenderRecordTests.New(id: "a"); var b = RenderRecordTests.New(id: "b");
        q.Enqueue(a); q.Enqueue(b);
        Assert.Equal(((int?)1, (int?)2), (q.PositionOf(a), q.PositionOf(b)));
        Assert.Same(a, await q.DequeueAsync(default));
        Assert.Equal(((int?)null, (int?)1), (q.PositionOf(a), q.PositionOf(b)));
        Assert.Equal(2 * 10, q.EstimatedWaitSeconds(10));   // b 在等＋a 處理中
    }

    [Fact]
    public async Task Estimate_uses_the_average_of_the_last_ten()
    {
        var q = new RenderQueue(Clock());
        for (var i = 0; i < 12; i++)
        {
            var r = RenderRecordTests.New(id: $"r{i}");
            q.Enqueue(r); await q.DequeueAsync(default);
            q.Done(r, i < 2 ? 100 : 6);   // 最早兩張很慢，已經擠出最近 10 張
        }
        Assert.Equal(0, q.EstimatedWaitSeconds(10));
        q.Enqueue(RenderRecordTests.New(id: "x"));
        Assert.Equal(6, q.EstimatedWaitSeconds(10));
    }

    [Fact]
    public void Daily_count_resets_at_taipei_midnight_and_returns_only_to_the_same_day()
    {
        var clock = Clock();
        var q = new RenderQueue(clock);
        var day = q.TakeDaily();
        Assert.Equal((new DateOnly(2026, 10, 9), 1), (day, q.DailyCount));
        clock.Advance(TimeSpan.FromMinutes(2));   // 10/10 00:01 台灣時間
        Assert.Equal(0, q.DailyCount);
        q.TakeDaily();
        q.ReturnDaily(day);   // 昨天的保留不能扣今天的
        Assert.Equal(1, q.DailyCount);
    }

    [Fact]
    public void Drain_takes_everything_still_waiting()
    {
        var q = new RenderQueue(Clock());
        q.Enqueue(RenderRecordTests.New(id: "a")); q.Enqueue(RenderRecordTests.New(id: "b"));
        Assert.Equal(new[] { "a", "b" }, q.DrainWaiting().Select(r => r.Id));
        Assert.Equal(0, q.EstimatedWaitSeconds(10));
    }
}
