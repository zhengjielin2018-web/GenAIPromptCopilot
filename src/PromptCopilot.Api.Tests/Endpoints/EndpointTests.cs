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

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, TurnInput input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SessionEvent(session.Id, 1, "Collecting", input.Adoption is null ? null : input.Text);
            await Task.Delay(10, ct);
            if (input.Text == ThrowTrigger) throw new InvalidOperationException("boom");
            yield return new FinalEvent("message", Message: $"echo: {input.Text}");
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

    /// <summary>不打 DB。id 1 是一筆已拆分 facet 的 civitai preset；id 2 尚未拆分；其他都不存在。</summary>
    private sealed class FakePresets() : PresetRepository(null!)
    {
        public override Task<PresetDetail?> GetAsync(long id, CancellationToken ct) => Task.FromResult(id switch
        {
            1 => new PresetDetail(1, "霓虹雨夜街頭", "Scene", "昏暗雨夜的賽博龐克街道", ["neon", "rain"], ["scene.location", "scene.weather"],
                "neon city street, rain, night", null, null, "civitai:12345:0", SourceAttribution.UrlFor("civitai:12345:0"),
                new Dictionary<string, IReadOnlyList<string>> { ["scene.location"] = new[] { "neon city street" }, ["scene.weather"] = new[] { "rain" } }),
            2 => new PresetDetail(2, "未拆分", "Scene", "d", ["x"], ["scene.location"], "x", null, null, null, null),
            _ => null,
        });
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
                s.AddSingleton<PresetRepository>(new FakePresets());
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
        s.RecordFinalize(new FinalPrompt("1girl", "lowres", "tips", "一個女生",
            PositiveSources: new[] { new TagSource("1girl", "rag", new long[] { 1 }, "霓虹雨夜街頭") },
            NegativeSources: new[] { new TagSource("lowres", "base", Array.Empty<long>(), null) }));
        return s;
    }

    private Session PortraitSession(bool retrieval = true)
    {
        var store = _factory.Services.GetRequiredService<SessionStore>();
        var s = store.Create(retrieval);
        s.ApplyProfile("portrait", _factory.Services.GetRequiredService<PromptCopilot.Api.Configuration.FacetCatalog>());
        return s;
    }

    /// <summary>串流裡 session frame 的 data 那一行。</summary>
    private static string SessionFrameData(string sse)
    {
        var lines = sse.Split('\n');
        return lines[Array.IndexOf(lines, "event: session") + 1];
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
    public async Task Create_session_without_body_defaults_retrieval_on()
    {
        var r = await _client.PostAsync("/api/sessions", null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("on", (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["retrieval"]);
    }

    /// <summary>manual-tests/chat.py 帶 content-type: application/json 但沒有 body，要當成沒帶。</summary>
    [Fact]
    public async Task Create_session_with_empty_json_body_defaults_on()
    {
        var content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        var r = await _client.PostAsync("/api/sessions", content);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("on", (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["retrieval"]);
    }

    [Fact]
    public async Task Create_session_with_retrieval_off_is_reported_on_create_and_get()
    {
        var r = await _client.PostAsJsonAsync("/api/sessions", new { retrieval = "OFF" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("off", body!["retrieval"]);
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/sessions/{body["sessionId"]}");
        Assert.Equal("off", doc.GetProperty("retrieval").GetString());
        Assert.False(_factory.Services.GetRequiredService<SessionStore>().TryGet(body["sessionId"])!.RetrievalEnabled);
    }

    [Fact]
    public async Task Create_session_rejects_unknown_retrieval_value()
    {
        var r = await _client.PostAsJsonAsync("/api/sessions", new { retrieval = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("on 或 off", (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
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
        Assert.DoesNotContain("\"text\"", SessionFrameData(text));      // 一般的一輪 session 事件不帶 text（只有 adopt 才帶）

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

    /// <summary>設計 §6.1／§6.2：伺服器組句、串流第一個事件帶那句。</summary>
    [Fact]
    public async Task Adopt_composes_the_user_sentence_and_streams_it()
    {
        var s = PortraitSession();
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/messages", new { adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("\"text\":\"採用〈霓虹雨夜街頭〉（知識庫 #1）：地點類型照它的（neon city street）；前景元素、中景／主體周邊、背景與遠景、光源與時間、天氣氛圍保留我的。\"", body);
        Assert.Contains("\"text\":", SessionFrameData(body));
        Assert.Contains("echo: 採用〈霓虹雨夜街頭〉", body);
    }

    /// <summary>設計 §6.1：有 adopt 時 text 忽略。模型與 audit 看到的只能是伺服器組的句子，不能夾帶使用者自己塞的字。</summary>
    [Fact]
    public async Task Adopt_with_text_runs_the_adoption_and_ignores_the_text()
    {
        var s = PortraitSession();
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/messages",
            new { text = "別理我這句", adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        var data = SessionFrameData(body);
        using var frame = System.Text.Json.JsonDocument.Parse(data[data.IndexOf('{')..]);
        Assert.StartsWith("採用〈", frame.RootElement.GetProperty("text").GetString());
        var echo = body.Split('\n').Single(l => l.Contains("echo: "));
        Assert.StartsWith("採用〈", echo[(echo.IndexOf("echo: ", StringComparison.Ordinal) + "echo: ".Length)..]);
        Assert.DoesNotContain("別理我這句", echo);
    }

    [Theory]
    [InlineData("""{"adopt":{"presetId":99,"dimension":"scene","take":["scene.location"]}}""", HttpStatusCode.BadRequest, "找不到")]
    [InlineData("""{"adopt":{"presetId":2,"dimension":"scene","take":["scene.location"]}}""", HttpStatusCode.BadRequest, "尚未拆分")]
    [InlineData("""{"adopt":{"presetId":1,"dimension":"scene","take":[]}}""", HttpStatusCode.BadRequest, "take 不可為空")]
    [InlineData("""{"adopt":{"presetId":1,"dimension":"scene","take":["clothing.upper"]}}""", HttpStatusCode.BadRequest, "不屬於維度")]
    [InlineData("""{}""", HttpStatusCode.BadRequest, "text 不可為空")]
    public async Task Adopt_rejects_bad_requests(string json, HttpStatusCode status, string message)
    {
        var s = PortraitSession();
        var r = await _client.PostAsync($"/api/sessions/{s.Id}/messages", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(status, r.StatusCode);
        Assert.Contains(message, (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }

    [Fact]
    public async Task Adopt_is_409_when_retrieval_is_off_or_profile_is_unset()
    {
        var off = PortraitSession(retrieval: false);
        var r1 = await _client.PostAsJsonAsync($"/api/sessions/{off.Id}/messages", new { adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.Conflict, r1.StatusCode);
        var fresh = _factory.Services.GetRequiredService<SessionStore>().Create();
        var r2 = await _client.PostAsJsonAsync($"/api/sessions/{fresh.Id}/messages", new { adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Contains("題材", (await r2.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }

    [Fact]
    public async Task Get_session_returns_authoritative_state_and_null_final_before_finalize()
    {
        var id = (await (await _client.PostAsync("/api/sessions", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/sessions/{id}");
        Assert.Equal(id, doc.GetProperty("sessionId").GetString());
        Assert.Equal("Collecting", doc.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.GetProperty("profile").ValueKind);
        Assert.Equal(0, doc.GetProperty("askCount").GetInt32());
        Assert.Equal(2, doc.GetProperty("askLimit").GetInt32());
        Assert.Equal(0, doc.GetProperty("facetStates").EnumerateObject().Count());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.GetProperty("lastFinal").ValueKind);
        Assert.Equal("on", doc.GetProperty("retrieval").GetString());
    }

    [Fact]
    public async Task Get_session_returns_facets_and_last_final_after_finalize()
    {
        var s = FinalizedSession();
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/sessions/{s.Id}");
        Assert.Equal("Finalized", doc.GetProperty("status").GetString());
        Assert.Equal("portrait", doc.GetProperty("profile").GetString());
        Assert.Equal("missing", doc.GetProperty("facetStates").GetProperty("style.genre").GetString());
        var f = doc.GetProperty("lastFinal");
        Assert.Equal("1girl", f.GetProperty("positive").GetString());
        Assert.Equal("一個女生", f.GetProperty("intentSummary").GetString());
        var tag = f.GetProperty("positiveSources")[0];
        Assert.Equal("1girl", tag.GetProperty("tag").GetString());
        Assert.Equal("rag", tag.GetProperty("origin").GetString());
        Assert.Equal(1, tag.GetProperty("presetIds")[0].GetInt64());
        Assert.Equal("霓虹雨夜街頭", tag.GetProperty("presetTitle").GetString());
        Assert.Equal("base", f.GetProperty("negativeSources")[0].GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Get_session_includes_facet_tags()
    {
        var store = _factory.Services.GetRequiredService<SessionStore>();
        var catalog = _factory.Services.GetRequiredService<PromptCopilot.Api.Configuration.FacetCatalog>();
        var s = store.Create(); s.ApplyProfile("portrait", catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Covered }, catalog, new Dictionary<string, string> { ["clothing.footwear"] = "sandals" });
        var body = await (await _client.GetAsync($"/api/sessions/{s.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("\"facetTags\":{\"clothing.footwear\":\"sandals\"}", body);
    }

    [Fact]
    public async Task Get_session_404_for_unknown()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/sessions/nope")).StatusCode);
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

    /// <summary>資料一旦公開散布，出處要跟著資料走：抽屜靠這兩個欄位顯示「出處：Civitai」。</summary>
    [Fact]
    public async Task Preset_detail_carries_its_source_ref_and_url()
    {
        var d = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/presets/1");

        Assert.Equal("civitai:12345:0", d.GetProperty("sourceRef").GetString());
        Assert.Equal("https://civitai.com/images/12345", d.GetProperty("sourceUrl").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/presets/99")).StatusCode);
    }

    /// <summary>Swagger 上寫的回應碼要等於端點真的會回的。沒標就只剩框架預設的 200：
    /// 開 session 其實回 201，400／404／409 也全部看不到，照文件寫的客戶端會漏接。</summary>
    [Theory]
    [InlineData("/api/sessions", "post", "201,400")]
    [InlineData("/api/sessions/{id}", "get", "200,404")]
    [InlineData("/api/sessions/{id}/messages", "post", "200,400,404,409")]
    [InlineData("/api/sessions/{id}/save-to-shared", "post", "200,400,404,409")]
    [InlineData("/api/presets/{id}", "get", "200,404")]
    public async Task OpenApi_lists_the_status_codes_each_route_returns(string path, string method, string codes)
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/swagger/v1/swagger.json");
        var responses = doc.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses");
        Assert.Equal(codes.Split(','), responses.EnumerateObject().Select(p => p.Name).Order());
    }

    /// <summary>新加的端點忘了寫說明，Swagger 上就只剩一行路徑。</summary>
    [Fact]
    public async Task Every_operation_has_a_summary_and_a_description()
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/swagger/v1/swagger.json");
        var undocumented = doc.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(op => (Name: $"{op.Name.ToUpperInvariant()} {p.Name}", Op: op.Value)))
            .Where(x => !x.Op.TryGetProperty("summary", out var s) || string.IsNullOrWhiteSpace(s.GetString())
                     || !x.Op.TryGetProperty("description", out var d) || string.IsNullOrWhiteSpace(d.GetString()))
            .Select(x => x.Name).ToList();
        Assert.Empty(undocumented);
    }

    /// <summary>子專案 3 設計 §2.6：finalized 多了 intentSummary，Swagger 的事件表要跟著寫，照文件寫的客戶端才知道有這個欄位。</summary>
    [Fact]
    public async Task Messages_description_documents_intent_summary_on_finalized()
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/swagger/v1/swagger.json");
        var description = doc.GetProperty("paths").GetProperty("/api/sessions/{id}/messages").GetProperty("post").GetProperty("description").GetString();
        Assert.Contains("intentSummary", description);
    }

    [Fact]
    public async Task Facets_config_lists_six_dimensions()
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/config/facets");
        Assert.Equal(6, doc.GetProperty("dimensions").GetArrayLength());
        Assert.True(doc.GetProperty("profiles").TryGetProperty("vehicle", out _));
    }
}
