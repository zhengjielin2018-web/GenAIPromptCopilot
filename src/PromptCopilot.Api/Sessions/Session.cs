using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Sessions;

public enum FacetState { Covered, Missing, Waived, NotApplicable }
public enum SessionStatus { Collecting, Finalized }
/// <summary>PositiveSources／NegativeSources：定稿時由伺服器比對 ledger 標的 tag 來源（<see cref="TagAttribution"/>）。
/// 可為 null 只是為了讓舊的呼叫端不用改；讀取端一律 <c>?? Array.Empty</c>。
/// Reviewed=false：這份定稿是在關掉程式端審查（測試用開關）的那一輪產生的，輸出側沒檢過，不能存進共享庫。</summary>
public sealed record FinalPrompt(string Positive, string Negative, string Tips, string IntentSummary,
    IReadOnlyList<TagSource>? PositiveSources = null, IReadOnlyList<TagSource>? NegativeSources = null, bool Reviewed = true);

public sealed record SessionSnapshot(
    SessionStatus Status, string? Profile, int AskCount, int DiscussStreak, bool AutoFill,
    Dictionary<string, FacetState> FacetStates, Dictionary<string, string> FacetNotes, int HistoryCount, PresetLedger Ledger, FinalPrompt? LastFinal, int TurnIndex,
    Dictionary<string, string> FacetTags, List<Adoption> Adoptions);

public sealed class Session
{
    public string Id { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public SessionStatus Status { get; private set; } = SessionStatus.Collecting;
    public string? Profile { get; private set; }
    public int AskCount { get; private set; }
    public int DiscussStreak { get; private set; }
    public bool AutoFill { get; set; }
    public Dictionary<string, FacetState> FacetStates { get; private set; } = new();
    public ChatHistory ChatHistory { get; } = new();
    public PresetLedger Ledger { get; private set; } = new();
    /// <summary>facet 級的備註，例如「使用者委託此項」。跨輪保留，隨 profile 重設。</summary>
    public Dictionary<string, string> FacetNotes { get; private set; } = new();
    /// <summary>模型標 covered 時給的英文 tag（設計 §5.5），整套組合推薦的錨。只有 covered 的 facet 有；狀態改成別的就移除；隨 profile 重設。</summary>
    public Dictionary<string, string> FacetTags { get; private set; } = new();
    /// <summary>採用過的組合，依採用先後（設計 §6.3）。定稿時 TagAttribution 用它標 adopted。</summary>
    public List<Adoption> Adoptions { get; private set; } = new();
    public FinalPrompt? LastFinal { get; private set; }
    public int TurnIndex { get; set; }
    /// <summary>建立時定死：off 是量測用的對照組（計畫 §4.1）。中途不能切，所以不進 Snapshot／Restore。</summary>
    public bool RetrievalEnabled { get; }
    public string RetrievalMode => RetrievalEnabled ? "on" : "off";
    public SemaphoreSlim Lock { get; } = new(1, 1);

    /// <summary>推薦組法（2026-09-30 設計 §4.4）：最新一張定稿卡的輪次，換一批只接受它；出了新的追問卡就清掉。
    /// 下面這幾個都不進 Snapshot／Restore：推薦在一輪成立之後才產生，被攔截的輪走不到這裡。</summary>
    public int? LatestSlateTurn { get; private set; }
    /// <summary>最近一次定稿的 positive tag（已排除基礎詞），換一批時重算錨用。</summary>
    public IReadOnlyList<string> LastFinalTags { get; private set; } = Array.Empty<string>();
    private readonly Dictionary<string, Dictionary<string, int>> _seenSets = new();
    private readonly Dictionary<string, int> _slateBatches = new();
    private static readonly IReadOnlyDictionary<string, int> NoneSeen = new Dictionary<string, int>();

    public Session(string id, bool retrievalEnabled = true) { Id = id; RetrievalEnabled = retrievalEnabled; }

    public void ApplyProfile(string profile, FacetCatalog catalog)
    {
        Profile = profile;
        FacetStates = catalog.IdsForProfile(profile).ToDictionary(id => id, _ => FacetState.Missing);
        FacetNotes = new();
        FacetTags = new();
    }

    /// <summary>只收 profile 適用的 facet；其餘丟掉（不信 LLM 自述）。tags 只在該 facet 這次標 covered 時存；非 covered 一律移除舊的。</summary>
    public void ApplyFacetStates(IReadOnlyDictionary<string, FacetState> updates, FacetCatalog catalog, IReadOnlyDictionary<string, string>? tags = null)
    {
        if (Profile is null) return;
        var applicable = catalog.IdsForProfile(Profile);
        foreach (var (id, state) in updates)
        {
            if (!applicable.Contains(id)) continue;
            FacetStates[id] = state;
            if (state != FacetState.Covered) { FacetTags.Remove(id); continue; }
            if (tags is not null && tags.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t)) FacetTags[id] = t.Trim();
        }
    }

    /// <summary>grounded 由 covered 推導，不由 LLM 回報。</summary>
    public IReadOnlySet<string> GroundedDimensions(FacetCatalog catalog) =>
        FacetStates.Where(kv => kv.Value == FacetState.Covered).Select(kv => catalog.DimensionOf(kv.Key)).ToHashSet();

    public void RecordAsk() => AskCount++;
    public void RecordDiscuss() { if (Status == SessionStatus.Collecting) DiscussStreak++; }
    public void RecordFinalize(FinalPrompt final) { LastFinal = final; Status = SessionStatus.Finalized; DiscussStreak = 0; }

    /// <summary>採用寫進 ledger：定稿 chip 能開抽屜、system prompt 的 offered 區段會列它。dist 0、grounded true：
    /// 使用者親手選的，當然可借入。</summary>
    public void RecordAdoption(Adoption a, LedgerEntry preset)
    {
        Adoptions.Add(a);
        Ledger.Record(preset, new LedgerHit(a.Dimension, 0, true));
        Ledger.MarkOffered(preset.Id, new OfferedRef(a.TurnIndex, a.Dimension, "採用"));
    }

    /// <summary>新的定稿卡：批次歸零；看過次數保留，下一張卡才會把看過的往後延（設計 §3.1）。</summary>
    public void BeginSlate(int turnIndex, IReadOnlyList<string> finalTags)
    {
        LatestSlateTurn = turnIndex;
        LastFinalTags = finalTags.ToList();
        _slateBatches.Clear();
    }

    public void EndSlate() => LatestSlateTurn = null;

    /// <summary>該維度的「tag 集合 key → 看過次數」。</summary>
    public IReadOnlyDictionary<string, int> SeenFor(string dimension) => _seenSets.TryGetValue(dimension, out var d) ? d : NoneSeen;

    /// <summary>該維度目前是第幾批；這張卡還沒出過就是 0。</summary>
    public int SlateBatch(string dimension) => _slateBatches.GetValueOrDefault(dimension);

    public void RecordSlate(string dimension, int batch, IEnumerable<string> keys)
    {
        _slateBatches[dimension] = batch;
        if (!_seenSets.TryGetValue(dimension, out var d)) _seenSets[dimension] = d = new Dictionary<string, int>();
        foreach (var k in keys) d[k] = d.GetValueOrDefault(k) + 1;
    }

    public SessionSnapshot Snapshot() => new(Status, Profile, AskCount, DiscussStreak, AutoFill,
        new Dictionary<string, FacetState>(FacetStates), new Dictionary<string, string>(FacetNotes), ChatHistory.Count, Ledger.Clone(), LastFinal, TurnIndex,
        new Dictionary<string, string>(FacetTags), new List<Adoption>(Adoptions));

    public void Restore(SessionSnapshot s)
    {
        Status = s.Status; Profile = s.Profile; AskCount = s.AskCount; DiscussStreak = s.DiscussStreak; AutoFill = s.AutoFill;
        FacetStates = new Dictionary<string, FacetState>(s.FacetStates);
        FacetNotes = new Dictionary<string, string>(s.FacetNotes);
        FacetTags = new Dictionary<string, string>(s.FacetTags);
        Adoptions = new List<Adoption>(s.Adoptions);
        while (ChatHistory.Count > s.HistoryCount) ChatHistory.RemoveAt(ChatHistory.Count - 1);
        Ledger = s.Ledger.Clone(); LastFinal = s.LastFinal; TurnIndex = s.TurnIndex;
    }
}
