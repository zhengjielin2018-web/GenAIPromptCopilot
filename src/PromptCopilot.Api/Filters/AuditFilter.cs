using System.Diagnostics;
using System.Text.Json;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Filters;

public sealed class AuditFilter(IAuditSink sink, ILogger<AuditFilter> logger) : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        var turn = context.Kernel.Turn();
        var callId = $"{context.RequestSequenceIndex}-{context.FunctionSequenceIndex}";
        turn.Emit(new ToolCallEvent(callId, context.Function.Name, TurnContextExtensions.Summary(context.Arguments)));
        var sw = Stopwatch.StartNew();
        turn.CurrentCallId = callId;            // plugin 發 tool_result 時要用同一個 id（主規格 §10.2）
        try { await next(context); }
        finally { turn.CurrentCallId = null; }
        var result = context.Result.ToString();
        // 檢索與定稿存完整：查 tag 問題時要看得到查了什麼、撈到什麼、寫了什麼，截到 200 字就只能重跑推斷（known-issues #7）。
        // 其他 tool 的參數與結果沒那麼常要回頭看，照舊截短。
        var max = context.Function.Name is ToolNames.SearchPresets or ToolNames.FinalizePrompt ? int.MaxValue : 200;
        try
        {
            await sink.WriteAsync(new AuditEntry(turn.Session.Id, turn.TurnIndex, "Tool_Invoked",
                PayloadJson: JsonSerializer.Serialize(new { name = context.Function.Name, args = TurnContextExtensions.Summary(context.Arguments, max), result = result.Length > max ? result[..max] : result }),
                LatencyMs: (int)sw.ElapsedMilliseconds), context.CancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Tool_Invoked audit write failed: {Function} session {SessionId}", context.Function.Name, turn.Session.Id);
            turn.Rejections.Add($"audit 寫入失敗：{e.GetType().Name}");   // 稽核掛掉不該讓 tool 呼叫失敗
        }
    }
}
