using System.Text.Json;
using PromptCopilot.Api.Rendering;

namespace PromptCopilot.Api.Tests.Rendering;

public class RenderRecordTests
{
    private static readonly byte[] Png = { 1, 2, 3 };
    private static readonly SelfCheckVerdict Hair = new("appearance.hair", "髮型", "silver hair", "present", "銀色長髮");

    public static RenderRecord New(bool safetyOn = true, string id = "r1") =>
        new(id, new RenderRequest("s1", 3, "1girl", "lowres", 42, new[] { new SelfCheckItem("appearance.hair", "髮型", "silver hair") }, safetyOn, PromptReviewed: true),
            new DateOnly(2026, 10, 9));

    [Fact]
    public void Starts_queued_then_generating()
    {
        var r = New();
        Assert.Equal(RenderStatus.Queued, r.Status);
        r.MarkGenerating(120);
        Assert.Equal((RenderStatus.Generating, (int?)120), (r.Status, r.QueueMs));
        Assert.False(r.IsFinished);
        Assert.True(r.CountsTowardLimit);   // 收件就保留額度
    }

    [Fact]
    public void With_review_on_the_image_is_hidden_until_review_passes()
    {
        var r = New();
        r.MarkGenerating(0); r.MarkSubmitted("j1");
        r.ImageArrived(Png, 100, 4600);
        Assert.Equal(RenderStatus.Reviewing, r.Status);
        Assert.Null(r.Image);
        r.ReviewPassed(1800);
        Assert.Equal(RenderStatus.SelfChecking, r.Status);
        Assert.Equal(Png, r.Image);
        r.SelfCheckFinished(new[] { Hair }, 2100);
        Assert.Equal((RenderStatus.Done, SelfCheckStatus.Ok), (r.Status, r.SelfCheckState));
        Assert.True(r.IsFinished);
    }

    [Fact]
    public void Self_check_finishing_first_goes_straight_to_done_when_review_passes()
    {
        var r = New();
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(new[] { Hair }, 900);
        Assert.Equal(RenderStatus.Reviewing, r.Status);
        Assert.Null(r.Image);
        r.ReviewPassed(1800);
        Assert.Equal(RenderStatus.Done, r.Status);
    }

    [Fact]
    public void With_review_off_the_image_is_available_right_after_it_arrives()
    {
        var r = New(safetyOn: false);
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        Assert.Equal(RenderStatus.SelfChecking, r.Status);
        Assert.Equal(Png, r.Image);
    }

    [Fact]
    public void Blocking_drops_the_image_and_the_self_check_and_later_results_are_ignored()
    {
        var r = New();
        r.MarkGenerating(0); r.MarkSubmitted("j1"); r.ImageArrived(Png, 1, 1);
        r.Block(RenderMessages.ImageBlocked, "image", "裸露", reviewMs: 1500);
        r.SelfCheckFinished(new[] { Hair }, 900);
        r.ReviewPassed(1);
        Assert.Equal((RenderStatus.Blocked, "image", (int?)1500), (r.Status, r.BlockStage, r.ReviewMs));
        Assert.Null(r.Image);
        Assert.Empty(r.SelfCheck);
        Assert.Equal(RenderMessages.ImageBlocked, r.Message);
        Assert.True(r.CountsTowardLimit);   // 已送 RunPod，錢花了
    }

    [Fact]
    public void Finishing_before_submission_does_not_count_toward_limits()
    {
        var r = New();
        r.Block(RenderMessages.PromptBlocked, "prompt", "nsfw");
        Assert.False(r.CountsTowardLimit);
        var f = New(id: "r2");
        f.MarkGenerating(0); f.Fail(RenderMessages.Failed, "submit", "500");
        Assert.False(f.CountsTowardLimit);
    }

    [Fact]
    public void Self_check_failure_is_unavailable_and_still_done()
    {
        var r = New(safetyOn: false);
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(null, 300);
        Assert.Equal((RenderStatus.Done, SelfCheckStatus.Unavailable), (r.Status, r.SelfCheckState));
        Assert.Equal(Png, r.Image);
    }

    [Fact]
    public void Empty_self_check_is_ok_not_unavailable()
    {
        var r = New(safetyOn: false);
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(Array.Empty<SelfCheckVerdict>(), 0);
        Assert.Equal(SelfCheckStatus.Ok, r.SelfCheckState);
    }

    [Fact]
    public void View_uses_the_wire_names()
    {
        var r = New();
        r.MarkGenerating(10); r.ImageArrived(Png, 100, 4600); r.ReviewPassed(1800);
        var json = JsonSerializer.Serialize(r.View(position: null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"status\":\"self_checking\"", json);
        Assert.Contains("\"selfCheck\":{\"status\":\"pending\",\"items\":[]}", json);
        Assert.Contains("\"safety\":\"on\"", json);
        Assert.Contains("\"renderId\":\"r1\"", json);
        Assert.Contains("\"turnIndex\":3", json);
        Assert.Equal(new[] { "queued", "generating", "reviewing", "self_checking", "done", "failed", "blocked" },
            Enum.GetValues<RenderStatus>().Select(RenderWire.Status));
    }
}
