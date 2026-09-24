using System.Text.RegularExpressions;

namespace PromptCopilot.Api.Sessions;

/// <summary>定稿 tag 的來源。Origin：<c>rag</c>（命中 ledger 裡的片段）｜<c>llm</c>（模型自己寫的）｜<c>base</c>（基礎畫質詞／負向詞）。
/// rag 的 PresetIds 依片段寫進 ledger 的先後，PresetTitle 取第一個；其餘兩種 PresetIds 空、PresetTitle 為 null。</summary>
public sealed record TagSource(string Tag, string Origin, IReadOnlyList<long> PresetIds, string? PresetTitle);

/// <summary>定稿時由伺服器比對 ledger 標 tag 來源，不信模型自述（主規格 §9）。純函式、無 I/O。
/// 只看 ledger：SearchSimilarPrompts 的結果不進 ledger（只供參考），不算來源。</summary>
public static class TagAttribution
{
    public const string Rag = "rag";
    public const string Llm = "llm";
    public const string Base = "base";

    // 靜態欄位依宣告順序初始化：下面的基礎詞表要呼叫 Normalize，這兩個 Regex 必須排在前面。
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Weight = new(@":\s*-?\d*\.?\d+$", RegexOptions.Compiled);

    // system.md「提示詞規則」列的基礎詞：永遠生成、不屬於任何 facet。優先於 rag——片段裡常有它們，但那不是借來的。
    private static readonly HashSet<string> BasePositive = Keys("masterpiece, best quality, highly detailed");
    private static readonly HashSet<string> BaseNegative = Keys("lowres, bad anatomy, worst quality");

    public static IReadOnlyList<TagSource> Attribute(string prompt, PresetLedger ledger, bool negative)
    {
        var tags = Split(prompt);
        if (tags.Count == 0) return Array.Empty<TagSource>();

        var index = new Dictionary<string, List<LedgerEntry>>();
        foreach (var e in ledger.Entries)
            foreach (var key in Split(negative ? e.NegativeSnippet : e.PromptSnippet).Select(Normalize))
            {
                if (key.Length == 0) continue;
                if (!index.TryGetValue(key, out var hits)) index[key] = hits = new List<LedgerEntry>();
                if (!hits.Contains(e)) hits.Add(e);
            }

        var baseWords = negative ? BaseNegative : BasePositive;
        return tags.Select(tag =>
        {
            var key = Normalize(tag);
            if (baseWords.Contains(key)) return new TagSource(tag, Base, Array.Empty<long>(), null);
            if (index.TryGetValue(key, out var hits)) return new TagSource(tag, Rag, hits.Select(h => h.Id).ToArray(), hits[0].Title);
            return new TagSource(tag, Llm, Array.Empty<long>(), null);
        }).ToList();
    }

    /// <summary>以逗號分段、去頭尾空白、丟掉空段。顯示用的原文就是這裡的每一段。</summary>
    private static List<string> Split(string? text) =>
        (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    /// <summary>比對用的正規化：小寫、底線當空白、連續空白壓成一個，再反覆剝掉最外層成對括號與 <c>:數字</c> 權重
    /// （<c>(masterpiece:1.2)</c> → <c>masterpiece</c>、<c>((tag))</c> → <c>tag</c>）。</summary>
    private static string Normalize(string tag)
    {
        var s = Spaces.Replace(tag.ToLowerInvariant().Replace('_', ' '), " ").Trim();
        while (true)
        {
            var before = s;
            if (WrappedInParentheses(s)) s = s[1..^1].Trim();
            s = Weight.Replace(s, "").TrimEnd();
            if (s == before) return s;
        }
    }

    /// <summary>開頭的 ( 要跟結尾的 ) 成對：<c>(a) (b)</c> 頭尾都是括號，但不是同一對。</summary>
    private static bool WrappedInParentheses(string s)
    {
        if (s.Length < 2 || s[0] != '(' || s[^1] != ')') return false;
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i == s.Length - 1;
        }
        return false;
    }

    private static HashSet<string> Keys(string words) => Split(words).Select(Normalize).ToHashSet();
}
