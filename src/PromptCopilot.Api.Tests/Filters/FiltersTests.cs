using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Filters;

public class FiltersTests
{
    private sealed class MemorySink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = new();
        public Exception? Throw { get; set; }
        public Task WriteAsync(AuditEntry e, CancellationToken ct) { if (Throw is not null) throw Throw; Entries.Add(e); return Task.CompletedTask; }
    }

    private static (Kernel kernel, TurnContext turn, Channel<AgentEvent> events) Kernel()
    {
        var ch = Channel.CreateUnbounded<AgentEvent>();
        var turn = new TurnContext(new Session("s"), 2, GuardResult.Ok(false), ToolNames.Always, ch.Writer);
        var k = Microsoft.SemanticKernel.Kernel.CreateBuilder().Build();
        k.Data[TurnContextExtensions.DataKey] = turn;
        return (k, turn, ch);
    }

    /// <summary>手工組一個 AutoFunctionInvocationContext。建構子簽名隨 SK 版本略有不同（U7）。</summary>
    private static AutoFunctionInvocationContext Ctx(Kernel k, string functionName, params (string key, object value)[] args)
    {
        var fn = KernelFunctionFactory.CreateFromMethod(() => "ok", functionName);
        return new AutoFunctionInvocationContext(k, fn, new FunctionResult(fn), new ChatHistory(), new ChatMessageContent(AuthorRole.Assistant, ""))
        {
            Arguments = new KernelArguments(args.ToDictionary(a => a.key, a => (object?)a.value)),
        };
    }

    private static Func<AutoFunctionInvocationContext, Task> Next(Action? onCalled = null) => _ => { onCalled?.Invoke(); return Task.CompletedTask; };

    [Fact]
    public async Task Terminal_terminates_only_when_outcome_was_set()
    {
        var (k, turn, _) = Kernel();
        var f = new TerminalToolFilter();
        var c1 = Ctx(k, "SearchPresets");
        await f.OnAutoFunctionInvocationAsync(c1, Next());
        Assert.False(c1.Terminate);
        var c2 = Ctx(k, "Discuss");
        await f.OnAutoFunctionInvocationAsync(c2, Next(() => turn.Outcome = new MessageOutcome("m", Array.Empty<OptionItem>())));
        Assert.True(c2.Terminate);
    }

    [Fact]
    public async Task Budget_short_circuits_and_sets_outcome_once_exceeded()
    {
        var (k, turn, _) = Kernel();
        var f = new ToolBudgetFilter(new OrchestratorOptions { MaxToolCallsPerTurn = 2 });
        var called = 0;
        for (var i = 0; i < 2; i++) { var c = Ctx(k, "SearchPresets"); await f.OnAutoFunctionInvocationAsync(c, Next(() => called++)); Assert.False(c.Terminate); }
        var c3 = Ctx(k, "SearchPresets");
        await f.OnAutoFunctionInvocationAsync(c3, Next(() => called++));
        Assert.True(c3.Terminate); Assert.Equal(2, called);
        Assert.IsType<BudgetExhaustedOutcome>(turn.Outcome);
        Assert.Contains("預算", c3.Result.ToString());
        Assert.Equal(3, turn.ToolCalls);
    }

    [Fact]
    public async Task OutputSafety_only_classifies_dialog_tools()
    {
        var (k, _, _) = Kernel();
        var chat = new FakeChatCompletion();
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        await f.OnAutoFunctionInvocationAsync(Ctx(k, "SearchPresets", ("query", "x")), Next(() => called = true));
        Assert.True(called); Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task OutputSafety_terminates_with_blocked_outcome_when_flagged()
    {
        var (k, turn, _) = Kernel();
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        var c = Ctx(k, "Discuss", ("message", "…"));
        await f.OnAutoFunctionInvocationAsync(c, Next(() => called = true));
        Assert.False(called); Assert.True(c.Terminate);
        Assert.Equal("r", Assert.IsType<BlockedOutcome>(turn.Outcome).Reason);
    }

    /// <summary>測試用的審查開關關著：分類器不呼叫、不攔，tool 照常跑。</summary>
    [Fact]
    public async Task OutputSafety_passes_through_without_classifying_when_the_turn_has_safety_off()
    {
        var (k, turn, _) = Kernel();
        turn.SafetyOn = false;
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        var c = Ctx(k, "FinalizePrompt", ("positivePrompt", "1girl, bikini"));
        await f.OnAutoFunctionInvocationAsync(c, Next(() => called = true));
        Assert.True(called); Assert.False(c.Terminate);
        Assert.Null(turn.Outcome);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task OutputSafety_passes_clean_content()
    {
        var (k, turn, _) = Kernel();
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"ok"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        await f.OnAutoFunctionInvocationAsync(Ctx(k, "FinalizePrompt", ("positivePrompt", "1girl")), Next(() => called = true));
        Assert.True(called); Assert.Null(turn.Outcome);
    }

    /// <summary>Gemini 回來的參數是 JsonElement，原文把非 ASCII 全 escape 成 \uXXXX。
    /// 分類器看的就是這段文字，拿到跳脫序列等於要它自己先解碼一次才能判斷。</summary>
    [Fact]
    public void ArgsText_hands_over_unescaped_text()
    {
        var text = TurnContextExtensions.ArgsText(new KernelArguments { ["preamble"] = GeminiPreamble, ["asks"] = GeminiAsks });

        Assert.Contains("少女在海邊", text);
        Assert.Contains("寫實", text);
        Assert.Contains("雨夜的霓虹街頭", text);
        Assert.DoesNotContain("\\u", text);
        Assert.Contains("photo", text);                          // 英文 tag 照舊看得到
        Assert.Contains("dimension", text);                      // 巢狀結構還在，只是不 escape
        Assert.DoesNotContain("\"雨夜的霓虹街頭\"", text);        // 字串元素連引號都不留
    }

    [Fact]
    public async Task OutputSafety_classifies_the_unescaped_text()
    {
        var (k, _, _) = Kernel();
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"ok"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        await f.OnAutoFunctionInvocationAsync(Ctx(k, "AskUser", ("preamble", GeminiPreamble), ("asks", GeminiAsks)), Next());

        var sent = Assert.Single(chat.Calls)[0].Content;
        Assert.Contains("少女", sent);
        Assert.Contains("寫實", sent);
        Assert.Contains("雨夜的霓虹街頭", sent);
        Assert.DoesNotContain("\\u", sent);
    }

    /// <summary>主規格 §6.2 把 FinalizePrompt 的輸出檢查限在 positivePrompt：SD 的負向詞本來就
    /// 長成「nsfw, nude, naked」（那是排除清單），整包參數餵進去等於叫分類器攔自己的排除詞。
    /// `intentSummary` 會進共享庫，跟 `positivePrompt` 同一等級（子專案 3 設計 §2.3）。</summary>
    [Fact]
    public void OutputTextFor_finalize_takes_positive_tips_and_intent_summary()
    {
        var text = TurnContextExtensions.OutputTextFor("FinalizePrompt", new KernelArguments
        {
            ["positivePrompt"] = "1girl, silver hair, neon street",
            ["negativePrompt"] = "nsfw, nude, naked, lowres",
            ["tips"] = "服裝留白，可自行補上",
            ["intentSummary"] = "雨夜霓虹街頭的少女",
            ["facetStates"] = new[] { new { facetId = "appearance.hair", state = "covered" } },
        });

        Assert.Contains("silver hair", text);
        Assert.Contains("服裝留白", text);
        Assert.Contains("雨夜霓虹街頭的少女", text);
        Assert.DoesNotContain("nsfw", text);
        Assert.DoesNotContain("nude", text);
        Assert.DoesNotContain("facetId", text);
    }

    [Fact]
    public void OutputTextFor_askuser_keeps_nested_labels_and_drops_states()
    {
        var text = TurnContextExtensions.OutputTextFor("AskUser", new KernelArguments
        {
            ["preamble"] = GeminiPreamble,
            ["asks"] = GeminiAsks,
            ["facetStates"] = new[] { new { facetId = "style.genre", state = "missing" } },
        });

        Assert.Contains("雨夜的霓虹街頭", text);
        Assert.Contains("少女在海邊", text);      // question
        Assert.Contains("寫實", text);            // option label
        Assert.Contains("photo", text);           // option tags
        Assert.DoesNotContain("facetId", text);
        Assert.DoesNotContain("\\u", text);
    }

    [Fact]
    public void OutputTextFor_discuss_keeps_message_and_option_labels()
    {
        var text = TurnContextExtensions.OutputTextFor("Discuss", new KernelArguments
        {
            ["message"] = "寫實走光影，動漫走筆觸。",
            ["options"] = new[] { new OptionItem("霓虹夜景", "neon night", 5L) },
            ["facetStates"] = new[] { new { facetId = "style.genre", state = "covered" } },
        });

        Assert.Contains("光影", text);
        Assert.Contains("霓虹夜景", text);
        Assert.Contains("neon night", text);
        Assert.DoesNotContain("facetId", text);
    }

    [Fact]
    public async Task OutputSafety_never_sends_the_negative_prompt_to_the_classifier()
    {
        var (k, turn, _) = Kernel();
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"ok"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        await f.OnAutoFunctionInvocationAsync(
            Ctx(k, "FinalizePrompt", ("positivePrompt", "1girl, silver hair"), ("negativePrompt", "nsfw, nude, naked"), ("tips", "t")),
            Next(() => called = true));

        var sent = Assert.Single(chat.Calls)[0].Content!;
        Assert.Contains("silver hair", sent);
        Assert.DoesNotContain("nude", sent);
        Assert.True(called); Assert.Null(turn.Outcome);
    }

    // 這兩個 fixture 必須是「原文帶 \uXXXX」的 JsonElement，否則測不到東西：
    // GetRawText() 只是把來源原文原樣吐回去，來源要是已經解碼過的中文，舊實作也會過。
    // JsonSerializer 的預設 encoder 正好會把非 ASCII escape 掉，跟 Gemini 回來的樣子一致，
    // 所以用它在執行期產生來源，而不是在原始碼裡寫字面的反斜線 u。
    private static readonly JsonElement GeminiAsks = Escaped(new[]
    {
        new { dimension = "風格", question = "少女在海邊，想要什麼風格？", options = new[] { new { label = "寫實", tags = "photo" } } },
    });
    private static readonly JsonElement GeminiPreamble = Escaped(new { p = "雨夜的霓虹街頭" }).GetProperty("p");

    /// <summary>Clone：JsonDocument 一被回收，沒 detach 的 JsonElement 就失效了。</summary>
    private static JsonElement Escaped(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    [Fact]
    public async Task Audit_emits_tool_call_event_and_writes_one_row()
    {
        var (k, _, ch) = Kernel();
        var sink = new MemorySink();
        await new AuditFilter(sink, NullLogger<AuditFilter>.Instance).OnAutoFunctionInvocationAsync(Ctx(k, "SetProfile", ("profile", "portrait")), Next());
        Assert.True(ch.Reader.TryRead(out var e)); Assert.IsType<ToolCallEvent>(e);
        var row = Assert.Single(sink.Entries);
        Assert.Equal("Tool_Invoked", row.EventType); Assert.Equal("s", row.SessionId); Assert.Equal(2, row.TurnIndex);
        Assert.Contains("SetProfile", row.PayloadJson);
    }

    /// <summary>tool_result.callId 要對得上 tool_call.callId（主規格 §10.2）。call id 只有
    /// AuditFilter 算得出來，所以由它掛到 TurnContext 上讓 plugin 取用。</summary>
    [Fact]
    public async Task Audit_publishes_the_call_id_for_the_plugins_to_reuse()
    {
        var (k, turn, ch) = Kernel();
        string? seen = null;
        await new AuditFilter(new MemorySink(), NullLogger<AuditFilter>.Instance).OnAutoFunctionInvocationAsync(
            Ctx(k, "SearchPresets", ("dimension", "style")), _ => { seen = turn.CurrentCallId; return Task.CompletedTask; });

        Assert.True(ch.Reader.TryRead(out var e));
        var call = Assert.IsType<ToolCallEvent>(e);
        Assert.Equal("0-0", seen);                    // {RequestSequenceIndex}-{FunctionSequenceIndex}
        Assert.Equal(call.CallId, seen);
        Assert.Null(turn.CurrentCallId);              // 跑完就清掉，不會漏到下一個 tool
    }

    [Fact]
    public async Task Audit_failure_does_not_fail_the_tool_call()
    {
        var (k, turn, _) = Kernel();
        var sink = new MemorySink { Throw = new IOException("db down") };
        var called = false;
        await new AuditFilter(sink, NullLogger<AuditFilter>.Instance).OnAutoFunctionInvocationAsync(Ctx(k, "SetProfile"), Next(() => called = true));
        Assert.True(called);
        Assert.Contains(turn.Rejections, r => r.Contains("audit"));
    }
}
