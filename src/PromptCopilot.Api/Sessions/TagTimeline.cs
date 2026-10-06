namespace PromptCopilot.Api.Sessions;

/// <summary>檢索時機設計 §5.1：每個 tag（<see cref="TagAttribution.Normalize"/> 之後）第一次出現時，是模型寫的還是知識庫片段寫的。
/// 第一次記下的就定了，之後不覆蓋。只給報表分「借來／碰巧對上」用，不改定稿 tag 的來源分類。
/// 由 <see cref="PresetLedger"/> 擁有：ledger 進快照（Clone），這本跟著回滾。</summary>
public sealed class TagTimeline
{
    public enum Source { Model, Snippet }

    private readonly Dictionary<string, Source> _first = new();

    /// <summary>模型寫的 tag，逗號分隔：facet 狀態的 tags、檢索 facet 項目的 tags、沒帶 presetId 的選項 tags。</summary>
    public void SeeModel(string? tags) => See(tags, Source.Model);

    /// <summary>片段的 positive，逗號分隔：寫進 ledger 時記。</summary>
    public void SeeSnippet(string? snippet) => See(snippet, Source.Snippet);

    /// <summary>沒出現過回 null。</summary>
    public Source? FirstSeen(string tag) =>
        _first.TryGetValue(TagAttribution.Normalize(tag), out var s) ? s : null;

    private void See(string? text, Source source)
    {
        foreach (var key in TagAttribution.Split(text).Select(TagAttribution.Normalize))
            if (key.Length > 0) _first.TryAdd(key, source);
    }

    public TagTimeline Clone()
    {
        var c = new TagTimeline();
        foreach (var (k, v) in _first) c._first[k] = v;
        return c;
    }
}

/// <summary>定稿 positive 裡 origin 為 rag 的 tag（原文、依 prompt 順序）分成借來與碰巧對上。</summary>
public sealed record RagSplitResult(IReadOnlyList<string> Borrowed, IReadOnlyList<string> Echo);

public static class RagSplit
{
    /// <summary>借來：timeline 裡這個 tag（整段相等）第一次出現是片段。其餘都算碰巧對上：模型先寫的，
    /// 或 timeline 根本沒有、只靠字尾規則對上片段的（<c>long black hair</c> ↔ 片段的 <c>black hair</c>）。</summary>
    public static RagSplitResult Classify(IReadOnlyList<TagSource>? sources, TagTimeline timeline)
    {
        var borrowed = new List<string>();
        var echo = new List<string>();
        foreach (var s in sources ?? Array.Empty<TagSource>())
        {
            if (s.Origin != TagAttribution.Rag) continue;
            (timeline.FirstSeen(s.Tag) == TagTimeline.Source.Snippet ? borrowed : echo).Add(s.Tag);
        }
        return new RagSplitResult(borrowed, echo);
    }
}
