using System.Threading.Channels;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Plugins;

public class DialogPluginTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private static readonly OrchestratorOptions O = new();

    /// <summary>askUserOffered：工具清單多掛 AskUser（Collecting、追問額度還有、使用者沒說隨便）。
    /// 預設的 <see cref="ToolNames.Always"/> 裡沒有它，等於額度用完或使用者說了隨便。</summary>
    private static (DialogPlugin plugin, TurnContext turn, Session s) Make(bool profile = true, bool finalized = false, bool askUserOffered = false)
    {
        var s = new Session("s");
        if (profile) s.ApplyProfile("portrait", Catalog);
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
        s.Ledger.Record(new LedgerEntry { Id = 5, Title = "t", PromptSnippet = "p", FacetIds = new[] { "style.genre" } }, new LedgerHit("style", 0.2, false));
        var tools = askUserOffered ? new HashSet<string>(ToolNames.Always) { ToolNames.AskUser } : ToolNames.Always;
        var turn = new TurnContext(s, 1, GuardResult.Ok(false), tools, Channel.CreateUnbounded<AgentEvent>().Writer);
        return (new DialogPlugin(turn, Catalog, O), turn, s);
    }

    private static FacetStateEntry[] States(params (string id, string st)[] xs) => xs.Select(x => new FacetStateEntry(x.id, x.st)).ToArray();
    private static AskItem Ask(string dim, string fid) => new(dim, "q?", new[] { fid }, new[] { new OptionItem("a", "t", 5), new OptionItem("b", "t", null) });

    [Fact]
    public void AskUser_requires_profile()
    {
        var (p, turn, s) = Make(profile: false);
        var r = p.AskUser("hi", new[] { Ask("style", "style.genre") }, Array.Empty<FacetStateEntry>());
        Assert.Contains("SetProfile", r); Assert.Null(turn.Outcome); Assert.Equal(0, s.AskCount);
    }

    [Fact]
    public void AskUser_success_counts_marks_offered_and_applies_states()
    {
        var (p, turn, s) = Make();
        var r = p.AskUser("hi", new[] { Ask("style", "style.genre") }, States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        var o = Assert.IsType<AskOutcome>(turn.Outcome);
        Assert.Single(o.Asks);
        Assert.Equal(1, s.AskCount);
        Assert.Equal(FacetState.Covered, s.FacetStates["pose.gaze"]);
        Assert.Single(s.Ledger.Get(5)!.OfferedAs);
    }

    [Fact]
    public void AskUser_with_everything_filtered_returns_error_and_does_not_count()
    {
        var (p, turn, s) = Make();
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["style.genre"] = FacetState.Covered }, Catalog);
        var r = p.AskUser("hi", new[] { Ask("style", "style.genre") }, Array.Empty<FacetStateEntry>());
        Assert.Contains("清洗後", r); Assert.Null(turn.Outcome); Assert.Equal(0, s.AskCount);
    }

    [Fact]
    public void Discuss_without_profile_ignores_states_but_succeeds()
    {
        var (p, turn, s) = Make(profile: false);
        var r = p.Discuss("這個系統可以幫你…", States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        Assert.IsType<MessageOutcome>(turn.Outcome);
        Assert.Empty(s.FacetStates);
        Assert.Equal(1, s.DiscussStreak);
    }

    [Fact]
    public void Discuss_when_finalized_rejects_changed_facets()
    {
        var (p, turn, s) = Make(finalized: true);
        var r = p.Discuss("好的", States(("pose.gaze", "covered")));
        Assert.Contains("FinalizePrompt", r); Assert.Null(turn.Outcome);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
    }

    /// <summary>SetFacetStates 每一輪都在工具清單裡。拿「現在的」狀態比對，等於先用 SetFacetStates
    /// 改掉再用 Discuss 回報同一組值就永遠相等——定稿後的變更閘門（主規格 §4.5）等於不存在。
    /// 要比的是這一輪開始時的狀態。</summary>
    [Fact]
    public void Discuss_when_finalized_rejects_facets_changed_earlier_in_the_same_turn()
    {
        var (p, turn, s) = Make(finalized: true);
        Assert.Equal("ok：套用 1 筆", new SessionPlugin(turn, Catalog).SetFacetStates(States(("pose.gaze", "covered"))));

        var r = p.Discuss("好的", States(("pose.gaze", "covered")));

        Assert.Contains("FinalizePrompt", r);
        Assert.Null(turn.Outcome);
    }

    [Fact]
    public void Discuss_when_finalized_with_same_facets_succeeds_and_streak_unchanged()
    {
        var (p, turn, s) = Make(finalized: true);
        var r = p.Discuss("blurry 是基礎負向詞", States(("pose.gaze", "missing")), new[] { new OptionItem("x", "t", 99) });
        Assert.Equal("ok", r);
        var o = Assert.IsType<MessageOutcome>(turn.Outcome);
        Assert.Null(o.Options[0].PresetId);           // 99 不在 ledger → 降級
        Assert.Equal(0, s.DiscussStreak);
    }

    [Fact]
    public void FinalizePrompt_sets_final_and_status()
    {
        var (p, turn, s) = Make();
        var r = p.FinalizePrompt("1girl", "lowres", "tips", "一個女生", States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        Assert.IsType<FinalizedOutcome>(turn.Outcome);
        Assert.Equal(SessionStatus.Finalized, s.Status);
        Assert.Equal("1girl", s.LastFinal!.Positive);
    }

    [Fact]
    public void FinalizePrompt_requires_intent_summary()
    {
        var (p, turn, s) = Make();
        var r = p.FinalizePrompt("1girl", "lowres", "t", "   ", Array.Empty<FacetStateEntry>());
        Assert.Contains("intentSummary", r);
        Assert.Null(turn.Outcome);
        Assert.Equal(SessionStatus.Collecting, s.Status);
    }

    [Fact]
    public void FinalizePrompt_stores_trimmed_intent_summary_and_emits_it()
    {
        var (p, turn, s) = Make();
        var r = p.FinalizePrompt("1girl", "lowres", "t", "  雨夜霓虹街頭的銀髮少女，寫實攝影  ", Array.Empty<FacetStateEntry>());
        Assert.Equal("ok", r);
        Assert.Equal("雨夜霓虹街頭的銀髮少女，寫實攝影", s.LastFinal!.IntentSummary);
        var ev = AgenticOrchestrator.ToFinal(turn.Outcome!);
        Assert.Equal("finalized", ev.Kind);
        Assert.Equal("雨夜霓虹街頭的銀髮少女，寫實攝影", ev.IntentSummary);
    }

    /// <summary>tag 來源由伺服器比對 ledger 標，不信模型自述（主規格 §9）。Make 的 ledger 有 id 5、片段 "p"。</summary>
    [Fact]
    public void FinalizePrompt_attributes_each_tag_against_the_ledger()
    {
        var (p, turn, s) = Make();
        var r = p.FinalizePrompt("p, 1girl, masterpiece", "lowres, blurry", "t", "一個女生", Array.Empty<FacetStateEntry>());

        Assert.Equal("ok", r);
        var pos = s.LastFinal!.PositiveSources!;
        Assert.Equal(new[] { "p", "1girl", "masterpiece" }, pos.Select(x => x.Tag));
        Assert.Equal(new[] { "rag", "llm", "base" }, pos.Select(x => x.Origin));
        Assert.Equal(new long[] { 5 }, pos[0].PresetIds);
        Assert.Equal("t", pos[0].PresetTitle);
        Assert.Equal(new[] { "base", "llm" }, s.LastFinal.NegativeSources!.Select(x => x.Origin));
        var ev = AgenticOrchestrator.ToFinal(turn.Outcome!);
        Assert.Same(s.LastFinal.PositiveSources, ev.PositiveSources);
        Assert.Same(s.LastFinal.NegativeSources, ev.NegativeSources);
    }

    /// <summary>定稿閘門（主規格 §4.6）：AskUser 還在清單上＝追問額度沒用完，有缺就不准定稿。
    /// system.md 寫了「還有 missing 就 AskUser」，模型不遵守（2026-09-25 實測 20/31 facet 缺仍定稿），改由程式擋。</summary>
    [Fact]
    public void FinalizePrompt_is_refused_while_facets_are_missing_and_AskUser_is_offered()
    {
        var (p, turn, s) = Make(askUserOffered: true);
        var r = p.FinalizePrompt("1girl", "lowres", "t", "一個女生", States(("pose.gaze", "covered")));

        var missing = s.FacetStates.Count(kv => kv.Value == FacetState.Missing);
        Assert.StartsWith("錯誤", r);
        Assert.Contains("AskUser", r);
        Assert.Contains($"{missing} 個", r);
        Assert.Contains("style.genre", r);
        Assert.DoesNotContain("pose.gaze", r);            // 這一次呼叫帶進來的 covered 已套用，不算缺
        Assert.Null(turn.Outcome);
        Assert.Equal(SessionStatus.Collecting, s.Status);
        Assert.Null(s.LastFinal);
    }

    /// <summary>有委託 note 的 facet 維持 missing，但使用者已經交代「你決定」，不算缺。</summary>
    [Fact]
    public void FinalizePrompt_gate_does_not_count_facets_with_a_delegation_note()
    {
        var (p, turn, s) = Make(askUserOffered: true);
        foreach (var id in s.FacetStates.Keys.Where(id => id != "style.genre")) s.FacetNotes[id] = "使用者委託此項";

        var r = p.FinalizePrompt("1girl", "lowres", "t", "一個女生", Array.Empty<FacetStateEntry>());

        Assert.Contains("還有 1 個 facet", r);
        Assert.EndsWith("style.genre", r);
        Assert.Null(turn.Outcome);
    }

    [Fact]
    public void FinalizePrompt_passes_the_gate_when_every_missing_facet_is_delegated()
    {
        var (p, turn, s) = Make(askUserOffered: true);
        var delegated = s.FacetStates.Keys.Where(id => id != "pose.gaze").Select(id => new FacetStateEntry(id, "missing", "使用者委託此項")).ToArray();
        Assert.StartsWith("ok", new SessionPlugin(turn, Catalog).SetFacetStates(delegated));

        var r = p.FinalizePrompt("1girl", "lowres", "t", "一個女生", States(("pose.gaze", "covered")));

        Assert.Equal("ok", r);
        Assert.IsType<FinalizedOutcome>(turn.Outcome);
        Assert.Equal(SessionStatus.Finalized, s.Status);
    }

    /// <summary>AskUser 不在清單上＝額度用完或使用者說了隨便：有缺也照樣定稿，missing 留白。</summary>
    [Fact]
    public void FinalizePrompt_with_missing_facets_is_allowed_once_AskUser_is_gone()
    {
        var (p, turn, s) = Make(askUserOffered: false);
        var r = p.FinalizePrompt("1girl", "lowres", "t", "一個女生", Array.Empty<FacetStateEntry>());
        Assert.Equal("ok", r);
        Assert.IsType<FinalizedOutcome>(turn.Outcome);
        Assert.Contains(s.FacetStates.Values, v => v == FacetState.Missing);
    }

    /// <summary>預算用盡的強制定稿（主規格 §4.6）只掛 FinalizePrompt，擋下去這一輪就沒有出口。</summary>
    [Fact]
    public void FinalizePrompt_forced_by_the_budget_skips_the_gate()
    {
        var (p, turn, s) = Make(askUserOffered: true);
        turn.ForcedFinalize = true;
        var r = p.FinalizePrompt("1girl", "lowres", "t", "一個女生", Array.Empty<FacetStateEntry>());
        Assert.Equal("ok", r);
        Assert.IsType<FinalizedOutcome>(turn.Outcome);
        Assert.Equal(SessionStatus.Finalized, s.Status);
    }

    [Fact]
    public void FinalizePrompt_passes_the_gate_when_nothing_is_missing()
    {
        var (p, turn, s) = Make(askUserOffered: true);
        var ids = s.FacetStates.Keys.ToArray();
        var states = ids.Select((id, i) => new FacetStateEntry(id, (i % 3) switch { 0 => "covered", 1 => "waived", _ => "notApplicable" })).ToArray();

        var r = p.FinalizePrompt("1girl", "lowres", "t", "一個女生", states);

        Assert.Equal("ok", r);
        Assert.IsType<FinalizedOutcome>(turn.Outcome);
        Assert.DoesNotContain(s.FacetStates.Values, v => v == FacetState.Missing);
    }

    [Fact]
    public void RequestSaveConsent_requires_finalized()
    {
        var (p, turn, _) = Make();
        Assert.Contains("定稿", p.RequestSaveConsent()); Assert.Null(turn.Outcome);
        var (p2, turn2, _) = Make(finalized: true);
        Assert.Equal("ok", p2.RequestSaveConsent()); Assert.IsType<SaveConsentOutcome>(turn2.Outcome);
    }

    [Fact]
    public void Unknown_facet_state_string_is_skipped_with_note()
    {
        var (p, turn, s) = Make();
        p.Discuss("x", States(("pose.gaze", "bogus"), ("pose.main", "waived")));
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.Equal(FacetState.Waived, s.FacetStates["pose.main"]);
        Assert.Contains(turn.Rejections, r => r.Contains("bogus"));
    }
}
