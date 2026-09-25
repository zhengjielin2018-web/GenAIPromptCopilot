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

    /// <summary>前端 types/api.ts 的 PresetRef／TagSource 靠 sourceRef 標圖片與片段的來源；null 時照 WhenWritingNull 省略。</summary>
    [Fact]
    public async Task Tool_result_presets_and_tag_sources_carry_sourceRef()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream(); ctx.Response.Body = body;
        await SseWriter.WriteOneAsync(ctx.Response, new ToolResultEvent("c1", "SearchPresets", "s",
            new[] { new PresetRef(5, "霓虹", "u", "civitai:12345:0"), new PresetRef(6, "無來源", null) }), default);
        await SseWriter.WriteOneAsync(ctx.Response, new FinalEvent("finalized", Positive: "p", Negative: "", Tips: "t", IntentSummary: "i",
            PositiveSources: new[] { new TagSource("p", "rag", new long[] { 5 }, "霓虹", "civitai:12345:0") }, NegativeSources: Array.Empty<TagSource>()), default);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("\"presets\":[{\"id\":5,\"title\":\"霓虹\",\"imageUrl\":\"u\",\"sourceRef\":\"civitai:12345:0\"},{\"id\":6,\"title\":\"無來源\"}]", text);
        Assert.Contains("\"positiveSources\":[{\"tag\":\"p\",\"origin\":\"rag\",\"presetIds\":[5],\"presetTitle\":\"霓虹\",\"sourceRef\":\"civitai:12345:0\"}]", text);
    }

    /// <summary>前端 types/api.ts 的 SearchPresetsDetail／SearchSimilarDetail 靠這些欄位名；detail 為 null 時整個鍵省略。</summary>
    [Fact]
    public async Task Tool_result_detail_is_camelCase_and_omitted_when_null()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream(); ctx.Response.Body = body;
        var detail = new SearchPresetsDetail(new[]
        {
            new SearchPresetsItem("clothing", "clothing.footwear", "鞋履", "拖鞋", true, 300, 5, null,
                new[] { new SearchPresetsHit(5, "霓虹", "高", 0.201, true, new Dictionary<string, string> { ["clothing.footwear"] = "covered" }) }),
            new SearchPresetsItem("hair", "hair", "hair", "捲髮", false, 0, 0, "維度 hair 對 portrait 不適用或不存在", Array.Empty<SearchPresetsHit>()),
        });
        await SseWriter.WriteOneAsync(ctx.Response, new ToolResultEvent("c1", "SearchPresets", "s", Array.Empty<PresetRef>(), detail), default);
        await SseWriter.WriteOneAsync(ctx.Response, new ToolResultEvent("c2", "SearchSimilarPrompts", "s", null,
            new SearchSimilarDetail(new[] { new SearchSimilarHit("雨夜霓虹街頭的銀髮少女", "portrait", 0.18) })), default);
        await SseWriter.WriteOneAsync(ctx.Response, new ToolResultEvent("c3", "SearchPresets", "s", null), default);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("\"detail\":{\"items\":[{\"dimension\":\"clothing\",\"facetId\":\"clothing.footwear\",\"label\":\"鞋履\",\"query\":\"拖鞋\",\"grounded\":true,\"poolSize\":300,\"k\":5,\"hits\":[{\"id\":5,\"title\":\"霓虹\",\"band\":\"高\",\"dist\":0.201,\"usable\":true,\"facets\":{\"clothing.footwear\":\"covered\"}}]}", text);
        Assert.Contains("\"error\":\"維度 hair 對 portrait 不適用或不存在\",\"hits\":[]", text);
        Assert.Contains("\"detail\":{\"hits\":[{\"intent\":\"雨夜霓虹街頭的銀髮少女\",\"profile\":\"portrait\",\"dist\":0.18}]}", text);
        var third = text.Split("event: tool_result\n")[3];
        Assert.DoesNotContain("\"detail\"", third);
    }
}
