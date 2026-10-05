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

/// <summary>SearchPresets 的一個項目。<c>facetId</c> 與 <c>dimension</c> 至少給一個，facetId 優先：
/// facetId 把候選池縮到單一 facet（使用者講到的每個 facet 各一項），dimension 查整個維度（沒講的維度給兩個對比方向，可重複）。</summary>
public sealed record SearchQuery
{
    public SearchQuery(string? dimension, string query, string? facetId = null, string? tags = null)
    {
        Dimension = dimension; Query = query; FacetId = facetId; Tags = tags;
    }

    // 反序列化與 SK 產 schema 都走這個建構子。SK 把「沒有預設值的建構子參數」一律列進 required（不看 nullable），
    // 寫成多參數的 positional record 時 dimension 會變成必填；這裡只讓 query 當建構子參數，其餘是可省略的 init 屬性。
    [JsonConstructor]
    private SearchQuery(string query) => Query = query;

    [JsonPropertyName("dimension")] public string? Dimension { get; init; }
    [JsonPropertyName("query")] public string Query { get; init; }
    [JsonPropertyName("facetId")] public string? FacetId { get; init; }
    /// <summary>facet 項目專用（2026-09-29，facet 向量設計 §5.1）：使用者對這個 facet 的描述翻成的英文 SD tag，逗號分隔，寫法同 SetFacetStates 的 tags。
    /// 查詢句會組成「原話（tags）」；維度項目帶了也忽略。伺服器不拿它更新 session.FacetTags。</summary>
    [JsonPropertyName("tags")] public string? Tags { get; init; }
}

/// <summary>一輪的結果。終止型 tool 成功時由 plugin 設到 TurnContext；迴圈看到非 null 就停。</summary>
public abstract record TurnOutcome;
public sealed record AskOutcome(string Preamble, IReadOnlyList<AskItem> Asks) : TurnOutcome;
public sealed record MessageOutcome(string Message, IReadOnlyList<OptionItem> Options) : TurnOutcome;
/// <summary>確認卡（先確認再動手設計 §3.2）：Choices 沒有歧義時是空的。</summary>
public sealed record ConfirmOutcome(string Message, IReadOnlyList<string> Choices) : TurnOutcome;
public sealed record FinalizedOutcome(FinalPrompt Final) : TurnOutcome;
public sealed record SaveConsentOutcome : TurnOutcome;
public sealed record BudgetExhaustedOutcome : TurnOutcome;
public sealed record BlockedOutcome(string Reason) : TurnOutcome;

public sealed record CleanResult<T>(IReadOnlyList<T> Kept, IReadOnlyList<string> Rejected);
