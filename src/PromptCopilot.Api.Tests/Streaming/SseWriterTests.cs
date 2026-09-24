using Microsoft.AspNetCore.Http;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Tests.Streaming;

public class SseWriterTests
{
    private static async IAsyncEnumerable<AgentEvent> Events()
    {
        yield return new SessionEvent("s1", 1, "Collecting");
        yield return new FinalEvent("message", Message: "哈囉", Options: new[] { new OptionItem("A", "t", null) });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Writes_event_and_data_lines_in_camelCase_without_nulls()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream(); ctx.Response.Body = body;
        await SseWriter.WriteAsync(ctx.Response, Events(), default);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Equal("text/event-stream", ctx.Response.ContentType);
        Assert.Contains("event: session\ndata: {\"type\":\"session\",\"sessionId\":\"s1\",\"turnIndex\":1,\"status\":\"Collecting\"}\n\n", text);
        Assert.Contains("event: final\n", text);
        Assert.Contains("\"kind\":\"message\"", text);
        Assert.Contains("哈囉", text);
        Assert.DoesNotContain("\"preamble\"", text);      // null 不輸出
        Assert.DoesNotContain("\"presetId\":null", text);
    }

    /// <summary>前端 types/api.ts 的 TagSource 靠這幾個欄位名；presetTitle 為 null 時照 WhenWritingNull 省略。</summary>
    [Fact]
    public async Task Finalized_event_carries_tag_sources_in_camelCase()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream(); ctx.Response.Body = body;
        var ev = new FinalEvent("finalized", Positive: "p, 1girl", Negative: "", Tips: "t", IntentSummary: "i",
            PositiveSources: new[] { new TagSource("p", "rag", new long[] { 5 }, "霓虹"), new TagSource("1girl", "llm", Array.Empty<long>(), null) },
            NegativeSources: Array.Empty<TagSource>());
        await SseWriter.WriteOneAsync(ctx.Response, ev, default);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("\"positiveSources\":[{\"tag\":\"p\",\"origin\":\"rag\",\"presetIds\":[5],\"presetTitle\":\"霓虹\"},{\"tag\":\"1girl\",\"origin\":\"llm\",\"presetIds\":[]}]", text);
        Assert.Contains("\"negativeSources\":[]", text);
    }
}
