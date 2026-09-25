using System.Threading.Channels;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Plugins;

public class SessionPluginTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private static (SessionPlugin plugin, Session s, ChannelReader<AgentEvent> events) Make()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        var ch = Channel.CreateUnbounded<AgentEvent>();
        var turn = new TurnContext(s, 1, GuardResult.Ok(false), ToolNames.Always, ch.Writer);
        return (new SessionPlugin(turn, Catalog), s, ch.Reader);
    }

    /// <summary>設計 §5.5：模型標 covered 時附的英文 tag 存進 session，dimensions 事件帶給前端。</summary>
    [Fact]
    public void Apply_stores_tags_of_covered_facets_and_emits_them_in_dimensions()
    {
        var (p, s, events) = Make();
        var r = p.SetFacetStates(new[]
        {
            new FacetStateEntry("clothing.footwear", "covered", Tags: "sandals"),
            new FacetStateEntry("clothing.upper", "missing", Tags: "shirt"),
            new FacetStateEntry("pose.gaze", "covered"),
        });
        Assert.Equal("ok：套用 3 筆", r);
        Assert.Equal("sandals", s.FacetTags["clothing.footwear"]);
        Assert.False(s.FacetTags.ContainsKey("clothing.upper"));
        Assert.False(s.FacetTags.ContainsKey("pose.gaze"));
        Assert.True(events.TryRead(out var e));
        var d = Assert.IsType<DimensionsEvent>(e);
        Assert.Equal("sandals", d.FacetTags!["clothing.footwear"]);
        Assert.Equal("covered", d.FacetStates["pose.gaze"]);
    }

    [Fact]
    public void Apply_without_tags_leaves_existing_tags_alone_until_state_changes()
    {
        var (p, s, _) = Make();
        p.SetFacetStates(new[] { new FacetStateEntry("clothing.footwear", "covered", Tags: "sandals") });
        p.SetFacetStates(new[] { new FacetStateEntry("clothing.footwear", "covered") });     // 沒帶 tags：保留
        Assert.Equal("sandals", s.FacetTags["clothing.footwear"]);
        p.SetFacetStates(new[] { new FacetStateEntry("clothing.footwear", "missing") });
        Assert.Empty(s.FacetTags);
    }
}
