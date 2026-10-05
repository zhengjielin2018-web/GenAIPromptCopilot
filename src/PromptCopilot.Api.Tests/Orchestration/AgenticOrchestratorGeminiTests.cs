using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Tests.Orchestration;

/// <summary>known-issues #3：用真的 Google connector 跑 AgenticOrchestrator 的純文字補救，看它實際送出去的請求。
/// fake chat 只看得到 ChatHistory，看不到 connector 怎麼把它排成 contents／systemInstruction——這個 bug 就出在那一步。
/// HTTP 用假 handler，不打 Gemini；組法跟 Program.cs 一樣經過 GeminiRoleFixHandler。</summary>
public class AgenticOrchestratorGeminiTests
{
    /// <summary>2026-09-29 對 gemini-3.5-flash-lite（v1beta generateContent）實打拿到的狀態與訊息（外層照 Gemini 一般的錯誤格式包）：contents 以 model 結尾，
    /// 有沒有帶 tools 都一樣；後面補一則 user 就 200。</summary>
    private const string EndsWithModel400 =
        """{"error":{"code":400,"message":"Requests ending with a model turn are not supported.","status":"INVALID_ARGUMENT"}}""";

    /// <summary>照順序回罐頭回應、記下每次請求的本文；contents 以 model 結尾時照 Gemini 的規則回 400，不消耗罐頭。</summary>
    private sealed class GeminiLike(params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(replies);
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            var last = JsonDocument.Parse(body).RootElement.GetProperty("contents").EnumerateArray().Last();
            if (last.GetProperty("role").GetString() == "model")
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(EndsWithModel400, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(queue.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }

    private static IChatCompletionService Gemini(HttpMessageHandler http) =>
        new GoogleAIGeminiChatCompletionService("gemini-test", "test", GoogleAIVersion.V1_Beta, new HttpClient(new GeminiRoleFixHandler(http)));

    /// <summary>純文字回覆，帶 thoughtSignature：跟實測擷取到的第一次回覆同形狀。</summary>
    private static string TextReply(string text, string signature) =>
        HistoryTrimmerGeminiTests.Reply(JsonSerializer.Serialize(new { text, thoughtSignature = signature },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    private static string[] SystemParts(JsonElement body) =>
        body.GetProperty("systemInstruction").GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("text").GetString()!).ToArray();

    private static string[] Roles(JsonElement body) =>
        body.GetProperty("contents").EnumerateArray().Select(c => c.GetProperty("role").GetString()!).ToArray();

    /// <summary>contents 裡所有 text part（解析後比對：connector 可能把中文跳脫成 \uXXXX，直接比本文字串會誤判）。</summary>
    private static string[] Texts(JsonElement body) =>
        body.GetProperty("contents").EnumerateArray()
            .SelectMany(c => c.GetProperty("parts").EnumerateArray())
            .Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()!).ToArray();

    private static JsonElement Parse(string body) => JsonDocument.Parse(body).RootElement;

    /// <summary>第一次回純文字 → 補提示重試。重試請求必須以 user 結尾（修正前以 model 結尾，Gemini 回 400、整輪 Turn_Failed），
    /// 重試提示在 systemInstruction；下一輪的請求不再帶這則提示。</summary>
    [Fact]
    public async Task Plain_text_retry_sends_a_request_gemini_accepts_and_drops_the_reminder_afterwards()
    {
        var http = new GeminiLike(
            TextReply("好的，我來幫你整理。", "SIG-2"), TextReply("寫實走光影，動漫走筆觸。", "SIG-3"),     // 第一輪：兩次純文字 → 包成 Discuss
            TextReply("動漫多半是平塗。", "SIG-4"), TextReply("動漫多半是平塗。", "SIG-5"));                  // 第二輪：同一條路再走一次
        var h = new AgenticOrchestratorTests.Harness { ChatOverride = Gemini(http) };

        var first = await h.RunAsync("寫實跟動漫差在哪");

        Assert.Empty(first.OfType<ErrorEvent>());
        Assert.Equal("message", Assert.Single(first.OfType<FinalEvent>()).Kind);
        Assert.Equal(2, http.Bodies.Count);                                                  // 沒有被 400 擋掉的請求
        var retry = Parse(http.Bodies[1]);
        Assert.Equal(new[] { "user" }, Roles(retry));                                       // 第一次的 model 文字不在重試請求裡
        var sys = SystemParts(retry);
        Assert.Equal(2, sys.Length);
        Assert.Contains("你必須呼叫", sys[1]);
        Assert.DoesNotContain("好的，我來幫你整理。", Texts(retry));

        var second = await h.RunAsync("那動漫呢");

        Assert.Equal("message", Assert.Single(second.OfType<FinalEvent>()).Kind);
        var next = Parse(http.Bodies[2]);
        Assert.Single(SystemParts(next));                                                   // 上一輪的重試提示沒有被帶過來
        Assert.DoesNotContain(Texts(next), t => t.Contains("你必須呼叫"));
        Assert.Equal(new[] { "user", "model", "user" }, Roles(next));
        Assert.Contains("\"thoughtSignature\":\"SIG-3\"", http.Bodies[2]);                  // 留下來的是重試那次的回覆，簽章跟著走
    }

    /// <summary>跟 Program.cs 一樣的 handler 鏈：名稱修正在最外層，看得到請求宣告了哪些工具。</summary>
    private static IChatCompletionService GeminiGuarded(HttpMessageHandler http) =>
        new GoogleAIGeminiChatCompletionService("gemini-test", "test", GoogleAIVersion.V1_Beta,
            new HttpClient(new GeminiToolNameHandler(new GeminiRoleFixHandler(http), NullLogger<GeminiToolNameHandler>.Instance)));

    private static string CallReply(string name, string signature) => HistoryTrimmerGeminiTests.Reply(
        $$$"""{"functionCall":{"name":"{{{name}}}","args":{"message":"一位金色短髮的中年女士站在雨夜的霓虹街頭。"}},"thoughtSignature":"{{{signature}}}"}""");

    /// <summary>contents 裡 model 送出的 functionCall 名稱（不含 tools 的宣告）。</summary>
    private static string[] CalledNames(JsonElement body) =>
        body.GetProperty("contents").EnumerateArray()
            .SelectMany(c => c.GetProperty("parts").EnumerateArray())
            .Where(p => p.TryGetProperty("functionCall", out _)).Select(p => p.GetProperty("functionCall").GetProperty("name").GetString()!).ToArray();

    /// <summary>不管問什麼都回同一則：模型卡在同一個呼叫上的樣子。</summary>
    private sealed class Stuck(string reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>known-issues #13：模型寫裸名 Confirm。修正前 SK 回「function that wasn't defined」、不經 filter，模型重送到逾時；
    /// 現在改寫成 Dialog_Confirm，照常進 plugin，一輪就出確認卡。送回去的 call 是全名、簽章照帶。</summary>
    [Fact]
    public async Task Bare_tool_name_lands_on_the_declared_tool()
    {
        var http = new GeminiLike(CallReply("Confirm", "SIG-1"), TextReply("好", "SIG-2"));
        var h = new AgenticOrchestratorTests.Harness { ChatOverride = GeminiGuarded(http) };

        var events = await h.RunAsync("一位金色短髮的中年女士站在雨夜的霓虹街頭");

        Assert.Empty(events.OfType<ErrorEvent>());
        Assert.Equal("confirm", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.DoesNotContain(h.Audit.Entries, a => a.EventType == "Protocol_Violation");
        Assert.Equal(2, http.Bodies.Count);
        Assert.Equal(new[] { "Dialog_Confirm" }, CalledNames(Parse(http.Bodies[1])));
        Assert.Contains("\"thoughtSignature\":\"SIG-1\"", http.Bodies[1]);
        var done = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Equal(1, Parse(done.PayloadJson!).GetProperty("toolNameRepairs").GetInt32());
    }

    /// <summary>確認輪叫 SetProfile（這一輪沒有）：第二次就中止，改走補提示重試，提示點名它不在清單裡；重試照清單叫就成功。</summary>
    [Fact]
    public async Task Repeated_undeclared_call_is_cut_short_and_retried_with_a_reminder()
    {
        var http = new GeminiLike(CallReply("SetProfile", "SIG-1"), CallReply("SetProfile", "SIG-2"), CallReply("Dialog_Confirm", "SIG-3"), TextReply("好", "SIG-4"));
        var h = new AgenticOrchestratorTests.Harness { ChatOverride = GeminiGuarded(http) };

        var events = await h.RunAsync("一位金色短髮的中年女士站在雨夜的霓虹街頭");

        Assert.Empty(events.OfType<ErrorEvent>());
        Assert.Equal("confirm", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal(4, http.Bodies.Count);
        var violation = Assert.Single(h.Audit.Entries, a => a.EventType == "Protocol_Violation");
        var payload = Parse(violation.PayloadJson!);
        Assert.Equal(1, payload.GetProperty("attempt").GetInt32());
        Assert.Equal(new[] { "SetProfile", "SetProfile" }, payload.GetProperty("undeclared").EnumerateArray().Select(n => n.GetString()).ToArray());
        var reminder = SystemParts(Parse(http.Bodies[2]))[^1];
        Assert.Contains("SetProfile 不在這一輪的工具清單裡", reminder);
        Assert.Contains("Dialog_Confirm", reminder);
    }

    /// <summary>模型怎麼提醒都卡在沒宣告的呼叫上：幾次就收掉、回 protocol_violation，不再一路打到 120 秒逾時（修正前一次 SK 呼叫就跑滿 128 次）。</summary>
    [Fact]
    public async Task Model_stuck_on_an_undeclared_call_fails_fast_instead_of_timing_out()
    {
        var http = new Stuck(CallReply("SetProfile", "SIG-1"));
        var h = new AgenticOrchestratorTests.Harness { ChatOverride = GeminiGuarded(http) };

        var events = await h.RunAsync("一位金色短髮的中年女士站在雨夜的霓虹街頭");

        Assert.Equal("protocol_violation", Assert.Single(events.OfType<ErrorEvent>()).Code);
        Assert.Equal(3, http.Calls);                                 // 兩次中止第一次 SK 呼叫，重試的第一次就中止
        Assert.Null(h.Session.PendingConfirmation);
    }

    /// <summary>這個修正依賴的 connector 形狀：ChatHistory 裡任何位置的 system 訊息都被搬進 systemInstruction，不留在 contents。
    /// 所以暫時的 system 提示用完一定要拿掉；connector 換版後這條先紅，就知道要重看 AgenticOrchestrator 的補救與強制定稿。</summary>
    [Fact]
    public async Task Connector_lifts_every_system_message_into_system_instruction()
    {
        var http = new GeminiLike(TextReply("好", "SIG-1"));
        var history = new ChatHistory("SYS-PROMPT");
        history.AddUserMessage("第一句");
        history.AddAssistantMessage("回覆");
        history.AddSystemMessage("MID-SYS");
        history.AddUserMessage("第二句");

        await Gemini(http).GetChatMessageContentsAsync(history, new GeminiPromptExecutionSettings());

        var body = Parse(http.Bodies[0]);
        Assert.Equal(new[] { "SYS-PROMPT", "MID-SYS" }, SystemParts(body));
        Assert.Equal(new[] { "user", "model", "user" }, Roles(body));
        Assert.DoesNotContain("MID-SYS", Texts(body));
    }
}
