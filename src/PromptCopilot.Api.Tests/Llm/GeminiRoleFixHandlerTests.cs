using System.Net;
using System.Text;
using System.Text.Json;
using PromptCopilot.Api.Llm;

namespace PromptCopilot.Api.Tests.Llm;

public class GeminiRoleFixHandlerTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? MediaType { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static async Task<Capture> SendAsync(string body)
    {
        var capture = new Capture();
        using var client = new HttpClient(new GeminiRoleFixHandler(capture));
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await client.PostAsync("https://example.invalid/v1beta/models/m:generateContent", content);
        return capture;
    }

    [Fact]
    public async Task Rewrites_the_tool_result_role_to_user()
    {
        var capture = await SendAsync("""
            {"contents":[{"role":"user","parts":[{"text":"hi"}]},
                         {"role":"model","parts":[{"functionCall":{"name":"F","args":{}},"thoughtSignature":"SIG"}]},
                         {"role":"function","parts":[{"functionResponse":{"name":"F","response":{"content":"ok"}}}]}]}
            """);
        var contents = JsonDocument.Parse(capture.Body!).RootElement.GetProperty("contents");
        Assert.Equal(new[] { "user", "model", "user" }, contents.EnumerateArray().Select(c => c.GetProperty("role").GetString()).ToArray());
        Assert.Equal("application/json", capture.MediaType);
    }

    [Fact]
    public async Task Keeps_thought_signature_and_the_rest_of_the_payload()
    {
        var capture = await SendAsync("""
            {"contents":[{"role":"model","parts":[{"functionCall":{"name":"F","args":{}},"thoughtSignature":"SIG"}]},
                         {"role":"function","parts":[{"functionResponse":{"name":"F","response":{"content":"中文"}}}]}],
             "tools":[{"functionDeclarations":[{"name":"F","description":"d"}]}]}
            """);
        var root = JsonDocument.Parse(capture.Body!).RootElement;
        Assert.Equal("SIG", root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("中文", root.GetProperty("contents")[1].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response").GetProperty("content").GetString());
        Assert.Equal("F", root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Leaves_a_request_without_tool_results_untouched()
    {
        const string body = """{"contents":[{"role":"user","parts":[{"text":"hi"}]}]}""";
        Assert.Equal(body, (await SendAsync(body)).Body);
    }

    [Fact]
    public async Task Does_not_touch_the_string_outside_contents()
    {
        // function declaration 的描述裡剛好有同樣字樣時不該被改：預檢是字串比對，實際改動走 JSON。
        var capture = await SendAsync("""
            {"contents":[{"role":"function","parts":[]}],
             "tools":[{"functionDeclarations":[{"name":"F","description":"role\":\"function\" 只是說明文字"}]}]}
            """);
        var root = JsonDocument.Parse(capture.Body!).RootElement;
        Assert.Equal("user", root.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.Equal("""role":"function" 只是說明文字""", root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("""{"contents":"not an array","role":"function"}""")]
    [InlineData("""not json but mentions "role":"function" """)]
    public async Task Passes_through_bodies_it_cannot_parse(string body) =>
        Assert.Equal(body, (await SendAsync(body)).Body);
}
