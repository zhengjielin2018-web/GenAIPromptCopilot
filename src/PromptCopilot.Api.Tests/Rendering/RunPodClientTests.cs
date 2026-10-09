using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class RunPodClientTests
{
    private static readonly string Png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

    /// <summary>delay 不真的睡，時鐘前進輪詢間隔。</summary>
    private static (RunPodClient Client, StubHttpHandler Handler) Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, ManualTimeProvider? time = null)
    {
        var clock = time ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.runpod.ai/v2/ep/") };
        return (new RunPodClient(http, TimeSpan.FromSeconds(1), clock, (d, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(d); return Task.CompletedTask; }), handler);
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Statuses(params string[] statuses)
    {
        var queue = new Queue<string>(statuses);
        return req => Task.FromResult(req.Method == HttpMethod.Post
            ? StubHttpHandler.Json(new { id = "j1", status = "CANCELLED" })
            : StubHttpHandler.Json(new { id = "j1", status = queue.Count > 1 ? queue.Dequeue() : queue.Peek(), delayTime = 1500, executionTime = 4600 }));
    }

    [Fact]
    public async Task Wait_polls_until_the_job_finishes()
    {
        var (c, h) = Client(Statuses("IN_QUEUE", "IN_PROGRESS", "COMPLETED"));
        var job = await c.WaitAsync("j1", TimeSpan.FromSeconds(60), default);
        Assert.Equal(("COMPLETED", (int?)1500, (int?)4600), (job.Status, job.DelayTime, job.ExecutionTime));
        Assert.Equal(Enumerable.Repeat((HttpMethod.Get, "/v2/ep/status/j1"), 3), h.Requests);
    }

    [Fact]
    public async Task Wait_cancels_the_job_when_it_times_out()
    {
        var (c, h) = Client(Statuses("IN_QUEUE"));
        await Assert.ThrowsAsync<TimeoutException>(() => c.WaitAsync("j1", TimeSpan.FromSeconds(3), default));
        Assert.Equal((HttpMethod.Post, "/v2/ep/cancel/j1"), h.Requests[^1]);
    }

    public static TheoryData<string> Failures => new() { "503", "connection" };

    [Theory, MemberData(nameof(Failures))]
    public async Task Wait_cancels_the_job_when_polling_fails(string failure)
    {
        var n = 0;
        var (c, h) = Client(req =>
        {
            if (req.Method == HttpMethod.Post) return Task.FromResult(StubHttpHandler.Json(new { id = "j1", status = "CANCELLED" }));
            if (++n == 1) return Task.FromResult(StubHttpHandler.Json(new { id = "j1", status = "IN_QUEUE" }));
            return failure == "503" ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : throw new HttpRequestException("reset");
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => c.WaitAsync("j1", TimeSpan.FromSeconds(60), default));
        Assert.Equal((HttpMethod.Post, "/v2/ep/cancel/j1"), h.Requests[^1]);
    }

    [Fact]
    public async Task Wait_cancels_the_job_when_the_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        var (c, h) = Client(req =>
        {
            if (req.Method == HttpMethod.Post) return Task.FromResult(StubHttpHandler.Json(new { id = "j1", status = "CANCELLED" }));
            cts.Cancel();   // 服務停止：下一次 delay 會丟
            return Task.FromResult(StubHttpHandler.Json(new { id = "j1", status = "IN_QUEUE" }));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.WaitAsync("j1", TimeSpan.FromSeconds(60), cts.Token));
        Assert.Equal((HttpMethod.Post, "/v2/ep/cancel/j1"), h.Requests[^1]);
    }

    [Fact]
    public async Task Submit_sends_the_workflow_once_and_does_not_retry()
    {
        var (c, h) = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() => c.SubmitAsync(new JsonObject { ["3"] = new JsonObject() }, default));
        Assert.Single(h.Requests);
        Assert.Equal((HttpMethod.Post, "/v2/ep/run"), h.Requests[0]);
        Assert.Contains("\"workflow\"", h.Bodies[0]);
    }

    private static RunPodJob Job(object output) =>
        new("j1", "COMPLETED", 1, 2, JsonSerializer.SerializeToElement(output), null);

    [Fact]
    public void ExtractImage_decodes_base64()
    {
        var png = RunPodClient.ExtractImage(Job(new { images = new[] { new { filename = "a.png", type = "base64", data = Png } } }));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png);
    }

    [Fact]
    public void ExtractImage_rejects_s3_output_missing_images_and_bad_base64()
    {
        Assert.Contains("base64", Assert.Throws<InvalidOperationException>(() =>
            RunPodClient.ExtractImage(Job(new { images = new[] { new { type = "s3_url", data = "https://x" } } }))).Message);
        Assert.Contains("沒有回圖", Assert.Throws<InvalidOperationException>(() =>
            RunPodClient.ExtractImage(Job(new { errors = new[] { "Prompt outputs failed validation" } }))).Message);
        Assert.Throws<InvalidOperationException>(() =>
            RunPodClient.ExtractImage(Job(new { images = new[] { new { type = "base64", data = "%%%" } } })));
        Assert.Throws<InvalidOperationException>(() =>
            RunPodClient.ExtractImage(Job(new { images = new[] { new { type = "base64", data = "" } } })));
        Assert.Throws<InvalidOperationException>(() => RunPodClient.ExtractImage(new RunPodJob("j1", "COMPLETED", null, null, null, null)));
    }
}
