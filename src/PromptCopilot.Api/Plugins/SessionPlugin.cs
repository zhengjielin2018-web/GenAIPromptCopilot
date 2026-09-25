using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

/// <summary>Tags（2026-09-25，設計 §5.5）：facet 標 covered 時附使用者那一項的英文 tag（逗號分隔），是整套組合推薦的錨。其他狀態忽略。</summary>
public sealed record FacetStateEntry(
    [property: JsonPropertyName("facetId")] string FacetId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("tags")] string? Tags = null);

public sealed class SessionPlugin(TurnContext turn, FacetCatalog catalog)
{
    [KernelFunction(ToolNames.SetProfile)]
    [Description("設定題材 profile（portrait | landscape | object | vehicle；動物歸 object）。會把該 profile 的全部 facet 重設為 missing。")]
    public string SetProfile([Description("portrait | landscape | object | vehicle")] string profile)
    {
        if (!catalog.IsProfile(profile)) return $"錯誤：profile 必須是 {string.Join(" | ", catalog.Profiles.Keys)}";
        turn.Session.ApplyProfile(profile, catalog);
        turn.Emit(turn.DimensionsSnapshot());
        return $"ok：profile={profile}，{turn.Session.FacetStates.Count} 個 facet 已重設為 missing";
    }

    [KernelFunction(ToolNames.SetFacetStates)]
    [Description("更新 facet 狀態。使用者第一次描述題材時，先用它把已描述的 facet 標 covered，再依流程檢索或追問；covered 的 facet 附 tags：使用者那一項的英文 tag（例：涼鞋 → sandals；銀色雙馬尾 → silver hair, twintails）。另外用於 waived（使用者明說不要指定）與單一項目的委託（note 記「使用者委託此項」，狀態維持 missing）。")]
    public string SetFacetStates(FacetStateEntry[] updates) => Apply(turn, catalog, updates);

    /// <summary>三個終止型 tool 也用這個：解析、過濾、套用、發 dimensions 事件。</summary>
    internal static string Apply(TurnContext turn, FacetCatalog catalog, IReadOnlyList<FacetStateEntry> updates)
    {
        if (turn.Session.Profile is null) { turn.Rejections.Add("Profile 為 null，facetStates 忽略"); return "ok（profile 未設定，facet 狀態未套用）"; }
        var parsed = new Dictionary<string, FacetState>();
        var tags = new Dictionary<string, string>();
        foreach (var u in updates)
        {
            if (!FacetStateParser.TryParse(u.State, out var st)) { turn.Rejections.Add($"facet {u.FacetId} 的狀態 '{u.State}' 無法解析，略過"); continue; }
            parsed[u.FacetId] = st;
            if (!string.IsNullOrWhiteSpace(u.Note)) turn.Session.FacetNotes[u.FacetId] = u.Note!;
            if (!string.IsNullOrWhiteSpace(u.Tags)) tags[u.FacetId] = u.Tags!;
        }
        turn.Session.ApplyFacetStates(parsed, catalog, tags);
        turn.Emit(turn.DimensionsSnapshot());
        return $"ok：套用 {parsed.Count} 筆";
    }
}
