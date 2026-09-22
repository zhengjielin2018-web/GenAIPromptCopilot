using System.Text.RegularExpressions;

namespace PromptCopilot.Api.Safety;

/// <summary>快速路徑。比對前把非字母數字正規化成空白（同 Python 管線 R14/R15），英文詞整 token 比對，CJK 子字串。</summary>
public sealed class Denylist
{
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private readonly List<(string term, bool ascii)> _terms;

    public Denylist(IEnumerable<string> terms) =>
        _terms = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => (t.Trim().ToLowerInvariant(), t.All(char.IsAscii))).ToList();

    public bool Hits(string text, out string term)
    {
        var norm = NonWord.Replace(text.ToLowerInvariant(), " ");
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var (t, ascii) in _terms)
        {
            if (ascii ? tokens.Contains(t) : norm.Contains(t)) { term = t; return true; }
        }
        term = ""; return false;
    }
}
