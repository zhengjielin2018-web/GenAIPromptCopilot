namespace PromptCopilot.Api.Streaming;

/// <summary>整套組合推薦事件（設計 §5.4）。跟在 final 與 dimensions 之後；模型看不到這些。
/// Facets 列該維度對本 profile 的全部 facet（yaml 順序），State 是本輪結束時的四態，Tags 取自 facet_tags（沒有就空）。</summary>
public sealed record RecommendedFacet(string FacetId, string Label, string State, IReadOnlyList<string> Tags);
public sealed record RecommendedSet(long PresetId, string Title, string? ImageUrl, string? SourceRef, double Dist, IReadOnlyList<RecommendedFacet> Facets);
/// <summary>Anchored：候選是先用「含使用者講的元素」過濾的；AnchorTags 是實際命中的錨。false 時是純向量排序的「最接近你描述的組合」。</summary>
public sealed record RecommendedDimension(string Dimension, string Label, bool Anchored, IReadOnlyList<string> AnchorTags, IReadOnlyList<RecommendedSet> Sets);
public sealed record RecommendationsEvent(int TurnIndex, IReadOnlyList<RecommendedDimension> Dimensions) : AgentEvent("recommendations");
