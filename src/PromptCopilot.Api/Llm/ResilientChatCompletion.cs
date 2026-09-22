using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Llm;

/// <summary>包在一次 LLM 呼叫外面的三層重試（多輪 §5.6）。
/// 它包的是「整個 auto-invoke 迴圈」，不是單一趟 HTTP 往返——orchestrator 開著 FunctionChoiceBehavior.Auto()，
/// 一次 GetChatMessageContentsAsync 裡面可能跑好幾趟往返、好幾個 tool。
/// 重試因此是續跑而不是重跑：SK 直接在傳進來的 ChatHistory 上追加，已完成的 call 與結果都還在，
/// 重試會從斷掉的地方接下去。兩個已知邊角：失敗若落在「call 已寫進 history、結果還沒寫」之間，
/// 那個 function 會被再執行一次；失敗若落在終止型 tool 之後，可能再進去一次（由 TurnContext.Outcome
/// 與 TerminalToolFilter 兜住）。</summary>
public sealed class ResilientChatCompletion : IChatCompletionService
{
    private readonly IChatCompletionService _inner;
    private readonly LlmOptions _o;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ResilientChatCompletion(IChatCompletionService inner, IOptions<LlmOptions> options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inner = inner; _o = options.Value; _delay = delay ?? Task.Delay;
    }

    public IReadOnlyDictionary<string, object?> Attributes => _inner.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        int transport = 0, unusable = 0, blocked = 0, attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ChatMessageContent> result;
            try
            {
                attempts++;      // 實際打出去幾次；audit 的 Turn_Failed/Blocked_Upstream 要記（主規格 §4.6）
                result = await _inner.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                switch (LlmFailureClassifier.Classify(e))
                {
                    case LlmFailureKind.Transport when transport < _o.TransportRetries:
                        await _delay(TimeSpan.FromMilliseconds(_o.TransportBackoffMs * Math.Pow(2, transport)), cancellationToken);
                        transport++; continue;
                    case LlmFailureKind.ContentBlock when blocked < _o.ContentBlockRetries:
                        blocked++; continue;
                    case LlmFailureKind.ContentBlock:
                        throw new UpstreamBlockedException(LlmFailureClassifier.BlockReasonOf(e) ?? "SAFETY", e, attempts);
                    case LlmFailureKind.Unusable when unusable < _o.UnusableRetries:
                        unusable++; continue;
                    default:
                        throw;
                }
            }

            var problem = LlmFailureClassifier.ProblemOf(result);
            if (problem is null) return result;
            var (kind, reason) = problem.Value;
            if (kind == LlmFailureKind.ContentBlock)
            {
                if (blocked < _o.ContentBlockRetries) { blocked++; continue; }
                throw new UpstreamBlockedException(reason ?? "SAFETY", attempts: attempts);
            }
            if (unusable < _o.UnusableRetries) { unusable++; continue; }
            throw new UnusableResponseException(reason, attempts);
        }
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("本專案不串流 token；終止型 tool 的參數一次到位（多輪 §5.1）。");
}
