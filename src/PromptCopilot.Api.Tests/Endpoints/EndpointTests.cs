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
        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SessionEvent(session.Id, 1, "Collecting");
            await Task.Delay(10, ct);
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
