using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Rendering;

public sealed record RunPodJob(string Id, string Status, int? DelayTime, int? ExecutionTime, JsonElement? Output, string? Error)
{
    private static readonly HashSet<string> Terminal = new() { "COMPLETED", "FAILED", "CANCELLED", "TIMED_OUT" };
    public bool IsTerminal => Terminal.Contains(Status);
}

public interface IRunPodClient
{
    /// <summary>送出失敗不重送：結果不明時 RunPod 可能已經建了工作，重送會多跑（多付）一次。</summary>
    Task<string> SubmitAsync(JsonObject workflow, CancellationToken ct);

    /// <summary>輪詢到終止狀態。沒等到就離開（逾時、查狀態出錯、取消）一律先送 cancel（盡力而為）再丟出去，
    /// 不讓已經送出的工作在背景繼續計費（同 scripts/render_spike.py 的 wait）。</summary>
    Task<RunPodJob> WaitAsync(string jobId, TimeSpan timeout, CancellationToken ct);
}

/// <summary>RunPod Serverless 的 REST（/run、/status、/cancel）。行為照 scripts/render_spike.py 的 RunPodClient。</summary>
public sealed class RunPodClient(HttpClient http, TimeSpan pollInterval, TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay = null) : IRunPodClient
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));

    /// <summary>EndpointId 是空的（沒開）時 BaseAddress 照樣合法，只是不會有人呼叫。</summary>
    public static RunPodClient Create(RenderOptions o, TimeProvider time)
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri($"https://api.runpod.ai/v2/{o.EndpointId}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        return new RunPodClient(http, TimeSpan.FromMilliseconds(o.PollIntervalMs), time);
    }

    public async Task<string> SubmitAsync(JsonObject workflow, CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync("run", new { input = new { workflow } }, ct);
        r.EnsureSuccessStatusCode();
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s ? s : throw new InvalidOperationException("RunPod 的 /run 沒有回 id");
    }

    public async Task<RunPodJob> WaitAsync(string jobId, TimeSpan timeout, CancellationToken ct)
    {
        var start = time.GetUtcNow();
        try
        {
            while (true)
            {
                using var r = await http.GetAsync($"status/{jobId}", ct);
                r.EnsureSuccessStatusCode();
                var job = Parse(await r.Content.ReadFromJsonAsync<JsonElement>(ct));
                if (job.IsTerminal) return job;
                if (time.GetUtcNow() - start > timeout) throw new TimeoutException($"工作 {jobId} 超過 {timeout.TotalSeconds:0} 秒沒結束，已要求取消");
                await _delay(pollInterval, ct);
            }
        }
        catch
        {
            await CancelAsync(jobId);
            throw;
        }
    }

    /// <summary>盡力而為：呼叫端的 token 可能已經取消了（服務停止），用自己的短逾時。</summary>
    private async Task CancelAsync(string jobId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { using var _ = await http.PostAsync($"cancel/{jobId}", content: null, cts.Token); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
    }

    internal static RunPodJob Parse(JsonElement e) => new(
        e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
        e.TryGetProperty("status", out var st) ? st.GetString() ?? "?" : "?",
        Int(e, "delayTime"), Int(e, "executionTime"),
        e.TryGetProperty("output", out var o) && o.ValueKind != JsonValueKind.Null ? o.Clone() : null,
        e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null);

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    /// <summary>worker-comfyui 5.x 的輸出：output.images[{filename, type, data}]。只收 base64（endpoint 沒設 S3 時的預設）。</summary>
    public static byte[] ExtractImage(RunPodJob job)
    {
        if (job.Output is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("images", out var images)
            || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            throw new InvalidOperationException($"工作沒有回圖：{job.Error ?? job.Output?.ToString() ?? "（沒有 output）"}");
        var first = images[0];
        var type = first.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != "base64") throw new InvalidOperationException($"只支援 base64 輸出，收到 {type}；endpoint 不要設 S3 相關的環境變數");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(first.TryGetProperty("data", out var d) ? d.GetString() ?? "" : ""); }
        catch (FormatException e) { throw new InvalidOperationException("圖片的 base64 解不開", e); }
        return bytes.Length > 0 ? bytes : throw new InvalidOperationException("工作回的圖是空的");
    }
}
