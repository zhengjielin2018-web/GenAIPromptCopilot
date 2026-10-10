using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Fakes;
using PromptCopilot.Api.Tests.Rendering;

namespace PromptCopilot.Api.Tests.Endpoints;

public class RenderEndpointTests
{
    private sealed class CountingReviewer : IImageReviewer
    {
        public int Calls;
        public Task<ImageVerdict> ReviewAsync(GeminiImage image, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(new ImageVerdict(false, false, null, "ok")); }
    }

    private sealed class MetChecker : ISelfChecker
    {
        public Task<IReadOnlyList<RequirementVerdict>> CheckAsync(GeminiImage image, IReadOnlyList<RequirementMatch> items, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RequirementVerdict>>(items.Select(i => new RequirementVerdict(i.Id, i.Text, i.Source, i.Tags, i.NegativeTags, "met", "none", "有")).ToList());
    }

    private readonly CountingReviewer _reviewer = new();
    private readonly RenderPipelineTests.FakeExtractor _extractor = new();

    private WebApplicationFactory<Program> Factory(Dictionary<string, string?>? extra = null)
    {
        var config = new Dictionary<string, string?> { ["Render:EndpointId"] = "ep", ["Render:ApiKey"] = "k" };
        foreach (var (k, v) in extra ?? new()) config[k] = v;
        return new EndpointTests.Factory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IRunPodClient>(new RenderPipelineTests.FakeRunPod());
                s.AddSingleton<IImageReviewer>(_reviewer);
                s.AddSingleton<ISelfChecker>(new MetChecker());
                s.AddSingleton<IRequirementExtractor>(_extractor);
                s.AddSingleton<IAuditSink>(new RecordingAudit());
            });
        });
    }

    private static Session Finalized(WebApplicationFactory<Program> f, bool reviewed = true, int turn = 4)
    {
        var s = f.Services.GetRequiredService<SessionStore>().Create();
        s.ApplyProfile("portrait", f.Services.GetRequiredService<FacetCatalog>());
        s.TurnIndex = turn;
        s.ChatHistory.AddUserMessage("銀髮少女");
        s.RecordFinalize(new FinalPrompt("1girl, silver hair", "lowres", "", "", Reviewed: reviewed));
        return s;
    }

    private static async Task<JsonElement> WaitFor(HttpClient c, string url, string status)
    {
        for (var i = 0; i < 200; i++)
        {
            var doc = await c.GetFromJsonAsync<JsonElement>(url);
            if (doc.GetProperty("status").GetString() == status) return doc;
            await Task.Delay(10);
        }
        throw new TimeoutException($"沒等到 {status}");
    }

    [Fact]
    public async Task Post_then_poll_until_done_and_fetch_the_image()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        var s = Finalized(f);
        var post = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4 });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("renderId").GetString();
        var done = await WaitFor(c, $"/api/sessions/{s.Id}/renders/{id}", "done");
        Assert.Equal(4, done.GetProperty("turnIndex").GetInt32());
        var sc = done.GetProperty("selfCheck");
        Assert.Equal(("ok", 100), (sc.GetProperty("status").GetString(), sc.GetProperty("score").GetInt32()));
        Assert.Equal("使用者要求 1 條，1 條符合", sc.GetProperty("summary").GetString());
        var item = sc.GetProperty("items")[0];
        Assert.Equal(("r1", "met", "none"), (item.GetProperty("id").GetString(), item.GetProperty("verdict").GetString(), item.GetProperty("issue").GetString()));
        Assert.Contains("使用者：銀髮少女", _extractor.LastTranscript);
        Assert.Equal(0, _extractor.MatchCalls);
        var img = await c.GetAsync($"/api/sessions/{s.Id}/renders/{id}/image");
        Assert.Equal("image/png", img.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, await img.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, _reviewer.Calls);
    }

    [Fact]
    public async Task Safety_off_skips_every_classifier()
    {
        await using var f = Factory(new() { ["Safety:AllowDisable"] = "true" });
        var c = f.CreateClient();
        var s = Finalized(f, reviewed: false);   // 沒審過的定稿＋審查關著：也不補審
        var post = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4, safety = "off" });
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("renderId").GetString();
        var done = await WaitFor(c, $"/api/sessions/{s.Id}/renders/{id}", "done");
        Assert.Equal("off", done.GetProperty("safety").GetString());
        Assert.Equal(0, _reviewer.Calls);
        Assert.Empty(((FakeChatCompletion)f.Services.GetRequiredService<IChatCompletionService>()).Calls);
    }

    [Fact]
    public async Task Disabled_render_is_404()
    {
        await using var f = new EndpointTests.Factory();
        var r = await f.CreateClient().PostAsJsonAsync("/api/sessions/x/renders", new { turnIndex = 1 });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("生圖沒有開啟", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bad_requests_are_rejected_with_the_right_codes()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/sessions/nope/renders", new { turnIndex = 1 })).StatusCode);

        var s = Finalized(f);
        var bad = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4, safety = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var off = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4, safety = "off" });
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);

        var wrongTurn = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 3 });
        Assert.Equal(HttpStatusCode.Conflict, wrongTurn.StatusCode);
        Assert.Contains("只有最新一張定稿卡可以生成預覽", await wrongTurn.Content.ReadAsStringAsync());

        var notFinal = f.Services.GetRequiredService<SessionStore>().Create();
        var nf = await c.PostAsJsonAsync($"/api/sessions/{notFinal.Id}/renders", new { turnIndex = 0 });
        Assert.Contains("尚未定稿", await nf.Content.ReadAsStringAsync());

        await s.Lock.WaitAsync();
        try
        {
            var busy = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4 });
            Assert.Contains("這個 session 還有一輪在跑", await busy.Content.ReadAsStringAsync());
        }
        finally { s.Lock.Release(); }
    }

    [Fact]
    public async Task Unfinished_previous_render_is_409_and_session_limit_is_429()
    {
        await using var f = Factory(new() { ["Render:PerSessionLimit"] = "1" });
        var c = f.CreateClient();
        var s = Finalized(f);
        s.Renders["held"] = RenderRecordTests.New(id: "held");   // 還在排隊的
        var held = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4 });
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Contains("上一張還在生", await held.Content.ReadAsStringAsync());

        s.Renders["held"].MarkSubmitted("j"); s.Renders["held"].Fail(RenderMessages.Failed, "runpod", null);
        var limit = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4 });
        Assert.Equal((HttpStatusCode)429, limit.StatusCode);
    }

    [Fact]
    public async Task Image_is_not_served_while_reviewing_and_render_404s_are_explicit()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        var s = Finalized(f);
        var r = RenderRecordTests.New(id: "rv");
        s.Renders["rv"] = r;
        r.MarkGenerating(0); r.ImageArrived(new byte[] { 1 }, 1, 1);
        Assert.Equal("reviewing", (await c.GetFromJsonAsync<JsonElement>($"/api/sessions/{s.Id}/renders/rv")).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/sessions/{s.Id}/renders/rv/image")).StatusCode);
        r.ReviewPassed(1);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/sessions/{s.Id}/renders/rv/image")).StatusCode);
        var missing = await c.GetAsync($"/api/sessions/{s.Id}/renders/nope");
        Assert.Contains("找不到這張預覽", await missing.Content.ReadAsStringAsync());
    }

    /// <summary>使用者沒再開口就重新生成：沿用 session 上的清單快照（符合度設計 §4.3）。</summary>
    [Fact]
    public async Task Same_user_words_reuse_the_snapshot()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        var s = Finalized(f);
        var key = IntentTranscript.Build(s, f.Services.GetRequiredService<FacetCatalog>()).Key;
        var fixedList = new[] { new Requirement("r1", "銀色長髮", RequirementSources.User) };
        s.Requirements = new RequirementSnapshot(key, fixedList);
        var post = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4 });
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("renderId").GetString();
        await WaitFor(c, $"/api/sessions/{s.Id}/renders/{id}", "done");
        Assert.Equal((0, 1), (_extractor.ExtractCalls, _extractor.MatchCalls));
        Assert.Same(fixedList, _extractor.LastFixed);
    }

    [Fact]
    public async Task A_snapshot_for_other_words_is_not_reused()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        var s = Finalized(f);
        s.Requirements = new RequirementSnapshot("someone-else", new[] { new Requirement("r1", "黑髮", RequirementSources.User) });
        var post = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4 });
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("renderId").GetString();
        await WaitFor(c, $"/api/sessions/{s.Id}/renders/{id}", "done");
        Assert.Equal((1, 0), (_extractor.ExtractCalls, _extractor.MatchCalls));
        Assert.Equal("銀色長髮", s.Requirements!.Items[0].Text);   // 換成新整理的
    }
}
