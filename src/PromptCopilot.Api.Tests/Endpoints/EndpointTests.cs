using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Endpoints;

public class EndpointTests : IClassFixture<EndpointTests.Factory>
{
    public sealed class FakeOrchestrator : IPromptOrchestrator
    {
        /// <summary>送這一句＝要求這一輪在第一個事件之後炸掉。用輸入當開關而不是實例欄位，
        /// 免得同 class 的其他測試共用這個 singleton 時互相影響。</summary>
        public const string ThrowTrigger = "throw-mid-stream";

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SessionEvent(session.Id, 1, "Collecting");
            await Task.Delay(10, ct);
            if (userMessage == ThrowTrigger) throw new InvalidOperationException("boom");
            yield return new FinalEvent("message", Message: $"echo: {userMessage}");
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(s =>
            {
                s.AddSingleton<IChatCompletionService>(new FakeChatCompletion());   // 不建真的 Gemini service
                s.AddSingleton<IPromptOrchestrator, FakeOrchestrator>();
            });
        }
    }

    private readonly HttpClient _client;
    public EndpointTests(Factory f) => _client = f.CreateClient();

    [Fact]
    public async Task Create_session_returns_id()
    {
        var r = await _client.PostAsync("/api/sessions", null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.False(string.IsNullOrEmpty(body!["sessionId"]));
    }

    [Fact]
    public async Task Messages_streams_sse_for_known_session_and_404_for_unknown()
    {
        var id = (await (await _client.PostAsync("/api/sessions", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
        var r = await _client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = "hi" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/event-stream", r.Content.Headers.ContentType!.ToString());
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("event: session", text); Assert.Contains("echo: hi", text);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/sessions/nope/messages", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = " " })).StatusCode);
    }

    [Fact]
    public async Task Messages_emits_error_frame_when_orchestrator_throws_mid_stream()
    {
        var id = (await (await _client.PostAsync("/api/sessions", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
        var r = await _client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = FakeOrchestrator.ThrowTrigger });

        // headers 早就送出去了，所以還是 200 + text/event-stream；失敗只能用 error 事件講
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/event-stream", r.Content.Headers.ContentType!.ToString());
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("event: session", text);
        Assert.Contains("event: error", text);
        Assert.True(text.IndexOf("event: session", StringComparison.Ordinal) < text.IndexOf("event: error", StringComparison.Ordinal),
            $"error frame 應該接在 session frame 後面，實際收到：{text}");
        Assert.Contains("\"code\":\"turn_failed\"", text);
        Assert.Contains("boom", text);

        // 鎖有在 finally 放掉：同一個 session 還能再跑一輪
        var again = await _client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = "hi" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("echo: hi", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Save_requires_finalized()
    {
        var id = (await (await _client.PostAsync("/api/sessions", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
        var r = await _client.PostAsJsonAsync($"/api/sessions/{id}/save-to-shared", new { intent = "x" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Facets_config_lists_six_dimensions()
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/config/facets");
        Assert.Equal(6, doc.GetProperty("dimensions").GetArrayLength());
        Assert.True(doc.GetProperty("profiles").TryGetProperty("vehicle", out _));
    }
}
