using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PromptCopilot.Api.Llm;

/// <summary>
/// 觀察每一次 generateContent 的回應，給 audit 與容器 log 用（known-issues #7）。
/// Connectors.Google 1.80.1-alpha 對被擋的 prompt 只丟 <c>KernelException("Prompt was blocked due to Gemini API safety reasons.")</c>，
/// 400 只留 <c>Response status code does not indicate success: 400 (Bad Request).</c>：
/// 輸入被拒（<c>promptFeedback.blockReason</c>）與輸出被截（<c>candidates[].finishReason</c>）分不出來，
/// <c>safetyRatings</c> 與上游的錯誤本文也都丟了。這裡在 connector 解析之前把本文讀出來，記進
/// <see cref="UpstreamDiagnostics.Current"/>，再換一份同樣內容的 content 往上交，connector 照常解析。
/// 只看請求、不改請求；改 role 的是 <see cref="GeminiRoleFixHandler"/>。每次呼叫寫一行 Information log。
/// </summary>
public sealed class GeminiDiagnosticsHandler(HttpMessageHandler inner, ILogger<GeminiDiagnosticsHandler> logger) : DelegatingHandler(inner)
{
    /// <summary>非 2xx 本文寫進 audit 的上限。Gemini 的錯誤 JSON 通常幾百字，2 KB 夠看，也不會讓一筆 audit 失控。</summary>
    public const int MaxErrorBodyChars = 2048;

    // connector 用的是 models/{model}:generateContent；串流的 :streamGenerateContent 本專案不用，大小寫也不同，不會誤中
    private const string Marker = ":generateContent";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsolutePath.Contains(Marker, StringComparison.Ordinal) != true)
            return await base.SendAsync(request, ct);

        var diag = UpstreamDiagnostics.Current;
        diag?.CallStarted();
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        byte[] bytes;
        try
        {
            response = await base.SendAsync(request, ct);
            bytes = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) { throw; }       // 留著在途：逾時的 audit 要算它卡了多久
        catch { diag?.CallAbandoned(); throw; }

        // 換一份位元組相同、header 相同的 content 往上交；原本那份已經讀完了
        var copy = new ByteArrayContent(bytes);
        foreach (var h in response.Content.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        response.Content.Dispose();
        response.Content = copy;

        var body = Encoding.UTF8.GetString(bytes);
        var status = (int)response.StatusCode;
        UpstreamHttpError? error = null;
        (UpstreamBlock? Block, string? Finish, string? PromptBlock) seen = default;
        if (response.IsSuccessStatusCode) seen = Inspect(body);
        else error = new UpstreamHttpError(status, body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] + "…" : body);
        diag?.CallCompleted(error, seen.Block);

        logger.LogInformation("Gemini {Status} {ElapsedMs} ms finish={FinishReason} block={BlockReason}",
            status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, seen.Finish ?? "-", seen.PromptBlock ?? "-");
        return response;
    }

    /// <summary>從 2xx 本文挑出攔截資訊。promptFeedback.blockReason 優先（輸入被拒時根本沒有 candidates）；
    /// 否則看 candidates[0].finishReason 是否屬內容攔截（與 <see cref="LlmFailureClassifier.ContentBlockReasons"/> 同一組）。
    /// 解析不了或形狀不對就什麼都不記——這裡只是旁觀，不能讓它把一次正常的呼叫弄壞。</summary>
    internal static (UpstreamBlock? Block, string? Finish, string? PromptBlock) Inspect(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;

            JsonElement? candidate = root.TryGetProperty("candidates", out var cs) && cs.ValueKind == JsonValueKind.Array
                && cs.GetArrayLength() > 0 && cs[0].ValueKind == JsonValueKind.Object ? cs[0] : null;
            var finish = candidate is { } c0 ? Str(c0, "finishReason") : null;

            if (root.TryGetProperty("promptFeedback", out var fb) && fb.ValueKind == JsonValueKind.Object && Str(fb, "blockReason") is { } blockReason)
                return (new UpstreamBlock("input_blocked", blockReason, Ratings(fb)), finish, blockReason);
            if (candidate is { } c && finish is not null && LlmFailureClassifier.ContentBlockReasons.Contains(finish.ToUpperInvariant()))
                return (new UpstreamBlock("output_blocked", finish, Ratings(c)), finish, null);
            return (null, finish, null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return default; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<UpstreamSafetyRating> Ratings(JsonElement owner)
    {
        if (!owner.TryGetProperty("safetyRatings", out var rs) || rs.ValueKind != JsonValueKind.Array) return Array.Empty<UpstreamSafetyRating>();
        var list = new List<UpstreamSafetyRating>();
        foreach (var r in rs.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            bool? blocked = r.TryGetProperty("blocked", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.GetBoolean() : null;
            list.Add(new UpstreamSafetyRating(Str(r, "category"), Str(r, "probability"), blocked));
        }
        return list;
    }
}
