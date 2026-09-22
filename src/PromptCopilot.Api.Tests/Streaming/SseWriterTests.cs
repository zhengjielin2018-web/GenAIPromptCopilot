using Microsoft.AspNetCore.Http;
using PromptCopilot.Api.Plugins;
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
}
