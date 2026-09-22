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
            await WriteOneAsync(response, e, ct);
    }

    /// <summary>frame 的格式只定義在這裡。串流中途失敗要補發 error 事件時，headers 已經送出去了，
    /// 不能再走 <see cref="WriteAsync"/>（設 ContentType 會炸），所以單獨開這個入口。</summary>
    public static async Task WriteOneAsync(HttpResponse response, AgentEvent e, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(e, e.GetType(), Json);   // 用實際型別，才會帶子類欄位
        await response.WriteAsync($"event: {e.Type}\ndata: {data}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }
}
