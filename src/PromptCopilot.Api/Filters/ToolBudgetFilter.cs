using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Plugins;

namespace PromptCopilot.Api.Filters;

public sealed class ToolBudgetFilter(OrchestratorOptions options) : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        var turn = context.Kernel.Turn();
        turn.ToolCalls++;
        if (turn.ToolCalls > options.MaxToolCallsPerTurn)
        {
            turn.Outcome ??= new BudgetExhaustedOutcome();
            context.Result = new FunctionResult(context.Function, "錯誤：本輪 tool 呼叫預算已用盡，將以現有資訊強制定稿");
            context.Terminate = true;
            return;
        }
        await next(context);
    }
}
