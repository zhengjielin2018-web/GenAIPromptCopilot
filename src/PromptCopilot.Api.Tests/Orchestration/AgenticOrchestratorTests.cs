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
        /// <summary>輸出側分類器：純文字包成 Discuss 之前那一次檢查走它（C1）。</summary>
        public FakeChatCompletion ClassifierChat { get; } = new();
        public MemorySink Audit { get; } = new();
        public IAuditSink? SinkOverride { get; set; }
        public string[] Deny { get; set; } = Array.Empty<string>();
        public OrchestratorOptions Options { get; } = new() { MaxToolCallsPerTurn = 8, HistoryTurns = 10 };
        public Session Session { get; } = new("s1");

        /// <summary>把 Chat 包進真的重試層。要驗 attempts 就不能繞過它。</summary>
        public bool Resilient { get; set; }

        public AgenticOrchestrator Build()
        {
            var llm = Microsoft.Extensions.Options.Options.Create(new LlmOptions());
            var guard = new SafetyGuard(new Denylist(Deny), new SafetyClassifier(GuardChat, llm));
            var prompts = new SystemPromptBuilder(Catalog, Options, Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md"));
            IChatCompletionService chat = Resilient
                ? new ResilientChatCompletion(Chat, llm, (_, _) => Task.CompletedTask)
                : Chat;
            return new AgenticOrchestrator(chat, Catalog, guard, prompts, SinkOverride ?? Audit, Options,
                new SafetyClassifier(ClassifierChat, llm),
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
            ClassifierChat.Then(FakeChatCompletion.Text(OkVerdict));      // 用不到就留在佇列裡
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
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Equal(12, completed.PromptVersion!.Length);
        // 主規格 §5.1：LLM 挑了哪些 facet 追問要看得見；不另開事件，寫在 Turn_Completed 的 payload 裡
        Assert.Contains("""askedFacetIds":["style.genre"]""", completed.PayloadJson!);
        Assert.Contains("waivedFacetIds", completed.PayloadJson!);
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

    /// <summary>I8：guard 在 try 外面時，它丟的例外（上游攔截、分類器壞掉）會整個飛出
    /// ExecuteAsync——使用者拿到端點那個泛用錯誤 frame，audit 一筆都沒有。</summary>
    [Fact]
    public async Task Guard_failure_is_reported_as_blocked_not_as_a_raw_exception()
    {
        var h = new Harness();
        h.GuardChat.Throw(new UpstreamBlockedException("SAFETY"));
        var events = new List<AgentEvent>();
        await foreach (var e in h.Build().RunTurnAsync(h.Session, "一個少女", default)) events.Add(e);

        var b = Assert.Single(events.OfType<BlockedEvent>());
        Assert.Equal("Blocked_Upstream", b.Reason);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_Upstream");
        Assert.Empty(h.Chat.Calls);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
    }

    /// <summary>I11 + 主規格 §12.1：一輪失敗只寫一筆，payload 要記實際嘗試次數。</summary>
    [Fact]
    public async Task Upstream_block_writes_one_row_carrying_the_attempt_count()
    {
        var h = new Harness { Resilient = true };
        h.Chat.Throw(new KernelException("Prompt was blocked due to Gemini API safety reasons."))
              .Throw(new KernelException("Prompt was blocked due to Gemini API safety reasons."));
        var events = await h.RunAsync("一個少女");

        Assert.Equal("Blocked_Upstream", Assert.Single(events.OfType<BlockedEvent>()).Reason);
        Assert.Equal(2, h.Chat.Calls.Count);                       // 預設 ContentBlockRetries = 1
        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Blocked_Upstream");
        Assert.Contains("\"attempts\":2", row.PayloadJson!);
        Assert.DoesNotContain(h.Audit.Entries, a => a.EventType == "Turn_Failed");
    }

    /// <summary>失敗的內文留在 audit，不送到使用者眼前。</summary>
    [Fact]
    public async Task Generic_failure_writes_one_turn_failed_row_and_keeps_the_message_out_of_the_stream()
    {
        var h = new Harness();
        h.Chat.Throw(new InvalidOperationException("pgbouncer pool exhausted"));
        var events = await h.RunAsync("一個少女");

        var err = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal("turn_failed", err.Code);
        Assert.DoesNotContain("pgbouncer", err.Message);
        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Failed");
        Assert.Contains("pgbouncer", row.PayloadJson!);
    }

    /// <summary>I3：客戶端中途斷線時，端點的 finally 會放掉 session 鎖。若列舉器 dispose 不等
    /// 背景那一輪收尾，下一輪就能在還在回滾的 Session 上開跑。</summary>
    [Fact]
    public async Task Enumerator_disposal_waits_for_the_turn_to_finish_unwinding()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource();
        h.GuardChat.Then(FakeChatCompletion.Text(OkVerdict));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            await gate.Task;
            throw new InvalidOperationException("boom");
        });

        var e = h.Build().RunTurnAsync(h.Session, "一個少女", default).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        Assert.IsType<SessionEvent>(e.Current);

        var disposal = e.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);          // 還在跑就回來＝鎖會被提早放掉
        gate.SetResult();
        await disposal;

        Assert.Equal(0, h.Session.TurnIndex);        // 回滾已經做完
        Assert.Null(h.Session.Profile);
        Assert.Empty(h.Session.ChatHistory);
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

    /// <summary>C1：包裝那條路沒經過 kernel，OutputSafetyFilter 不會跑。模型散文照樣是送到使用者
    /// 眼前的文字（主規格 §6.2），包之前必須自己檢一次。</summary>
    [Fact]
    public async Task Plain_text_wrap_is_blocked_when_the_output_classifier_flags_it()
    {
        var h = new Harness();
        h.ClassifierChat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"露骨描述"}"""));
        h.Chat.Then(FakeChatCompletion.Text("一段沒人檢查過的散文。")).Then(FakeChatCompletion.Text("一段沒人檢查過的散文。"));

        var events = await h.RunAsync("寫實跟動漫差在哪");

        var b = Assert.Single(events.OfType<BlockedEvent>());
        Assert.Equal("Blocked_Output", b.Reason);
        Assert.Contains("露骨描述", b.Message);
        Assert.Empty(events.OfType<FinalEvent>());
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.Equal(0, h.Session.DiscussStreak);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_Output");
    }

    /// <summary>命中的詞只進 audit payload，不進回給使用者的訊息。</summary>
    [Fact]
    public async Task Denylist_term_goes_to_the_audit_payload_not_to_the_user()
    {
        var h = new Harness { Deny = new[] { "nude" } };
        var events = await h.RunAsync("a nude girl");

        var b = Assert.Single(events.OfType<BlockedEvent>());
        Assert.Equal("Blocked_NSFW", b.Reason);
        Assert.DoesNotContain("nude", b.Message);
        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Blocked_NSFW");
        Assert.Contains("nude", row.PayloadJson!);
        Assert.Empty(h.Chat.Calls);
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

    /// <summary>純文字訊息會留在 history 裡跨輪存活：包裝只能看這一輪，否則會把上一輪的回答當成這一輪的答案再發一次。</summary>
    [Fact]
    public async Task Plain_text_wrap_ignores_previous_turn_assistant_text()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("寫實走光影，動漫走筆觸。"))
              .Then(FakeChatCompletion.Text("寫實走光影，動漫走筆觸。"));
        var first = await h.RunAsync("寫實跟動漫差在哪");
        Assert.Equal("message", Assert.Single(first.OfType<FinalEvent>()).Kind);
        Assert.Equal(1, h.Session.DiscussStreak);

        h.Chat.Then(FakeChatCompletion.Text("")).Then(FakeChatCompletion.Text(""));   // 這一輪什麼文字都沒產
        var second = await h.RunAsync("那動漫呢");
        Assert.Equal("protocol_violation", Assert.Single(second.OfType<ErrorEvent>()).Code);
        Assert.Empty(second.OfType<FinalEvent>());
        Assert.Equal(1, h.Session.DiscussStreak);                                     // 沒有拿上一輪的句子再包一次
    }
}
