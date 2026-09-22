using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Llm;

public class ResilientChatCompletionTests
{
    private static readonly ChatMessageContent Ok = FakeChatCompletion.Text("ok");
    private static HttpOperationException Http(HttpStatusCode c) => new(c, null, c.ToString(), null);
    private static KernelException Blocked(string reason) => new($"Prompt was blocked due to Gemini API safety reasons: {reason}");

    private static (ResilientChatCompletion sut, FakeChatCompletion inner, List<TimeSpan> delays) Make(int transport = 3, int unusable = 3, int block = 1)
    {
        var inner = new FakeChatCompletion();
        var delays = new List<TimeSpan>();
        var sut = new ResilientChatCompletion(inner,
            Options.Create(new LlmOptions { TransportRetries = transport, UnusableRetries = unusable, ContentBlockRetries = block, TransportBackoffMs = 1000 }),
            (d, _) => { delays.Add(d); return Task.CompletedTask; });
        return (sut, inner, delays);
    }

    [Fact]
    public async Task Transport_errors_retry_with_doubling_backoff()
    {
        var (sut, inner, delays) = Make();
        inner.Throw(Http(HttpStatusCode.TooManyRequests)).Throw(Http(HttpStatusCode.ServiceUnavailable)).Then(Ok);
        var r = await sut.GetChatMessageContentsAsync(new ChatHistory());
        Assert.Equal("ok", r[0].Content);
        Assert.Equal(3, inner.Calls.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task Transport_gives_up_after_retries()
    {
        var (sut, inner, _) = Make(transport: 3);
        for (var i = 0; i < 4; i++) inner.Throw(Http(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpOperationException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal(4, inner.Calls.Count);
    }

    [Fact]
    public async Task Non_transient_http_error_is_not_retried()
    {
        var (sut, inner, _) = Make();
        inner.Throw(Http(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpOperationException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task Content_block_is_retried_exactly_once_then_surfaces_reason()
    {
        var (sut, inner, delays) = Make();
        inner.Throw(Blocked("PROHIBITED_CONTENT")).Throw(Blocked("PROHIBITED_CONTENT"));
        var ex = await Assert.ThrowsAsync<UpstreamBlockedException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal("PROHIBITED_CONTENT", ex.Reason);
        Assert.Equal(2, inner.Calls.Count);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Content_block_that_clears_on_retry_succeeds()
    {
        var (sut, inner, _) = Make();
        inner.Throw(Blocked("SAFETY")).Then(Ok);
        Assert.Equal("ok", (await sut.GetChatMessageContentsAsync(new ChatHistory()))[0].Content);
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public async Task Block_reported_in_metadata_is_treated_as_content_block()
    {
        var (sut, inner, _) = Make(block: 0);
        inner.Then(FakeChatCompletion.WithMeta(null, "FinishReason", "SAFETY"));
        var ex = await Assert.ThrowsAsync<UpstreamBlockedException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal("SAFETY", ex.Reason);
    }

    [Fact]
    public async Task Empty_response_retries_then_gives_up()
    {
        var (sut, inner, _) = Make(unusable: 3);
        for (var i = 0; i < 4; i++) inner.Then(FakeChatCompletion.WithMeta(null, "FinishReason", "MAX_TOKENS"));
        var ex = await Assert.ThrowsAsync<UnusableResponseException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal("MAX_TOKENS", ex.FinishReason);
        Assert.Equal(4, inner.Calls.Count);
    }

    [Fact]
    public async Task Empty_response_then_success()
    {
        var (sut, inner, _) = Make();
        inner.Then(FakeChatCompletion.Text("")).Then(Ok);
        Assert.Equal("ok", (await sut.GetChatMessageContentsAsync(new ChatHistory()))[0].Content);
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public async Task Function_call_with_no_text_is_a_usable_response()
    {
        var (sut, inner, _) = Make();
        inner.Then(FakeChatCompletion.WithCalls(new FunctionCallContent("SetProfile", "Session", "1")));
        var r = await sut.GetChatMessageContentsAsync(new ChatHistory());
        Assert.Single(r[0].Items.OfType<FunctionCallContent>());
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_retrying()
    {
        var inner = new FakeChatCompletion().Throw(Http(HttpStatusCode.TooManyRequests)).Then(Ok);
        var cts = new CancellationTokenSource();
        var sut = new ResilientChatCompletion(inner, Options.Create(new LlmOptions()), (_, ct) => { cts.Cancel(); return Task.FromCanceled(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetChatMessageContentsAsync(new ChatHistory(), cancellationToken: cts.Token));
        Assert.Single(inner.Calls);
    }
}
