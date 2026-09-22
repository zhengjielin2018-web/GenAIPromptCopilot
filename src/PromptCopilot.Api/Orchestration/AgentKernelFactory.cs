using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;

namespace PromptCopilot.Api.Orchestration;

/// <summary>每輪建一個 kernel：plugin 拿這一輪的 TurnContext，函式清單依 ToolSetBuilder 過濾，filter 由外到內掛上。</summary>
public sealed class AgentKernelFactory(IChatCompletionService chat, FacetCatalog catalog, IEmbeddingClient embed,
    PresetRepository presets, HistoryRepository histories, SafetyClassifier classifier, IAuditSink audit, OrchestratorOptions options)
{
    public Kernel Create(TurnContext turn, IReadOnlySet<string> tools, bool includeBudget)
    {
        var b = Kernel.CreateBuilder();
        b.Services.AddSingleton(chat);
        var k = b.Build();
        k.Data[TurnContextExtensions.DataKey] = turn;

        AddFiltered(k, "Knowledge", new KnowledgePlugin(turn, catalog, embed, presets, histories), tools);
        AddFiltered(k, "Session", new SessionPlugin(turn, catalog), tools);
        AddFiltered(k, "Dialog", new DialogPlugin(turn, catalog, options), tools);

        k.AutoFunctionInvocationFilters.Add(new AuditFilter(audit));
        if (includeBudget) k.AutoFunctionInvocationFilters.Add(new ToolBudgetFilter(options));
        k.AutoFunctionInvocationFilters.Add(new OutputSafetyFilter(classifier));
        k.AutoFunctionInvocationFilters.Add(new TerminalToolFilter());
        return k;
    }

    /// <summary>違規的選項根本不在清單裡（主規格 §4.1）：只註冊 tools 內的函式。</summary>
    public static void AddFiltered(Kernel k, string pluginName, object plugin, IReadOnlySet<string> tools)
    {
        var all = KernelPluginFactory.CreateFromObject(plugin, pluginName);
        var funcs = all.Where(f => tools.Contains(f.Name)).ToList();
        if (funcs.Count > 0) k.Plugins.Add(KernelPluginFactory.CreateFromFunctions(pluginName, funcs));
    }
}
