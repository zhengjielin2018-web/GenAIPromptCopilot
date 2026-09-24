using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Llm;

/// <summary>
/// 走真正的 Google connector（<see cref="GoogleAIGeminiChatCompletionService"/> + <see cref="GeminiRoleFixHandler"/>），
/// 用假 <see cref="HttpMessageHandler"/> 攔請求、餵罐頭回應，離線跑：不需要 API key 或資料庫。
/// 驗證兩件事：(1) SK 幫 <c>SearchPresets</c> 產的 function declaration schema 長得對；
/// (2) connector 把 Gemini 回傳的兩個項目的 functionCall 正確綁到 <see cref="KnowledgePlugin"/>，
/// 讓 embedding 以一次 batch 呼叫送出兩個 query（批次化的重點）。
/// </summary>
public class GeminiToolDeclarationTests
{
    private sealed class Emb : IEmbeddingClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct)
        { Calls.Add(texts); return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[768]).ToList()); }
    }

    private sealed class Pre() : PresetRepository(null!)
    {
        public override Task<IReadOnlyList<PresetHit>> SearchAsync(float[] q, IReadOnlyList<string> f, int k, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PresetHit>>(Array.Empty<PresetHit>());
        public override Task<long> PoolSizeAsync(IReadOnlyList<string> f, CancellationToken ct) => Task.FromResult(10L);
    }

    private sealed class His() : HistoryRepository(null!);

    /// <summary>把送出去的 request body 記下來，再照 queue 回罐頭回應——不打真正的網路。</summary>
    private sealed class Canned(Queue<string> replies, List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            bodies.Add(await r.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }

    private static string Call(string name, string argsJson) =>
        """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"NAME","args":ARGS}}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""".Replace("NAME", name).Replace("ARGS", argsJson);

    private const string Text = """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""";

    [Fact]
    public async Task SearchPresets_declaration_and_binding_through_the_google_connector()
    {
        var catalog = FacetCatalogTests.Real();
        var session = new Session("p");
        session.ApplyProfile("portrait", catalog);
        var turn = new TurnContext(session, 1, GuardResult.Ok(false), ToolNames.Always, Channel.CreateUnbounded<AgentEvent>().Writer) { CurrentCallId = "c1" };
        var embed = new Emb();
        var presets = new Pre();
        var kernel = new Kernel();
        AgentKernelFactory.AddFiltered(kernel, "Knowledge", new KnowledgePlugin(turn, catalog, embed, presets, new His()), ToolNames.Always);

        var bodies = new List<string>();
        var reply = Call("Knowledge_SearchPresets", """{"queries":[{"dimension":"style","query":"寫實攝影"},{"dimension":"scene","query":"稻田"}]}""");
        var chat = new GoogleAIGeminiChatCompletionService("gemini-x", "fake", GoogleAIVersion.V1_Beta,
            new HttpClient(new GeminiRoleFixHandler(new Canned(new Queue<string>(new[] { reply, Text }), bodies))));

        var history = new ChatHistory("sys");
        history.AddUserMessage("u");
        await chat.GetChatMessageContentsAsync(history, new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() }, kernel);

        // (1) 第一趟送出去的 request 裡，SK 幫 SearchPresets 產的 schema 要對得上 Contracts.cs 的 SearchQuery。
        var decl = JsonDocument.Parse(bodies[0]).RootElement
            .GetProperty("tools")[0].GetProperty("functionDeclarations")
            .EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Knowledge_SearchPresets");
        Assert.False(string.IsNullOrWhiteSpace(decl.GetProperty("description").GetString()));

        var queriesParam = decl.GetProperty("parameters").GetProperty("properties").GetProperty("queries");
        Assert.Equal("array", queriesParam.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(queriesParam.GetProperty("description").GetString()));

        var items = queriesParam.GetProperty("items");
        Assert.Equal("object", items.GetProperty("type").GetString());
        var required = items.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("dimension", required);
        Assert.Contains("query", required);
        Assert.Equal("string", items.GetProperty("properties").GetProperty("dimension").GetProperty("type").GetString());
        Assert.Equal("string", items.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());

        // (2) connector 把 Gemini 回的兩項 functionCall 綁到同一次 KnowledgePlugin 呼叫，embedding 一次 batch 帶兩個 query。
        var call = Assert.Single(embed.Calls);
        Assert.Equal(new[] { "寫實攝影", "稻田" }, call);
    }
}
