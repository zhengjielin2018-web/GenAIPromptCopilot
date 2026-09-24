using System.Text.Json.Serialization;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Streaming;

/// <summary>SSE 事件（主規格 §10.2 + 多輪 §5.1）。Type 是 SSE 的 event: 名稱，其餘欄位序列化成 data。</summary>
// PropertyOrder：STJ 先列子型別自己的屬性、繼承來的擺最後；type 是判別欄位，用 -1 把它拉回第一個。
public abstract record AgentEvent([property: JsonPropertyOrder(-1)] string Type);

public sealed record SessionEvent(string SessionId, int TurnIndex, string Status) : AgentEvent("session");
public sealed record ToolCallEvent(string CallId, string Name, string ArgsSummary) : AgentEvent("tool_call");
public sealed record PresetRef(long Id, string Title, string? ImageUrl);
public sealed record ToolResultEvent(string CallId, string Name, string Summary, IReadOnlyList<PresetRef>? Presets) : AgentEvent("tool_result");
public sealed record DimensionsEvent(string? Profile, IReadOnlyDictionary<string, string> FacetStates) : AgentEvent("dimensions");
public sealed record FinalEvent(
    string Kind,
    string? Preamble = null, IReadOnlyList<AskItem>? Asks = null,
    string? Message = null, IReadOnlyList<OptionItem>? Options = null,
    string? Positive = null, string? Negative = null, string? Tips = null,
    string? IntentSummary = null,
    IReadOnlyList<TagSource>? PositiveSources = null, IReadOnlyList<TagSource>? NegativeSources = null) : AgentEvent("final");
public sealed record BlockedEvent(string Reason, string Message) : AgentEvent("blocked");
public sealed record ErrorEvent(string Code, string Message) : AgentEvent("error");
