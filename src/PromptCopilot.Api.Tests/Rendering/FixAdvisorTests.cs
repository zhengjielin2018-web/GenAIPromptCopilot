using PromptCopilot.Api.Rendering;

namespace PromptCopilot.Api.Tests.Rendering;

public class FixAdvisorTests
{
    private static readonly string[] None = Array.Empty<string>();
    private const string Pos = "1girl, silver hair, twintails, beach";
    private const string Neg = "lowres";

    private static RequirementVerdict V(string id, string text, string verdict, string[]? tags = null, string[]? neg = null, string source = RequirementSources.User)
    {
        var t = tags ?? new[] { "x" };
        var n = neg ?? None;
        return new RequirementVerdict(id, text, source, t, n, verdict, SelfCheckScore.IssueOf(verdict, t, n), "理由");
    }

    private static SelfCheckResult R(params RequirementVerdict[] items) => SelfCheckScore.Build(items, "key1", reused: false);

    private static FixSuggestion Advise(SelfCheckResult current, long seed = 1, IReadOnlyList<PastRender>? others = null, int n = 2, string pos = Pos) =>
        FixAdvisor.Advise(current, seed, pos, Neg, others ?? Array.Empty<PastRender>(), n);

    private static readonly RequirementVerdict HairUnmet = V("r1", "銀色雙馬尾", "unmet", new[] { "silver hair", "twintails" });

    [Fact]
    public void Prompt_missing_comes_first_even_with_not_rendered()
    {
        var s = Advise(R(HairUnmet, V("r2", "抱著貓", "unmet", None), V("r3", "戴眼鏡", "unmet", None), V("r4", "海邊", "met")));
        Assert.Equal(SuggestionKinds.FixPrompt, s.Kind);
        Assert.Equal("建議：請助理補上「抱著貓」、「戴眼鏡」，seed 不變再生一張", s.Text);
        Assert.Equal("請修改 prompt：補上「抱著貓」、「戴眼鏡」。這是我要的，但目前的 prompt 沒寫進去。其他地方不要動。", s.Message);
        Assert.Equal(new[] { "r2", "r3" }, s.ItemIds);
    }

    [Fact]
    public void Only_not_rendered_suggests_a_reroll()
    {
        var s = Advise(R(HairUnmet, V("r2", "海邊", "met")));
        Assert.Equal((SuggestionKinds.Reroll, "建議：換一個 seed 重生（「銀色雙馬尾」這次沒畫出來）", (string?)null), (s.Kind, s.Text, s.Message));
        Assert.Equal(new[] { "r1" }, s.ItemIds);
    }

    [Fact]
    public void Nothing_wrong_is_none()
    {
        var s = Advise(R(V("r1", "海邊", "met"), V("r2", "某畫師", "unclear")));
        Assert.Equal((SuggestionKinds.None, (string?)null, (string?)null), (s.Kind, s.Text, s.Message));
        Assert.Empty(s.ItemIds);
        Assert.Empty(s.Notes);
    }

    [Fact]
    public void Failing_on_enough_seeds_suggests_rewriting_the_tags()
    {
        var earlier = new PastRender(7, Pos, Neg, R(HairUnmet));
        var s = Advise(R(HairUnmet, V("r2", "海邊", "met")), seed: 8, others: new[] { earlier });
        Assert.Equal(SuggestionKinds.RewriteTags, s.Kind);
        Assert.Equal("建議：請助理改寫「銀色雙馬尾」的寫法（換了 2 個 seed 都沒畫出來），seed 不變再生一張", s.Text);
        Assert.Equal("請改寫 prompt 裡這些要求的寫法：「銀色雙馬尾」（目前是 silver hair, twintails，換了 2 個 seed 都沒畫出來）。可以換同義的 tag 或加權重。其他地方不要動。", s.Message);
        Assert.Equal(new[] { "r1" }, s.ItemIds);
    }

    /// <summary>Review Focus 3：同一個 seed 生兩張只算一次（改 prompt 時 seed 不變）；prompt 改過、tag 也改寫過就從頭算。</summary>
    [Fact]
    public void Seeds_are_counted_once_and_reset_when_tags_change()
    {
        var sameSeed = new PastRender(7, Pos, Neg, R(HairUnmet));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 7, others: new[] { sameSeed }).Kind);

        var rewritten = new PastRender(5, "1girl, silver hair, twin tails, beach", Neg, R(V("r1", "銀色雙馬尾", "unmet", new[] { "silver hair", "twin tails" })));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 8, others: new[] { rewritten }).Kind);
    }

    /// <summary>實機 eval-cases R16 的 B：文字步每張重新對 tag，prompt 沒變也會對到不同的 tag。prompt 沒變就算同一條，不能因此從頭算。</summary>
    [Fact]
    public void Same_prompt_counts_even_when_the_matched_tags_differ()
    {
        var earlier = new PastRender(7, Pos, Neg, R(V("r1", "銀色雙馬尾", "unmet", new[] { "silver hair" })));
        Assert.Equal(SuggestionKinds.RewriteTags, Advise(R(HairUnmet), seed: 8, others: new[] { earlier }).Kind);
    }

    /// <summary>Review Focus 2：使用者又開口、清單換了（listKey 不同），舊清單的失敗不算。</summary>
    [Fact]
    public void Other_lists_do_not_count()
    {
        var oldList = new PastRender(7, Pos, Neg, SelfCheckScore.Build(new[] { HairUnmet }, "key0", false));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 8, others: new[] { oldList }).Kind);
    }

    [Fact]
    public void Threshold_comes_from_the_setting_and_at_least_one()
    {
        Assert.Equal(SuggestionKinds.RewriteTags, Advise(R(HairUnmet), n: 1).Kind);
        Assert.Equal(SuggestionKinds.RewriteTags, Advise(R(HairUnmet), n: 0).Kind);
        var earlier = new PastRender(7, Pos, Neg, R(HairUnmet));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 8, others: new[] { earlier }, n: 3).Kind);
    }

    [Fact]
    public void Prompt_fix_beats_rewrite()
    {
        var earlier = new PastRender(7, Pos, Neg, R(HairUnmet));
        Assert.Equal(SuggestionKinds.FixPrompt, Advise(R(HairUnmet, V("r2", "抱著貓", "unmet", None)), seed: 8, others: new[] { earlier }).Kind);
    }

    [Fact]
    public void Lucky_items_ride_along_with_a_prompt_change()
    {
        var lucky = V("r5", "不要帽子", "met", None);
        var fix = Advise(R(V("r2", "抱著貓", "unmet", None), lucky));
        Assert.Equal("請修改 prompt：補上「抱著貓」。這是我要的，但目前的 prompt 沒寫進去。另外「不要帽子」這次剛好畫出來，也請寫進 prompt。其他地方不要動。", fix.Message);
        Assert.Equal(new[] { "r2", "r5" }, fix.ItemIds);
        Assert.Empty(fix.Notes);

        var reroll = Advise(R(HairUnmet, lucky));
        Assert.Equal(new[] { "r1" }, reroll.ItemIds);
        Assert.Equal(new[] { "「不要帽子」prompt 沒寫，這次剛好畫出來；換 seed 可能就不見" }, reroll.Notes);

        Assert.Equal(new[] { "「不要帽子」prompt 沒寫，這次剛好畫出來；換 seed 可能就不見" }, Advise(R(lucky)).Notes);
    }

    [Fact]
    public void Delegated_misses_are_noted_not_fixed()
    {
        var s = Advise(R(V("r1", "海邊", "met"),
            V("r6", "白色洋裝", "unmet", source: RequirementSources.Delegated), V("r7", "微笑", "unmet", None, source: RequirementSources.Delegated)));
        Assert.Equal(SuggestionKinds.None, s.Kind);
        Assert.Equal(new[] { "另外：模型幫你挑的「白色洋裝」、「微笑」沒畫出來（不計分，不建議修）" }, s.Notes);
    }

    [Fact]
    public void Unclear_items_are_ignored()
    {
        Assert.Equal(SuggestionKinds.None, Advise(R(V("r1", "某畫師", "unclear", None))).Kind);
    }

    [Fact]
    public void Same_prompt_and_seed_as_before_is_noted_after_other_notes()
    {
        var before = new PastRender(7, Pos, Neg, R(V("r2", "抱著貓", "unmet", None)));
        var s = Advise(R(V("r2", "抱著貓", "unmet", None), V("r6", "白色洋裝", "unmet", source: RequirementSources.Delegated)), seed: 7, others: new[] { before });
        Assert.Equal(new[]
        {
            "另外：模型幫你挑的「白色洋裝」沒畫出來（不計分，不建議修）",
            "這張的 prompt 和 seed 跟之前某張一樣，畫面不會變；上次的修正可能沒有改到 prompt",
        }, s.Notes);
        Assert.Empty(Advise(R(V("r1", "海邊", "met")), seed: 7, others: new[] { before }, pos: Pos + ", cat").Notes);
    }

    [Fact]
    public void Rewrite_lists_negative_tags()
    {
        var noHat = V("r3", "不要帽子", "unmet", None, new[] { "hat" });
        var s = Advise(R(noHat), n: 1);
        Assert.Contains("「不要帽子」（目前是 負向：hat，換了 1 個 seed 都沒畫出來）", s.Message);
    }
}
