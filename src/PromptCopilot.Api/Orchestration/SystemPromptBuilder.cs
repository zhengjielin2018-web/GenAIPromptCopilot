using System.Security.Cryptography;
using System.Text;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Orchestration;

/// <summary>主規格 §4.9 + 多輪 §6.2。每輪重組；hash 進 audit 讓 eval 對得上 prompt 版本。</summary>
public sealed class SystemPromptBuilder(FacetCatalog catalog, OrchestratorOptions options, string templatePath)
{
    private readonly string _template = File.ReadAllText(templatePath);

    public (string Prompt, string Version) Build(Session s, IReadOnlySet<string> tools)
    {
        var prompt = _template
            .Replace("{{TOOLS}}", string.Join("\n", tools.Order().Select(t => $"- `{t}`")))
            .Replace("{{FACETS}}", s.Profile is null ? catalog.PromptListing() : catalog.ProfileListing(s.Profile))
            .Replace("{{SESSION_FACTS}}", Facts(s))
            .Replace("{{OFFERED}}", Offered(s))
            // 樣板的換行在別台機器上可能被 git 轉成 CRLF，Facts/Offered 又是用 Environment.NewLine 接的：
            // 同一份 prompt 會算出兩個 hash，eval 就對不回 prompt 版本。統一成 \n 再算。
            .Replace("\r\n", "\n");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return (prompt, Convert.ToHexString(hash)[..12].ToLowerInvariant());
    }

    private string Facts(Session s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- profile：{s.Profile ?? "尚未設定（請先 SetProfile）"}");
        sb.AppendLine($"- 狀態：{s.Status}");
        sb.AppendLine($"- AutoFill：{s.AutoFill.ToString().ToLowerInvariant()}");
        sb.AppendLine($"- 追問已用：{s.AskCount}");
        if (s.Profile is not null)
        {
            sb.AppendLine("- facet 狀態：");
            foreach (var dim in catalog.Dimensions)
            {
                var ids = catalog.FacetsOf(s.Profile, dim);
                if (ids.Count == 0) continue;
                sb.AppendLine($"  [{dim}] {catalog.DimensionLabel(dim, s.Profile)}");
                foreach (var id in ids)
                {
                    var note = s.FacetNotes.TryGetValue(id, out var n) ? $"　note：{n}" : "";
                    sb.AppendLine($"    - {id}（{catalog.Facets[id].Label}）：{FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(id, FacetState.Missing))}{note}");
                }
            }
        }
        if (s.LastFinal is { } f)
        {
            sb.AppendLine("- 目前的定稿：");
            sb.AppendLine($"  positive：{f.Positive}");
            sb.AppendLine($"  negative：{f.Negative}");
        }
        return sb.ToString().TrimEnd();
    }

    private string Offered(Session s)
    {
        var recent = s.Ledger.RecentlyOffered(options.OfferedOptionsLimit);
        if (recent.Count == 0) return "";
        var sb = new StringBuilder("## 你先前提供過的選項（使用者可能回頭引用）\n\n");
        foreach (var e in recent)
        {
            var last = e.OfferedAs.OrderByDescending(o => o.TurnIndex).First();
            var dim = last.Dimension is null ? "" : $"{catalog.DimensionLabel(last.Dimension, s.Profile ?? "portrait")} ";
            sb.AppendLine($"[T{last.TurnIndex}] {dim}{last.Label} (preset {e.Id}) — {e.PromptSnippet}");
        }
        return sb.ToString().TrimEnd();
    }
}
