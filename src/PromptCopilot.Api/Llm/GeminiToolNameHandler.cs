using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PromptCopilot.Api.Llm;

/// <summary>一輪之內模型反覆呼叫沒宣告的工具，<see cref="GeminiToolNameHandler"/> 中止這次 SK 呼叫（known-issues #13）。
/// 不是傳輸錯誤也不是內容攔截，重試層不重試；orchestrator 把它當成「這次呼叫沒有結果」，走補提示重試或強制收尾。</summary>
public sealed class UndeclaredToolCallException(IReadOnlyList<string> names)
    : Exception($"模型呼叫了這一輪沒宣告的工具：{string.Join("、", names)}")
{
    public IReadOnlyList<string> Names { get; } = names;
}

/// <summary>
/// kernel 宣告的工具名是 <c>&lt;Plugin&gt;_&lt;Function&gt;</c>（<c>Dialog_Confirm</c>），模型有時只寫 <c>Confirm</c>。
/// Connectors.Google 1.80.1-alpha 的 auto-invoke 對沒宣告的名字只回一句
/// <c>Error: Function call request for a function that wasn't defined.</c>：不經任何 <c>IAutoFunctionInvocationFilter</c>，
/// tool 預算數不到、終止型 filter 也停不下來，一次 SK 呼叫最多跑 128 趟，實際上先撞到整輪 120 秒逾時（known-issues #13）。
/// 這裡在 connector 解析回應之前處理：
/// <list type="bullet">
/// <item>裸名剛好對上一個宣告（<c>_</c> 後面那段相同）就改成那個全名，呼叫照常進 plugin 與 filter。
/// <c>thoughtSignature</c> 原樣留著：Gemini 3 只驗有沒有帶，不綁函式名稱（2026-10-05 實測：改名後送回 200，拿掉簽章 400）。</item>
/// <item>改不回來的（這一輪根本沒有的工具、對上兩個宣告的裸名）照原樣交給 SK，記進 <see cref="UpstreamDiagnostics"/>；
/// 同一輪累積到 <see cref="UndeclaredLimit"/> 次就丟 <see cref="UndeclaredToolCallException"/>。</item>
/// </list>
/// 只看請求、不改請求。宣告清單從同一個請求的 <c>tools[].functionDeclarations[].name</c> 讀，沒帶 tools 的請求（分類器）不處理。
/// 不在一輪裡（<see cref="UpstreamDiagnostics.Current"/> 是 null）照樣改名，只是不計數、不中止。
/// </summary>
public sealed class GeminiToolNameHandler(HttpMessageHandler inner, ILogger<GeminiToolNameHandler> logger) : DelegatingHandler(inner)
{
    /// <summary>同一輪第幾次沒宣告的呼叫就中止。第一次放過去，SK 回的錯誤給模型一次機會自己改；
    /// 實測它多半原封不動重送，第二次就收掉，交給 orchestrator 帶著點名的提示重試。</summary>
    public const int UndeclaredLimit = 2;

    private const string DeclarationMarker = "\"functionDeclarations\"";
    private const string CallMarker = "\"functionCall\"";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var declared = request.Content is null ? null : Declared(await request.Content.ReadAsStringAsync(ct));
        var response = await base.SendAsync(request, ct);
        if (declared is not { Count: > 0 } || !response.IsSuccessStatusCode) return response;

        var body = await response.Content.ReadAsStringAsync(ct);
        // 先做字串預檢：純文字回覆不必 parse
        if (!body.Contains(CallMarker, StringComparison.Ordinal)) return response;
        var (fixedBody, repairs, undeclared) = Repair(body, declared);

        var diag = UpstreamDiagnostics.Current;
        foreach (var (from, to) in repairs)
        {
            diag?.ToolNameRepaired();
            logger.LogInformation("Gemini tool name repaired {From} → {To}", from, to);
        }
        var count = 0;
        foreach (var name in undeclared)
        {
            count = diag?.UndeclaredToolCall(name) ?? 0;
            logger.LogWarning("Gemini called undeclared tool {Name} ({Count}/{Limit} this turn)", name, count, UndeclaredLimit);
        }
        if (count >= UndeclaredLimit)
        {
            response.Dispose();
            throw new UndeclaredToolCallException(diag!.UndeclaredToolCalls);
        }

        if (fixedBody is not null)
        {
            var media = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            response.Content.Dispose();
            response.Content = new StringContent(fixedBody, Encoding.UTF8, new MediaTypeHeaderValue(media));
        }
        return response;
    }

    /// <summary>請求宣告的工具名。沒帶 tools、或解析不了，就是 null。</summary>
    internal static IReadOnlyList<string>? Declared(string body)
    {
        if (!body.Contains(DeclarationMarker, StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) return null;
            var names = new List<string>();
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("functionDeclarations", out var fds) || fds.ValueKind != JsonValueKind.Array) continue;
                foreach (var fd in fds.EnumerateArray())
                    if (fd.ValueKind == JsonValueKind.Object && fd.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        names.Add(n.GetString()!);
            }
            return names;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>逐一看 <c>candidates[].content.parts[].functionCall.name</c>。宣告過的（跟 connector 一樣不分大小寫）不動；
    /// 裸名只對上一個宣告就改；其餘列進 undeclared。有改才回新的本文，否則 Body 是 null；解析不了就什麼都不做。</summary>
    internal static (string? Body, List<(string From, string To)> Repairs, List<string> Undeclared) Repair(string body, IReadOnlyList<string> declared)
    {
        var repairs = new List<(string From, string To)>();
        var undeclared = new List<string>();
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return (null, repairs, undeclared); }
        if (root?["candidates"] is not JsonArray candidates) return (null, repairs, undeclared);

        foreach (var candidate in candidates)
        {
            if (candidate?["content"]?["parts"] is not JsonArray parts) continue;
            foreach (var part in parts)
            {
                if (part?["functionCall"] is not JsonObject call || call["name"] is not JsonValue v || !v.TryGetValue<string>(out var name)) continue;
                if (declared.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var matches = declared.Where(d => BareName(d).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    call["name"] = matches[0];
                    repairs.Add((name, matches[0]));
                }
                else undeclared.Add(name);
            }
        }
        return (repairs.Count > 0 ? root.ToJsonString() : null, repairs, undeclared);
    }

    /// <summary>connector 拆全名的方式：第一個 <c>_</c> 之後是函式名（GeminiFunction.NameSeparator）。</summary>
    private static string BareName(string declared)
    {
        var i = declared.IndexOf('_');
        return i < 0 ? declared : declared[(i + 1)..];
    }
}
