using System.Net;
using System.Net.Http.Json;

namespace PromptCopilot.Api.Tests.Fakes;

/// <summary>記下每個請求（方法、路徑、body），回呼叫端給的回應；回應函式可以丟例外模擬連線錯誤。</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path)> Requests { get; } = new();
    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) { Requests.Add((request.Method, request.RequestUri!.AbsolutePath)); Bodies.Add(body); }
        return await respond(request);
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = JsonContent.Create(body) };
}
