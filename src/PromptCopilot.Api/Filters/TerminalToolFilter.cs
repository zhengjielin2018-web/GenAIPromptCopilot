using Microsoft.SemanticKernel;

namespace PromptCopilot.Api.Filters;

/// <summary>終止型 tool 成功後（plugin 設了 outcome）停下 auto-invoke 迴圈。</summary>
public sealed class TerminalToolFilter : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        await next(context);
        if (context.Kernel.Turn().Outcome is not null) context.Terminate = true;
    }
}
