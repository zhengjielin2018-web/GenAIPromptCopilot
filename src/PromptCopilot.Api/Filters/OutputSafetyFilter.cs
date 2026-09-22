using Microsoft.SemanticKernel;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;

namespace PromptCopilot.Api.Filters;

/// <summary>多輪 §5.2：三個會把文字送到使用者眼前的 tool 都檢。命中用 Terminate + BlockedOutcome，不丟例外。</summary>
public sealed class OutputSafetyFilter(SafetyClassifier classifier) : IAutoFunctionInvocationFilter
{
    private static readonly HashSet<string> Guarded = new() { ToolNames.Discuss, ToolNames.AskUser, ToolNames.FinalizePrompt };

    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        if (!Guarded.Contains(context.Function.Name)) { await next(context); return; }
        var v = await classifier.ClassifyOutputAsync(TurnContextExtensions.OutputTextFor(context.Function.Name, context.Arguments), context.CancellationToken);
        if (v.Nsfw || v.RealPerson)
        {
            var turn = context.Kernel.Turn();
            turn.Outcome = new BlockedOutcome(v.Reason);
            context.Result = new FunctionResult(context.Function, "錯誤：輸出被攔截");
            context.Terminate = true;
            return;
        }
        await next(context);
    }
}
