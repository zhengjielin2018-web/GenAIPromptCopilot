using System.Diagnostics;
using System.Text.Json;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Filters;

public sealed class AuditFilter(IAuditSink sink) : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        var turn = context.Kernel.Turn();
        var callId = $"{context.RequestSequenceIndex}-{context.FunctionSequenceIndex}";
        turn.Emit(new ToolCallEvent(callId, context.Function.Name, TurnContextExtensions.Summary(context.Arguments)));
        var sw = Stopwatch.StartNew();
        await next(context);
        var result = context.Result.ToString();
        try
        {
            await sink.WriteAsync(new AuditEntry(turn.Session.Id, turn.TurnIndex, "Tool_Invoked",
                PayloadJson: JsonSerializer.Serialize(new { name = context.Function.Name, args = TurnContextExtensions.Summary(context.Arguments, 200), result = result.Length > 200 ? result[..200] : result }),
                LatencyMs: (int)sw.ElapsedMilliseconds), context.CancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            turn.Rejections.Add($"audit 寫入失敗：{e.GetType().Name}");   // 稽核掛掉不該讓 tool 呼叫失敗
        }
    }
}
