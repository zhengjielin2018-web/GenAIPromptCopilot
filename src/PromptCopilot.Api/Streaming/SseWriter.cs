using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptCopilot.Api.Streaming;

public static class SseWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task WriteAsync(HttpResponse response, IAsyncEnumerable<AgentEvent> events, CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await foreach (var e in events.WithCancellation(ct))
        {
            var data = JsonSerializer.Serialize(e, e.GetType(), Json);   // 用實際型別，才會帶子類欄位
            await response.WriteAsync($"event: {e.Type}\ndata: {data}\n\n", Encoding.UTF8, ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
