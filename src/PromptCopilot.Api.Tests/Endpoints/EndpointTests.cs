using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
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

    /// <summary>不打 Gemini。</summary>
    private sealed class FakeEmbeddings : IEmbeddingClient
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[768]).ToList());
    }

    /// <summary>不打 DB。</summary>
    private sealed class FakeHistories() : HistoryRepository(null!)
    {
        public override Task<Guid> InsertAsync(HistoryInsert h, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
    }

    public sealed class ExplodingAudit : IAuditSink
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken ct) => throw new IOException("audit db down");
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(s =>
            {
                s.AddSingleton<IChatCompletionService>(new FakeChatCompletion());   // 不建真的 Gemini service
                s.AddSingleton<IPromptOrchestrator, FakeOrchestrator>();
                s.AddSingleton<IEmbeddingClient>(new FakeEmbeddings());
                s.AddSingleton<HistoryRepository>(new FakeHistories());
                s.AddSingleton<IAuditSink>(new ExplodingAudit());                   // 稽核掛掉不該讓 200 變 500
            });
        }
    }

    private readonly HttpClient _client;
    private readonly Factory _factory;
    public EndpointTests(Factory f) { _factory = f; _client = f.CreateClient(); }

    /// <summary>直接從 store 拿 session 佈置成已定稿：走 HTTP 的話得先跑完一輪真的對話。</summary>
    private Session FinalizedSession()
    {
        var store = _factory.Services.GetRequiredService<SessionStore>();
        var s = store.Create();
        s.ApplyProfile("portrait", _factory.Services.GetRequiredService<PromptCopilot.Api.Configuration.FacetCatalog>());
        s.RecordFinalize(new FinalPrompt("1girl", "lowres", "tips"));
        return s;
    }

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
        Assert.DoesNotContain("boom", text);          // 例外訊息留在 log，不送到使用者眼前

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

    /// <summary>儲存會讀 FacetStates 與 LastFinal。那一輪還在跑（還可能被回滾）時讀，讀到的是半途的狀態。</summary>
    [Fact]
    public async Task Save_is_rejected_while_a_turn_holds_the_session_lock()
    {
        var s = FinalizedSession();
        Assert.True(await s.Lock.WaitAsync(0));
        try
        {
            var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/save-to-shared", new { intent = "雨夜霓虹" });
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        }
        finally { s.Lock.Release(); }
    }

    /// <summary>稽核寫在資料列插入之後。讓它把回應弄成 500，使用者一重試就多一筆重複的資料。</summary>
    [Fact]
    public async Task Save_succeeds_even_when_the_audit_write_fails()
    {
        var s = FinalizedSession();
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/save-to-shared", new { intent = "雨夜霓虹" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["id"]));
        Assert.Equal(1, s.Lock.CurrentCount);        // 鎖有放掉
    }

    [Fact]
    public async Task Facets_config_lists_six_dimensions()
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/config/facets");
        Assert.Equal(6, doc.GetProperty("dimensions").GetArrayLength());
        Assert.True(doc.GetProperty("profiles").TryGetProperty("vehicle", out _));
    }
}
