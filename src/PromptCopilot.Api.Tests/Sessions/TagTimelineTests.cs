using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Sessions;

/// <summary>檢索時機設計 §5.1：tag 第一次出現的先後，與定稿 rag tag 的借來／碰巧對上。</summary>
public class TagTimelineTests
{
    private static LedgerEntry Entry(long id, string snippet) =>
        new() { Id = id, Title = $"t{id}", PromptSnippet = snippet, FacetIds = new[] { "scene.location" } };

    private static readonly LedgerHit AnyHit = new("scene", 0.2, true);

    [Fact]
    public void First_sighting_wins_and_matching_is_normalized()
    {
        var t = new TagTimeline();
        t.SeeModel("City_Street, (rain:1.2)");
        t.SeeSnippet("city street, rain, night");
        t.SeeModel("night");
        Assert.Equal(TagTimeline.Source.Model, t.FirstSeen("city street"));
        Assert.Equal(TagTimeline.Source.Model, t.FirstSeen("RAIN"));
        Assert.Equal(TagTimeline.Source.Snippet, t.FirstSeen("night"));         // 片段先到；模型後寫不覆蓋
        Assert.Null(t.FirstSeen("lamppost"));
    }

    [Fact]
    public void Blank_and_null_input_records_nothing()
    {
        var t = new TagTimeline();
        t.SeeModel(null); t.SeeModel(" , ,"); t.SeeSnippet("");
        Assert.Null(t.FirstSeen(""));
        Assert.Null(t.FirstSeen(" "));
    }

    [Fact]
    public void Ledger_records_snippet_tags_and_clone_is_independent()
    {
        var l = new PresetLedger();
        l.Record(Entry(4581, "night, cafe, neon lights, streetspace"), AnyHit);
        var c = l.Clone();
        l.Timeline.SeeModel("long coat");
        Assert.Equal(TagTimeline.Source.Snippet, c.Timeline.FirstSeen("streetspace"));
        Assert.Null(c.Timeline.FirstSeen("long coat"));
        Assert.Equal(TagTimeline.Source.Model, l.Timeline.FirstSeen("long coat"));
    }

    [Fact]
    public void Session_restore_rolls_the_timeline_back()
    {
        var s = new Session("s");
        var snap = s.Snapshot();
        s.Ledger.Timeline.SeeModel("hime cut");
        s.Restore(snap);
        Assert.Null(s.Ledger.Timeline.FirstSeen("hime cut"));
    }

    /// <summary>設計 §5.1 的三個例子，加上「模型看過片段之後才寫同一個 tag」仍算借來（Review Focus 的第一次出現不覆蓋）。</summary>
    [Fact]
    public void Rag_tags_split_into_borrowed_and_echo()
    {
        var l = new PresetLedger();
        l.Timeline.SeeModel("city street");                                         // 第一輪 SetFacetStates 先寫
        l.Record(Entry(13876, "city street, night, lamppost"), AnyHit);
        l.Record(Entry(7140, "smile, mature female, long hair, black hair"), AnyHit);
        l.Record(Entry(4581, "night, cafe, neon lights, streetspace"), AnyHit);
        l.Timeline.SeeModel("streetspace, hime cut");                               // 定稿的 facetStates 才寫：片段已先到

        var sources = TagAttribution.Attribute("masterpiece, long black hair, streetspace, city street, hime cut", l, negative: false);
        var split = RagSplit.Classify(sources, l.Timeline);

        Assert.Equal(new[] { "streetspace" }, split.Borrowed);
        Assert.Equal(new[] { "long black hair", "city street" }, split.Echo);       // 只靠字尾對上；模型先寫
    }

    [Fact]
    public void Only_rag_tags_are_split()
    {
        var l = new PresetLedger();
        l.Record(Entry(1, "sandals"), AnyHit);
        var sources = new[]
        {
            new TagSource("sandals", TagAttribution.Adopted, new long[] { 1 }, "t1"),
            new TagSource("masterpiece", TagAttribution.Base, Array.Empty<long>(), null),
            new TagSource("1girl", TagAttribution.Llm, Array.Empty<long>(), null),
        };
        var split = RagSplit.Classify(sources, l.Timeline);
        Assert.Empty(split.Borrowed);
        Assert.Empty(split.Echo);
        Assert.Empty(RagSplit.Classify(null, l.Timeline).Echo);
    }
}
