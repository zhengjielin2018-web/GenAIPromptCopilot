using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Llm;

/// <summary>需要 GEMINI_API_KEY 與 PC_INTEGRATION=1。沒有 key 會失敗，刻意不吞。</summary>
[Trait("Category", "Integration")]
public class GeminiContractTests
{
    private static LlmOptions Llm => new() { ApiKey = TestEnv.GeminiKey ?? "" };

    /// <summary>組法與 Program.cs 同步：少了 GeminiRoleFixHandler，帶 tool 回覆的第二趟會被 Gemini 回 400。</summary>
    private static IChatCompletionService Chat() =>
        new ResilientChatCompletion(
            new GoogleAIGeminiChatCompletionService(Llm.Model, Llm.ApiKey, GoogleAIVersion.V1_Beta,
                new HttpClient(new GeminiRoleFixHandler(new HttpClientHandler()))),
            Options.Create(Llm));

    private static (Kernel Kernel, TurnContext Turn, ChatHistory History) Agent(IReadOnlySet<string> tools, string userText)
    {
        var catalog = FacetCatalogTests.Real();
        var session = new Session("c1");
        var turn = new TurnContext(session, 1, GuardResult.Ok(false), tools, Channel.CreateUnbounded<AgentEvent>().Writer);
        var kernel = Kernel.CreateBuilder().Build();
        AgentKernelFactory.AddFiltered(kernel, "Session", new SessionPlugin(turn, catalog), tools);
        AgentKernelFactory.AddFiltered(kernel, "Dialog", new DialogPlugin(turn, catalog, new OrchestratorOptions()), tools);
        var (prompt, _) = new SystemPromptBuilder(catalog, new OrchestratorOptions(), Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")).Build(session, tools);
        var history = new ChatHistory(prompt);
        history.AddUserMessage(userText);
        return (kernel, turn, history);
    }

    [IntegrationFact]
    public async Task Classifier_returns_parseable_verdict()
    {
        var v = await new SafetyClassifier(Chat(), Options.Create(Llm)).ClassifyInputAsync("一個女生站在海邊", default);
        Assert.False(v.Nsfw); Assert.False(v.RealPerson);
    }

    [IntegrationFact]
    public async Task First_turn_produces_a_function_call_not_prose()
    {
        var tools = ToolNames.Always.Union(new[] { ToolNames.AskUser, ToolNames.Discuss }).ToHashSet();
        var (kernel, _, history) = Agent(tools, "一個銀髮少女站在雨夜的霓虹街頭");
        var settings = new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false) };
        var msg = (await Chat().GetChatMessageContentsAsync(history, settings, kernel))[0];
        Assert.NotEmpty(msg.Items.OfType<FunctionCallContent>());
    }

    /// <summary>真正跑一次 auto-invoke：至少兩趟往返（tool 呼叫→結果→下一步）。
    /// 第一趟成功不代表第二趟會成功——connector 的 function role 就是在這裡被 Gemini 400 掉的。</summary>
    [IntegrationFact]
    public async Task Auto_invoke_survives_sending_a_tool_result_back()
    {
        var tools = ToolNames.Always.Union(new[] { ToolNames.AskUser, ToolNames.Discuss }).ToHashSet();
        var (kernel, turn, history) = Agent(tools, "一個銀髮少女站在雨夜的霓虹街頭");
        var settings = new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        var returned = await Chat().GetChatMessageContentsAsync(history, settings, kernel);
        // SK 把 function call 與 tool 結果寫進傳進去的 history；兩者都在才表示第二趟往返真的回來了
        Assert.Contains(history, m => m.Items.OfType<FunctionCallContent>().Any());
        Assert.Contains(history, m => m.Role == AuthorRole.Tool);
        Assert.True(turn.Session.Profile is not null || turn.Outcome is not null,
            $"auto-invoke 應至少跑完一個 tool；回傳 role={returned[0].Role}");
    }

    [IntegrationFact]
    public async Task Embedding_is_768_dim_unit_vector()
    {
        var c = new GeminiEmbeddingClient(new HttpClient(), Options.Create(new EmbeddingOptions()), Options.Create(Llm));
        var v = (await c.EmbedAsync(new[] { "雨夜霓虹街頭" }, GeminiEmbeddingClient.RetrievalQuery, default))[0];
        Assert.Equal(768, v.Length);
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), 3);
    }
}
