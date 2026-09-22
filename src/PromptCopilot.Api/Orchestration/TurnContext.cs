using System.Threading.Channels;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

/// <summary>一輪的可變狀態。plugin 與 filter 都拿同一個實例。</summary>
public sealed class TurnContext(Session session, int turnIndex, GuardResult guard, IReadOnlySet<string> tools, ChannelWriter<AgentEvent> events)
{
    public Session Session { get; } = session;
    public int TurnIndex { get; } = turnIndex;
    public GuardResult Guard { get; } = guard;
    public IReadOnlySet<string> Tools { get; } = tools;
    public TurnOutcome? Outcome { get; set; }
    public int ToolCalls { get; set; }
    /// <summary>清洗被拒的理由與其他值得記的事，最後寫進 audit。</summary>
    public List<string> Rejections { get; } = new();

    public void Emit(AgentEvent e) => events.TryWrite(e);

    public DimensionsEvent DimensionsSnapshot() =>
        new(Session.Profile, Session.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)));
}

public static class FacetStateParser
{
    public static bool TryParse(string s, out FacetState state)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "covered": state = FacetState.Covered; return true;
            case "missing": state = FacetState.Missing; return true;
            case "waived": state = FacetState.Waived; return true;
            case "notapplicable": state = FacetState.NotApplicable; return true;
            default: state = default; return false;
        }
    }

    public static string ToWire(FacetState s) => s switch
    {
        FacetState.Covered => "covered", FacetState.Missing => "missing", FacetState.Waived => "waived", _ => "notApplicable",
    };
}
