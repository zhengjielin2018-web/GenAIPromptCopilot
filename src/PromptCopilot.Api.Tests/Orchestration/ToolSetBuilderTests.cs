using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Orchestration;

public class ToolSetBuilderTests
{
    private static readonly OrchestratorOptions O = new() { MaxAskCount = 2, MaxDiscussStreak = 8 };
    private static Session S(int asks = 0, int streak = 0, bool finalized = false)
    {
        var s = new Session("s");
        for (var i = 0; i < asks; i++) s.RecordAsk();
        for (var i = 0; i < streak; i++) s.RecordDiscuss();
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        return s;
    }

    [Fact]
    public void Fresh_session_has_ask_and_discuss_but_no_save_consent()
    {
        var t = ToolSetBuilder.Build(S(), false, O);
        Assert.Contains(ToolNames.AskUser, t); Assert.Contains(ToolNames.Discuss, t);
        Assert.DoesNotContain(ToolNames.RequestSaveConsent, t);
        Assert.True(ToolNames.Always.IsSubsetOf(t));
    }

    [Fact]
    public void Ask_disappears_at_max_ask_count() => Assert.DoesNotContain(ToolNames.AskUser, ToolSetBuilder.Build(S(asks: 2), false, O));

    [Fact]
    public void Discuss_disappears_at_max_streak_while_collecting() => Assert.DoesNotContain(ToolNames.Discuss, ToolSetBuilder.Build(S(streak: 8), false, O));

    [Fact]
    public void Discuss_stays_when_finalized_regardless_of_streak()
    {
        var s = S(streak: 8); s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        var t = ToolSetBuilder.Build(s, false, O);
        Assert.Contains(ToolNames.Discuss, t);
        Assert.DoesNotContain(ToolNames.AskUser, t);
        Assert.Contains(ToolNames.RequestSaveConsent, t);
    }

    [Fact]
    public void WantsAutoComplete_removes_both_ask_and_discuss()
    {
        var t = ToolSetBuilder.Build(S(), true, O);
        Assert.DoesNotContain(ToolNames.AskUser, t); Assert.DoesNotContain(ToolNames.Discuss, t);
        Assert.Contains(ToolNames.FinalizePrompt, t);
    }

    [Fact]
    public void Exhausted_collecting_session_has_only_always_tools()
    {
        var t = ToolSetBuilder.Build(S(asks: 2, streak: 8), false, O);
        Assert.Equal(ToolNames.Always, t);
    }
}
