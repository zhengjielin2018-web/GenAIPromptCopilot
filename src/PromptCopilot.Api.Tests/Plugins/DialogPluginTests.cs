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

    // ---- Confirm（先確認再動手設計 §3.2）----

    [Fact]
    public void Confirm_without_choices_records_the_pending_proposal_and_changes_nothing()
    {
        var (p, turn, s) = Make();
        var before = new Dictionary<string, FacetState>(s.FacetStates);
        var r = p.Confirm("  我理解的畫面：一位金色短髮的中年女士站在雨夜的霓虹街頭。  ");
        Assert.Equal("ok", r);
        var o = Assert.IsType<ConfirmOutcome>(turn.Outcome);
        Assert.Equal("我理解的畫面：一位金色短髮的中年女士站在雨夜的霓虹街頭。", o.Message);
        Assert.Empty(o.Choices);
        var pending = s.PendingConfirmation!;
        Assert.Equal(1, pending.TurnIndex);
        Assert.Equal(o.Message, pending.Message);
        Assert.Empty(pending.Choices);
        Assert.False(pending.AutoComplete);
        Assert.Equal(before, s.FacetStates);                                         // 確認不改任何 facet
    }

    [Fact]
    public void Confirm_keeps_two_to_four_trimmed_distinct_choices()
    {
        var (p, turn, s) = Make();
        var r = p.Confirm("她兩手已經拿著相機和飲料，再拿雨傘會拿不下。你想要哪一種？",
            new[] { " 換掉飲料，改拿雨傘 ", "換掉相機，改拿雨傘", "", "換掉飲料，改拿雨傘", "三樣都拿（可能不自然）" });
        Assert.Equal("ok", r);
        Assert.Equal(new[] { "換掉飲料，改拿雨傘", "換掉相機，改拿雨傘", "三樣都拿（可能不自然）" }, Assert.IsType<ConfirmOutcome>(turn.Outcome).Choices);
        Assert.Equal(3, s.PendingConfirmation!.Choices.Count);
    }

    [Theory]
    [InlineData("   ", null)]                                     // 空 message
    [InlineData("m", new[] { "只有一個" })]
    [InlineData("m", new[] { "a", " a " })]                        // 去重後剩 1 個
    [InlineData("m", new[] { "a", "b", "c", "d", "e" })]
    public void Confirm_rejects_bad_arguments_without_an_outcome(string message, string[]? choices)
    {
        var (p, turn, s) = Make();
        Assert.StartsWith("錯誤", p.Confirm(message, choices));
        Assert.Null(turn.Outcome);
        Assert.Null(s.PendingConfirmation);
        Assert.NotEmpty(turn.Rejections);
    }

    [Fact]
    public void Confirm_rejects_a_choice_longer_than_40_chars()
    {
        var (p, turn, _) = Make();
        var r = p.Confirm("m", new[] { new string('長', 41), "短" });
        Assert.StartsWith("錯誤", r);
        Assert.Contains("40", r);
        Assert.Null(turn.Outcome);
    }

    [Fact]
    public void Confirm_remembers_that_the_user_asked_to_auto_complete()
    {
        var s = new Session("s");
        var turn = new TurnContext(s, 4, GuardResult.Ok(true), ToolNames.ProposeAlways, Channel.CreateUnbounded<AgentEvent>().Writer);
        Assert.Equal("ok", new DialogPlugin(turn, Catalog, O).Confirm("我會直接定稿，風格補成寫實攝影。"));
        Assert.True(s.PendingConfirmation!.AutoComplete);
        Assert.Equal(4, s.PendingConfirmation.TurnIndex);
    }

    [Fact]
    public void Confirm_works_before_a_profile_is_set()
    {
        var (p, turn, _) = Make(profile: false);
        Assert.Equal("ok", p.Confirm("我理解的畫面：一隻貓。"));
        Assert.IsType<ConfirmOutcome>(turn.Outcome);
    }

    [Fact]
    public void Confirm_outcome_becomes_a_confirm_final_event()
    {
        var ev = AgenticOrchestrator.ToFinal(new ConfirmOutcome("m", new[] { "a", "b" }));
        Assert.Equal("confirm", ev.Kind);
        Assert.Equal("m", ev.Message);
        Assert.Equal(new[] { "a", "b" }, ev.Choices);
    }

    /// <summary>設計 §3.3：Discuss 不分狀態都不能改 facet，否則它是繞過確認的後門。</summary>
    [Fact]
    public void Discuss_while_collecting_rejects_changed_facets()
    {
        var (p, turn, s) = Make();
        var r = p.Discuss("好的", States(("pose.gaze", "covered")));
        Assert.Equal("錯誤：Discuss 不能改 facet 狀態；使用者要改畫面時，請用 Confirm 跟他確認", r);
        Assert.Null(turn.Outcome);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.Equal(0, s.DiscussStreak);
    }

    /// <summary>設計 §1、§3.3：Discuss 在確認之前什麼都不改。狀態相同但夾帶新 note 與不同 tags 時，
    /// 照舊回 ok，但 FacetNotes／FacetTags 一個字都不能動（它們會影響定稿閘門、晶片標題與推薦錨點）。</summary>
    [Fact]
    public void Discuss_with_unchanged_states_never_writes_notes_or_tags()
    {
        var (_, _, s) = Make(finalized: true);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["style.genre"] = FacetState.Covered }, Catalog,
            new Dictionary<string, string> { ["style.genre"] = "anime" });
        s.FacetNotes["pose.gaze"] = "使用者委託此項";
        var turn = new TurnContext(s, 2, GuardResult.Ok(false), ToolNames.Always, Channel.CreateUnbounded<AgentEvent>().Writer);   // 輪初狀態含上面的改動
        var p = new DialogPlugin(turn, Catalog, O);

        var r = p.Discuss("好的", new[]
        {
            new FacetStateEntry("style.genre", "covered", Tags: "photo realism"),
            new FacetStateEntry("pose.gaze", "missing", Note: "偷塞的備註"),
        });

        Assert.Equal("ok", r);
        Assert.IsType<MessageOutcome>(turn.Outcome);
        Assert.Equal(new Dictionary<string, string> { ["style.genre"] = "anime" }, s.FacetTags);
        Assert.Equal(new Dictionary<string, string> { ["pose.gaze"] = "使用者委託此項" }, s.FacetNotes);
    }

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
        Assert.Contains("Confirm", r); Assert.Null(turn.Outcome);
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

        Assert.Contains("Confirm", r);
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
        Assert.True(s.LastFinal.Reviewed);
    }

    /// <summary>審查開關關著的那一輪，輸出側沒檢過：定稿要記下來，save-to-shared 才擋得住。</summary>
    [Fact]
    public void FinalizePrompt_with_review_off_marks_the_final_unreviewed()
    {
        var (p, turn, s) = Make();
        turn.SafetyOn = false;
        Assert.Equal("ok", p.FinalizePrompt("1girl", "lowres", "tips", "一個女生", States(("pose.gaze", "covered"))));
        Assert.False(s.LastFinal!.Reviewed);
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
        // Discuss 不能改 facet（設計 §3.3），Apply 的容錯改從 SetFacetStates 走同一條路驗
        new SessionPlugin(turn, Catalog).SetFacetStates(States(("pose.gaze", "bogus"), ("pose.main", "waived")));
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.Equal(FacetState.Waived, s.FacetStates["pose.main"]);
        Assert.Contains(turn.Rejections, r => r.Contains("bogus"));
    }

    // ---- 檢索時機（2026-10-06）----

    /// <summary>設計 §5：選項數與帶 presetId 的進本輪計數；沒帶 presetId 的 tags 是模型寫的，帶的不記（片段那邊記過）。</summary>
    [Fact]
    public void AskUser_counts_options_and_records_tags_of_options_without_a_preset()
    {
        var (p, turn, s) = Make();
        var ask = new AskItem("style", "q?", new[] { "style.genre" },
            new[] { new OptionItem("寫實", "photorealistic", 5), new OptionItem("動漫", "anime style", null) });
        Assert.Equal("ok", p.AskUser("hi", new[] { ask }, Array.Empty<FacetStateEntry>()));
        Assert.Equal(2, turn.OptionsTotal);
        Assert.Equal(1, turn.OptionsWithPreset);
        Assert.Equal(TagTimeline.Source.Model, s.Ledger.Timeline.FirstSeen("anime style"));
        Assert.Null(s.Ledger.Timeline.FirstSeen("photorealistic"));
    }

    [Fact]
    public void Discuss_counts_options_too()
    {
        var (p, turn, _) = Make();
        var r = p.Discuss("可以參考這些方向", Array.Empty<FacetStateEntry>(),
            new[] { new OptionItem("雨夜咖啡廳", "night, cafe", 5), new OptionItem("書店", "bookstore", null) });
        Assert.Equal("ok", r);
        Assert.Equal(2, turn.OptionsTotal);
        Assert.Equal(1, turn.OptionsWithPreset);
    }
}
