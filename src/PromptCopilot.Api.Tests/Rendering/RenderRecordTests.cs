using System.Text.Json;
using PromptCopilot.Api.Rendering;

namespace PromptCopilot.Api.Tests.Rendering;

public class RenderRecordTests
{
    private static readonly byte[] Png = { 1, 2, 3 };
    private static readonly SelfCheckResult Hair = SelfCheckScore.Build(new[]
    {
        new RequirementVerdict("r1", "銀色長髮", RequirementSources.User, new[] { "silver hair" }, Array.Empty<string>(), "met", "none", "銀色長髮"),
    }, "k1", reused: false);

    public static RenderRequest Request(string session = "s1", bool safetyOn = true, bool reviewed = true) =>
        new(session, 3, "1girl", "lowres", 42, new IntentInput("對話：\n使用者：銀髮", "k1"), safetyOn, PromptReviewed: reviewed);

    public static RenderRecord New(bool safetyOn = true, string id = "r1") =>
        new(id, Request(safetyOn: safetyOn), new DateOnly(2026, 10, 9));

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
        r.SelfCheckFinished(Hair, 2100);
        Assert.Equal((RenderStatus.Done, SelfCheckStatus.Ok), (r.Status, r.SelfCheckState));
        Assert.True(r.IsFinished);
    }

    [Fact]
    public void Self_check_finishing_first_goes_straight_to_done_when_review_passes()
    {
        var r = New();
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(Hair, 900);
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
        r.SelfCheckFinished(Hair, 900);
        r.ReviewPassed(1);
        Assert.Equal((RenderStatus.Blocked, "image", (int?)1500), (r.Status, r.BlockStage, r.ReviewMs));
        Assert.Null(r.Image);
        Assert.Null(r.SelfCheck);
        Assert.Equal(RenderMessages.ImageBlocked, r.Message);
        Assert.True(r.CountsTowardLimit);   // 已送 RunPod，錢花了
    }

    /// <summary>自評比審圖先好、審圖才擋下：自評狀態不能還是 ok（看起來像「自評好了、沒有項目」）。</summary>
    [Fact]
    public void Blocking_after_the_self_check_finished_marks_it_unavailable()
    {
        var r = New();
        r.MarkGenerating(0); r.MarkSubmitted("j1"); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(Hair, 900);
        r.Block(RenderMessages.ImageBlocked, "image", "裸露", reviewMs: 1500);
        Assert.Equal((RenderStatus.Blocked, SelfCheckStatus.Unavailable), (r.Status, r.SelfCheckState));
        Assert.Equal("unavailable", r.View(null).SelfCheck.Status);
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
        r.SelfCheckFinished(SelfCheckScore.Build(Array.Empty<RequirementVerdict>(), "k1", false), 0);
        Assert.Equal(SelfCheckStatus.Ok, r.SelfCheckState);
    }

    [Fact]
    public void View_uses_the_wire_names()
    {
        var r = New();
        r.MarkGenerating(10); r.ImageArrived(Png, 100, 4600); r.ReviewPassed(1800);
        var json = JsonSerializer.Serialize(r.View(position: null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"status\":\"self_checking\"", json);
        Assert.Contains("\"selfCheck\":{\"status\":\"pending\",\"score\":null,\"summary\":null,\"items\":[],\"suggestion\":null}", json);
        Assert.Contains("\"seed\":42", json);
        Assert.Contains("\"requirementsMs\":null", json);
        Assert.Contains("\"safety\":\"on\"", json);
        Assert.Contains("\"renderId\":\"r1\"", json);
        Assert.Contains("\"turnIndex\":3", json);
        Assert.Equal(new[] { "queued", "generating", "reviewing", "self_checking", "done", "failed", "blocked" },
            Enum.GetValues<RenderStatus>().Select(RenderWire.Status));
    }

    [Fact]
    public void Done_view_has_score_summary_and_items_in_wire_names()
    {
        var r = New(safetyOn: false);
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.RequirementsFinished(800);
        r.SelfCheckFinished(Hair, 2100);
        var json = JsonSerializer.Serialize(r.View(position: null), RenderAudit.Json);   // Web 命名＋中文不跳脫，才比對得到中文
        Assert.Contains("\"score\":100", json);
        Assert.Contains("\"summary\":\"使用者要求 1 條，1 條符合\"", json);
        Assert.Contains("\"items\":[{\"id\":\"r1\",\"text\":\"銀色長髮\",\"source\":\"user\",\"tags\":[\"silver hair\"],\"negativeTags\":[],\"verdict\":\"met\",\"issue\":\"none\",\"reason\":\"銀色長髮\"}]", json);
        Assert.Contains("\"requirementsMs\":800", json);
        Assert.Contains("\"selfCheckMs\":2100", json);
    }

    [Fact]
    public void Score_and_items_wait_for_done()
    {
        var r = New();
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(Hair, 900);                     // 審圖還沒過：reviewing
        var v = r.View(null).SelfCheck;
        Assert.Equal(("ok", (int?)null, (string?)null, 0), (v.Status, v.Score, v.Summary, v.Items.Count));
    }

    [Fact]
    public void Suggestion_is_shown_only_when_done()
    {
        var advised = Hair with { Suggestion = new FixSuggestion(SuggestionKinds.None, null, null, Array.Empty<string>(), new[] { "註記" }) };
        var r = New();
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(advised, 900);
        Assert.Null(r.View(null).SelfCheck.Suggestion);                 // 還在 reviewing
        r.ReviewPassed(1);
        Assert.Equal(new[] { "註記" }, r.View(null).SelfCheck.Suggestion!.Notes);
    }
}
