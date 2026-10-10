using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Rendering;

/// <summary>收件時組「使用者想法」的材料（符合度設計 §4.1）：使用者原話、每句前面助理問了什麼、交給模型決定的項目。
/// 在端點拿著 session 鎖時呼叫，背景的 pipeline 不碰 ChatHistory。助理的問題常常不在文字裡，而在 Confirm／AskUser／Discuss 的參數裡。
/// Key 只看使用者的話與委託：助理的話或 prompt 變了、使用者沒開口時，鍵不變，清單快照就能重用（§4.3）。</summary>
public static class IntentTranscript
{
    public const int AssistantMaxChars = 300;

    public static IntentInput Build(Session s, FacetCatalog catalog)
    {
        var lines = new List<string>();
        var userWords = new List<string>();
        foreach (var m in s.ChatHistory)
        {
            if (m.Role == AuthorRole.User)
            {
                var text = m.Content?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                lines.Add($"使用者：{text}");
                userWords.Add(text);
                continue;
            }
            if (m.Role != AuthorRole.Assistant) continue;   // system、工具結果都不取
            if (!string.IsNullOrWhiteSpace(m.Content)) lines.Add($"助理：{Cut(m.Content.Trim())}");
            foreach (var c in m.Items.OfType<FunctionCallContent>())
                if (ToolLine(c) is { } line) lines.Add(line);
        }
        var delegations = Delegations(s, catalog);
        var transcript = "對話：\n" + string.Join("\n", lines) + "\n\n" + delegations;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", userWords) + "\n\n" + delegations))).ToLowerInvariant();
        return new IntentInput(transcript, key);
    }

    private static string? ToolLine(FunctionCallContent c)
    {
        switch (c.FunctionName)
        {
            case ToolNames.Confirm:
            {
                var choices = string.Join("／", Arr(Arg(c, "choices")).Select(e => Str(e)).Where(x => x.Length > 0));
                return "助理（確認）：" + Cut(Str(Arg(c, "message")) + (choices.Length > 0 ? $"　選項：{choices}" : ""));
            }
            case ToolNames.AskUser:
            {
                var body = new StringBuilder(Str(Arg(c, "preamble")));
                foreach (var ask in Arr(Arg(c, "asks")))
                {
                    body.Append('；').Append(Prop(ask, "question"));
                    var labels = Labels(ask.ValueKind == JsonValueKind.Object && ask.TryGetProperty("options", out var o) ? o : null);
                    if (labels.Length > 0) body.Append("　選項：").Append(labels);
                }
                return "助理（追問）：" + Cut(body.ToString());
            }
            case ToolNames.Discuss:
            {
                var labels = Labels(Arg(c, "options"));
                return "助理（回應）：" + Cut(Str(Arg(c, "message")) + (labels.Length > 0 ? $"　參考：{labels}" : ""));
            }
            default: return null;   // 檢索、SetFacetStates、FinalizePrompt 之類：不是在跟使用者說話
        }
    }

    /// <summary>委託備註（有 note 就是委託，同 SystemPromptBuilder）→ 明說不指定 → 整份隨便補；facet 照 facets.yaml 的順序。</summary>
    private static string Delegations(Session s, FacetCatalog catalog)
    {
        var items = new List<string>();
        foreach (var f in catalog.Facets.Values)
            if (s.FacetNotes.TryGetValue(f.Id, out var note) && !string.IsNullOrWhiteSpace(note)) items.Add($"- {f.Label}（{note.Trim()}）");
        foreach (var f in catalog.Facets.Values)
            if (s.FacetStates.TryGetValue(f.Id, out var st) && st == FacetState.Waived) items.Add($"- 使用者明說不指定：{f.Label}");
        if (s.AutoFill) items.Add("- 使用者要求其餘沒講的隨便補");
        return items.Count == 0 ? "交給模型決定的：（無）" : "交給模型決定的：\n" + string.Join("\n", items);
    }

    private static string Cut(string s) => s.Length <= AssistantMaxChars ? s : s[..AssistantMaxChars] + "…";

    /// <summary>connector 存的是 JsonElement；HistoryTrimmer 壓過的 options／asks 是 JSON 字串；其他字串參數是純文字。</summary>
    private static JsonElement? Arg(FunctionCallContent c, string name)
    {
        if (c.Arguments is null || !c.Arguments.TryGetValue(name, out var v) || v is null) return null;
        if (v is JsonElement e) return e;
        var text = v.ToString() ?? "";
        var head = text.TrimStart();
        if (head.StartsWith('[') || head.StartsWith('{'))
        {
            try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
            catch (JsonException) { }
        }
        return JsonSerializer.SerializeToElement(text);
    }

    private static string Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString()!.Trim() : "";

    private static string Prop(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) ? Str(p) : "";

    private static IEnumerable<JsonElement> Arr(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Labels(JsonElement? options) =>
        string.Join("／", Arr(options).Select(o => Prop(o, "label")).Where(x => x.Length > 0));
}
