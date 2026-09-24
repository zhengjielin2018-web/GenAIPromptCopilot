using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Sessions;

public class TagAttributionTests
{
    /// <summary>依給的順序寫進 ledger：插入順序就是 PresetIds 的順序。</summary>
    private static PresetLedger Ledger(params (long id, string title, string positive, string? negative)[] entries)
    {
        var l = new PresetLedger();
        foreach (var (id, title, positive, negative) in entries)
            l.Record(new LedgerEntry { Id = id, Title = title, PromptSnippet = positive, NegativeSnippet = negative, FacetIds = new[] { "style.genre" } },
                new LedgerHit("style", 0.2, true));
        return l;
    }

    private static TagSource Only(IReadOnlyList<TagSource> xs, string tag) => Assert.Single(xs, x => x.Tag == tag);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,, ")]
    public void Empty_prompt_yields_no_sources(string prompt)
    {
        Assert.Empty(TagAttribution.Attribute(prompt, Ledger((1, "a", "silver hair", null)), negative: false));
    }

    [Fact]
    public void Tags_come_back_in_prompt_order_with_their_original_text()
    {
        var r = TagAttribution.Attribute("  (Silver_Hair:1.2) ,1girl,  neon   lights ", Ledger((1, "霓虹", "silver hair, neon lights", null)), negative: false);

        Assert.Equal(new[] { "(Silver_Hair:1.2)", "1girl", "neon   lights" }, r.Select(x => x.Tag));
        Assert.Equal(new[] { "rag", "llm", "rag" }, r.Select(x => x.Origin));
    }

    [Fact]
    public void Matching_ignores_case_and_repeated_whitespace()
    {
        var r = TagAttribution.Attribute("silver hair, NEON   lights", Ledger((7, "霓虹雨夜", "Silver  Hair, neon Lights", null)), negative: false);

        Assert.Equal(new[] { "rag", "rag" }, r.Select(x => x.Origin));
        Assert.All(r, x => Assert.Equal(new long[] { 7 }, x.PresetIds));
        Assert.All(r, x => Assert.Equal("霓虹雨夜", x.PresetTitle));
    }

    [Theory]
    [InlineData("(neon lights:1.2)")]
    [InlineData("((neon lights))")]
    [InlineData("((neon lights:0.8))")]
    [InlineData("( neon lights )")]
    public void Weight_syntax_and_wrapping_parentheses_are_stripped_for_matching(string tag)
    {
        var s = Assert.Single(TagAttribution.Attribute(tag, Ledger((3, "霓虹", "rain, neon lights", null)), negative: false));
        Assert.Equal("rag", s.Origin);
        Assert.Equal(tag.Trim(), s.Tag);
    }

    [Fact]
    public void Weighted_tags_inside_a_snippet_are_normalized_too()
    {
        var s = Assert.Single(TagAttribution.Attribute("rain", Ledger((3, "雨", "(rain:1.3), night", null)), negative: false));
        Assert.Equal("rag", s.Origin);
    }

    [Fact]
    public void Underscore_and_space_are_the_same()
    {
        var r = TagAttribution.Attribute("white hair, long_hair", Ledger((2, "髮", "white_hair, long hair", null)), negative: false);
        Assert.Equal(new[] { "rag", "rag" }, r.Select(x => x.Origin));
    }

    [Fact]
    public void A_tag_found_in_two_snippets_lists_both_in_ledger_order_and_takes_the_first_title()
    {
        var ledger = Ledger((9, "雨夜街頭", "neon lights, rain", null), (3, "賽博龐克", "cyberpunk, neon lights", null));

        var s = Only(TagAttribution.Attribute("neon lights", ledger, negative: false), "neon lights");

        Assert.Equal("rag", s.Origin);
        Assert.Equal(new long[] { 9, 3 }, s.PresetIds);
        Assert.Equal("雨夜街頭", s.PresetTitle);
    }

    [Fact]
    public void Unmatched_tags_are_llm_with_no_preset()
    {
        var s = Assert.Single(TagAttribution.Attribute("1girl", Ledger((1, "a", "silver hair", null)), negative: false));
        Assert.Equal("llm", s.Origin);
        Assert.Empty(s.PresetIds);
        Assert.Null(s.PresetTitle);
    }

    [Fact]
    public void Negative_prompt_matches_negative_snippets_only_and_skips_entries_without_one()
    {
        var ledger = Ledger((1, "有負向", "blurry", "extra fingers"), (2, "沒負向", "extra fingers", null));

        var r = TagAttribution.Attribute("extra fingers, blurry", ledger, negative: true);

        var fingers = Only(r, "extra fingers");
        Assert.Equal("rag", fingers.Origin);
        Assert.Equal(new long[] { 1 }, fingers.PresetIds);       // 2 只在正向片段裡有
        Assert.Equal("llm", Only(r, "blurry").Origin);           // 1 的正向片段不算
    }

    [Fact]
    public void Base_quality_words_win_over_a_snippet_that_also_has_them()
    {
        var ledger = Ledger((1, "畫質", "masterpiece, best quality, silver hair", "lowres, worst quality"));

        var pos = TagAttribution.Attribute("(masterpiece:1.2), Best Quality, highly_detailed, silver hair", ledger, negative: false);
        Assert.Equal(new[] { "base", "base", "base", "rag" }, pos.Select(x => x.Origin));
        Assert.Empty(pos[0].PresetIds);
        Assert.Null(pos[0].PresetTitle);

        var neg = TagAttribution.Attribute("lowres, bad anatomy, worst quality, masterpiece", ledger, negative: true);
        Assert.Equal(new[] { "base", "base", "base", "llm" }, neg.Select(x => x.Origin));   // 正向的基礎詞在負向不算
    }

    [Fact]
    public void Negative_base_words_are_not_base_in_the_positive_prompt()
    {
        var s = Assert.Single(TagAttribution.Attribute("lowres", new PresetLedger(), negative: false));
        Assert.Equal("llm", s.Origin);
    }
}
