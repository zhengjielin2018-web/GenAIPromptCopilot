using Microsoft.Extensions.Caching.Memory;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Sessions;

public class SessionTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private static Session New() => new("s1");

    [Fact]
    public void ApplyProfile_resets_facets_to_missing_but_keeps_counters()
    {
        var s = New();
        s.RecordAsk(); s.RecordDiscuss(); s.AutoFill = true;
        s.ApplyProfile("portrait", Catalog);
        Assert.Equal(31, s.FacetStates.Count);
        Assert.All(s.FacetStates.Values, v => Assert.Equal(FacetState.Missing, v));
        Assert.Equal(1, s.AskCount);
        Assert.Equal(1, s.DiscussStreak);
        Assert.True(s.AutoFill);
    }

    [Fact]
    public void ApplyFacetStates_ignores_ids_outside_profile()
    {
        var s = New(); s.ApplyProfile("landscape", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["scene.season"] = FacetState.Covered, ["clothing.head"] = FacetState.Covered }, Catalog);
        Assert.Equal(FacetState.Covered, s.FacetStates["scene.season"]);
        Assert.False(s.FacetStates.ContainsKey("clothing.head"));
    }

    [Fact]
    public void GroundedDimensions_derives_from_covered_only()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["pose.gaze"] = FacetState.Covered, ["style.genre"] = FacetState.Waived }, Catalog);
        Assert.Equal(new[] { "pose" }, s.GroundedDimensions(Catalog));
    }

    [Fact]
    public void RecordDiscuss_increments_streak_only_while_collecting()
    {
        var s = New();
        s.RecordDiscuss(); Assert.Equal(1, s.DiscussStreak);
        s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        Assert.Equal(SessionStatus.Finalized, s.Status);
        Assert.Equal(0, s.DiscussStreak);
        s.RecordDiscuss(); Assert.Equal(0, s.DiscussStreak);
    }

    [Fact]
    public void RecordAsk_does_not_touch_streak()
    {
        var s = New(); s.RecordDiscuss(); s.RecordAsk();
        Assert.Equal(1, s.AskCount); Assert.Equal(1, s.DiscussStreak);
    }

    [Fact]
    public void Snapshot_restore_reverts_everything_including_history_and_ledger()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddUserMessage("hi");
        var snap = s.Snapshot();

        s.RecordAsk(); s.RecordDiscuss(); s.AutoFill = true; s.TurnIndex = 5;
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["pose.gaze"] = FacetState.Covered }, Catalog);
        s.ApplyProfile("landscape", Catalog);
        s.FacetNotes["clothing.footwear"] = "使用者委託此項";
        s.ChatHistory.AddAssistantMessage("x"); s.ChatHistory.AddUserMessage("y");
        s.Ledger.Record(new LedgerEntry { Id = 7, Title = "t", PromptSnippet = "a", FacetIds = Array.Empty<string>() }, new LedgerHit("style", 0.1, true));
        s.RecordFinalize(new FinalPrompt("p", "n", "t"));

        s.Restore(snap);
        Assert.Equal(0, s.AskCount); Assert.Equal(0, s.DiscussStreak); Assert.False(s.AutoFill);
        Assert.Equal(SessionStatus.Collecting, s.Status); Assert.Null(s.LastFinal);
        Assert.Equal("portrait", s.Profile); Assert.Equal(0, s.TurnIndex);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.False(s.FacetStates.ContainsKey("scene.season"));
        Assert.Single(s.ChatHistory);
        Assert.False(s.Ledger.Contains(7));
        Assert.Empty(s.FacetNotes);
    }

    [Fact]
    public void SessionStore_creates_and_finds_by_id()
    {
        var store = new SessionStore(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        var s = store.Create();
        Assert.Same(s, store.TryGet(s.Id));
        Assert.Null(store.TryGet("nope"));
    }
}
