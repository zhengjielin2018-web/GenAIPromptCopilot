using System.Net;
using System.Text;
using System.Text.Json;
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
