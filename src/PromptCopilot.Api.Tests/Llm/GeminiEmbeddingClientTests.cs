using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Llm;

namespace PromptCopilot.Api.Tests.Llm;

public class GeminiEmbeddingClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpRequestMessage req, string body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            var n = JsonDocument.Parse(body).RootElement.GetProperty("requests").GetArrayLength();
            var embeddings = string.Join(",", Enumerable.Repeat("""{"values":[3,4]}""", n));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"embeddings":[{{embeddings}}]}""") };
        }
    }

    private static (GeminiEmbeddingClient client, Handler h) Make()
    {
        var h = new Handler();
        var c = new GeminiEmbeddingClient(new HttpClient(h),
            Options.Create(new EmbeddingOptions { Model = "gemini-embedding-001", Dimensions = 768, Endpoint = "https://x/v1beta" }),
            Options.Create(new LlmOptions { ApiKey = "KEY" }));
        return (c, h);
    }

    [Fact]
    public async Task Sends_task_type_dimensions_and_key_header_then_normalizes()
    {
        var (c, h) = Make();
        var v = await c.EmbedAsync(new[] { "雨夜" }, GeminiEmbeddingClient.RetrievalQuery, default);
        var (req, body) = h.Calls.Single();
        Assert.Equal("https://x/v1beta/models/gemini-embedding-001:batchEmbedContents", req.RequestUri!.ToString());
        Assert.Equal("KEY", req.Headers.GetValues("x-goog-api-key").Single());
        var r = JsonDocument.Parse(body).RootElement.GetProperty("requests")[0];
        Assert.Equal("RETRIEVAL_QUERY", r.GetProperty("taskType").GetString());
        Assert.Equal(768, r.GetProperty("outputDimensionality").GetInt32());
        Assert.Equal("models/gemini-embedding-001", r.GetProperty("model").GetString());
        Assert.Equal(0.6f, v[0][0], 5); Assert.Equal(0.8f, v[0][1], 5);   // [3,4] → [0.6,0.8]
    }

    [Fact]
    public async Task Chunks_at_batch_size()
    {
        var (c, h) = Make();
        var v = await c.EmbedAsync(Enumerable.Range(0, GeminiEmbeddingClient.BatchSize + 5).Select(i => $"t{i}").ToList(), GeminiEmbeddingClient.RetrievalQuery, default);
        Assert.Equal(GeminiEmbeddingClient.BatchSize + 5, v.Count);
        Assert.Equal(2, h.Calls.Count);
    }

    [Fact]
    public async Task Empty_input_makes_no_call()
    {
        var (c, h) = Make();
        Assert.Empty(await c.EmbedAsync(Array.Empty<string>(), GeminiEmbeddingClient.RetrievalQuery, default));
        Assert.Empty(h.Calls);
    }
}
