using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Orchestration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class HistoryTrimmerTests
{
    private static ChatMessageContent Call(string name, object args)
    {
        var m = new ChatMessageContent(AuthorRole.Assistant, content: null);
        var ka = new KernelArguments();
        foreach (var p in JsonSerializer.SerializeToElement(args).EnumerateObject()) ka[p.Name] = p.Value;
        m.Items.Add(new FunctionCallContent(name, "Dialog", "c1", ka));
        return m;
    }

    private static ChatMessageContent ToolResult(string name, string json)
    {
        var m = new ChatMessageContent(AuthorRole.Tool, content: null);
        m.Items.Add(new FunctionResultContent(new FunctionCallContent(name, "Knowledge", "c1"), json));
        return m;
    }

    [Fact]
    public void CompressTurn_strips_tags_from_options_and_shrinks_search_results()
    {
        var h = new ChatHistory();
        h.AddSystemMessage("sys"); h.AddUserMessage("u1");
        var from = h.Count;
        h.Add(ToolResult("SearchPresets", """{"results":[{"dimension":"style","query":"寫實","grounded":false,"poolSize":4455,"hits":[{"id":1,"title":"a","positive":"long text"},{"id":2,"title":"b","positive":"x"}]}]}"""));
        h.Add(Call("Discuss", new { message = "m", options = new[] { new { label = "A", tags = "photo realism", presetId = 1 } } }));

        HistoryTrimmer.CompressTurn(h, from);

        var result = h[from].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Contains("\"title\":\"a\"", result); Assert.DoesNotContain("long text", result);
        Assert.Contains("\"poolSize\":4455", result); Assert.DoesNotContain("\"query\"", result);
        var call = h[from + 1].Items.OfType<FunctionCallContent>().Single();
        var options = call.Arguments!["options"]!.ToString()!;
        Assert.Contains("\"label\":\"A\"", options); Assert.DoesNotContain("photo realism", options);
    }

    [Fact]
    public void CompressTurn_keeps_error_items_and_drops_hit_bodies_per_result()
    {
        var h = new ChatHistory();
        h.Add(ToolResult("SearchPresets", """{"results":[{"dimension":"hair","query":"x","error":"維度 hair 不存在"},{"dimension":"scene","query":"稻田","grounded":true,"poolSize":6752,"hits":[{"id":3,"title":"c","positive":"very long"}]}]}"""));

        HistoryTrimmer.CompressTurn(h, 0);

        var result = h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Contains("\"error\":\"維度 hair 不存在\"", result);
        Assert.Contains("\"title\":\"c\"", result);
        Assert.DoesNotContain("very long", result);
    }

    [Fact]
    public void CompressTurn_keeps_facetId_on_facet_items_only()
    {
        var h = new ChatHistory();
        h.Add(ToolResult("SearchPresets", """{"results":[{"dimension":"clothing","facetId":"clothing.footwear","query":"拖鞋","grounded":true,"poolSize":19,"hits":[{"id":5,"title":"拖鞋","positive":"very long"}]},{"dimension":"style","facetId":null,"query":"寫實","grounded":false,"poolSize":4455,"hits":[]}]}"""));

        HistoryTrimmer.CompressTurn(h, 0);

        var result = h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        var items = JsonDocument.Parse(result).RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal("clothing.footwear", items[0].GetProperty("facetId").GetString());
        Assert.Equal("clothing", items[0].GetProperty("dimension").GetString());
        Assert.Equal(19, items[0].GetProperty("poolSize").GetInt64());
        Assert.False(items[1].TryGetProperty("facetId", out _));                    // 有才保留
        Assert.DoesNotContain("very long", result);
    }

    [Fact]
    public void CompressTurn_leaves_legacy_single_dimension_shape_untouched()
    {
        const string legacy = """{"dimension":"style","poolSize":4455,"hits":[{"id":1,"title":"a","positive":"long text"}]}""";
        var h = new ChatHistory();
        h.Add(ToolResult("SearchPresets", legacy));

        HistoryTrimmer.CompressTurn(h, 0);

        Assert.Equal(legacy, h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString());
    }

    [Fact]
    public void CompressTurn_shrinks_similar_prompts_to_40_chars_of_intent()
    {
        var h = new ChatHistory();
        h.Add(ToolResult("SearchSimilarPrompts", $$"""[{"intent":"{{new string('字', 60)}}","positive":"p","profile":"portrait","dist":0.2}]"""));
        HistoryTrimmer.CompressTurn(h, 0);
        var result = h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.DoesNotContain("\"positive\"", result);
        Assert.Contains(new string('字', 40), result); Assert.DoesNotContain(new string('字', 41), result);
    }

    [Fact]
    public void CompressTurn_leaves_wrong_shaped_options_untouched()
    {
        var h = new ChatHistory();
        h.Add(Call("Discuss", new { options = new[] { "A", "B" }, asks = new[] { "not an object" } }));

        HistoryTrimmer.CompressTurn(h, 0);

        var call = h[0].Items.OfType<FunctionCallContent>().Single();
        Assert.Equal("""["A","B"]""", call.Arguments!["options"]!.ToString());
        Assert.Equal("""["not an object"]""", call.Arguments["asks"]!.ToString());
    }

    [Fact]
    public void CompressTurn_leaves_wrong_shaped_search_result_untouched()
    {
        const string json = """[{"dimension":"style","hits":[]}]""";
        var h = new ChatHistory();
        h.Add(ToolResult("SearchPresets", json));

        HistoryTrimmer.CompressTurn(h, 0);

        Assert.Equal(json, h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString());
    }

    [Fact]
    public void Truncate_keeps_system_and_last_n_turns()
    {
        var h = new ChatHistory();
        h.AddSystemMessage("sys");
        for (var i = 1; i <= 5; i++) { h.AddUserMessage($"u{i}"); h.AddAssistantMessage($"a{i}"); }
        HistoryTrimmer.Truncate(h, keepTurns: 2);
        Assert.Equal(5, h.Count);
        Assert.Equal(AuthorRole.System, h[0].Role);
        Assert.Equal("u4", h[1].Content); Assert.Equal("a5", h[4].Content);
    }

    [Fact]
    public void Truncate_is_noop_when_within_limit()
    {
        var h = new ChatHistory(); h.AddSystemMessage("sys"); h.AddUserMessage("u1");
        HistoryTrimmer.Truncate(h, 10);
        Assert.Equal(2, h.Count);
    }

    /// <summary>組態填 0（或負數）時 userIdx[^keepTurns] 會直接 IndexOutOfRange，把整輪炸掉。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Truncate_with_non_positive_keep_turns_is_a_noop(int keepTurns)
    {
        var h = new ChatHistory();
        h.AddSystemMessage("sys");
        for (var i = 1; i <= 3; i++) { h.AddUserMessage($"u{i}"); h.AddAssistantMessage($"a{i}"); }
        HistoryTrimmer.Truncate(h, keepTurns);
        Assert.Equal(7, h.Count);
    }
}
