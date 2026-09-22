using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PromptCopilot.Api.Llm;

/// <summary>
/// Connectors.Google 1.80.1-alpha 把 tool 回覆那一則 content 標成 <c>"role":"function"</c>，
/// 但 Gemini API 只收 user／model，2026-09 起直接回 400：
/// <c>Role 'function' is not supported. Please use a valid role: …, USER, …, MODEL, USER.</c>
/// 第一次呼叫（只有 user）沒事，第二次（帶 functionResponse）必炸——也就是 auto-invoke 一定跑不完。
/// functionResponse part 本來就該掛在 user 底下（官方 SDK 也這樣送），所以送出前把 role 改掉即可，
/// 其餘欄位（含 functionCall 的 thoughtSignature，Gemini 3 系列要求必帶）原封不動。
/// connector 換版後先確認它不再送 function role，再拿掉這個 handler 與其 DI 註冊。
/// </summary>
public sealed class GeminiRoleFixHandler : DelegatingHandler
{
    private const string Marker = "\"role\":\"function\"";

    public GeminiRoleFixHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            // 先做字串預檢：絕大多數請求沒有 tool 回覆，不必為了它們反覆 parse 十幾 KB 的 JSON
            if (body.Contains(Marker, StringComparison.Ordinal) && Rewrite(body) is { } fixedBody)
            {
                var media = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                request.Content = new StringContent(fixedBody, Encoding.UTF8, new MediaTypeHeaderValue(media));
            }
        }
        return await base.SendAsync(request, ct);
    }

    /// <summary>只改 contents[].role；其他地方出現的同樣字串（例如 function declaration 的描述）不動。
    /// 解析不了就回 null，讓原始 body 照送——寧可讓上游回它的錯，也不要在這裡吞掉。</summary>
    internal static string? Rewrite(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return null; }
        if (root?["contents"] is not JsonArray contents) return null;

        var changed = false;
        foreach (var item in contents)
        {
            if (item is not JsonObject content) continue;
            if (content["role"]?.GetValue<string>() != "function") continue;
            content["role"] = "user";
            changed = true;
        }
        return changed ? root.ToJsonString() : null;
    }
}
