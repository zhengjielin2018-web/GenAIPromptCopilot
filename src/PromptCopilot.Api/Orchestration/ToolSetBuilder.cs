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

    public static readonly IReadOnlySet<string> Always = new HashSet<string>
        { SearchSimilarPrompts, SearchPresets, SetProfile, SetFacetStates, FinalizePrompt };
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>
        { AskUser, Discuss, FinalizePrompt, RequestSaveConsent };
}

/// <summary>主規格 §4.3 + 多輪 §3.3。LLM 不需要「遵守」規則：違規的選項根本不在清單裡。</summary>
public static class ToolSetBuilder
{
    public static IReadOnlySet<string> Build(Session s, bool wantsAutoComplete, OrchestratorOptions o)
    {
        var tools = new HashSet<string>(ToolNames.Always);
        if (!wantsAutoComplete)
        {
            if (s.Status == SessionStatus.Collecting && s.AskCount < o.MaxAskCount)
                tools.Add(ToolNames.AskUser);
            if (s.Status == SessionStatus.Finalized || s.DiscussStreak < o.MaxDiscussStreak)
                tools.Add(ToolNames.Discuss);
        }
        if (s.Status == SessionStatus.Finalized)
            tools.Add(ToolNames.RequestSaveConsent);
        return tools;
    }
}
