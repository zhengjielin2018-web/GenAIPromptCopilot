namespace PromptCopilot.Api.Rendering;

/// <summary>要求的來源（符合度設計 §4.2）：使用者說的、選的或認可的；或交給模型決定、由模型補上的（不計分）。</summary>
public static class RequirementSources
{
    public const string User = "user";
    public const string Delegated = "delegated";
}

/// <summary>收件時的對話整理（符合度設計 §4.1）。Key：使用者的話＋委託資訊的 hash，清單快照照它重用。</summary>
public sealed record IntentInput(string Transcript, string Key);

/// <summary>要求清單的一條，不含 tag：tag 每次照當下的 prompt 重新對（符合度設計 §4.3）。</summary>
public sealed record Requirement(string Id, string Text, string Source);

/// <summary>Session 上最新一份清單。Key 相同就重用，分數才能跨圖比較。</summary>
public sealed record RequirementSnapshot(string Key, IReadOnlyList<Requirement> Items);

/// <summary>文字步的結果：一條要求與這次 prompt 裡對應的 tag。兩個 tag 清單都空 = prompt 沒寫。</summary>
public sealed record RequirementMatch(string Id, string Text, string Source, IReadOnlyList<string> Tags, IReadOnlyList<string> NegativeTags);

/// <summary>看圖步的結果，對外格式的一條（符合度設計 §7）。Issue 由程式歸類，不問模型。</summary>
public sealed record RequirementVerdict(string Id, string Text, string Source, IReadOnlyList<string> Tags, IReadOnlyList<string> NegativeTags,
    string Verdict, string Issue, string Reason);

/// <summary>一張圖的評分。ListKey：IntentKey 前 8 碼，audit 用來看哪幾張是對同一份清單判的。</summary>
public sealed record SelfCheckResult(int? Score, string Summary, IReadOnlyList<RequirementVerdict> Items, string ListKey, bool ListReused);
