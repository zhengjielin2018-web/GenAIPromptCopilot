using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;

namespace PromptCopilot.Api.Tests.Orchestration;

/// <summary>known-issues #8：用真的 Google connector 產生 history、再看它送出去的下一次請求。
/// HistoryTrimmerTests 手工組的 FunctionResultContent 不是 connector 實際存的形狀，那組測試全綠時正式路徑一次都沒壓到。
/// HTTP 用假 handler，不打 Gemini；組法跟 Program.cs 一樣經過 GeminiRoleFixHandler。</summary>
public class HistoryTrimmerGeminiTests
{
    private const string Snippet = "masterpiece, best quality, 1girl, silver hair, standing in neon rainy street, reflective puddles";

    /// <summary>跟 KnowledgePlugin.ModelResult 同形狀：每筆命中帶 positive／negative 片段本文，壓縮要把它們拿掉。</summary>
    private static readonly string PresetsJson = JsonSerializer.Serialize(new
    {
        results = new object[]
        {
            new
            {
                dimension = "appearance", facetId = "appearance.hair", query = "銀髮", grounded = true, poolSize = 812,
                hits = new[]
                {
                    new { id = 41001, title = "銀髮少女", band = "很像", dist = 0.182, usable = "可借入提示詞",
                          facets = new Dictionary<string, string> { ["appearance.hair"] = "covered" }, positive = Snippet, negative = "lowres, bad hands" },
                    new { id = 41002, title = "月光銀髮", band = "相關", dist = 0.264, usable = "僅供建議",
                          facets = new Dictionary<string, string> { ["appearance.hair"] = "covered" }, positive = Snippet + ", moonlight", negative = "(無)" },
                },
            },
            new { dimension = "scene", facetId = (string?)null, query = "雨夜街頭", error = "維度 scene 暫時查不到" },
        },
    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });   // 跟 KnowledgePlugin 一樣不跳脫中文

    public sealed class FakeKnowledge
    {
        [KernelFunction(ToolNames.SearchPresets)] public string SearchPresets(string query) => PresetsJson;
        [KernelFunction(ToolNames.SetProfile)] public string SetProfile(string profile) => "已設定 profile";
    }

    /// <summary>照順序回罐頭回應，並記下每次請求的本文（壓縮前後都從這裡讀）。</summary>
    private sealed class Canned(params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(replies);
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(queue.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }

    internal static string Reply(string partsJson) =>
        $$$"""{"candidates":[{"content":{"role":"model","parts":[{{{partsJson}}}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""";

    private const string CallSearch = """{"functionCall":{"name":"Knowledge_SearchPresets","args":{"query":"銀髮"}},"thoughtSignature":"SIG-1"}""";
    private const string CallProfile = """{"functionCall":{"name":"Knowledge_SetProfile","args":{"profile":"portrait"}}}""";
    private static readonly string Text = Reply("""{"text":"好"}""");

    private sealed record Run(ChatHistory History, int StartIdx, Canned Http, IChatCompletionService Chat, Kernel Kernel);

    /// <summary>跑一輪 auto-invoke：第一次回傳 <paramref name="calls"/>，第二次回純文字結束。</summary>
    private static async Task<Run> FirstTurnAsync(string calls, params string[] extraReplies)
    {
        var http = new Canned(new[] { Reply(calls), Text }.Concat(extraReplies).ToArray());
        var chat = new GoogleAIGeminiChatCompletionService("gemini-test", "test", GoogleAIVersion.V1_Beta, new HttpClient(new GeminiRoleFixHandler(http)));
        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.AddFromObject(new FakeKnowledge(), "Knowledge");
        var history = new ChatHistory("sys");
        history.AddUserMessage("一個銀髮少女站在雨夜街頭");
        var startIdx = history.Count;                          // 跟 AgenticOrchestrator 一樣：user message 之後才算本輪
        await chat.GetChatMessageContentsAsync(history, Settings, kernel);
        return new Run(history, startIdx, http, chat, kernel);
    }

    private static GeminiPromptExecutionSettings Settings => new() { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };

    /// <summary>壓縮後再送下一輪，回傳那一次請求的本文。</summary>
    private static async Task<JsonElement> NextRequestAsync(Run run)
    {
        run.History.AddUserMessage("第二輪");
        await run.Chat.GetChatMessageContentsAsync(run.History, Settings, run.Kernel);
        return JsonDocument.Parse(run.Http.Bodies[^1]).RootElement;
    }

    private static List<(string Role, JsonElement Response)> FunctionResponses(JsonElement body) =>
        body.GetProperty("contents").EnumerateArray()
            .SelectMany(c => c.GetProperty("parts").EnumerateArray()
                .Where(p => p.TryGetProperty("functionResponse", out _))
                .Select(p => (c.GetProperty("role").GetString()!, p.GetProperty("functionResponse"))))
            .ToList();

    /// <summary>這個修正依賴的 connector 形狀。connector 換版後這條先紅，就知道 CompressTurn 要跟著改。</summary>
    [Fact]
    public async Task Connector_keeps_tool_results_on_CalledToolResults_not_in_Items()
    {
        var run = await FirstTurnAsync(CallSearch);

        var tool = Assert.IsType<GeminiChatMessageContent>(run.History.Single(m => m.Role == AuthorRole.Tool));
        Assert.Empty(tool.Items.OfType<FunctionResultContent>());
        var result = Assert.Single(tool.CalledToolResults!);
        Assert.Equal("Knowledge_SearchPresets", result.FullyQualifiedName);
        Assert.Equal(ToolNames.SearchPresets, result.FunctionResult.Function?.Name);
        Assert.Equal(PresetsJson, result.FunctionResult.GetValue<object>()?.ToString());
        var call = Assert.IsType<GeminiChatMessageContent>(run.History[run.StartIdx]);
        Assert.Equal("Knowledge_SearchPresets", Assert.Single(call.ToolCalls!).FullyQualifiedName);
    }

    [Fact]
    public async Task CompressTurn_shrinks_search_presets_in_the_next_gemini_request()
    {
        var run = await FirstTurnAsync(CallSearch, Text);
        Assert.Contains("silver hair", run.Http.Bodies[1]);    // 壓縮前：第二趟往返帶著片段本文

        HistoryTrimmer.CompressTurn(run.History, run.StartIdx);
        var body = await NextRequestAsync(run);

        var (role, response) = Assert.Single(FunctionResponses(body));
        Assert.Equal("user", role);
        Assert.Equal("Knowledge_SearchPresets", response.GetProperty("name").GetString());
        var content = response.GetProperty("response").GetProperty("content").GetString()!;
        var items = JsonDocument.Parse(content).RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(812, items[0].GetProperty("poolSize").GetInt32());
        Assert.Equal("appearance.hair", items[0].GetProperty("facetId").GetString());
        Assert.Equal(new[] { "id", "title" }, items[0].GetProperty("hits")[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("月光銀髮", items[0].GetProperty("hits")[1].GetProperty("title").GetString());
        Assert.Equal("維度 scene 暫時查不到", items[1].GetProperty("error").GetString());
        var raw = run.Http.Bodies[^1];
        Assert.DoesNotContain("silver hair", raw);
        Assert.DoesNotContain("lowres", raw);
        Assert.DoesNotContain("\"query\"", content);
        // functionCall 那一則原封不動（Gemini 3 要求 thoughtSignature 跟著回傳）
        Assert.Contains("\"thoughtSignature\":\"SIG-1\"", raw);
    }

    /// <summary>模型一次發多個呼叫時，connector 把全部結果放在同一則 tool 訊息；Gemini 要求回覆的 part 數與呼叫數相同，不能拆開。</summary>
    [Fact]
    public async Task CompressTurn_keeps_parallel_results_in_one_message_and_only_shrinks_search_presets()
    {
        var run = await FirstTurnAsync(CallSearch + "," + CallProfile, Text);
        var before = Assert.IsType<GeminiChatMessageContent>(run.History.Single(m => m.Role == AuthorRole.Tool));
        Assert.Equal(2, before.CalledToolResults!.Count);

        HistoryTrimmer.CompressTurn(run.History, run.StartIdx);
        var body = await NextRequestAsync(run);

        var tool = Assert.IsType<GeminiChatMessageContent>(run.History.Single(m => m.Role == AuthorRole.Tool));
        Assert.Equal(AuthorRole.Tool, tool.Role);
        Assert.Equal(before.ModelId, tool.ModelId);
        var withResponses = body.GetProperty("contents").EnumerateArray()
            .Where(c => c.GetProperty("parts").EnumerateArray().Any(p => p.TryGetProperty("functionResponse", out _))).ToList();
        var parts = Assert.Single(withResponses).GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("functionResponse")).ToList();
        Assert.Equal(new[] { "Knowledge_SearchPresets", "Knowledge_SetProfile" }, parts.Select(p => p.GetProperty("name").GetString()).ToArray());
        Assert.DoesNotContain("silver hair", parts[0].GetProperty("response").GetProperty("content").GetString());
        Assert.Contains("\"title\":\"銀髮少女\"", parts[0].GetProperty("response").GetProperty("content").GetString());
        Assert.Equal("已設定 profile", parts[1].GetProperty("response").GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompressTurn_leaves_a_gemini_tool_message_alone_when_nothing_is_compressible()
    {
        var run = await FirstTurnAsync(CallProfile);
        var tool = run.History.Single(m => m.Role == AuthorRole.Tool);

        HistoryTrimmer.CompressTurn(run.History, run.StartIdx);

        Assert.Same(tool, run.History.Single(m => m.Role == AuthorRole.Tool));
    }

    /// <summary>檢索時機設計 §4：確認輪留著的結果下一輪照樣帶片段；下一輪收尾壓掉之後才不帶；已壓過的 Gemini 訊息不重建（Review Focus 3）。</summary>
    [Fact]
    public async Task Kept_search_results_reach_the_next_request_and_are_compressed_at_its_end()
    {
        var run = await FirstTurnAsync(CallSearch, Text, Text);
        HistoryTrimmer.CompressTurn(run.History, run.StartIdx, keepSearchResults: true);

        await NextRequestAsync(run);
        Assert.Contains("silver hair", run.Http.Bodies[^1]);                          // 下一輪看得到完整片段

        var secondStart = run.History.ToList().FindIndex(m => m.Role == AuthorRole.User && m.Content == "第二輪") + 1;
        HistoryTrimmer.CompressSearchResultsBefore(run.History, secondStart);         // 下一輪收尾
        var tool = run.History.Single(m => m.Role == AuthorRole.Tool);
        await NextRequestAsync(run);
        Assert.DoesNotContain("silver hair", run.Http.Bodies[^1]);
        Assert.Contains("\"thoughtSignature\":\"SIG-1\"", run.Http.Bodies[^1]);

        HistoryTrimmer.CompressSearchResultsBefore(run.History, run.History.Count);  // 已壓過：不重建
        Assert.Same(tool, run.History.Single(m => m.Role == AuthorRole.Tool));
    }
}
