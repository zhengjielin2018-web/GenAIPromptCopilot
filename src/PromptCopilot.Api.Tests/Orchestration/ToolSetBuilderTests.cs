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
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
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
        var s = S(streak: 8); s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
        var t = ToolSetBuilder.Build(s, false, O);
        Assert.Contains(ToolNames.Discuss, t);
        Assert.DoesNotContain(ToolNames.AskUser, t);
        Assert.Contains(ToolNames.RequestSaveConsent, t);
    }

    /// <summary>釘住 Build 的 `Status == Finalized ||` 左分支：RecordFinalize 會把 streak 歸零，
    /// 所以只有用 Restore 造出「已定稿且 streak 未歸零」的狀態才咬得到這個分支。</summary>
    [Fact]
    public void Discuss_stays_when_finalized_with_nonzero_streak_via_restore()
    {
        var s = new Session("s");
        s.Restore(new SessionSnapshot(SessionStatus.Finalized, null, 0, 8, false,
            new(), new(), 0, new PresetLedger(), new FinalPrompt("p", "n", "t", "i"), 0, new()));
        Assert.Contains(ToolNames.Discuss, ToolSetBuilder.Build(s, false, O));
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

    /// <summary>計畫 §4.1：off 的 session 是量測用的對照組。拿掉的只有兩個檢索工具，其餘規則照舊。</summary>
    [Fact]
    public void Retrieval_off_removes_both_search_tools_and_nothing_else()
    {
        var on = ToolSetBuilder.Build(S(), false, O);
        var off = ToolSetBuilder.Build(new Session("s", retrievalEnabled: false), false, O);
        Assert.DoesNotContain(ToolNames.SearchPresets, off);
        Assert.DoesNotContain(ToolNames.SearchSimilarPrompts, off);
        Assert.Equal(on.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet(), off);
        Assert.True(ToolNames.Always.IsSubsetOf(on));   // Always 本身不動
    }

    [Fact]
    public void Retrieval_off_with_auto_complete_leaves_only_state_tools_and_finalize()
    {
        var off = ToolSetBuilder.Build(new Session("s", retrievalEnabled: false), true, O);
        Assert.Equal(new HashSet<string> { ToolNames.SetProfile, ToolNames.SetFacetStates, ToolNames.FinalizePrompt }, off);
    }

    [Fact]
    public void Retrieval_mode_defaults_on_and_survives_restore()
    {
        var s = new Session("s");
        Assert.True(s.RetrievalEnabled); Assert.Equal("on", s.RetrievalMode);
        var off = new Session("s", retrievalEnabled: false);
        off.Restore(s.Snapshot());
        Assert.False(off.RetrievalEnabled); Assert.Equal("off", off.RetrievalMode);
    }
}
