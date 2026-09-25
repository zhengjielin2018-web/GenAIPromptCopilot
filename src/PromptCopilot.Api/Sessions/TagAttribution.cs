using System.Text.RegularExpressions;

namespace PromptCopilot.Api.Sessions;

/// <summary>定稿 tag 的來源。Origin：<c>rag</c>（命中 ledger 裡的片段）｜<c>adopted</c>（採用的組合帶進來的）｜<c>llm</c>（模型自己寫的）｜<c>base</c>（基礎畫質詞／負向詞）。
/// rag 的 PresetIds 整段相等的命中在前、字尾相符在後，各自依片段寫進 ledger 的先後、不重複，PresetTitle 與 SourceRef 取第一個；
/// adopted 只帶那一套的 id、標題與出處；llm 與 base 的 PresetIds 空、PresetTitle 與 SourceRef 為 null。</summary>
public sealed record TagSource(string Tag, string Origin, IReadOnlyList<long> PresetIds, string? PresetTitle, string? SourceRef = null);

/// <summary>定稿時由伺服器比對 ledger 標 tag 來源，不信模型自述（主規格 §9）。純函式、無 I/O。
/// 只看 ledger：SearchSimilarPrompts 的結果不進 ledger（只供參考），不算來源。
/// 正規化後算 rag 的條件：整段相等，或以空白為界的字尾相符（片段 <c>platform sandals</c> ↔ tag <c>sandals</c>、
/// tag <c>short shorts</c> ↔ 片段 <c>shorts</c>）。片段多是更具體的複合 tag，模型寫的常是單品。
/// 只比字尾、只在空白邊界，不做子字串：<c>top</c> 不會命中 <c>laptop</c>；但 <c>top</c> 會命中 <c>crop top</c>，
/// 分不出泛指上衣還是那件 crop top——這是接受的代價。</summary>
public static class TagAttribution
{
    public const string Rag = "rag";
    public const string Llm = "llm";
    public const string Base = "base";
    /// <summary>採用組合帶進來的（設計 §6.5）：優先序 base → adopted → rag → llm。只看 positive：採用的都是正向 tag。</summary>
    public const string Adopted = "adopted";

    // 靜態欄位依宣告順序初始化：下面的基礎詞表要呼叫 Normalize，這兩個 Regex 必須排在前面。
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Weight = new(@":\s*-?\d*\.?\d+$", RegexOptions.Compiled);

    // system.md「提示詞規則」列的基礎詞：永遠生成、不屬於任何 facet。優先於 rag——片段裡常有它們，但那不是借來的。
    private static readonly HashSet<string> BasePositive = Keys("masterpiece, best quality, highly detailed");
    private static readonly HashSet<string> BaseNegative = Keys("lowres, bad anatomy, worst quality");

    /// <summary>已正規化的 tag 是不是正向基礎詞。推薦用它把基礎詞排出定稿錨：每次定稿都有、不屬於任何 facet。</summary>
    public static bool IsBase(string normalizedTag) => BasePositive.Contains(normalizedTag);

    public static IReadOnlyList<TagSource> Attribute(string prompt, PresetLedger ledger, bool negative, IReadOnlyList<Adoption>? adoptions = null)
    {
        var tags = Split(prompt);
        if (tags.Count == 0) return Array.Empty<TagSource>();

        // 整段相等走字典；字尾相符逐一比對片段（一個 session 幾十筆，tag 數 × 片段數即可）。兩者都依 ledger 插入順序。
        var index = new Dictionary<string, List<LedgerEntry>>();
        var snippets = new List<(string key, LedgerEntry entry)>();
        foreach (var e in ledger.Entries)
            foreach (var key in Split(negative ? e.NegativeSnippet : e.PromptSnippet).Select(Normalize))
            {
                if (key.Length == 0) continue;
                if (!index.TryGetValue(key, out var hits)) index[key] = hits = new List<LedgerEntry>();
                if (!hits.Contains(e)) hits.Add(e);
                snippets.Add((key, e));
            }

        var baseWords = negative ? BaseNegative : BasePositive;
        return tags.Select(tag =>
        {
            var key = Normalize(tag);
            if (baseWords.Contains(key)) return new TagSource(tag, Base, Array.Empty<long>(), null);
            if (!negative && adoptions is { Count: > 0 } && AdoptedBy(key, adoptions) is { } a)
                return new TagSource(tag, Adopted, new[] { a.PresetId }, a.Title, a.SourceRef);
            // 整段相等的命中排前面，其次字尾命中；同一筆 preset 只列一次。
            var hits = index.TryGetValue(key, out var exact) ? new List<LedgerEntry>(exact) : new List<LedgerEntry>();
            foreach (var (snippet, e) in snippets)
                if ((EndsWithWord(snippet, key) || EndsWithWord(key, snippet)) && !hits.Contains(e)) hits.Add(e);
            return hits.Count > 0
                ? new TagSource(tag, Rag, hits.Select(h => h.Id).ToArray(), hits[0].Title, hits[0].SourceRef)
                : new TagSource(tag, Llm, Array.Empty<long>(), null);
        }).ToList();
    }

    /// <summary>最近一次採用優先：同一個 tag 被兩套都帶進來時，使用者最後選的那套才是它的出處。</summary>
    private static Adoption? AdoptedBy(string key, IReadOnlyList<Adoption> adoptions)
    {
        for (var i = adoptions.Count - 1; i >= 0; i--)
            foreach (var taken in adoptions[i].Taken.Values)
                foreach (var raw in taken)
                {
                    var t = Normalize(raw);
                    if (t.Length > 0 && (t == key || EndsWithWord(t, key) || EndsWithWord(key, t))) return adoptions[i];
                }
        return null;
    }

    /// <summary>以空白為界的字尾：<paramref name="longer"/> 以「空白＋<paramref name="suffix"/>」結尾。兩邊都已正規化（單一空白）。</summary>
    public static bool EndsWithWord(string longer, string suffix) =>
        suffix.Length > 0 && longer.Length > suffix.Length
        && longer.EndsWith(suffix, StringComparison.Ordinal) && longer[longer.Length - suffix.Length - 1] == ' ';

    /// <summary>以逗號分段、去頭尾空白、丟掉空段。顯示用的原文就是這裡的每一段。</summary>
    public static List<string> Split(string? text) =>
        (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    /// <summary>比對用的正規化：小寫、底線當空白、連續空白壓成一個，再反覆剝掉最外層成對括號與 <c>:數字</c> 權重
    /// （<c>(masterpiece:1.2)</c> → <c>masterpiece</c>、<c>((tag))</c> → <c>tag</c>）。</summary>
    public static string Normalize(string tag)
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
