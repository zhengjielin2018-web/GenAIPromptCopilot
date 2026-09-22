using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Llm;

public interface IEmbeddingClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct);
}

/// <summary>直接打 REST 而不用 SK 抽象：要保證 taskType、outputDimensionality、L2 正規化三件事與 Python 管線一致。</summary>
public sealed class GeminiEmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> emb, IOptions<LlmOptions> llm) : IEmbeddingClient
{
    public const string RetrievalQuery = "RETRIEVAL_QUERY";
    public const string RetrievalDocument = "RETRIEVAL_DOCUMENT";
    public const int BatchSize = 32;

    private sealed record BatchResponse(List<EmbeddingValues> Embeddings);
    private sealed record EmbeddingValues(float[] Values);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct)
    {
        var o = emb.Value;
        var all = new List<float[]>(texts.Count);
        foreach (var chunk in texts.Chunk(BatchSize))
        {
            var payload = new
            {
                requests = chunk.Select(t => new
                {
                    model = $"models/{o.Model}",
                    content = new { parts = new[] { new { text = t } } },
                    taskType,
                    outputDimensionality = o.Dimensions,
                }),
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.Endpoint}/models/{o.Model}:batchEmbedContents")
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.Add("x-goog-api-key", llm.Value.ApiKey);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var parsed = await resp.Content.ReadFromJsonAsync<BatchResponse>(new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                         ?? throw new InvalidOperationException("embedding 回應為空");
            all.AddRange(parsed.Embeddings.Select(e => Normalize(e.Values)));
        }
        return all;
    }

    private static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }
}
