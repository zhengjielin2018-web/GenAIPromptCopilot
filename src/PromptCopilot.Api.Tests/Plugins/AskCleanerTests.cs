using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Plugins;

public class AskCleanerTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private static Session Portrait(params (string id, FacetState s)[] states)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(states.ToDictionary(x => x.id, x => x.s), Catalog);
        return s;
    }
    private static OptionItem Opt(string l, long? id = null) => new(l, "tag", id);
    private static AskItem Ask(string dim, IEnumerable<string> missing, int nOptions = 2) =>
        new(dim, "q?", missing.ToList(), Enumerable.Range(0, nOptions).Select(i => Opt($"o{i}")).ToList());

    [Fact]
    public void Truncates_asks_to_max()
    {
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre" }), Ask("camera", new[] { "camera.shot" }),
            Ask("clothing", new[] { "clothing.upper" }), Ask("scene", new[] { "scene.location" }) }, Portrait(), Catalog, maxAsks: 3);
        Assert.Equal(new[] { "style", "camera", "clothing" }, r.Kept.Select(a => a.Dimension));
        Assert.Single(r.Rejected);
    }

    [Fact]
    public void Truncates_options_to_four_and_drops_ask_with_fewer_than_two()
    {
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre" }, nOptions: 6), Ask("camera", new[] { "camera.shot" }, nOptions: 1) }, Portrait(), Catalog, 3);
        Assert.Single(r.Kept);
        Assert.Equal(4, r.Kept[0].Options.Count);
    }

    [Fact]
    public void Filters_facets_that_are_not_missing_or_not_in_dimension()
    {
        var s = Portrait(("style.genre", FacetState.Covered));
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre", "style.palette", "camera.shot", "style.nope" }) }, s, Catalog, 3);
        Assert.Equal(new[] { "style.palette" }, r.Kept[0].MissingFacetIds);
        Assert.Equal(3, r.Rejected.Count);
    }

    [Fact]
    public void Drops_ask_whose_facets_are_all_filtered()
    {
        var s = Portrait(("style.genre", FacetState.Waived));
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre" }) }, s, Catalog, 3);
        Assert.Empty(r.Kept);
    }

    [Fact]
    public void Options_unknown_presetId_downgrades_to_null_and_is_capped()
    {
        var ledger = new PresetLedger();
        ledger.Record(new LedgerEntry { Id = 5, Title = "t", PromptSnippet = "p", FacetIds = Array.Empty<string>() }, new LedgerHit("style", 0.2, true));
        var r = AskCleaner.CleanOptions(new[] { Opt("a", 5), Opt("b", 99), Opt("c"), Opt("d"), Opt("e") }, ledger, maxOptions: 4);
        Assert.Equal(4, r.Kept.Count);
        Assert.Equal(5, r.Kept[0].PresetId);
        Assert.Null(r.Kept[1].PresetId);
        Assert.Contains(r.Rejected, m => m.Contains("99"));
    }
}
