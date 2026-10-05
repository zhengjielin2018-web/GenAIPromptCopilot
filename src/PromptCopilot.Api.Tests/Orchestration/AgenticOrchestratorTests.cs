using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        /// <summary>換成真的 Google connector（HTTP 用假 handler）：驗送出去的請求形狀時用，Chat 腳本就不會被用到。</summary>
        public IChatCompletionService? ChatOverride { get; set; }

        /// <summary>預設不推薦：既有測試不該因為推薦而多出事件。</summary>
        public IRecommendationService Recommendations { get; set; } = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(null));

        /// <summary>要驗每輪摘要 log 時換成 ListLogger。</summary>
        public ILogger<AgenticOrchestrator> Logger { get; set; } = NullLogger<AgenticOrchestrator>.Instance;

        public AgenticOrchestrator Build()
        {
            var llm = Microsoft.Extensions.Options.Options.Create(new LlmOptions());
            var guard = new SafetyGuard(new Denylist(Deny), new SafetyClassifier(GuardChat, llm));
            var prompts = new SystemPromptBuilder(Catalog, Options, Path.Combine(AppContext.BaseDirectory, "Prompts"));
            IChatCompletionService chat = ChatOverride ?? (Resilient
                ? new ResilientChatCompletion(Chat, llm, (_, _) => Task.CompletedTask)
                : Chat);
            return new AgenticOrchestrator(chat, Catalog, guard, prompts, SinkOverride ?? Audit, Options,
                new SafetyClassifier(ClassifierChat, llm), Logger, Recommendations,
                kernelFactory: (turn, tools, _) =>
                {
                    var k = Kernel.CreateBuilder().Build();
                    k.Data[TurnContextExtensions.DataKey] = turn;          // 不掛 filter：filter 在 FiltersTests 另測
                    AgentKernelFactory.AddFiltered(k, "Session", new SessionPlugin(turn, Catalog), tools);
                    AgentKernelFactory.AddFiltered(k, "Dialog", new DialogPlugin(turn, Catalog, Options), tools);
                    return k;
                });
        }

        public Task<List<AgentEvent>> RunAsync(string text, CancellationToken ct = default) => RunAsync(new TurnInput(text), ct);

        public async Task<List<AgentEvent>> RunAsync(TurnInput input, CancellationToken ct = default)
        {
            GuardChat.Then(FakeChatCompletion.Text(OkVerdict));
            ClassifierChat.Then(FakeChatCompletion.Text(OkVerdict));      // 用不到就留在佇列裡
            var events = new List<AgentEvent>();
            await foreach (var e in Build().RunTurnAsync(Session, input, ct)) events.Add(e);
            return events;
        }

        /// <summary>動手輪的輸入：先在 session 放一筆待確認（確認輪會留下的樣子），再組按下按鈕的那一輪。
        /// choices 有給時 choice 預設選第 0 個。</summary>
        public TurnInput ConfirmInput(string message = "我理解的畫面：一個銀髮少女。", IReadOnlyList<string>? choices = null, int? choice = null,
            bool autoComplete = false, Session? session = null)
        {
            var s = session ?? Session;
            var c = choices ?? Array.Empty<string>();
            var pending = new PendingConfirmation(s.TurnIndex, message, c, autoComplete);
            s.SetPendingConfirmation(pending);
            var confirmed = new ConfirmedInput(pending, c.Count == 0 ? null : choice ?? 0);
            return new TurnInput(confirmed.Text, Confirmed: confirmed);
        }

        /// <summary>跑一輪動手輪。RunAsync 照樣排一個分類器判定：動手輪用不到，留在佇列裡無妨；要驗「沒呼叫分類器」看 GuardChat.Calls。</summary>
        public Task<List<AgentEvent>> ActAsync(string message = "我理解的畫面：一個銀髮少女。", IReadOnlyList<string>? choices = null, int? choice = null,
            bool autoComplete = false) =>
            RunAsync(ConfirmInput(message, choices, choice, autoComplete));
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
        await foreach (var e in h.Build().RunTurnAsync(h.Session, new TurnInput("x"), default)) events.Add(e);
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
        await foreach (var e in h.Build().RunTurnAsync(h.Session, new TurnInput("x"), default)) events.Add(e);
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
        var events = await h.ActAsync();

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
        Assert.DoesNotContain("tagOrigins", completed.PayloadJson!);                      // 只有定稿那一輪才寫
        Assert.Contains("\"retrieval\":\"on\"", completed.PayloadJson!);   // 計畫 §4.1：事後分組用
        Assert.Contains("\"safety\":\"on\"", completed.PayloadJson!);
        var askCall = h.Session.ChatHistory.SelectMany(m => m.Items.OfType<FunctionCallContent>()).Single(c => c.FunctionName == "AskUser");
        Assert.DoesNotContain("photo realism", askCall.Arguments!["asks"]!.ToString());   // history 已壓縮
    }

    [Fact]
    public async Task Llm_exception_rolls_back_everything_and_emits_error()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) => { await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" }); throw new InvalidOperationException("boom"); });
        var events = await h.ActAsync();

        var err = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal("turn_failed", err.Code);
        Assert.Null(h.Session.Profile);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.NotNull(h.Session.PendingConfirmation);                    // 動手輪失敗：待確認回來，卡片可以再按
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
        await foreach (var e in h.Build().RunTurnAsync(h.Session, new TurnInput("一個少女"), default)) events.Add(e);

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

        var e = h.Build().RunTurnAsync(h.Session, h.ConfirmInput(), default).GetAsyncEnumerator();
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
        var events = await h.ActAsync();

        Assert.Equal("protocol_violation", Assert.Single(events.OfType<ErrorEvent>()).Code);
        Assert.Empty(events.OfType<FinalEvent>());
        Assert.Null(h.Session.Profile);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.NotNull(h.Session.PendingConfirmation);                    // Review Focus 4
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Failed" && a.PayloadJson!.Contains("ProtocolViolationException"));
    }

    /// <summary>Review Focus 4：動手輪沒有 Discuss，兩次都回「非空白」純文字也不能包成 Discuss（那等於繞過確認），照樣協定違反、整輪回滾、待確認還在。</summary>
    [Fact]
    public async Task Act_turn_plain_text_twice_is_protocol_violation_and_keeps_the_pending_confirmation()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { FakeChatCompletion.Text("好的，我已經幫你改好了。") };
        }).Then(FakeChatCompletion.Text("真的改好了。"));
        var events = await h.ActAsync();

        Assert.Equal("protocol_violation", Assert.Single(events.OfType<ErrorEvent>()).Code);
        Assert.Empty(events.OfType<FinalEvent>());
        Assert.Null(h.Session.Profile);
        Assert.Equal(0, h.Session.DiscussStreak);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.NotNull(h.Session.PendingConfirmation);
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
            await foreach (var _ in h.Build().RunTurnAsync(h.Session, new TurnInput("x"), cts.Token)) { }
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
        var events = await h.ActAsync();               // 沒有例外浮到這裡

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
        await h.ActAsync();
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

    /// <summary>測試用的審查開關：輸入端判 nsfw 也照跑，這一輪的 TurnContext 帶著 SafetyOn=false 給 OutputSafetyFilter，
    /// audit 記 safety 才分得出哪幾輪是關著審查跑的。</summary>
    [Fact]
    public async Task Safety_off_runs_the_turn_despite_an_nsfw_input_verdict_and_audits_it()
    {
        var h = new Harness { Deny = new[] { "bikini" } };
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        bool? seen = null;
        h.Chat.ThenAsync(async (hist, k) =>
        {
            seen = k!.Turn().SafetyOn;
            return new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：穿 bikini 的女生。" }) };
        });

        var events = await h.RunAsync(new TurnInput("穿著改成 bikini", SafetyOn: false));

        Assert.Empty(events.OfType<BlockedEvent>());
        Assert.Equal("confirm", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.False(seen);
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"safety\":\"off\"", completed.PayloadJson!);
    }

    /// <summary>純文字包成 Discuss 那條路自己檢一次輸出（C1）；審查關著時那次也不做。</summary>
    [Fact]
    public async Task Safety_off_wraps_plain_text_without_the_output_check()
    {
        var h = new Harness();
        h.ClassifierChat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"露骨描述"}"""));
        h.Chat.Then(FakeChatCompletion.Text("一段散文。")).Then(FakeChatCompletion.Text("一段散文。"));

        var events = await h.RunAsync(new TurnInput("寫實跟動漫差在哪", SafetyOn: false));

        Assert.Empty(events.OfType<BlockedEvent>());
        Assert.Equal("message", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Empty(h.ClassifierChat.Calls);
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

    /// <summary>送出去時排在 contents 最後的那一則：system 訊息不算，connector 會把它們全部搬進 systemInstruction。</summary>
    private static ChatMessageContent LastOnTheWire(ChatHistory h) => h.Last(m => m.Role != AuthorRole.System);

    private static int[] SystemIndexes(ChatHistory h) =>
        h.Select((m, i) => (m, i)).Where(x => x.m.Role == AuthorRole.System).Select(x => x.i).ToArray();

    /// <summary>known-issues #3：第一次回純文字時，那則 model 訊息若還留在 history，重試請求的 contents 就以 model 結尾，
    /// Gemini 回 400「Requests ending with a model turn are not supported.」。</summary>
    [Fact]
    public async Task Retry_request_does_not_end_with_the_first_attempts_model_text()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("好的，我來幫你整理。"))
              .ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("寫實走光影，動漫走筆觸。")) });

        var events = await h.RunAsync("寫實跟動漫差在哪");

        Assert.Equal(2, h.Chat.Calls.Count);
        Assert.Equal(AuthorRole.User, LastOnTheWire(h.Chat.Calls[1]).Role);
        Assert.Contains("必須", h.Chat.Calls[1].Last().Content!);                   // 提示照樣帶著
        Assert.Equal("message", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.DoesNotContain(h.Session.ChatHistory, m => m.Content == "好的，我來幫你整理。");
    }

    /// <summary>第一次先跑了工具才回純文字：拿掉那則文字後，重試請求以工具結果結尾（Gemini 送成 user 底下的 functionResponse）。</summary>
    [Fact]
    public async Task Retry_request_after_a_tool_call_ends_with_the_tool_result()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { FakeChatCompletion.Text("我先設好 profile。") };
        })
        .ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) });

        var events = await h.ActAsync();

        Assert.Equal(AuthorRole.Tool, LastOnTheWire(h.Chat.Calls[1]).Role);
        Assert.Equal("ask", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal("portrait", h.Session.Profile);
    }

    /// <summary>重試提示只給那一次呼叫看：留在 history 的話，connector 每一輪都會把它併進 systemInstruction，直到 Truncate 把那一輪剪掉。</summary>
    [Fact]
    public async Task Retry_reminder_does_not_outlive_the_turn()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("好的，我來幫你整理。"))
              .ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) });
        await h.RunAsync("寫實跟動漫差在哪");
        Assert.Equal(new[] { 0 }, SystemIndexes(h.Session.ChatHistory));

        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) });
        await h.RunAsync("那動漫呢");
        Assert.Equal(new[] { 0 }, SystemIndexes(h.Chat.Calls[2]));                   // 下一輪的請求只帶 system prompt
    }

    /// <summary>重試仍沒有終止型工具、也沒產出文字：包裝退回用第一次的文字（它被暫時拿出 history，要放回去）。</summary>
    [Fact]
    public async Task Wrap_falls_back_to_the_first_attempts_text_when_the_retry_is_blank()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("寫實走光影，動漫走筆觸。")).Then(FakeChatCompletion.Text("  "));

        var events = await h.RunAsync("寫實跟動漫差在哪");

        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("message", final.Kind);
        Assert.Contains("光影", final.Message!);
        Assert.Equal(1, h.Session.DiscussStreak);
        Assert.Single(h.Session.ChatHistory, m => m.Content == "寫實走光影，動漫走筆觸。");
        Assert.Equal(new[] { 0 }, SystemIndexes(h.Session.ChatHistory));
    }

    /// <summary>強制定稿的提示同理：定稿成功後不能留在 history。</summary>
    [Fact]
    public async Task Forced_finalize_reminder_does_not_outlive_the_turn()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            k!.Turn().Outcome = new BudgetExhaustedOutcome();
            return new[] { FakeChatCompletion.Text("") };
        })
        .ThenAsync(async (hist, k) =>
        {
            Assert.Contains("預算已用盡", hist.Last().Content!);
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", new { positivePrompt = "masterpiece, 1girl", negativePrompt = "lowres", tips = "t", intentSummary = "一個女生", facetStates = Array.Empty<object>() }) };
        });

        var events = await h.ActAsync();

        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal(new[] { 0 }, SystemIndexes(h.Session.ChatHistory));
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
            Assert.Contains("Dialog_FinalizePrompt", hist.Last().Content!);
            Assert.Contains("covered", hist.Last().Content!);
            Assert.Contains("留白", hist.Last().Content!);
            Assert.Equal(AuthorRole.System, hist.Last().Role);
            Assert.Single(k!.Plugins);                                 // 只剩 Dialog
            Assert.Single(k.Plugins["Dialog"]);                        // 只剩 FinalizePrompt
            return new[] { await Invoke(hist, k, "Dialog", "FinalizePrompt", new { positivePrompt = "masterpiece, 1girl", negativePrompt = "lowres", tips = "t", intentSummary = "一個女生", facetStates = Array.Empty<object>() }) };
        });
        var events = await h.ActAsync();
        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("finalized", final.Kind);
        Assert.NotNull(final.PositiveSources);
        Assert.NotNull(final.NegativeSources);
        Assert.Equal(SessionStatus.Finalized, h.Session.Status);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Tool_Budget_Exhausted");
        // positive 的四種來源計數，加總等於 positive 的 tag 數（eval #25）
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("""tagOrigins":{"rag":0,"adopted":0,"llm":1,"base":1}""", completed.PayloadJson!);
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

    internal sealed class StubRecommendations(Func<TurnOutcome, Task<RecommendationsEvent?>> impl) : IRecommendationService
    {
        public int Calls { get; private set; }
        public Task<RecommendationsEvent?> BuildAsync(Session s, FinalizedOutcome outcome, int turnIndex, CancellationToken ct) { Calls++; return impl(outcome); }
        public Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct) => throw new NotSupportedException();
    }

    private static RecommendationsEvent SomeRecommendations(int turnIndex) => new(turnIndex, new[]
    {
        new RecommendedDimension("style", "風格", false, Array.Empty<string>(), new[]
        {
            new RecommendedSet(7, "油畫", null, null, 0.2, new[] { new RecommendedFacet("style.genre", "藝術流派／媒材", "missing", new[] { "oil painting" }) }),
        }),
    });

    private static object FinalizeArgs() => new
    {
        positivePrompt = "masterpiece, 1girl", negativePrompt = "lowres", tips = "t", intentSummary = "一個女生", facetStates = Array.Empty<object>(),
    };

    /// <summary>已定稿的 session：定稿後 AskUser 不在清單上，FinalizePrompt 過得了定稿閘門。</summary>
    private static void MakeFinalized(Session s)
    {
        s.ApplyProfile("portrait", Catalog);
        s.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));
    }

    /// <summary>先確認再動手設計 §8：未定稿前不推薦。追問卡不呼叫推薦服務，也不發事件。</summary>
    [Fact]
    public async Task Ask_turn_never_calls_recommendations()
    {
        var h = new Harness();
        var stub = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(SomeRecommendations(1)));
        h.Recommendations = stub;
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.ActAsync();
        Assert.Equal("ask", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal(0, stub.Calls);
        Assert.Empty(events.OfType<RecommendationsEvent>());
    }

    /// <summary>設計 §5.1：推薦事件跟在 final 與 dimensions 之後；audit 記推薦了哪些 preset。</summary>
    [Fact]
    public async Task Recommendations_event_follows_final_and_is_audited()
    {
        var h = new Harness();
        h.Recommendations = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(SomeRecommendations(1)));
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        var events = await h.ActAsync("我會把背景改成黃昏。");
        var kinds = events.Select(e => e.Type).ToList();
        Assert.True(kinds.IndexOf("final") < kinds.LastIndexOf("dimensions") && kinds.LastIndexOf("dimensions") < kinds.IndexOf("recommendations"));
        Assert.Equal(7, Assert.Single(events.OfType<RecommendationsEvent>()).Dimensions[0].Sets[0].PresetId);
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("""recommendations":{"dimensions":[{"dimension":"style","anchored":false,"similar":false,"presetIds":[7]}]}""", completed.PayloadJson!);
    }

    /// <summary>2026-09-30 推薦組法設計 §5.2：定稿卡才有 batch 與每套的 reason／rank／prob；追問卡省略。</summary>
    [Fact]
    public async Task Slate_recommendations_audit_batch_and_each_set()
    {
        var h = new Harness();
        h.Recommendations = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(new RecommendationsEvent(1, new[]
        {
            new RecommendedDimension("style", "風格", false, Array.Empty<string>(), new[]
            {
                new RecommendedSet(7, "油畫", null, null, 0.2, Array.Empty<RecommendedFacet>(), "anchored", new[] { "oil painting" }, 0, 1.0),
                new RecommendedSet(8, "水彩", null, null, 0.3, Array.Empty<RecommendedFacet>(), "explore", Array.Empty<string>(), 2, 0.25),
            }, Batch: 1),
        })));
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        await h.ActAsync("我會把背景改成黃昏。");
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("""presetIds":[7,8],"batch":1,"sets":[{"presetId":7,"reason":"anchored","rank":0,"prob":1},{"presetId":8,"reason":"explore","rank":2,"prob":0.25}]""", completed.PayloadJson!);
    }

    /// <summary>設計 §9：推薦是附加的。final 已宣告，推薦炸了不能回滾、不能發 error。</summary>
    [Fact]
    public async Task Recommendation_failure_does_not_roll_back_the_turn()
    {
        var h = new Harness();
        h.Recommendations = new StubRecommendations(_ => throw new InvalidOperationException("db down"));
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        var events = await h.ActAsync("我會把背景改成黃昏。");
        Assert.Single(events.OfType<FinalEvent>());
        Assert.Empty(events.OfType<ErrorEvent>());
        Assert.Empty(events.OfType<RecommendationsEvent>());
        Assert.Equal("masterpiece, 1girl", h.Session.LastFinal!.Positive);   // 新的定稿已成立
        var failed = Assert.Single(h.Audit.Entries, a => a.EventType == "Recommendation_Failed");
        Assert.Contains("\"errorClass\":\"InvalidOperationException\"", failed.PayloadJson!);
        Assert.Contains("\"stage\":\"recommend\"", failed.PayloadJson!);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Completed");
    }

    [Fact]
    public async Task Recommendation_timeout_is_audited_as_timeout()
    {
        var h = new Harness();
        h.Options.RecommendationTimeoutSeconds = 0;        // CancellationTokenSource(0)：token 立刻取消
        h.Recommendations = new ObservingRecommendations();  // 會觀察 ct 的 stub，永遠等到被取消
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        var events = await h.ActAsync("我會把背景改成黃昏。");
        Assert.Single(events.OfType<FinalEvent>());
        Assert.Contains("\"errorClass\":\"Timeout\"", Assert.Single(h.Audit.Entries, a => a.EventType == "Recommendation_Failed").PayloadJson!);
    }

    private sealed class ObservingRecommendations : IRecommendationService
    {
        public async Task<RecommendationsEvent?> BuildAsync(Session s, FinalizedOutcome outcome, int turnIndex, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
        public Task<RecommendedDimension> NextAsync(Session s, string dimension, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Retrieval_off_never_calls_recommendations()
    {
        var h = new Harness();
        var stub = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(SomeRecommendations(1)));
        h.Recommendations = stub;
        var off = new Session("off", retrievalEnabled: false);
        MakeFinalized(off);
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        h.GuardChat.Then(FakeChatCompletion.Text(OkVerdict)); h.ClassifierChat.Then(FakeChatCompletion.Text(OkVerdict));
        var events = new List<AgentEvent>();
        await foreach (var e in h.Build().RunTurnAsync(off, h.ConfirmInput("我會把背景改成黃昏。", session: off), default)) events.Add(e);
        Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal(0, stub.Calls);
        Assert.Empty(events.OfType<RecommendationsEvent>());
    }

    private static TurnInput AdoptInput() => new(
        "採用〈和風女僕〉（知識庫 #41720）：上半身照它的（purple kimono, detached sleeves）；鞋履保留我的。",
        new Adoption(0, 41720, "和風女僕", "civitai:9:0", "clothing",
            new Dictionary<string, IReadOnlyList<string>> { ["clothing.upper"] = new[] { "purple kimono", "detached sleeves" } },
            new[] { "clothing.footwear" }, new[] { "clothing.upper" }, Array.Empty<string>()),
        new LedgerEntry { Id = 41720, Title = "和風女僕", PromptSnippet = "purple kimono, detached sleeves, sandals", FacetIds = new[] { "clothing.upper", "clothing.footwear" }, SourceRef = "civitai:9:0" });

    /// <summary>設計 §6.3／§8：採用輪的 session 事件帶伺服器組的句子；記帳、寫 ledger；定稿 tag 標 adopted；audit 記 adoption。</summary>
    [Fact]
    public async Task Adoption_turn_records_adoption_marks_ledger_and_audits()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        h.Session.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));   // 採用發生在定稿之後；Finalized 時不掛 AskUser，定稿閘門不會擋
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.StartsWith("採用〈", hist.Last(m => m.Role == AuthorRole.User).Content!);
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", new
            {
                positivePrompt = "masterpiece, 1girl, purple kimono, detached sleeves, sandals", negativePrompt = "lowres", tips = "t", intentSummary = "和服少女",
                facetStates = new[] { new { facetId = "clothing.upper", state = "covered", tags = "purple kimono, detached sleeves" } },
            }) };
        });
        var events = await h.RunAsync(AdoptInput());

        Assert.Equal(AdoptInput().Text, Assert.Single(events.OfType<SessionEvent>()).Text);
        var a = Assert.Single(h.Session.Adoptions);
        Assert.Equal(1, a.TurnIndex);                                                   // orchestrator 補上真正的輪次
        Assert.Equal("採用", Assert.Single(h.Session.Ledger.Get(41720)!.OfferedAs).Label);
        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal(new[] { "base", "llm", "adopted", "adopted", "rag" }, final.PositiveSources!.Select(x => x.Origin));   // sandals 在 ledger 片段裡
        var completed = Assert.Single(h.Audit.Entries, a2 => a2.EventType == "Turn_Completed");
        Assert.Contains("""adoption":{"presetId":41720,"dimension":"clothing","take":["clothing.upper"],"filled":["clothing.upper"],"replaced":[]}""", completed.PayloadJson!);
        Assert.Contains("""tagOrigins":{"rag":1,"adopted":2,"llm":1,"base":1}""", completed.PayloadJson!);
        Assert.Contains("\"kind\":\"adopt\"", completed.PayloadJson!);
        Assert.Contains("\"ragSplit\":{\"borrowed\":[\"sandals\"],\"echo\":[]}", completed.PayloadJson!);
        Assert.StartsWith("採用〈", completed.RawInput!);
    }

    /// <summary>2026-09-30 推薦組法設計 §5.2：前端送了 batch 就要進 audit；沒送（既有測試）維持原樣不帶這個鍵。</summary>
    [Fact]
    public async Task Adoption_audit_carries_the_batch_when_given()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        h.Session.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", new
        {
            positivePrompt = "masterpiece, 1girl, purple kimono", negativePrompt = "lowres", tips = "t", intentSummary = "和服少女",
            facetStates = new[] { new { facetId = "clothing.upper", state = "covered", tags = "purple kimono" } },
        }) });
        var input = AdoptInput();
        await h.RunAsync(input with { Adoption = input.Adoption! with { Batch = 2 } });
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("""replaced":[],"batch":2}""", completed.PayloadJson!);
    }

    [Fact]
    public async Task Ordinary_turn_session_event_has_no_text_and_no_adoption_in_audit()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：一個銀髮少女。" }) });
        var events = await h.RunAsync("一個銀髮少女");
        Assert.Null(Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.DoesNotContain("adoption", Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed").PayloadJson!);
    }

    /// <summary>設計 §9：採用那一輪失敗要連 Adoptions 與 ledger 一起回滾。</summary>
    [Fact]
    public async Task Failed_adoption_turn_rolls_back_adoptions_and_ledger()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        h.Session.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));
        h.Chat.Throw(new InvalidOperationException("boom"));
        var events = await h.RunAsync(AdoptInput());
        Assert.Single(events.OfType<ErrorEvent>());
        Assert.Empty(h.Session.Adoptions);
        Assert.False(h.Session.Ledger.Contains(41720));
    }

    // ---- known-issues #7：上游觀察紀錄進 audit、每輪一行摘要 log ----

    private static readonly UpstreamSafetyRating Explicit = new("HARM_CATEGORY_SEXUALLY_EXPLICIT", "MEDIUM", true);

    /// <summary>例外的 reason 是從訊息字面猜的；handler 從回應本文讀到的才可靠，audit 與使用者看到的都用它。</summary>
    [Fact]
    public async Task Upstream_block_uses_the_reason_and_ratings_the_handler_saw()
    {
        var h = new Harness();
        h.Chat.Then(_ =>
        {
            var d = UpstreamDiagnostics.Current!;
            d.CallStarted();
            d.CallCompleted(null, new UpstreamBlock("input_blocked", "PROHIBITED_CONTENT", new[] { Explicit, new UpstreamSafetyRating("HARM_CATEGORY_HARASSMENT", "NEGLIGIBLE", null) }));
            throw new UpstreamBlockedException("SAFETY", attempts: 2);
        });
        var events = await h.RunAsync("一個少女");

        Assert.Contains("PROHIBITED_CONTENT", Assert.Single(events.OfType<BlockedEvent>()).Message);
        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Blocked_Upstream");
        Assert.Equal("""{"reason":"PROHIBITED_CONTENT","stage":"loop","attempts":2,"upstream":{"kind":"input_blocked","reason":"PROHIBITED_CONTENT","safetyRatings":[{"category":"HARM_CATEGORY_SEXUALLY_EXPLICIT","probability":"MEDIUM","blocked":true},{"category":"HARM_CATEGORY_HARASSMENT","probability":"NEGLIGIBLE"}]}}""", row.PayloadJson);
    }

    [Fact]
    public async Task Output_block_seen_by_the_handler_is_recorded_as_output_blocked()
    {
        var h = new Harness();
        h.Chat.Then(_ =>
        {
            UpstreamDiagnostics.Current!.CallCompleted(null, new UpstreamBlock("output_blocked", "SAFETY", new[] { Explicit }));
            throw new UpstreamBlockedException("SAFETY");
        });
        await h.RunAsync("一個少女");

        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Blocked_Upstream");
        Assert.Contains("""upstream":{"kind":"output_blocked","reason":"SAFETY","safetyRatings":[{"category":"HARM_CATEGORY_SEXUALLY_EXPLICIT","probability":"MEDIUM","blocked":true}]}""", row.PayloadJson!);
    }

    /// <summary>handler 沒看到攔截（例如 fake、或 connector 換版改走別的路）：照舊用例外的 reason，不硬塞空的 upstream。</summary>
    [Fact]
    public async Task Upstream_block_without_a_handler_record_keeps_the_old_payload()
    {
        var h = new Harness();
        h.Chat.Throw(new UpstreamBlockedException("SAFETY"));
        await h.RunAsync("x");

        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Blocked_Upstream");
        Assert.Equal("""{"reason":"SAFETY","stage":"loop"}""", row.PayloadJson);
    }

    /// <summary>#3：connector 只留 "400 (Bad Request)"，上游到底嫌哪裡要看本文。</summary>
    [Fact]
    public async Task Http_error_turn_failed_carries_the_upstream_status_and_body()
    {
        var h = new Harness();
        const string body = """{"error":{"code":400,"message":"Please ensure that function call turn comes immediately after a user turn","status":"INVALID_ARGUMENT"}}""";
        h.Chat.Then(_ =>
        {
            var d = UpstreamDiagnostics.Current!;
            d.CallStarted();
            d.CallCompleted(new UpstreamHttpError(400, body), null);
            throw new HttpOperationException(System.Net.HttpStatusCode.BadRequest, body, "Response status code does not indicate success: 400 (Bad Request).", null);
        });
        await h.RunAsync("一個女生");

        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Failed");
        using var doc = JsonDocument.Parse(row.PayloadJson!);
        var p = doc.RootElement;
        Assert.Equal("HttpOperationException", p.GetProperty("errorClass").GetString());
        Assert.Equal("Response status code does not indicate success: 400 (Bad Request).", p.GetProperty("message").GetString());
        Assert.Equal(400, p.GetProperty("upstream").GetProperty("status").GetInt32());
        Assert.Equal(body, p.GetProperty("upstream").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Failure_without_an_http_error_record_has_no_upstream_field()
    {
        var h = new Harness();
        h.Chat.Then(_ =>
        {
            UpstreamDiagnostics.Current!.CallCompleted(null, null);           // 最近一次是 200
            throw new InvalidOperationException("boom");
        });
        await h.RunAsync("一個少女");

        Assert.DoesNotContain("upstream", Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Failed").PayloadJson!);
    }

    /// <summary>逾時：看得出這一輪打了幾次 Gemini、最後一次是不是卡在那裡。</summary>
    [Fact]
    public async Task Timeout_turn_failed_carries_the_call_count_and_the_pending_call()
    {
        var h = new Harness();
        h.Options.TurnTimeoutSeconds = 1;
        h.Chat.ThenAsync(async (hist, k, tct) =>
        {
            var d = UpstreamDiagnostics.Current!;
            d.CallStarted(); d.CallCompleted(null, null);
            d.CallStarted();                                          // 第二次一直沒回來
            await Task.Delay(Timeout.Infinite, tct);
            return Array.Empty<ChatMessageContent>();
        });
        await h.RunAsync("一個少女");

        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Failed");
        using var doc = JsonDocument.Parse(row.PayloadJson!);
        var p = doc.RootElement;
        Assert.Equal("Timeout", p.GetProperty("errorClass").GetString());
        var upstream = p.GetProperty("upstream");
        Assert.Equal(2, upstream.GetProperty("calls").GetInt32());
        Assert.InRange(upstream.GetProperty("pendingMs").GetInt64(), 500, 60_000);
    }

    [Fact]
    public async Task Timeout_with_no_call_in_flight_omits_pending_ms()
    {
        var h = new Harness();
        h.Options.TurnTimeoutSeconds = 1;
        h.Chat.ThenAsync(async (hist, k, tct) =>
        {
            var d = UpstreamDiagnostics.Current!;
            d.CallStarted(); d.CallCompleted(null, null);
            await Task.Delay(Timeout.Infinite, tct);                  // 卡在我們自己這邊，不是 Gemini
            return Array.Empty<ChatMessageContent>();
        });
        await h.RunAsync("一個少女");

        var row = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Failed");
        Assert.Contains("""
            "upstream":{"calls":1}
            """, row.PayloadJson!);
    }

    [Fact]
    public async Task Each_turn_ends_with_one_summary_log_line()
    {
        var log = new ListLogger<AgenticOrchestrator>();
        var h = new Harness { Logger = log };
        h.Chat.ThenAsync(async (hist, k) =>
        {
            UpstreamDiagnostics.Current!.CallStarted();
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        await h.ActAsync();
        h.Chat.Then(_ =>
        {
            UpstreamDiagnostics.Current!.CallStarted();
            UpstreamDiagnostics.Current!.CallStarted();
            throw new UpstreamBlockedException("SAFETY");
        });
        await h.RunAsync("她穿圍裙");

        var lines = log.Lines.Where(l => l.Message.StartsWith("Turn ")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(LogLevel.Information, l.Level));
        // 工具數：這個 harness 沒掛 filter，ToolCalls 不會累加，所以是 0
        Assert.Matches(@"^Turn s1#1 Turn_Completed AskOutcome tools=0 gemini=1 \d+ ms$", lines[0].Message);
        // 每輪一個新的 holder：第二輪從 0 算起；被擋的輪次 TurnIndex 回滾了，log 仍記它是第 2 輪
        Assert.Matches(@"^Turn s1#2 Blocked_Upstream SAFETY tools=0 gemini=2 \d+ ms$", lines[1].Message);
    }

    [Fact]
    public async Task Summary_log_covers_guard_blocks_and_failures_too()
    {
        var log = new ListLogger<AgenticOrchestrator>();
        var h = new Harness { Logger = log };
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        await foreach (var _ in h.Build().RunTurnAsync(h.Session, new TurnInput("x"), default)) { }
        h.Chat.Throw(new InvalidOperationException("boom"));
        await h.RunAsync("一個少女");

        var lines = log.Lines.Where(l => l.Message.StartsWith("Turn ")).Select(l => l.Message).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Matches(@"^Turn s1#1 Blocked_NSFW - tools=0 gemini=0 \d+ ms$", lines[0]);
        Assert.Matches(@"^Turn s1#1 Turn_Failed InvalidOperationException tools=0 gemini=0 \d+ ms$", lines[1]);
    }

    // ---- 先確認再動手（2026-10-05）----

    /// <summary>設計 §3.1：打字的那一輪只拿得到確認、討論與檢索；想直接改畫面也沒有工具可叫。</summary>
    [Fact]
    public async Task Text_turn_only_offers_confirm_discuss_and_search()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal(new[] { "Confirm", "Discuss" }, k!.Plugins["Dialog"].Select(f => f.Name).Order());
            Assert.False(k.Plugins.Contains("Session"));
            return new[] { await Invoke(hist, k, "Dialog", "Confirm", new { message = "我理解的畫面：一位金色短髮的中年女士站在雨夜的霓虹街頭。" }) };
        });
        var events = await h.RunAsync("一位金色短髮的中年女士站在雨夜的霓虹街頭");

        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("confirm", final.Kind);
        Assert.Empty(final.Choices!);
        Assert.Null(Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.Null(h.Session.Profile);                                                // 確認前什麼都沒動
        Assert.Equal(1, h.Session.PendingConfirmation!.TurnIndex);
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"outcome\":\"ConfirmOutcome\"", completed.PayloadJson!);
        Assert.Contains("\"confirmChoices\":0", completed.PayloadJson!);
        Assert.DoesNotContain("\"confirmed\"", completed.PayloadJson!);
    }

    /// <summary>設計 §3.5：按下確認的那一輪不跑輸入分類器、session 事件帶「對，就這樣」，只拿得到動手的工具，待確認被消耗掉。</summary>
    [Fact]
    public async Task Confirmed_turn_skips_the_guard_and_gets_the_tools_that_change_the_picture()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal("對，就這樣", hist.Last(m => m.Role == AuthorRole.User).Content);
            Assert.Contains("SetProfile", k!.Plugins["Session"].Select(f => f.Name));
            Assert.DoesNotContain("Confirm", k.Plugins["Dialog"].Select(f => f.Name));
            Assert.DoesNotContain("Discuss", k.Plugins["Dialog"].Select(f => f.Name));
            await Invoke(hist, k, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.ActAsync();

        Assert.Empty(h.GuardChat.Calls);
        Assert.Equal("對，就這樣", Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.Equal("ask", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Null(h.Session.PendingConfirmation);
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"confirmed\":{\"turnIndex\":0}", completed.PayloadJson!);      // 沒有選項：choice 省略
        Assert.Equal("對，就這樣", completed.RawInput);
    }

    [Fact]
    public async Task Choosing_an_interpretation_sends_that_sentence_and_audits_the_choice()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal("換掉飲料，改拿雨傘", hist.Last(m => m.Role == AuthorRole.User).Content);
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        var events = await h.ActAsync("她兩手已經拿著相機和飲料，你想要哪一種？", new[] { "換掉相機，改拿雨傘", "換掉飲料，改拿雨傘" }, choice: 1);

        Assert.Equal("換掉飲料，改拿雨傘", Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Contains("\"confirmed\":{\"turnIndex\":0,\"choice\":1}", Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed").PayloadJson!);
    }

    /// <summary>設計 §3.4：「隨便」在確認輪只記在待確認裡；按下確認才打開 AutoFill，動手輪也不給 AskUser。</summary>
    [Fact]
    public async Task Auto_complete_is_switched_on_only_by_the_confirmed_turn()
    {
        var h = new Harness();
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":true,"reason":"ok"}"""));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Equal(new[] { "Confirm" }, k!.Plugins["Dialog"].Select(f => f.Name));
            return new[] { await Invoke(hist, k, "Dialog", "Confirm", new { message = "我會直接定稿，風格補成寫實攝影。" }) };
        });
        await h.RunAsync("隨便，直接給我");
        Assert.False(h.Session.AutoFill);
        Assert.True(h.Session.PendingConfirmation!.AutoComplete);

        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.DoesNotContain("AskUser", k!.Plugins["Dialog"].Select(f => f.Name));
            await Invoke(hist, k, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        var events = await h.RunAsync(new TurnInput(ConfirmValidator.AcceptText, Confirmed: new ConfirmedInput(h.Session.PendingConfirmation!, null)));
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.True(h.Session.AutoFill);
    }

    /// <summary>設計 §6.1：確認輪預算用完只能強制確認，不能強制定稿（那等於跳過確認）。</summary>
    [Fact]
    public async Task Budget_exhausted_in_a_text_turn_forces_confirm_with_only_that_tool()
    {
        var h = new Harness();
        h.Chat.ThenAsync((hist, k) =>
        {
            k!.Turn().Outcome = new BudgetExhaustedOutcome();
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { FakeChatCompletion.Text("") });
        })
        .ThenAsync(async (hist, k) =>
        {
            Assert.Equal(AuthorRole.System, hist.Last().Role);
            Assert.Contains("Dialog_Confirm", hist.Last().Content!);
            Assert.Single(k!.Plugins);
            Assert.Equal("Confirm", Assert.Single(k.Plugins["Dialog"]).Name);
            return new[] { await Invoke(hist, k, "Dialog", "Confirm", new { message = "我理解的畫面：一個女生。" }) };
        });
        var events = await h.RunAsync("一個女生");
        Assert.Equal("confirm", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Null(h.Session.Profile);
        Assert.Equal(SessionStatus.Collecting, h.Session.Status);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Tool_Budget_Exhausted");
    }

    /// <summary>Review Focus 5：確認卡出現後打「好」是新的一輪確認，不是動手；討論不清掉待確認，新的確認卡取代舊的。</summary>
    [Fact]
    public async Task Typed_ok_after_a_confirm_card_is_another_text_turn()
    {
        var h = new Harness();
        h.Session.SetPendingConfirmation(new PendingConfirmation(0, "我理解的畫面：一個女生。", Array.Empty<string>(), false));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.False(k!.Plugins.Contains("Session"));
            return new[] { await Invoke(hist, k, "Dialog", "Discuss", DiscussArgs("要套用的話請按確認卡上的按鈕。")) };
        });
        await h.RunAsync("好");
        Assert.Equal("我理解的畫面：一個女生。", h.Session.PendingConfirmation!.Message);

        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：一個穿紅衣的女生。" }) });
        await h.RunAsync("衣服要紅色");
        Assert.Equal(2, h.Session.PendingConfirmation!.TurnIndex);
        Assert.Equal("我理解的畫面：一個穿紅衣的女生。", h.Session.PendingConfirmation.Message);
    }

    /// <summary>設計 §3.4：採用輪也是動手輪，清掉待確認。</summary>
    [Fact]
    public async Task Adoption_clears_a_pending_confirmation()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Session.SetPendingConfirmation(new PendingConfirmation(0, "m", Array.Empty<string>(), false));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) });
        await h.RunAsync(AdoptInput());
        Assert.Null(h.Session.PendingConfirmation);
    }

    /// <summary>補救提示只列這一輪實際有的收尾工具（設計 §5.3）。</summary>
    [Fact]
    public async Task Retry_reminder_names_only_this_turns_terminal_tools()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("好的。"))
              .ThenAsync(async (hist, k) =>
              {
                  Assert.Contains("你必須呼叫 Dialog_Confirm、Dialog_Discuss 之一", hist.Last().Content!);
                  Assert.DoesNotContain("FinalizePrompt", hist.Last().Content!);
                  return new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) };
              });
        var events = await h.RunAsync("寫實跟動漫差在哪");
        Assert.Equal("message", Assert.Single(events.OfType<FinalEvent>()).Kind);      // 斷言在 callback 裡：沒有這行，callback 丟例外也會過
        Assert.Equal(2, h.Chat.Calls.Count);
    }

    /// <summary>只剩一個收尾工具時用單數說法，而且用宣告的全名：裸名會落進 SK 的未定義路徑、繞過預算（設計 §5.3）。</summary>
    [Fact]
    public async Task Retry_reminder_with_a_single_terminal_tool_uses_the_singular_full_name()
    {
        var h = new Harness();
        MakeFinalized(h.Session);                                    // 已定稿：動手輪沒有 AskUser，只剩 FinalizePrompt
        h.Chat.Then(FakeChatCompletion.Text("好的。"))
              .ThenAsync(async (hist, k) =>
              {
                  Assert.Contains("你必須呼叫 Dialog_FinalizePrompt 來結束這一輪", hist.Last().Content!);
                  Assert.DoesNotContain("之一", hist.Last().Content!);
                  return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) };
              });
        var events = await h.ActAsync();
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal(2, h.Chat.Calls.Count);
    }

    /// <summary>設計 §5.2：動手輪的 system prompt 帶使用者確認的內容與選的解讀。</summary>
    [Fact]
    public async Task Confirmed_turn_prompt_carries_what_the_user_confirmed()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Chat.ThenAsync(async (hist, k) =>
        {
            var sys = hist[0].Content!;
            Assert.Contains("### 使用者已確認", sys);
            Assert.Contains("使用者選的是：換掉飲料，改拿雨傘", sys);
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        var events = await h.ActAsync("她兩手已經拿著相機和飲料，你想要哪一種？", new[] { "換掉相機，改拿雨傘", "換掉飲料，改拿雨傘" }, choice: 1);
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
    }

    [Fact]
    public async Task Text_turn_prompt_is_the_propose_flow()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.Contains("這一輪是**確認輪**", hist[0].Content!);
            Assert.DoesNotContain("### 使用者已確認", hist[0].Content!);
            return new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我理解的畫面：一個女生。" }) };
        });
        var events = await h.RunAsync("一個女生");
        Assert.Equal("confirm", Assert.Single(events.OfType<FinalEvent>()).Kind);
    }

    // ---- 檢索時機（2026-10-06）----

    /// <summary>設計 §5.2：每輪記輪別與檢索計數；確認輪說隨便時記 autoComplete。</summary>
    [Fact]
    public async Task Turn_completed_records_kind_search_counts_and_auto_complete()
    {
        var h = new Harness();
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":true,"reason":"ok"}"""));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我會直接定稿，風格補成寫實攝影。" }) });
        await h.RunAsync("隨便，直接給我");

        h.Chat.ThenAsync(async (hist, k) =>
        {
            var t = k!.Turn(); t.Searches = 1; t.SearchItems = 3; t.SearchItemErrors = 1;   // KnowledgePlugin 會累加；harness 沒掛它，直接寫
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        await h.RunAsync(new TurnInput(ConfirmValidator.AcceptText, Confirmed: new ConfirmedInput(h.Session.PendingConfirmation!, null)));

        var completed = h.Audit.Entries.Where(a => a.EventType == "Turn_Completed").Select(a => a.PayloadJson!).ToList();
        Assert.Equal(2, completed.Count);
        Assert.Contains("\"kind\":\"propose\"", completed[0]);
        Assert.Contains("\"searches\":0", completed[0]);
        Assert.Contains("\"autoComplete\":true", completed[0]);
        Assert.Contains("\"kind\":\"act\"", completed[1]);
        Assert.Contains("\"searches\":1", completed[1]);
        Assert.Contains("\"searchItems\":3", completed[1]);
        Assert.Contains("\"searchItemErrors\":1", completed[1]);
        Assert.DoesNotContain("autoComplete", completed[1]);                        // 只有確認輪記
    }

    /// <summary>設計 §5.2：追問卡記選項數與帶 presetId 的；不是定稿就沒有 ragSplit。</summary>
    [Fact]
    public async Task Ask_turn_records_option_counts()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        await h.ActAsync();
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"options\":{\"total\":2,\"withPreset\":0}", completed.PayloadJson!);
        Assert.DoesNotContain("ragSplit", completed.PayloadJson!);
    }

    /// <summary>設計 §5.1：定稿記 rag 的借來／碰巧對上（tag 原文）。</summary>
    [Fact]
    public async Task Finalized_turn_records_rag_split()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Session.Ledger.Timeline.SeeModel("cafe");                                  // 模型先寫
        h.Session.Ledger.Record(new LedgerEntry { Id = 4581, Title = "夜間咖啡廳", PromptSnippet = "night, cafe, neon lights, streetspace", FacetIds = new[] { "scene.location" } },
            new LedgerHit("scene", 0.2, true));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", new
        {
            positivePrompt = "masterpiece, cafe, streetspace, long coat", negativePrompt = "lowres", tips = "t", intentSummary = "雨夜咖啡廳前的女士",
            facetStates = Array.Empty<object>(),
        }) });
        await h.ActAsync("我會把背景改成深夜咖啡廳前。");
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"ragSplit\":{\"borrowed\":[\"streetspace\"],\"echo\":[\"cafe\"]}", completed.PayloadJson!);
        Assert.Contains("\"kind\":\"act\"", completed.PayloadJson!);
    }

    /// <summary>設計 §4：確認輪的 SearchPresets 結果，動手輪看得到完整片段，動手輪收尾才壓掉。</summary>
    [Fact]
    public async Task Propose_turn_search_results_survive_until_the_next_turn_ends()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        const string search = """{"results":[{"dimension":"clothing","facetId":"clothing.upper","query":"家居服","grounded":false,"poolSize":70,"hits":[{"id":9726,"title":"粉紅睡衣","positive":"pink pajamas"}]}]}""";
        static bool HasSnippet(ChatHistory hist) =>
            hist.Any(m => m.Items.OfType<FunctionResultContent>().Any(r => r.Result?.ToString()?.Contains("pink pajamas") == true));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            // 模擬 connector 已跑完一次 SearchPresets（harness 沒掛 KnowledgePlugin）
            var call = new FunctionCallContent(ToolNames.SearchPresets, "Knowledge", "c-search");
            var callMsg = new ChatMessageContent(AuthorRole.Assistant, content: null); callMsg.Items.Add(call); hist.Add(callMsg);
            var toolMsg = new ChatMessageContent(AuthorRole.Tool, content: null); toolMsg.Items.Add(new FunctionResultContent(call, search)); hist.Add(toolMsg);
            return new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "穿著我會從知識庫挑粉紅睡衣。" }) };
        });
        var proposeEvents = await h.RunAsync("衣服你幫我設計");
        Assert.Empty(proposeEvents.OfType<ErrorEvent>());
        Assert.True(HasSnippet(h.Session.ChatHistory));                               // 確認輪收尾沒壓

        // 在 lambda 裡斷言會被 RunTurnAsync 的 catch-all 吞掉（回滾＋ErrorEvent），所以只記錄、跑完再斷言
        var sawSnippetInAct = false;
        h.Chat.ThenAsync(async (hist, k) =>
        {
            sawSnippetInAct = HasSnippet(hist);                                       // 動手輪看得到
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var actEvents = await h.RunAsync(new TurnInput(ConfirmValidator.AcceptText, Confirmed: new ConfirmedInput(h.Session.PendingConfirmation!, null)));
        Assert.Empty(actEvents.OfType<ErrorEvent>());
        Assert.True(sawSnippetInAct);
        Assert.False(HasSnippet(h.Session.ChatHistory));                              // 動手輪收尾壓掉
    }
}
