using System.Threading.Channels;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

/// <summary>一輪的可變狀態。plugin 與 filter 都拿同一個實例。</summary>
public sealed class TurnContext(Session session, int turnIndex, GuardResult guard, IReadOnlySet<string> tools, ChannelWriter<AgentEvent> events,
    IReadOnlyDictionary<string, FacetState>? turnStartFacetStates = null)
{
    public Session Session { get; } = session;
    public int TurnIndex { get; } = turnIndex;
    public GuardResult Guard { get; } = guard;
    public IReadOnlySet<string> Tools { get; } = tools;
    /// <summary>這一輪開始時的 facet 狀態（ExecuteAsync 取快照時複製一份）。定稿後的變更閘門要拿它比對：
    /// 比「現值」的話，先 SetFacetStates 改掉再用終止型 tool 回報同一組值就永遠相等（主規格 §4.5）。</summary>
    public IReadOnlyDictionary<string, FacetState> TurnStartFacetStates { get; } =
        turnStartFacetStates ?? new Dictionary<string, FacetState>(session.FacetStates);
    public TurnOutcome? Outcome { get; set; }
    /// <summary>AuditFilter 正在跑的那個 tool 的 call id；plugin 發 tool_result 時拿它對上 tool_call。</summary>
    public string? CurrentCallId { get; set; }
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
