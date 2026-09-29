using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Llm;

public class GeminiDiagnosticsHandlerTests
{
    private const string GenerateUrl = "https://example.invalid/v1beta/models/m:generateContent";

    /// <summary>回一份寫死的回應，當作 Gemini。</summary>
    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpContent? Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(status) { Content = Sent });
        }
    }

    /// <summary>一直不回來，直到呼叫端取消。</summary>
    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static async Task<(HttpResponseMessage Response, string Body)> SendAsync(HttpMessageHandler inner, string url = GenerateUrl, ILogger<GeminiDiagnosticsHandler>? logger = null)
    {
        var client = new HttpClient(new GeminiDiagnosticsHandler(inner, logger ?? NullLogger<GeminiDiagnosticsHandler>.Instance));
        var response = await client.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
        return (response, await response.Content.ReadAsStringAsync());
    }

    private const string Stop = """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"hi"}]},"finishReason":"STOP",
          "safetyRatings":[{"category":"HARM_CATEGORY_HARASSMENT","probability":"NEGLIGIBLE"}]}],
         "usageMetadata":{"promptTokenCount":3}}
        """;

    [Fact]
    public async Task Normal_response_is_counted_and_passed_through_unchanged()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var (response, body) = await SendAsync(new Canned(HttpStatusCode.OK, Stop));

        Assert.Equal(Stop, body);                                   // connector 讀到的跟 Gemini 送的一模一樣
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal(1, diag.Calls);
        Assert.Null(diag.PendingMs);                                // 已經回來，不算在途
        Assert.Null(diag.Block);
        Assert.Null(diag.HttpError);
    }

    [Theory]
    [InlineData("PROHIBITED_CONTENT")]
    [InlineData("SAFETY")]
    public async Task Prompt_feedback_block_is_recorded_as_input_blocked_with_ratings(string reason)
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var json = $$$"""
            {"promptFeedback":{"blockReason":"{{{reason}}}","safetyRatings":[
               {"category":"HARM_CATEGORY_SEXUALLY_EXPLICIT","probability":"MEDIUM","blocked":true},
               {"category":"HARM_CATEGORY_HARASSMENT","probability":"NEGLIGIBLE"}]},
             "usageMetadata":{"promptTokenCount":3}}
            """;
        var (_, body) = await SendAsync(new Canned(HttpStatusCode.OK, json));

        Assert.Equal(json, body);
        var block = Assert.IsType<UpstreamBlock>(diag.Block);
        Assert.Equal("input_blocked", block.Kind);
        Assert.Equal(reason, block.Reason);
        Assert.Equal(new[]
        {
            new UpstreamSafetyRating("HARM_CATEGORY_SEXUALLY_EXPLICIT", "MEDIUM", true),
            new UpstreamSafetyRating("HARM_CATEGORY_HARASSMENT", "NEGLIGIBLE", null),
        }, block.SafetyRatings);
    }

    /// <summary>實測 PROHIBITED_CONTENT 常常連 safetyRatings 都不給（主規格 §6.2）：記空清單，不要炸。</summary>
    [Fact]
    public async Task Input_block_without_ratings_records_an_empty_list()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        await SendAsync(new Canned(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}"""));

        var block = Assert.IsType<UpstreamBlock>(diag.Block);
        Assert.Equal("input_blocked", block.Kind);
        Assert.Empty(block.SafetyRatings);
    }

    [Fact]
    public async Task Content_block_finish_reason_is_recorded_as_output_blocked_with_candidate_ratings()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        await SendAsync(new Canned(HttpStatusCode.OK, """
            {"candidates":[{"finishReason":"SAFETY","safetyRatings":[
               {"category":"HARM_CATEGORY_SEXUALLY_EXPLICIT","probability":"HIGH","blocked":true},
               {"probability":"LOW"}]}]}
            """));

        var block = Assert.IsType<UpstreamBlock>(diag.Block);
        Assert.Equal("output_blocked", block.Kind);
        Assert.Equal("SAFETY", block.Reason);
        Assert.Equal(new[]
        {
            new UpstreamSafetyRating("HARM_CATEGORY_SEXUALLY_EXPLICIT", "HIGH", true),
            new UpstreamSafetyRating(null, "LOW", null),                // 缺欄位照收
        }, block.SafetyRatings);
    }

    [Fact]
    public async Task Non_content_finish_reason_is_not_a_block()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        await SendAsync(new Canned(HttpStatusCode.OK, """{"candidates":[{"finishReason":"MAX_TOKENS"}]}"""));
        Assert.Null(diag.Block);
    }

    [Fact]
    public async Task Error_status_records_the_status_and_the_body()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        const string error = """{"error":{"code":400,"message":"Please ensure that function call turn comes immediately after a user turn","status":"INVALID_ARGUMENT"}}""";
        var (response, body) = await SendAsync(new Canned(HttpStatusCode.BadRequest, error));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, body);
        var http = Assert.IsType<UpstreamHttpError>(diag.HttpError);
        Assert.Equal(400, http.Status);
        Assert.Equal(error, http.Body);
        Assert.Null(diag.Block);
    }

    [Fact]
    public async Task Error_body_is_truncated_to_about_two_kilobytes()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var huge = new string('x', 10_000);
        var (_, body) = await SendAsync(new Canned(HttpStatusCode.InternalServerError, huge));

        Assert.Equal(huge, body);                                    // 往下傳的不截
        var http = Assert.IsType<UpstreamHttpError>(diag.HttpError);
        Assert.Equal(500, http.Status);
        Assert.True(http.Body.Length <= GeminiDiagnosticsHandler.MaxErrorBodyChars + 1);
        Assert.StartsWith("xxxx", http.Body);
    }

    /// <summary>紀錄是「最近一次呼叫」：重試過了之後，前一次的 400／攔截不該冒充這一輪的原因。</summary>
    [Fact]
    public async Task A_later_normal_response_clears_the_previous_error_and_block()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        await SendAsync(new Canned(HttpStatusCode.BadRequest, """{"error":{}}"""));
        await SendAsync(new Canned(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}"""));
        Assert.Null(diag.HttpError);
        Assert.NotNull(diag.Block);
        await SendAsync(new Canned(HttpStatusCode.OK, Stop));
        Assert.Null(diag.Block);
        Assert.Equal(3, diag.Calls);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"candidates":"not an array","promptFeedback":42}""")]
    [InlineData("""{"promptFeedback":{"blockReason":7,"safetyRatings":"x"}}""")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY","safetyRatings":[1,"a",{"category":3,"blocked":"yes"}]}]}""")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task Malformed_bodies_never_throw(string json)
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var (_, body) = await SendAsync(new Canned(HttpStatusCode.OK, json));
        Assert.Equal(json, body);
        Assert.Equal(1, diag.Calls);
    }

    [Fact]
    public async Task Other_urls_are_not_observed()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        var inner = new Canned(HttpStatusCode.BadRequest, """{"error":{}}""");
        var (response, _) = await SendAsync(inner, "https://example.invalid/v1beta/models/e:batchEmbedContents");

        Assert.Same(inner.Sent, response.Content);                  // 連 content 都沒換
        Assert.Equal(0, diag.Calls);
        Assert.Null(diag.HttpError);
    }

    /// <summary>embedding、背景工作等不在一輪裡的呼叫：沒有 holder 也照常通過。</summary>
    [Fact]
    public async Task Without_a_current_holder_the_call_still_goes_through()
    {
        UpstreamDiagnostics.Current = null;
        var (response, body) = await SendAsync(new Canned(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"promptFeedback":{"blockReason":"SAFETY"}}""", body);
    }

    /// <summary>逾時：呼叫被取消時仍算在途，orchestrator 才算得出它卡了多久。</summary>
    [Fact]
    public async Task A_cancelled_call_stays_pending()
    {
        var diag = UpstreamDiagnostics.Current = new UpstreamDiagnostics();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var client = new HttpClient(new GeminiDiagnosticsHandler(new Hanging(), NullLogger<GeminiDiagnosticsHandler>.Instance));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsync(GenerateUrl, new StringContent("{}"), cts.Token));

        Assert.Equal(1, diag.Calls);
        Assert.NotNull(diag.PendingMs);
        Assert.True(diag.PendingMs >= 0);
    }

    [Fact]
    public async Task Logs_one_information_line_per_call()
    {
        UpstreamDiagnostics.Current = null;
        var log = new ListLogger<GeminiDiagnosticsHandler>();
        await SendAsync(new Canned(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}"""), logger: log);
        await SendAsync(new Canned(HttpStatusCode.OK, Stop), logger: log);
        await SendAsync(new Canned(HttpStatusCode.BadRequest, "{}"), logger: log);

        Assert.All(log.Lines, l => Assert.Equal(LogLevel.Information, l.Level));
        Assert.Equal(3, log.Lines.Count);
        Assert.Matches(@"^Gemini 200 \d+ ms finish=- block=PROHIBITED_CONTENT$", log.Lines[0].Message);
        Assert.Matches(@"^Gemini 200 \d+ ms finish=STOP block=-$", log.Lines[1].Message);
        Assert.Matches(@"^Gemini 400 \d+ ms finish=- block=-$", log.Lines[2].Message);
    }
}
