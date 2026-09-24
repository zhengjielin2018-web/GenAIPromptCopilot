namespace PromptCopilot.Api.Sessions;

public sealed record LedgerHit(string Dimension, double Dist, bool Grounded);
public sealed record OfferedRef(int TurnIndex, string? Dimension, string Label);

public sealed class LedgerEntry
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string PromptSnippet { get; init; }
    public string? NegativeSnippet { get; init; }
    public required IReadOnlyList<string> FacetIds { get; init; }
    public string? ImageUrl { get; init; }
    /// <summary>資料來源識別（<c>civitai:…</c>／<c>kisegae:…</c>）；定稿 tag 來源帶給前端標出處。</summary>
    public string? SourceRef { get; init; }
    public List<LedgerHit> Hits { get; } = new();
    public List<OfferedRef> OfferedAs { get; } = new();

    public LedgerEntry Clone()
    {
        var c = new LedgerEntry { Id = Id, Title = Title, PromptSnippet = PromptSnippet, NegativeSnippet = NegativeSnippet, FacetIds = FacetIds, ImageUrl = ImageUrl, SourceRef = SourceRef };
        c.Hits.AddRange(Hits); c.OfferedAs.AddRange(OfferedAs);
        return c;
    }
}

/// <summary>session 內看過的 preset。去重歸屬（主規格 §9）與「攤給使用者看過的選項」（多輪 §6.1）共用這一本。</summary>
public sealed class PresetLedger
{
    private readonly Dictionary<long, LedgerEntry> _entries = new();

    public void Record(LedgerEntry seed, LedgerHit hit)
    {
        if (!_entries.TryGetValue(seed.Id, out var e)) { e = seed; _entries[seed.Id] = e; }
        e.Hits.Add(hit);
    }

    /// <summary>依寫進 ledger 的先後（Dictionary 不刪除時列舉順序即插入順序）。</summary>
    public IEnumerable<LedgerEntry> Entries => _entries.Values;

    public bool Contains(long id) => _entries.ContainsKey(id);
    public LedgerEntry? Get(long id) => _entries.GetValueOrDefault(id);

    public void MarkOffered(long id, OfferedRef r)
    {
        if (_entries.TryGetValue(id, out var e)) e.OfferedAs.Add(r);
    }

    /// <summary>OfferedAs 非空者，依最近一次 offered 的 turn 由新到舊，取前 limit。</summary>
    public IReadOnlyList<LedgerEntry> RecentlyOffered(int limit) =>
        _entries.Values.Where(e => e.OfferedAs.Count > 0)
            .OrderByDescending(e => e.OfferedAs.Max(o => o.TurnIndex)).ThenBy(e => e.Id)
            .Take(limit).ToList();

    public PresetLedger Clone()
    {
        var c = new PresetLedger();
        foreach (var (k, v) in _entries) c._entries[k] = v.Clone();
        return c;
    }
}
