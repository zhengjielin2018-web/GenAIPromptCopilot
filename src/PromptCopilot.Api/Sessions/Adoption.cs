namespace PromptCopilot.Api.Sessions;

/// <summary>使用者採用了推薦的一套組合（設計 §6.3）。Taken：照它的 facet → 該 facet 的 tag（資料庫原字）；Kept：保留我的。
/// Filled／Replaced 是採用前的狀態分類（missing／notApplicable → Filled；covered／waived → Replaced），給量測的擴充率與取代率用。
/// Title／SourceRef 讓 adopted chip 不用再查 ledger。</summary>
public sealed record Adoption(int TurnIndex, long PresetId, string Title, string? SourceRef, string Dimension,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Taken, IReadOnlyList<string> Kept,
    IReadOnlyList<string> Filled, IReadOnlyList<string> Replaced);
