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
        h.Add(ToolResult("SearchPresets", """{"dimension":"style","hits":[{"id":1,"title":"a","positive":"long text"},{"id":2,"title":"b","positive":"x"}]}"""));
        h.Add(Call("Discuss", new { message = "m", options = new[] { new { label = "A", tags = "photo realism", presetId = 1 } } }));

        HistoryTrimmer.CompressTurn(h, from);

        var result = h[from].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Contains("\"title\":\"a\"", result); Assert.DoesNotContain("long text", result);
        var call = h[from + 1].Items.OfType<FunctionCallContent>().Single();
        var options = call.Arguments!["options"]!.ToString()!;
        Assert.Contains("\"label\":\"A\"", options); Assert.DoesNotContain("photo realism", options);
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
}
