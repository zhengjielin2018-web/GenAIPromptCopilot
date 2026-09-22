using System.Net;
using Microsoft.SemanticKernel;

namespace PromptCopilot.Api.Llm;

/// <summary>Gemini 判定內容不該生成，且內部重試（多輪 §5.5：1 次）已用盡。</summary>
public sealed class UpstreamBlockedException(string reason, Exception? inner = null)
    : Exception($"Gemini 攔截了這次請求的內容（{reason}）", inner)
{
    public string Reason { get; } = reason;
}

/// <summary>200 但沒有可用內容（MAX_TOKENS、空 candidates），且重試已用盡。與內容無關。</summary>
public sealed class UnusableResponseException(string? finishReason)
    : Exception($"Gemini 連續回了無法使用的回應（finish_reason={finishReason ?? "unknown"}）")
{
    public string? FinishReason { get; } = finishReason;
}

public enum LlmFailureKind { Transport, ContentBlock, Unusable, Fatal }

public static class LlmFailureClassifier
{
    // 與 scripts/pipeline/gemini_client.py 的 CONTENT_BLOCK_REASONS 一致
    public static readonly IReadOnlySet<string> ContentBlockReasons = new HashSet<string>
        { "PROHIBITED_CONTENT", "SAFETY", "IMAGE_SAFETY", "BLOCKLIST", "JAILBREAK", "MODEL_ARMOR" };

    /// <summary>connector 例外訊息裡代表「被擋」的字樣；若換 connector 版本文字不同，改這裡。
    /// 1.80.1-alpha 實際丟的是 KernelException("Prompt was blocked due to Gemini API safety reasons.")。</summary>
    public static readonly IReadOnlyList<string> ContentBlockMarkers = new[] { "blocked", "safety" };

    public static LlmFailureKind Classify(Exception e) => e switch
    {
        HttpOperationException h when h.StatusCode is HttpStatusCode.TooManyRequests || (int?)h.StatusCode >= 500 => LlmFailureKind.Transport,
        HttpRequestException or TaskCanceledException or IOException => LlmFailureKind.Transport,
        KernelException when BlockReasonOf(e) is not null => LlmFailureKind.ContentBlock,
        KernelException => LlmFailureKind.Unusable,
        _ => LlmFailureKind.Fatal,
    };

    public static string? BlockReasonOf(Exception e)
    {
        var msg = e.Message;
        var reason = ContentBlockReasons.FirstOrDefault(r => msg.Contains(r, StringComparison.OrdinalIgnoreCase));
        if (reason is not null) return reason;
        return ContentBlockMarkers.Any(m => msg.Contains(m, StringComparison.OrdinalIgnoreCase)) ? "SAFETY" : null;
    }

    /// <summary>回應層的問題：metadata 帶攔截理由 → ContentBlock；沒文字也沒 function call → Unusable；否則 null。</summary>
    public static (LlmFailureKind Kind, string? Reason)? ProblemOf(IReadOnlyList<ChatMessageContent> result)
    {
        var m = result.Count > 0 ? result[0] : null;
        if (m is null) return (LlmFailureKind.Unusable, null);
        var block = Meta(m, "PromptFeedbackBlockReason");
        if (block is not null && ContentBlockReasons.Contains(block)) return (LlmFailureKind.ContentBlock, block);
        var finish = Meta(m, "FinishReason");
        if (finish is not null && ContentBlockReasons.Contains(finish)) return (LlmFailureKind.ContentBlock, finish);
        var hasCall = m.Items.OfType<FunctionCallContent>().Any();
        if (string.IsNullOrWhiteSpace(m.Content) && !hasCall) return (LlmFailureKind.Unusable, finish);
        return null;
    }

    /// <summary>鍵名與 GeminiMetadata 的 PromptFeedbackBlockReason / FinishReason 屬性同名（1.80.1-alpha 已核對）。</summary>
    private static string? Meta(ChatMessageContent m, string key) =>
        m.Metadata is not null && m.Metadata.TryGetValue(key, out var v) && v is not null ? v.ToString()!.ToUpperInvariant() : null;
}
