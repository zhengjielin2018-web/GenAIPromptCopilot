using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class ToolSetBuilderTests
{
    private static readonly OrchestratorOptions O = new() { MaxAskCount = 2, MaxDiscussStreak = 8 };
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    /// <summary>預設已判定題材（第一輪動手輪之後的樣子）；profile: null 是使用者第一句話的那一輪。</summary>
    private static Session S(int asks = 0, int streak = 0, bool finalized = false, string? profile = "portrait", bool retrieval = true)
    {
        var s = new Session("s", retrievalEnabled: retrieval);
        if (profile is not null) s.ApplyProfile(profile, Catalog);
        for (var i = 0; i < asks; i++) s.RecordAsk();
        for (var i = 0; i < streak; i++) s.RecordDiscuss();
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
        return s;
    }
    private static IReadOnlySet<string> Propose(Session s, bool auto = false) => ToolSetBuilder.Build(s, TurnKind.Propose, auto, O);
    private static IReadOnlySet<string> Act(Session s, bool auto = false) => ToolSetBuilder.Build(s, TurnKind.Act, auto, O);
    private static readonly string[] PictureTools = { ToolNames.SetProfile, ToolNames.SetFacetStates, ToolNames.AskUser, ToolNames.FinalizePrompt };

    /// <summary>先確認再動手設計 §3.1：打字的那一輪只能確認、討論、檢索。</summary>
    [Fact]
    public void Propose_turn_has_confirm_discuss_and_search_but_nothing_that_changes_the_picture()
    {
        var t = Propose(S());
        Assert.Equal(new HashSet<string> { ToolNames.Confirm, ToolNames.Discuss, ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }, t);
        Assert.Empty(t.Intersect(PictureTools));
    }

    [Fact]
    public void Act_turn_has_the_picture_tools_and_no_confirm_or_discuss()
    {
        var t = Act(S());
        Assert.True(ToolNames.Always.IsSubsetOf(t));
        Assert.Contains(ToolNames.AskUser, t);
        Assert.DoesNotContain(ToolNames.Confirm, t);
        Assert.DoesNotContain(ToolNames.Discuss, t);
        Assert.DoesNotContain(ToolNames.RequestSaveConsent, t);
    }

    [Fact]
    public void Ask_disappears_at_max_ask_count() => Assert.DoesNotContain(ToolNames.AskUser, Act(S(asks: 2)));

    [Fact]
    public void Discuss_disappears_at_max_streak_while_collecting() => Assert.DoesNotContain(ToolNames.Discuss, Propose(S(streak: 8)));

    [Fact]
    public void Finalized_propose_turn_keeps_discuss_and_offers_save_consent()
    {
        var s = S(streak: 8); s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
        var t = Propose(s);
        Assert.Contains(ToolNames.Discuss, t);
        Assert.Contains(ToolNames.RequestSaveConsent, t);
        Assert.Contains(ToolNames.Confirm, t);
    }

    [Fact]
    public void Finalized_act_turn_has_no_ask_and_no_save_consent()
    {
        var t = Act(S(finalized: true));
        Assert.DoesNotContain(ToolNames.AskUser, t);
        Assert.DoesNotContain(ToolNames.RequestSaveConsent, t);
        Assert.Contains(ToolNames.FinalizePrompt, t);
    }

    /// <summary>釘住 Build 的 `Status == Finalized ||` 左分支：RecordFinalize 會把 streak 歸零，
    /// 所以只有用 Restore 造出「已定稿且 streak 未歸零」的狀態才咬得到這個分支。</summary>
    [Fact]
    public void Discuss_stays_when_finalized_with_nonzero_streak_via_restore()
    {
        var s = new Session("s");
        s.Restore(new SessionSnapshot(SessionStatus.Finalized, null, 0, 8, false,
            new(), new(), 0, new PresetLedger(), new FinalPrompt("p", "n", "t", "i"), 0, new(), new()));
        Assert.Contains(ToolNames.Discuss, Propose(s));
    }

    /// <summary>第一句話那一輪還沒有題材：兩個檢索工具一定回「請先呼叫 SetProfile」，而 SetProfile 要到動手輪才有。
    /// 不給，模型就不會照錯誤去叫這一輪沒有的工具（known-issues #13 的觸發條件）。動手輪不受影響：SetProfile 就在同一輪。</summary>
    [Fact]
    public void Propose_turn_without_a_profile_has_no_search_tools()
    {
        Assert.Equal(new HashSet<string> { ToolNames.Confirm, ToolNames.Discuss }, Propose(S(profile: null)));
        Assert.Equal(new HashSet<string> { ToolNames.Confirm }, Propose(S(profile: null), auto: true));
        Assert.True(ToolNames.Always.IsSubsetOf(Act(S(profile: null))));
    }

    /// <summary>「隨便」的確認輪連 Discuss 都沒有：只能確認要補什麼（設計 §6.3）。</summary>
    [Fact]
    public void Auto_complete_propose_turn_leaves_only_confirm_and_search() =>
        Assert.Equal(new HashSet<string> { ToolNames.Confirm, ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }, Propose(S(), auto: true));

    [Fact]
    public void Auto_complete_act_turn_drops_ask()
    {
        var t = Act(S(), auto: true);
        Assert.DoesNotContain(ToolNames.AskUser, t);
        Assert.Contains(ToolNames.FinalizePrompt, t);
    }

    [Fact]
    public void Exhausted_collecting_act_turn_has_only_always_tools() => Assert.Equal(ToolNames.Always, Act(S(asks: 2, streak: 8)));

    /// <summary>計畫 §4.1：off 的 session 是量測用的對照組。兩種輪都只拿掉兩個檢索工具，其餘規則照舊。</summary>
    [Fact]
    public void Retrieval_off_removes_both_search_tools_in_both_kinds_and_nothing_else()
    {
        foreach (var kind in new[] { TurnKind.Propose, TurnKind.Act })
        {
            var on = ToolSetBuilder.Build(S(), kind, false, O);
            var off = ToolSetBuilder.Build(S(retrieval: false), kind, false, O);
            Assert.Equal(on.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet(), off);
        }
        Assert.True(ToolNames.Always.IsSubsetOf(Act(S())));   // Always 本身不動
    }

    [Fact]
    public void Retrieval_off_with_auto_complete_act_turn_leaves_only_state_tools_and_finalize() =>
        Assert.Equal(new HashSet<string> { ToolNames.SetProfile, ToolNames.SetFacetStates, ToolNames.FinalizePrompt },
            Act(new Session("s", retrievalEnabled: false), auto: true));

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
