using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Orchestration;

public static class ToolNames
{
    public const string SearchSimilarPrompts = "SearchSimilarPrompts";
    public const string SearchPresets = "SearchPresets";
    public const string SetProfile = "SetProfile";
    public const string SetFacetStates = "SetFacetStates";
    public const string AskUser = "AskUser";
    public const string Discuss = "Discuss";
    public const string FinalizePrompt = "FinalizePrompt";
    public const string RequestSaveConsent = "RequestSaveConsent";
    public const string Confirm = "Confirm";

    /// <summary>動手輪一定有的：會改畫面的工具與檢索（先確認再動手設計 §3.1）。</summary>
    public static readonly IReadOnlySet<string> Always = new HashSet<string>
        { SearchSimilarPrompts, SearchPresets, SetProfile, SetFacetStates, FinalizePrompt };
    /// <summary>確認輪一定有的：只能確認與檢索，沒有任何會改畫面的工具。</summary>
    public static readonly IReadOnlySet<string> ProposeAlways = new HashSet<string>
        { SearchSimilarPrompts, SearchPresets, Confirm };
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>
        { AskUser, Discuss, FinalizePrompt, RequestSaveConsent, Confirm };
}

/// <summary>先確認再動手設計 §3.1：使用者打字是確認輪；按確認卡或採用是動手輪。</summary>
public enum TurnKind { Propose, Act }

/// <summary>主規格 §4.3 + 多輪 §3.3 + 先確認再動手設計 §3.1。LLM 不需要「遵守」規則：違規的選項根本不在清單裡。
/// 確認輪沒有任何會改畫面的工具；動手輪沒有 Confirm 與 Discuss，只能照確認的內容動手。</summary>
public static class ToolSetBuilder
{
    public static IReadOnlySet<string> Build(Session s, TurnKind kind, bool wantsAutoComplete, OrchestratorOptions o)
    {
        var tools = new HashSet<string>(kind == TurnKind.Act ? ToolNames.Always : ToolNames.ProposeAlways);
        if (!s.RetrievalEnabled)
        {
            // 對照組：模型拿不到檢索工具，就不會「自稱」借用。Always／ProposeAlways 是一般情況的宣告，這裡減，不改它。
            tools.Remove(ToolNames.SearchPresets);
            tools.Remove(ToolNames.SearchSimilarPrompts);
        }
        if (kind == TurnKind.Act)
        {
            if (!wantsAutoComplete && s.Status == SessionStatus.Collecting && s.AskCount < o.MaxAskCount)
                tools.Add(ToolNames.AskUser);
            return tools;
        }
        if (!wantsAutoComplete && (s.Status == SessionStatus.Finalized || s.DiscussStreak < o.MaxDiscussStreak))
            tools.Add(ToolNames.Discuss);
        if (s.Status == SessionStatus.Finalized)
            tools.Add(ToolNames.RequestSaveConsent);
        return tools;
    }
}
