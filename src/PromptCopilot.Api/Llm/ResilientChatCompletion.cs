using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Llm;

/// <summary>包在單次 LLM 呼叫外面的三層重試（多輪 §5.6）。auto-invoke 關掉，所以這裡的一次呼叫就是一個 HTTP 往返。</summary>
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
        int transport = 0, unusable = 0, blocked = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ChatMessageContent> result;
            try
            {
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
                        throw new UpstreamBlockedException(LlmFailureClassifier.BlockReasonOf(e) ?? "SAFETY", e);
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
                throw new UpstreamBlockedException(reason ?? "SAFETY");
            }
            if (unusable < _o.UnusableRetries) { unusable++; continue; }
            throw new UnusableResponseException(reason);
        }
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("本專案不串流 token；終止型 tool 的參數一次到位（多輪 §5.1）。");
}
