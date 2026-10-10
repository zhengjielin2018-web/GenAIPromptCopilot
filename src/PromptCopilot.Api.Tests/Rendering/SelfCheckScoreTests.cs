using System.Text.Json;
using PromptCopilot.Api.Rendering;

namespace PromptCopilot.Api.Tests.Rendering;

public class SelfCheckScoreTests
{
    private static readonly string[] None = Array.Empty<string>();

    private static RequirementVerdict V(string id, string text, string verdict, string source = RequirementSources.User, string[]? tags = null, string[]? neg = null)
    {
        var t = tags ?? new[] { "x" };
        var n = neg ?? None;
        return new RequirementVerdict(id, text, source, t, n, verdict, SelfCheckScore.IssueOf(verdict, t, n), "理由");
    }

    [Theory]
    [InlineData("met", true, "none")]
    [InlineData("met", false, "none")]          // prompt 沒寫、剛好畫出來：不算問題，前端從空的 tag 另外標
    [InlineData("unclear", true, "unclear")]
    [InlineData("unclear", false, "unclear")]
    [InlineData("unmet", false, "prompt_missing")]
    [InlineData("unmet", true, "not_rendered")]
    public void Issue_follows_the_table(string verdict, bool hasTags, string issue) =>
        Assert.Equal(issue, SelfCheckScore.IssueOf(verdict, hasTags ? new[] { "silver hair" } : None, None));

    [Fact]
    public void A_negative_tag_counts_as_written()
    {
        Assert.Equal("not_rendered", SelfCheckScore.IssueOf("unmet", None, new[] { "hat" }));
    }

    [Fact]
    public void Score_counts_only_user_items_and_leaves_unclear_out()
    {
        var items = new[]
        {
            V("r1", "銀色雙馬尾", "unmet"), V("r2", "傍晚的海邊", "met"), V("r3", "不要帽子", "met"),
            V("r4", "某畫師", "unclear"),
            V("r5", "白色洋裝", "unmet", RequirementSources.Delegated),   // 委託不計分
        };
        Assert.Equal(67, SelfCheckScore.Score(items));   // 2 ÷ 3 = 66.7
    }

    [Fact]
    public void Halves_round_away_from_zero()
    {
        var items = Enumerable.Range(1, 8).Select(i => V($"r{i}", $"要求{i}", i == 1 ? "met" : "unmet")).ToArray();
        Assert.Equal(13, SelfCheckScore.Score(items));   // 12.5
    }

    [Fact]
    public void No_judgeable_user_item_has_no_score()
    {
        Assert.Null(SelfCheckScore.Score(Array.Empty<RequirementVerdict>()));
        Assert.Null(SelfCheckScore.Score(new[] { V("r1", "某畫師", "unclear"), V("r2", "白色洋裝", "met", RequirementSources.Delegated) }));
    }

    [Fact]
    public void Summary_has_three_shapes()
    {
        Assert.Equal("沒有可以判斷的要求", SelfCheckScore.Summary(new[] { V("r1", "白色洋裝", "met", RequirementSources.Delegated) }));
        Assert.Equal("使用者要求 2 條，都從圖上看不出來", SelfCheckScore.Summary(new[] { V("r1", "某畫師", "unclear"), V("r2", "85mm", "unclear") }));
        Assert.Equal("使用者要求 5 條，2 條符合；不符合：銀色雙馬尾、抱著貓；1 條看不出來", SelfCheckScore.Summary(new[]
        {
            V("r1", "銀色雙馬尾", "unmet"), V("r2", "傍晚的海邊", "met"), V("r3", "抱著貓", "unmet", tags: None),
            V("r4", "不要帽子", "met"), V("r5", "某畫師", "unclear"), V("r6", "白色洋裝", "unmet", RequirementSources.Delegated),
        }));
        Assert.Equal("使用者要求 1 條，1 條符合", SelfCheckScore.Summary(new[] { V("r1", "傍晚的海邊", "met") }));
    }

    [Fact]
    public void Build_keeps_the_items_and_shortens_the_key()
    {
        var items = new[] { V("r1", "傍晚的海邊", "met") };
        var r = SelfCheckScore.Build(items, "0123456789abcdef", reused: true);
        Assert.Equal((100, "使用者要求 1 條，1 條符合", "01234567", true), (r.Score, r.Summary, r.ListKey, r.ListReused));
        Assert.Same(items, r.Items);
    }

    [Fact]
    public void Audit_counts_without_texts()
    {
        var r = SelfCheckScore.Build(new[]
        {
            V("r1", "銀色雙馬尾", "unmet"), V("r2", "抱著貓", "unmet", tags: None), V("r3", "傍晚的海邊", "met"), V("r4", "某畫師", "unclear"),
            V("r5", "白色洋裝", "unmet", RequirementSources.Delegated), V("r6", "微笑", "met", RequirementSources.Delegated),
        }, "abcdef0123456789", reused: false);
        var json = JsonSerializer.Serialize(SelfCheckScore.Audit("ok", r), RenderAudit.Json);
        Assert.Equal("""{"status":"ok","score":33,"user":4,"met":1,"unmet":2,"unclear":1,"promptMissing":1,"notRendered":1,"delegated":2,"delegatedUnmet":1,"listReused":false,"listKey":"abcdef01"}""", json);
        Assert.DoesNotContain("雙馬尾", json);
    }

    [Fact]
    public void Audit_without_a_result_has_only_the_status()
    {
        var json = JsonSerializer.Serialize(SelfCheckScore.Audit("unavailable", null), RenderAudit.Json);
        Assert.Equal("""{"status":"unavailable","score":null,"user":null,"met":null,"unmet":null,"unclear":null,"promptMissing":null,"notRendered":null,"delegated":null,"delegatedUnmet":null,"listReused":null,"listKey":null}""", json);
    }
}
