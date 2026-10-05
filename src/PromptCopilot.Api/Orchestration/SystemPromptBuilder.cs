using System.Security.Cryptography;
using System.Text;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Orchestration;

/// <summary>主規格 §4.9 + 多輪 §6.2。每輪重組；hash 進 audit 讓 eval 對得上 prompt 版本。
/// 流程段依這一輪的種類二選一（先確認再動手設計 §5）：確認輪 flow-propose.md、動手輪 flow-act.md。</summary>
public sealed class SystemPromptBuilder(FacetCatalog catalog, OrchestratorOptions options, string promptsDir)
{
    public const string TemplateFile = "system.md";
    public const string ProposeFlowFile = "flow-propose.md";
    public const string ActFlowFile = "flow-act.md";

    /// <summary>樣板 {{RETRIEVAL_STEP}}／{{RETRIEVAL_RULE}} 的內容。on 是 2026-09-25 之前樣板裡的原文，搬進來只是為了 off 時能整段換掉。</summary>
    internal const string RetrievalStepOn =
        "再**用一次 `SearchPresets`**：使用者講到的每個 facet 各一項，用 `facetId` 加上他描述那一項的原話，並附上翻成英文 SD tag 的 `tags`（例：`clothing.footwear`＋「拖鞋」＋`slippers`、`appearance.hair`＋「銀色雙馬尾」＋`silver hair, twintails`；寫法跟 `SetFacetStates` 的 `tags` 一樣）；使用者沒講的維度每個用 `dimension` 給兩個對比方向的項目（例：「寫實攝影」與「日系動漫插畫」）。不要把整句描述丟給一個維度，也不要一個項目一次呼叫。需要風格參考時呼叫 `SearchSimilarPrompts`。";
    internal const string RetrievalStepOff = "本段對話沒有知識庫：不做檢索，直接依 facet 狀態追問或定稿。";
    internal const string RetrievalRuleOn =
        "- `SearchPresets` 回的片段標了「可借入提示詞」或「僅供建議」，以及每個 facet 對本次使用者是 covered 還是 missing：「僅供建議」的片段任何詞都不可進提示詞；「可借入」的片段，標 missing 的 facet 對應的詞也不可進，只可進建議。相似度「低」的片段仍可借用其中與描述相符的詞，不可借與描述矛盾的詞。";
    internal const string RetrievalRuleOff = "- 本段對話沒有知識庫片段，所有 tag 由你自行產生。";

    private readonly string _template = File.ReadAllText(Path.Combine(promptsDir, TemplateFile));
    private readonly string _proposeFlow = File.ReadAllText(Path.Combine(promptsDir, ProposeFlowFile));
    private readonly string _actFlow = File.ReadAllText(Path.Combine(promptsDir, ActFlowFile));

    public (string Prompt, string Version) Build(Session s, IReadOnlySet<string> tools, TurnKind kind, ConfirmedInput? confirmed = null)
    {
        var prompt = _template
            // 流程段最先換進來：它自己也有 {{RETRIEVAL_STEP}}
            .Replace("{{FLOW}}", kind == TurnKind.Act ? _actFlow : _proposeFlow)
            // 樣板自己的段落先換：之後才塞進來的 session 內容（定稿、選項）就不會誤中這兩個 placeholder。
            .Replace("{{RETRIEVAL_STEP}}", s.RetrievalEnabled ? RetrievalStepOn : RetrievalStepOff)
            .Replace("{{RETRIEVAL_RULE}}", s.RetrievalEnabled ? RetrievalRuleOn : RetrievalRuleOff)
            .Replace("{{TOOLS}}", string.Join("\n", tools.Order().Select(t => $"- `{t}`")))
            .Replace("{{FACETS}}", s.Profile is null ? catalog.PromptListing() : catalog.ProfileListing(s.Profile))
            .Replace("{{SESSION_FACTS}}", Facts(s))
            .Replace("{{OFFERED}}", Offered(s))
            // 確認內容最後才放：它是模型與使用者的文字，放進來之後不能再被任何 placeholder 替換掃到（Review Focus 2）
            .Replace("{{CONFIRMED}}", Confirmed(confirmed))
            // 樣板的換行在別台機器上可能被 git 轉成 CRLF，Facts/Offered 又是用 Environment.NewLine 接的：
            // 同一份 prompt 會算出兩個 hash，eval 就對不回 prompt 版本。統一成 \n 再算。
            .Replace("\r\n", "\n");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return (prompt, Convert.ToHexString(hash)[..12].ToLowerInvariant());
    }

    /// <summary>動手輪流程段開頭的「使用者已確認」區塊（設計 §5.2）；採用輪沒有確認內容，回空字串。</summary>
    private static string Confirmed(ConfirmedInput? c)
    {
        if (c is null) return "";
        var sb = new StringBuilder("### 使用者已確認\n\n");
        sb.Append(c.Pending.Message.Trim()).Append('\n');
        if (c.ChosenText is { } chosen) sb.Append("使用者選的是：").Append(chosen).Append('\n');
        // 「隨便／你決定」：補齊本身就是確認的內容。只寫「不要加入確認以外的改動」時，卡片沒列到的 missing 全被當成不能補（Task 10 驗收：定稿 llm 0–1 個 tag）
        sb.Append(c.Pending.AutoComplete
            ? "\n這一輪照上面的內容動手。使用者把沒講的交給你決定：補齊每一個 missing 的 facet 就是他確認的內容（見流程第 4 條），卡片沒列到的也要補。\n\n"
            : "\n這一輪照上面的內容動手，不要加入確認以外的改動。\n\n");
        return sb.ToString();
    }

    private string Facts(Session s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- profile：{s.Profile ?? "尚未設定（請先 SetProfile）"}");
        sb.AppendLine($"- 狀態：{s.Status}");
        sb.AppendLine($"- AutoFill：{s.AutoFill.ToString().ToLowerInvariant()}");
        sb.AppendLine($"- 知識庫：{s.RetrievalMode}");
        // 帶上限：只給已用次數時，模型看不出還能不能再問（2026-10-05 驗收修正輪）
        sb.AppendLine($"- 追問已用：{s.AskCount}／上限 {options.MaxAskCount}");
        if (s.Profile is { } profile)
        {
            // 算法同動手輪的 missing 維度（flow-act.md 第 1 條）：底下還有 missing 的 facet，waived 與有 note（委託）的不算。
            // 「隨便」的確認卡照這行逐維度列出要補什麼（flow-propose.md 第 2 條）；模型自己從下面逐 facet 的清單彙整時常漏。
            var missingDims = catalog.Dimensions
                .Where(dim => catalog.FacetsOf(profile, dim).Any(id =>
                    s.FacetStates.GetValueOrDefault(id, FacetState.Missing) == FacetState.Missing && !s.FacetNotes.ContainsKey(id)))
                .Select(dim => catalog.DimensionLabel(dim, profile))
                .ToList();
            sb.AppendLine($"- 還有 missing facet 的維度：{(missingDims.Count == 0 ? "（無）" : string.Join("、", missingDims))}");
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
