using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Orchestration;

public class AgenticOrchestratorTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private const string OkVerdict = """{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"ok"}""";

    internal sealed class MemorySink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = new();
        public Task WriteAsync(AuditEntry e, CancellationToken ct) { Entries.Add(e); return Task.CompletedTask; }
    }

    /// <summary>稽核資料庫掛掉。</summary>
    internal sealed class ExplodingSink : IAuditSink
    {
        public Task WriteAsync(AuditEntry e, CancellationToken ct) => throw new InvalidOperationException("audit db down");
    }

    internal sealed class Harness
    {
        public FakeChatCompletion Chat { get; } = new();
        public FakeChatCompletion GuardChat { get; } = new();
        public MemorySink Audit { get; } = new();
        public IAuditSink? SinkOverride { get; set; }
        public OrchestratorOptions Options { get; } = new() { MaxToolCallsPerTurn = 8, HistoryTurns = 10 };
        public Session Session { get; } = new("s1");

        public AgenticOrchestrator Build()
        {
            var guard = new SafetyGuard(new Denylist(Array.Empty<string>()), new SafetyClassifier(GuardChat, Microsoft.Extensions.Options.Options.Create(new LlmOptions())));
            var prompts = new SystemPromptBuilder(Catalog, Options, Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md"));
            return new AgenticOrchestrator(Chat, Catalog, guard, prompts, SinkOverride ?? Audit, Options,
                kernelFactory: (turn, tools, _) =>
                {
                    var k = Kernel.CreateBuilder().Build();
                    k.Data[TurnContextExtensions.DataKey] = turn;          // 不掛 filter：filter 在 FiltersTests 另測
                    AgentKernelFactory.AddFiltered(k, "Session", new SessionPlugin(turn, Catalog), tools);
                    AgentKernelFactory.AddFiltered(k, "Dialog", new DialogPlugin(turn, Catalog, Options), tools);
                    return k;
                });
        }

        public async Task<List<AgentEvent>> RunAsync(string text, CancellationToken ct = default)
        {
            GuardChat.Then(FakeChatCompletion.Text(OkVerdict));
            var events = new List<AgentEvent>();
            await foreach (var e in Build().RunTurnAsync(Session, text, ct)) events.Add(e);
            return events;
        }
    }

    /// <summary>模擬 connector 已把這個 tool 跑完：照 SK 的方式把 call 與 result 塞進 history。</summary>
    internal static async Task<ChatMessageContent> Invoke(ChatHistory hist, Kernel kernel, string plugin, string name, object args)
    {
        var ka = new KernelArguments();
        foreach (var p in JsonSerializer.SerializeToElement(args).EnumerateObject()) ka[p.Name] = p.Value;
        var call = new FunctionCallContent(name, plugin, Guid.NewGuid().ToString("N"), ka);
        var callMsg = new ChatMessageContent(AuthorRole.Assistant, content: null); callMsg.Items.Add(call);
        hist.Add(callMsg);
        var result = (await kernel.Plugins[plugin][name].InvokeAsync(kernel, ka)).ToString();
        var toolMsg = new ChatMessageContent(AuthorRole.Tool, content: null); toolMsg.Items.Add(new FunctionResultContent(call, result));
        hist.Add(toolMsg);
        return toolMsg;
    }

    internal static object AskArgs() => new
    {
        preamble = "有幾個地方想確認",
        asks = new[] { new { dimension = "style", question = "風格？", missingFacetIds = new[] { "style.genre" }, options = new[] { new { label = "寫實", tags = "photo realism", presetId = (long?)null }, new { label = "動漫", tags = "anime", presetId = (long?)null } } } },
        facetStates = new[] { new { facetId = "appearance.hair", state = "covered" } },
    };

    /// <summary>刻意不給 options：它有預設值，Gemini 照描述省略時必須還是綁得起來。</summary>
    private static object DiscussArgs(string message) => new { message, facetStates = Array.Empty<object>() };

    [Fact]
    public async Task Blocked_input_emits_blocked_and_never_calls_llm()
    {
        var h = new Harness();
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        var events = new List<AgentEvent>();
        await foreach (var e in h.Build().RunTurnAsync(h.Session, "x", default)) events.Add(e);
        Assert.Contains(events, e => e is BlockedEvent b && b.Reason == "Blocked_NSFW");
        Assert.Empty(h.Chat.Calls);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_NSFW");
    }

    /// <summary>輸入側同理：稽核掛掉不能把 BlockedEvent 吃掉。</summary>
    [Fact]
    public async Task Blocked_input_still_emits_blocked_when_audit_fails()
    {
        var h = new Harness { SinkOverride = new ExplodingSink() };
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        var events = new List<AgentEvent>();
        await foreach (var e in h.Build().RunTurnAsync(h.Session, "x", default)) events.Add(e);
        Assert.Equal("Blocked_NSFW", Assert.Single(events.OfType<BlockedEvent>()).Reason);
        Assert.Empty(h.Chat.Calls);
        Assert.Empty(h.Session.ChatHistory);
    }

    [Fact]
    public async Task Happy_path_ask_emits_final_ask_and_commits_session()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");

        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("ask", final.Kind); Assert.Single(final.Asks!);
        Assert.Contains(events, e => e is DimensionsEvent d && d.Profile == "portrait");
        Assert.Equal(1, h.Session.AskCount);
        Assert.Equal(FacetState.Covered, h.Session.FacetStates["appearance.hair"]);
        Assert.Equal(1, h.Session.TurnIndex);
        Assert.Equal(AuthorRole.System, h.Session.ChatHistory[0].Role);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Completed" && a.PromptVersion!.Length == 12);
        var askCall = h.Session.ChatHistory.SelectMany(m => m.Items.OfType<FunctionCallContent>()).Single(c => c.FunctionName == "AskUser");
        Assert.DoesNotContain("photo realism", askCall.Arguments!["asks"]!.ToString());   // history 已壓縮
    }

    [Fact]
    public async Task Llm_exception_rolls_back_everything_and_emits_error()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) => { await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" }); throw new InvalidOperationException("boom"); });
        var events = await h.RunAsync("一個少女");

        var err = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal("turn_failed", err.Code);
        Assert.Null(h.Session.Profile);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Failed" && a.PayloadJson!.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Upstream_block_rolls_back_and_emits_blocked_with_reason()
    {
        var h = new Harness();
        h.Chat.Throw(new UpstreamBlockedException("PROHIBITED_CONTENT"));
        var events = await h.RunAsync("x");
        var b = Assert.Single(events.OfType<BlockedEvent>());
        Assert.Equal("Blocked_Upstream", b.Reason); Assert.Contains("PROHIBITED_CONTENT", b.Message);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_Upstream");
    }

    [Fact]
    public async Task Output_block_outcome_rolls_back_and_emits_blocked()
    {
        var h = new Harness();
        h.Chat.ThenAsync((hist, k) =>
        {
            k!.Turn().Outcome = new BlockedOutcome("nsfw");          // 模擬 OutputSafetyFilter 命中
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { FakeChatCompletion.Text("") });
        });
        var events = await h.RunAsync("x");
        Assert.Equal("Blocked_Output", Assert.Single(events.OfType<BlockedEvent>()).Reason);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_Output");
    }

    /// <summary>補救過後仍沒有終止型 tool、又沒有可包裝的純文字（兩次都空白）：協定違反，整輪回滾。</summary>
    [Fact]
    public async Task No_outcome_is_protocol_violation_and_rolls_back()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { FakeChatCompletion.Text("  ") };            // 空白：包不成 Discuss
        }).Then(FakeChatCompletion.Text("  "));
        var events = await h.RunAsync("一個少女");

        Assert.Equal("protocol_violation", Assert.Single(events.OfType<ErrorEvent>()).Code);
        Assert.Empty(events.OfType<FinalEvent>());
        Assert.Null(h.Session.Profile);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Failed" && a.PayloadJson!.Contains("ProtocolViolationException"));
    }

    [Fact]
    public async Task Cancelled_token_rolls_back()
    {
        var h = new Harness();
        var cts = new CancellationTokenSource(); cts.Cancel();
        h.GuardChat.Then(FakeChatCompletion.Text(OkVerdict));   // 過得了 guard，才走得到交易裡的取消檢查
        h.Chat.Then(FakeChatCompletion.Text("x"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in h.Build().RunTurnAsync(h.Session, "x", cts.Token)) { }
        });
        Assert.Empty(h.Chat.Calls);                            // 取消檢查在呼叫 LLM 之前
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Failed" && a.PayloadJson!.Contains("ClientDisconnected"));
    }

    /// <summary>逾時是伺服器側取消：回滾、發 timeout 事件，但不能把例外丟給呼叫端。</summary>
    [Fact]
    public async Task Turn_timeout_rolls_back_and_emits_timeout_error()
    {
        var h = new Harness();
        h.Options.TurnTimeoutSeconds = 1;
        h.Chat.ThenAsync(async (hist, k, tct) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            await Task.Delay(Timeout.Infinite, tct);           // 這一輪永遠不回來
            return Array.Empty<ChatMessageContent>();
        });
        var events = await h.RunAsync("一個少女");               // 沒有例外浮到這裡

        Assert.Equal("timeout", Assert.Single(events.OfType<ErrorEvent>()).Code);
        Assert.Null(h.Session.Profile);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Failed" && a.PayloadJson!.Contains("Timeout"));
    }

    /// <summary>稽核是旁路：資料庫掛掉不能把使用者該看到的事件吃掉。</summary>
    [Fact]
    public async Task Audit_failure_on_rollback_path_still_emits_event()
    {
        var h = new Harness { SinkOverride = new ExplodingSink() };
        h.Chat.Throw(new UpstreamBlockedException("SAFETY"));
        var events = await h.RunAsync("x");

        var b = Assert.Single(events.OfType<BlockedEvent>());
        Assert.Equal("Blocked_Upstream", b.Reason);
        Assert.Contains("SAFETY", b.Message);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
    }

    [Fact]
    public async Task Second_turn_replaces_system_message_instead_of_stacking()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        await h.RunAsync("一個銀髮少女");
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) });
        await h.RunAsync("寫實跟動漫差在哪");
        Assert.Single(h.Session.ChatHistory, m => m.Role == AuthorRole.System);
        Assert.Equal(2, h.Session.TurnIndex);
    }

    [Fact]
    public async Task Plain_text_twice_is_wrapped_into_discuss_when_available()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("寫實走光影，動漫走筆觸。"))
              .Then(hist =>
              {
                  Assert.Equal(AuthorRole.System, hist.Last().Role);          // 補了一則系統提示
                  Assert.Contains("必須", hist.Last().Content!);
                  return new[] { FakeChatCompletion.Text("寫實走光影，動漫走筆觸。") };
              });
        var events = await h.RunAsync("寫實跟動漫差在哪");
        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("message", final.Kind); Assert.Contains("光影", final.Message!);
        Assert.Equal(1, h.Session.DiscussStreak);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Protocol_Violation");
    }

    [Fact]
    public async Task Plain_text_twice_without_discuss_is_an_error_and_rolls_back()
    {
        var h = new Harness();
        for (var i = 0; i < h.Options.MaxDiscussStreak; i++) h.Session.RecordDiscuss();   // Discuss 已被移除
        h.Chat.Then(FakeChatCompletion.Text("嗯")).Then(FakeChatCompletion.Text("嗯"));
        var events = await h.RunAsync("x");
        Assert.Equal("protocol_violation", Assert.Single(events.OfType<ErrorEvent>()).Code);
        Assert.Equal(h.Options.MaxDiscussStreak, h.Session.DiscussStreak);
        Assert.Empty(h.Session.ChatHistory);
    }

    [Fact]
    public async Task Budget_exhausted_forces_finalize_with_only_that_tool()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            k!.Turn().Outcome = new BudgetExhaustedOutcome();       // 模擬 ToolBudgetFilter 超限
            return new[] { FakeChatCompletion.Text("") };
        })
        .ThenAsync(async (hist, k) =>
        {
            Assert.Contains("定稿", hist.Last().Content!);
            Assert.Single(k!.Plugins);                                 // 只剩 Dialog
            Assert.Single(k.Plugins["Dialog"]);                        // 只剩 FinalizePrompt
            return new[] { await Invoke(hist, k, "Dialog", "FinalizePrompt", new { positivePrompt = "1girl", negativePrompt = "lowres", tips = "t", facetStates = Array.Empty<object>() }) };
        });
        var events = await h.RunAsync("一個少女");
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal(SessionStatus.Finalized, h.Session.Status);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Tool_Budget_Exhausted");
    }

    private sealed class ThrowingSink : IAuditSink
    {
        public Task WriteAsync(AuditEntry e, CancellationToken ct) =>
            e.EventType == "Turn_Completed" ? throw new IOException("db down") : Task.CompletedTask;
    }

    [Fact]
    public async Task Audit_failure_after_commit_does_not_roll_back()
    {
        var h = new Harness { SinkOverride = new ThrowingSink() };
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) });
        var events = await h.RunAsync("x");
        Assert.Single(events.OfType<FinalEvent>()); Assert.Empty(events.OfType<ErrorEvent>());
        Assert.Equal(1, h.Session.DiscussStreak);
    }
}
