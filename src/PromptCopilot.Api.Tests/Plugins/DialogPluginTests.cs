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

    private static (DialogPlugin plugin, TurnContext turn, Session s) Make(bool profile = true, bool finalized = false)
    {
        var s = new Session("s");
        if (profile) s.ApplyProfile("portrait", Catalog);
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        s.Ledger.Record(new LedgerEntry { Id = 5, Title = "t", PromptSnippet = "p", FacetIds = new[] { "style.genre" } }, new LedgerHit("style", 0.2, false));
        var turn = new TurnContext(s, 1, GuardResult.Ok(false), ToolNames.Always, Channel.CreateUnbounded<AgentEvent>().Writer);
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
        var r = p.Discuss("這個系統可以幫你…", null, States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        Assert.IsType<MessageOutcome>(turn.Outcome);
        Assert.Empty(s.FacetStates);
        Assert.Equal(1, s.DiscussStreak);
    }

    [Fact]
    public void Discuss_when_finalized_rejects_changed_facets()
    {
        var (p, turn, s) = Make(finalized: true);
        var r = p.Discuss("好的", null, States(("pose.gaze", "covered")));
        Assert.Contains("FinalizePrompt", r); Assert.Null(turn.Outcome);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
    }

    [Fact]
    public void Discuss_when_finalized_with_same_facets_succeeds_and_streak_unchanged()
    {
        var (p, turn, s) = Make(finalized: true);
        var r = p.Discuss("blurry 是基礎負向詞", new[] { new OptionItem("x", "t", 99) }, States(("pose.gaze", "missing")));
        Assert.Equal("ok", r);
        var o = Assert.IsType<MessageOutcome>(turn.Outcome);
        Assert.Null(o.Options[0].PresetId);           // 99 不在 ledger → 降級
        Assert.Equal(0, s.DiscussStreak);
    }

    [Fact]
    public void FinalizePrompt_sets_final_and_status()
    {
        var (p, turn, s) = Make();
        var r = p.FinalizePrompt("1girl", "lowres", "tips", States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        Assert.IsType<FinalizedOutcome>(turn.Outcome);
        Assert.Equal(SessionStatus.Finalized, s.Status);
        Assert.Equal("1girl", s.LastFinal!.Positive);
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
        p.Discuss("x", null, States(("pose.gaze", "bogus"), ("pose.main", "waived")));
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.Equal(FacetState.Waived, s.FacetStates["pose.main"]);
        Assert.Contains(turn.Rejections, r => r.Contains("bogus"));
    }
}
