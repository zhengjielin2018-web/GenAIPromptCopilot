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

    /// <summary>設計 §5.5：只有 covered 的 facet 有 tags；狀態改成別的就移除；不適用的 facet 一起被丟掉。</summary>
    [Fact]
    public void ApplyFacetStates_keeps_tags_only_for_covered_and_drops_them_when_state_changes()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(
            new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Covered, ["clothing.upper"] = FacetState.Missing, ["scene.season"] = FacetState.Covered },
            Catalog,
            new Dictionary<string, string> { ["clothing.footwear"] = " sandals ", ["clothing.upper"] = "shirt", ["scene.season"] = "autumn" });
        Assert.Equal(new Dictionary<string, string> { ["clothing.footwear"] = "sandals" }, s.FacetTags);   // missing 的不存；portrait 沒有 scene.season

        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Waived }, Catalog);
        Assert.Empty(s.FacetTags);
    }

    [Fact]
    public void ApplyProfile_clears_facet_tags()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["pose.gaze"] = FacetState.Covered }, Catalog, new Dictionary<string, string> { ["pose.gaze"] = "looking at viewer" });
        s.ApplyProfile("landscape", Catalog);
        Assert.Empty(s.FacetTags);
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
        s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));
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

    private static Adoption SomeAdoption(int turn = 3) =>
        new(turn, 41720, "和風女僕", "civitai:1:0", "clothing",
            new Dictionary<string, IReadOnlyList<string>> { ["clothing.upper"] = new[] { "purple kimono" } },
            new[] { "clothing.footwear" }, new[] { "clothing.upper" }, Array.Empty<string>());

    /// <summary>設計 §6.3：採用寫進 ledger，定稿 chip 能開抽屜、offered 區段會列它。</summary>
    [Fact]
    public void RecordAdoption_adds_to_list_ledger_and_offered()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.RecordAdoption(SomeAdoption(), new LedgerEntry { Id = 41720, Title = "和風女僕", PromptSnippet = "purple kimono, sandals", FacetIds = new[] { "clothing.upper", "clothing.footwear" } });
        Assert.Single(s.Adoptions);
        var e = s.Ledger.Get(41720)!;
        Assert.Equal("clothing", Assert.Single(e.Hits).Dimension);
        Assert.Equal("採用", Assert.Single(e.OfferedAs).Label);
        Assert.Equal(3, e.OfferedAs[0].TurnIndex);
    }

    [Fact]
    public void Snapshot_restore_reverts_everything_including_history_and_ledger()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddUserMessage("hi");
        s.RecordDiscuss();
        var snap = s.Snapshot();

        s.RecordAsk(); s.RecordDiscuss(); s.AutoFill = true; s.TurnIndex = 5;
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["pose.gaze"] = FacetState.Covered }, Catalog);
        s.ApplyProfile("landscape", Catalog);
        s.FacetNotes["clothing.footwear"] = "使用者委託此項";
        s.ChatHistory.AddAssistantMessage("x"); s.ChatHistory.AddUserMessage("y");
        s.Ledger.Record(new LedgerEntry { Id = 7, Title = "t", PromptSnippet = "a", FacetIds = Array.Empty<string>() }, new LedgerHit("style", 0.1, true));
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["scene.season"] = FacetState.Covered }, Catalog, new Dictionary<string, string> { ["scene.season"] = "autumn" });
        s.RecordAdoption(SomeAdoption(), new LedgerEntry { Id = 41720, Title = "t", PromptSnippet = "p", FacetIds = Array.Empty<string>() });
        s.RecordFinalize(new FinalPrompt("p", "n", "t", "i"));

        s.Restore(snap);
        Assert.Equal(0, s.AskCount); Assert.Equal(1, s.DiscussStreak); Assert.False(s.AutoFill);
        Assert.Equal(SessionStatus.Collecting, s.Status); Assert.Null(s.LastFinal);
        Assert.Equal("portrait", s.Profile); Assert.Equal(0, s.TurnIndex);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.False(s.FacetStates.ContainsKey("scene.season"));
        Assert.Single(s.ChatHistory);
        Assert.False(s.Ledger.Contains(7));
        Assert.Empty(s.FacetNotes);
        Assert.Empty(s.FacetTags);
        Assert.Empty(s.Adoptions); Assert.False(s.Ledger.Contains(41720));
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
