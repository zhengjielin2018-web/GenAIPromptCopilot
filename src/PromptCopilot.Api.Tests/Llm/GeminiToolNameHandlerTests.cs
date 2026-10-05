using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Llm;

/// <summary>known-issues #13：模型呼叫 <c>Confirm</c> 而宣告的是 <c>Dialog_Confirm</c>，SK 對沒宣告的名字直接回錯誤文字、不經任何 filter，
/// 一路重試到 120 秒逾時。handler 在回應進 connector 之前把唯一對得上的裸名改成全名；對不上的，同一輪第二次就中止。</summary>
public class GeminiToolNameHandlerTests
{
    private const string Url = "https://example.invalid/v1beta/models/m:generateContent";

    private const string Declares = """
        {"contents":[{"role":"user","parts":[{"text":"hi"}]}],
         "tools":[{"functionDeclarations":[{"name":"Dialog_Confirm","description":"d"},{"name":"Dialog_Discuss","description":"d"},{"name":"Knowledge_SearchPresets","description":"d"}]}]}
        """;

    private static string Reply(string partsJson) =>
        $$$"""{"candidates":[{"content":{"role":"model","parts":[{{{partsJson}}}]},"finishReason":"STOP","index":0}]}""";

    private static string Call(string name) =>
        $$$"""{"functionCall":{"name":"{{{name}}}","args":{"message":"金色短髮的中年女士"}},"thoughtSignature":"SIG+/="}""";

    private sealed class Canned(HttpStatusCode status, params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(replies);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(queue.Dequeue(), Encoding.UTF8, "application/json") });
    }

    private static HttpClient Client(HttpMessageHandler inner, ListLogger<GeminiToolNameHandler>? log = null) =>
        new(new GeminiToolNameHandler(inner, (Microsoft.Extensions.Logging.ILogger<GeminiToolNameHandler>?)log ?? NullLogger<GeminiToolNameHandler>.Instance));

    private static async Task<string> PostAsync(HttpClient client, string request = Declares)
    {
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Url, content);
        return await response.Content.ReadAsStringAsync();
    }

    private static JsonElement FirstPart(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0];

    [Fact]
    public async Task Rewrites_a_bare_name_to_the_one_declared_full_name_and_keeps_args_and_signature()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var log = new ListLogger<GeminiToolNameHandler>();

        var body = await PostAsync(Client(new Canned(HttpStatusCode.OK, Reply(Call("Confirm"))), log));

        var part = FirstPart(body);
        Assert.Equal("Dialog_Confirm", part.GetProperty("functionCall").GetProperty("name").GetString());
        Assert.Equal("金色短髮的中年女士", part.GetProperty("functionCall").GetProperty("args").GetProperty("message").GetString());
        Assert.Equal("SIG+/=", part.GetProperty("thoughtSignature").GetString());   // Gemini 3 要求簽章原樣帶回；只驗有沒有帶，不綁名字（2026-10-05 實測）
        Assert.Equal(1, diag.ToolNameRepairs);
        Assert.Empty(diag.UndeclaredToolCalls);
        Assert.Contains(log.Lines, l => l.Message.Contains("Confirm → Dialog_Confirm"));
    }

    [Fact]
    public async Task Leaves_declared_names_untouched_whatever_the_case()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var reply = Reply(Call("Dialog_Confirm") + "," + Call("dialog_discuss"));

        var body = await PostAsync(Client(new Canned(HttpStatusCode.OK, reply)));

        Assert.Equal(reply, body);                                    // 一個位元組都不動
        Assert.Equal(0, diag.ToolNameRepairs);
    }

    [Fact]
    public async Task Repairs_each_bare_call_in_a_parallel_batch()
    {
        UpstreamDiagnostics.Current = new UpstreamDiagnostics();

        var body = await PostAsync(Client(new Canned(HttpStatusCode.OK, Reply(Call("SearchPresets") + "," + Call("Confirm")))));

        var parts = JsonDocument.Parse(body).RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
        Assert.Equal(new[] { "Knowledge_SearchPresets", "Dialog_Confirm" },
            parts.EnumerateArray().Select(p => p.GetProperty("functionCall").GetProperty("name").GetString()).ToArray());
    }

    [Fact]
    public async Task A_bare_name_two_plugins_could_mean_is_not_guessed()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        const string request = """{"contents":[],"tools":[{"functionDeclarations":[{"name":"A_Confirm"},{"name":"B_Confirm"}]}]}""";

        var body = await PostAsync(Client(new Canned(HttpStatusCode.OK, Reply(Call("Confirm")))), request);

        Assert.Equal("Confirm", FirstPart(body).GetProperty("functionCall").GetProperty("name").GetString());
        Assert.Equal(new[] { "Confirm" }, diag.UndeclaredToolCalls);
    }

    /// <summary>確認輪叫 SetProfile 這種根本不在清單裡的：第一次放過去（SK 回錯誤，模型有一次機會改），同一輪第二次就中止，
    /// 不讓它一路重試到逾時。</summary>
    [Fact]
    public async Task Second_undeclared_call_in_a_turn_aborts_with_the_names()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var log = new ListLogger<GeminiToolNameHandler>();
        var client = Client(new Canned(HttpStatusCode.OK, Reply(Call("SetProfile")), Reply(Call("SetProfile"))), log);

        var first = await PostAsync(client);
        Assert.Equal("SetProfile", FirstPart(first).GetProperty("functionCall").GetProperty("name").GetString());

        var e = await Assert.ThrowsAsync<UndeclaredToolCallException>(() => PostAsync(client));
        Assert.Equal(new[] { "SetProfile", "SetProfile" }, e.Names);
        Assert.Equal(new[] { "SetProfile", "SetProfile" }, diag.UndeclaredToolCalls);
        Assert.Equal(2, log.Lines.Count(l => l.Message.Contains("SetProfile")));
    }

    [Fact]
    public async Task Outside_a_turn_nothing_is_counted_and_nothing_aborts()
    {
        UpstreamDiagnostics.Current = null;
        var client = Client(new Canned(HttpStatusCode.OK, Reply(Call("SetProfile")), Reply(Call("SetProfile")), Reply(Call("Confirm"))));

        await PostAsync(client);
        await PostAsync(client);
        var repaired = await PostAsync(client);

        Assert.Equal("Dialog_Confirm", FirstPart(repaired).GetProperty("functionCall").GetProperty("name").GetString());
    }

    /// <summary>分類器那類沒帶 tools 的請求，回應原封不動。</summary>
    [Fact]
    public async Task Requests_without_tool_declarations_pass_through()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var reply = Reply(Call("Confirm"));

        var body = await PostAsync(Client(new Canned(HttpStatusCode.OK, reply)), """{"contents":[{"role":"user","parts":[{"text":"hi"}]}]}""");

        Assert.Equal(reply, body);
        Assert.Empty(diag.UndeclaredToolCalls);
    }

    [Fact]
    public async Task Error_responses_pass_through()
    {
        UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        const string error = """{"error":{"code":400,"message":"functionCall Confirm","status":"INVALID_ARGUMENT"}}""";

        var body = await PostAsync(Client(new Canned(HttpStatusCode.BadRequest, error)));

        Assert.Equal(error, body);
    }
}
