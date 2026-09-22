using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace PromptCopilot.Api.Tests.Fakes;

/// <summary>腳本化的 chat completion：每次呼叫吐 Script 的下一項。項目可以丟例外、可以拿到 kernel 去 invoke plugin 模擬 auto-invoke 的結果。</summary>
public sealed class FakeChatCompletion : IChatCompletionService
{
    public Queue<Func<ChatHistory, Kernel?, CancellationToken, Task<IReadOnlyList<ChatMessageContent>>>> Script { get; } = new();
    public List<ChatHistory> Calls { get; } = new();
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    /// <summary>拿得到呼叫端傳進來的 token：可以用它模擬「這次呼叫一直不回來，直到逾時」。</summary>
    public FakeChatCompletion ThenAsync(Func<ChatHistory, Kernel?, CancellationToken, Task<IReadOnlyList<ChatMessageContent>>> step) { Script.Enqueue(step); return this; }
    public FakeChatCompletion ThenAsync(Func<ChatHistory, Kernel?, Task<IReadOnlyList<ChatMessageContent>>> step) => ThenAsync((h, k, _) => step(h, k));
    public FakeChatCompletion Then(Func<ChatHistory, IReadOnlyList<ChatMessageContent>> step) => ThenAsync((h, _) => Task.FromResult(step(h)));
    public FakeChatCompletion Then(ChatMessageContent msg) => Then(_ => new[] { msg });
    public FakeChatCompletion Throw(Exception e) => Then(_ => throw e);

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(new ChatHistory(chatHistory));
        if (Script.Count == 0) throw new InvalidOperationException("FakeChatCompletion script exhausted");
        return await Script.Dequeue()(chatHistory, kernel, cancellationToken);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public static ChatMessageContent Text(string content) => new(AuthorRole.Assistant, content);

    /// <summary>名字不是 Calls：C# 不准同型別上同名的屬性與方法（CS0102），Calls 屬性是呼叫紀錄。</summary>
    public static ChatMessageContent WithCalls(params FunctionCallContent[] calls)
    {
        var m = new ChatMessageContent(AuthorRole.Assistant, content: null);
        foreach (var c in calls) m.Items.Add(c);
        return m;
    }

    public static ChatMessageContent WithMeta(string? content, string key, string value) =>
        new(AuthorRole.Assistant, content, metadata: new Dictionary<string, object?> { [key] = value });
}
