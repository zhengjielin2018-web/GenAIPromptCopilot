namespace PromptCopilot.Api.Streaming;

/// <summary>tool_result 的 detail（子專案 RAG 量測設計 §3.5）：給前端「顯示檢索細節」與之後的 eval 腳本用。
/// 與回給模型的 JSON 同一份資料，不放 snippet 本文（抽屜已有）。audit 截 200 字，不能當來源，所以走事件。</summary>
public sealed record SearchPresetsDetail(IReadOnlyList<SearchPresetsItem> Items);

/// <summary>一個查詢項目。驗證失敗的項目 Error 有值、Hits 空、PoolSize 與 K 為 0、Label 是模型送的原始 facetId 或 dimension。</summary>
public sealed record SearchPresetsItem(
    string Dimension, string? FacetId, string Label, string Query,
    bool Grounded, long PoolSize, int K, string? Error,
    IReadOnlyList<SearchPresetsHit> Hits);

/// <summary>Usable 對應回給模型的「可借入提示詞」（true）／「僅供建議」（false）；Facets 是 facetId → wire 字串。</summary>
public sealed record SearchPresetsHit(
    long Id, string Title, string Band, double Dist, bool Usable,
    IReadOnlyDictionary<string, string> Facets);

public sealed record SearchSimilarDetail(IReadOnlyList<SearchSimilarHit> Hits);

/// <summary>Intent 只取前 40 字。</summary>
public sealed record SearchSimilarHit(string Intent, string Profile, double Dist);
