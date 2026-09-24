using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Sessions;

public enum FacetState { Covered, Missing, Waived, NotApplicable }
public enum SessionStatus { Collecting, Finalized }
/// <summary>PositiveSources／NegativeSources：定稿時由伺服器比對 ledger 標的 tag 來源（<see cref="TagAttribution"/>）。
/// 可為 null 只是為了讓舊的呼叫端不用改；讀取端一律 <c>?? Array.Empty</c>。</summary>
public sealed record FinalPrompt(string Positive, string Negative, string Tips, string IntentSummary,
    IReadOnlyList<TagSource>? PositiveSources = null, IReadOnlyList<TagSource>? NegativeSources = null);

public sealed record SessionSnapshot(
    SessionStatus Status, string? Profile, int AskCount, int DiscussStreak, bool AutoFill,
    Dictionary<string, FacetState> FacetStates, Dictionary<string, string> FacetNotes, int HistoryCount, PresetLedger Ledger, FinalPrompt? LastFinal, int TurnIndex);

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
    public FinalPrompt? LastFinal { get; private set; }
    public int TurnIndex { get; set; }
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public Session(string id) => Id = id;

    public void ApplyProfile(string profile, FacetCatalog catalog)
    {
        Profile = profile;
        FacetStates = catalog.IdsForProfile(profile).ToDictionary(id => id, _ => FacetState.Missing);
        FacetNotes = new();
    }

    /// <summary>只收 profile 適用的 facet；其餘丟掉（不信 LLM 自述）。</summary>
    public void ApplyFacetStates(IReadOnlyDictionary<string, FacetState> updates, FacetCatalog catalog)
    {
        if (Profile is null) return;
        var applicable = catalog.IdsForProfile(Profile);
        foreach (var (id, state) in updates)
            if (applicable.Contains(id)) FacetStates[id] = state;
    }

    /// <summary>grounded 由 covered 推導，不由 LLM 回報。</summary>
    public IReadOnlySet<string> GroundedDimensions(FacetCatalog catalog) =>
        FacetStates.Where(kv => kv.Value == FacetState.Covered).Select(kv => catalog.DimensionOf(kv.Key)).ToHashSet();

    public void RecordAsk() => AskCount++;
    public void RecordDiscuss() { if (Status == SessionStatus.Collecting) DiscussStreak++; }
    public void RecordFinalize(FinalPrompt final) { LastFinal = final; Status = SessionStatus.Finalized; DiscussStreak = 0; }

    public SessionSnapshot Snapshot() => new(Status, Profile, AskCount, DiscussStreak, AutoFill,
        new Dictionary<string, FacetState>(FacetStates), new Dictionary<string, string>(FacetNotes), ChatHistory.Count, Ledger.Clone(), LastFinal, TurnIndex);

    public void Restore(SessionSnapshot s)
    {
        Status = s.Status; Profile = s.Profile; AskCount = s.AskCount; DiscussStreak = s.DiscussStreak; AutoFill = s.AutoFill;
        FacetStates = new Dictionary<string, FacetState>(s.FacetStates);
        FacetNotes = new Dictionary<string, string>(s.FacetNotes);
        while (ChatHistory.Count > s.HistoryCount) ChatHistory.RemoveAt(ChatHistory.Count - 1);
        Ledger = s.Ledger.Clone(); LastFinal = s.LastFinal; TurnIndex = s.TurnIndex;
    }
}
