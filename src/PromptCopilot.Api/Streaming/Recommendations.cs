namespace PromptCopilot.Api.Streaming;

/// <summary>整套組合推薦事件（設計 §5.4）。跟在 final 與 dimensions 之後；模型看不到這些。
/// Facets 列該維度對本 profile 的全部 facet（yaml 順序），State 是本輪結束時的四態，Tags 取自 facet_tags（沒有就空）。</summary>
public sealed record RecommendedFacet(string FacetId, string Label, string State, IReadOnlyList<string> Tags);
/// <summary>Reason／AnchorTags／Rank／Prob（2026-09-30 推薦組法設計 §5.1）：只有定稿卡有；追問卡為 null，SSE 的 WhenWritingNull 會省略。
/// Reason：anchored／similar／query／explore；Rank：相關位是原名次、探索位是差異排名；Prob：被抽中的機率，相關位第 1 位記 1。</summary>
public sealed record RecommendedSet(long PresetId, string Title, string? ImageUrl, string? SourceRef, double Dist, IReadOnlyList<RecommendedFacet> Facets,
    string? Reason = null, IReadOnlyList<string>? AnchorTags = null, int? Rank = null, double? Prob = null);
/// <summary>Anchored：候選是先用「含使用者講的元素」字面過濾的；Similar（2026-09-29）：字面抓不到、改用 facet 向量離錨夠近的（facet 向量設計 §6）；兩者不會同時為 true。
/// AnchorTags 是實際命中（anchored）或有貢獻（similar）的錨。兩者都 false 時是純向量排序的「最接近你描述的組合」。
/// Batch（2026-09-30）：定稿卡的第幾批，定稿卡上那批是 1、換一批依序 2、3……；
/// 定稿卡這一排的 Anchored／Similar 固定 false、AnchorTags 為空，理由看每一套的 Reason。追問卡為 null。</summary>
public sealed record RecommendedDimension(string Dimension, string Label, bool Anchored, IReadOnlyList<string> AnchorTags, IReadOnlyList<RecommendedSet> Sets,
    bool Similar = false, int? Batch = null);
public sealed record RecommendationsEvent(int TurnIndex, IReadOnlyList<RecommendedDimension> Dimensions) : AgentEvent("recommendations");
