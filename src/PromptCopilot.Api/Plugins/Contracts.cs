using System.Text.Json.Serialization;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

// JsonPropertyName 固定 camelCase：SK 據此產 function declaration schema 給 Gemini，也據此反序列化 Gemini 回來的參數；SSE 序列化同名。
/// <summary>攤給使用者的一個方向。PresetId 可為 null：LLM 可提知識庫沒有的方向，由輸出過濾兜住。</summary>
public sealed record OptionItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tags")] string Tags,
    [property: JsonPropertyName("presetId")] long? PresetId);

public sealed record AskItem(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("missingFacetIds")] IReadOnlyList<string> MissingFacetIds,
    [property: JsonPropertyName("options")] IReadOnlyList<OptionItem> Options);

/// <summary>SearchPresets 的一個項目。同一維度可重複出現（使用者沒講的維度給兩個對比方向）。</summary>
public sealed record SearchQuery(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("query")] string Query);

/// <summary>一輪的結果。終止型 tool 成功時由 plugin 設到 TurnContext；迴圈看到非 null 就停。</summary>
public abstract record TurnOutcome;
public sealed record AskOutcome(string Preamble, IReadOnlyList<AskItem> Asks) : TurnOutcome;
public sealed record MessageOutcome(string Message, IReadOnlyList<OptionItem> Options) : TurnOutcome;
public sealed record FinalizedOutcome(FinalPrompt Final) : TurnOutcome;
public sealed record SaveConsentOutcome : TurnOutcome;
public sealed record BudgetExhaustedOutcome : TurnOutcome;
public sealed record BlockedOutcome(string Reason) : TurnOutcome;

public sealed record CleanResult<T>(IReadOnlyList<T> Kept, IReadOnlyList<string> Rejected);
