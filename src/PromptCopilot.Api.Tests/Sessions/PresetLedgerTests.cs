using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Sessions;

public class PresetLedgerTests
{
    private static LedgerEntry Seed(long id, string? title = null) => new()
    {
        Id = id, Title = title ?? $"t{id}", PromptSnippet = "a, b", NegativeSnippet = null, FacetIds = new[] { "style.genre" }
    };

    [Fact]
    public void Record_appends_hits_and_never_overwrites_existing()
    {
        var l = new PresetLedger();
        l.Record(Seed(1), new LedgerHit("style", 0.2, true));
        l.Record(Seed(1, "changed"), new LedgerHit("scene", 0.3, false));
        var e = l.Get(1)!;
        Assert.Equal("t1", e.Title);
        Assert.Equal(2, e.Hits.Count);
    }

    [Fact]
    public void RecentlyOffered_returns_only_offered_sorted_by_latest_turn_and_capped()
    {
        var l = new PresetLedger();
        for (long i = 1; i <= 5; i++) l.Record(Seed(i), new LedgerHit("style", 0.2, true));
        l.MarkOffered(2, new OfferedRef(1, "style", "A"));
        l.MarkOffered(4, new OfferedRef(3, "camera", "B"));
        l.MarkOffered(5, new OfferedRef(2, "camera", "C"));
        var recent = l.RecentlyOffered(limit: 2);
        Assert.Equal(new long[] { 4, 5 }, recent.Select(e => e.Id));
    }

    [Fact]
    public void MarkOffered_ignores_unknown_ids()
    {
        var l = new PresetLedger();
        l.MarkOffered(99, new OfferedRef(1, null, "x"));
        Assert.Empty(l.RecentlyOffered(10));
    }

    [Fact]
    public void Clone_is_deep_for_hits_and_offered()
    {
        var l = new PresetLedger();
        l.Record(Seed(1), new LedgerHit("style", 0.2, true));
        var c = l.Clone();
        c.Record(Seed(1), new LedgerHit("scene", 0.1, false));
        c.MarkOffered(1, new OfferedRef(1, "style", "A"));
        Assert.Single(l.Get(1)!.Hits);
        Assert.Empty(l.Get(1)!.OfferedAs);
    }
}
