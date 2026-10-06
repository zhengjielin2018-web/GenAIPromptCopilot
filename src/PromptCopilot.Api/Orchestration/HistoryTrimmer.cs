using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace PromptCopilot.Api.Orchestration;

/// <summary>多輪 §6.3。壓縮吃的是 LLM 產的 JSON：形狀不對就整段跳過，絕不丟例外。
/// 壓縮不改語意，只丟掉 ledger 已經有的內容。</summary>
public static class HistoryTrimmer
{
    private static readonly HashSet<string> OptionCarriers = new() { ToolNames.AskUser, ToolNames.Discuss };

    /// <summary>keepSearchResults：確認輪收尾時 SearchPresets 結果先不壓，留給下一輪用完整片段（檢索時機設計 §4）；
    /// 下一輪收尾由 <see cref="CompressSearchResultsBefore"/> 壓掉。選項的 tags 照舊剝掉。</summary>
    public static void CompressTurn(ChatHistory h, int fromIndex, bool keepSearchResults = false)
    {
        Func<string, bool> include = keepSearchResults ? name => name != ToolNames.SearchPresets : _ => true;
        for (var i = fromIndex; i < h.Count; i++)
        {
            if (CompressResults(h, i, include)) continue;
            foreach (var c in h[i].Items.OfType<FunctionCallContent>())
            {
                if (!OptionCarriers.Contains(c.FunctionName) || c.Arguments is null) continue;
                if (c.Arguments.TryGetValue("options", out var o) && o is not null) c.Arguments["options"] = StripTags(o.ToString()!);
                if (c.Arguments.TryGetValue("asks", out var a) && a is not null) c.Arguments["asks"] = StripAskTags(a.ToString()!);
            }
        }
    }

    /// <summary>檢索時機設計 §4：把 endIndex 之前還沒壓的 SearchPresets 結果壓掉。每輪收尾都呼叫，
    /// 效果是確認輪留下的結果留到下一輪收尾。已壓過的 <see cref="CompressResult"/> 認得出來、原樣不動。</summary>
    public static void CompressSearchResultsBefore(ChatHistory h, int endIndex)
    {
        for (var i = 0; i < Math.Min(endIndex, h.Count); i++)
            CompressResults(h, i, name => name == ToolNames.SearchPresets);
    }

    /// <summary>第 i 則訊息裡 include 認可的工具結果換成壓縮版。回 true：這則是 Gemini 的工具訊息，已整則換掉。</summary>
    private static bool CompressResults(ChatHistory h, int i, Func<string, bool> include)
    {
        if (h[i] is GeminiChatMessageContent { CalledToolResults.Count: > 0 } g && CompressGemini(h, i, g, include) is { } rebuilt)
        {
            h[i] = rebuilt;
            return true;
        }
        foreach (var r in h[i].Items.OfType<FunctionResultContent>().ToList())
        {
            if (!include(r.FunctionName ?? "")) continue;
            var compressed = CompressResult(r.FunctionName ?? "", r.Result?.ToString() ?? "");
            if (compressed is null) continue;
            h[i].Items.Remove(r);
            h[i].Items.Add(new FunctionResultContent(r.FunctionName, r.PluginName, r.CallId, compressed));
        }
        return false;
    }

    /// <summary>known-issues #8：Connectors.Google 1.80.1-alpha 把 tool 結果放在 <c>CalledToolResults</c>（送出時就是這裡序列化成 functionResponse），
    /// <c>Items</c> 只有一個空的 TextContent，上面 FunctionResultContent 那條路碰不到。<c>GeminiFunctionToolResult</c> 與 <c>FunctionResult</c>
    /// 都沒有公開的 setter，只能整則重建：壓得動的結果換成帶壓縮字串的新 FunctionResult，其餘沿用原物件。
    /// 改放 FunctionResultContent 不行——connector 序列化時直接丟 NotSupportedException。回 null 表示這則不動。</summary>
    private static GeminiChatMessageContent? CompressGemini(ChatHistory h, int index, GeminiChatMessageContent msg, Func<string, bool> include)
    {
        try
        {
            var results = msg.CalledToolResults!;
            var calls = PrecedingToolCalls(h, index);
            var rebuilt = new List<GeminiFunctionToolResult>(results.Count);
            var changed = false;
            foreach (var r in results)
            {
                var name = r.FunctionResult.Function?.Name ?? "";
                var compressed = include(name) ? CompressResult(name, r.FunctionResult.GetValue<object>()?.ToString() ?? "") : null;
                // 新的 GeminiFunctionToolResult 要一個 tool call，但它只從 call 取 FullyQualifiedName（就是 functionResponse 的 name）；
                // 原本那個 call 沒公開，從前一則 model 訊息的 ToolCalls 按名稱找回來。同名的 call 可以互換。
                var call = compressed is null ? null : calls.FirstOrDefault(c => c.FullyQualifiedName == r.FullyQualifiedName);
                if (call is null) { rebuilt.Add(r); continue; }
                rebuilt.Add(new GeminiFunctionToolResult(call, new FunctionResult(r.FunctionResult, compressed)));
                changed = true;
            }
            if (!changed) return null;
            if (rebuilt.Count > 1)
                return MultiResultCtor?.Invoke(new object?[] { msg.Role, msg.Content, msg.ModelId, rebuilt, msg.Metadata }) as GeminiChatMessageContent;
            var single = new GeminiChatMessageContent(rebuilt[0]) { ModelId = msg.ModelId };
            ((KernelContent)single).Metadata = msg.Metadata;           // GeminiChatMessageContent.Metadata 是唯讀的 new 屬性，setter 在基底
            return single;
        }
        catch (Exception) { return null; }                             // 重建靠的是 connector 的內部形狀，出錯寧可不壓，也不能讓一輪失敗
    }

    /// <summary>往前找最近一則帶 ToolCalls 的 model 訊息（auto-invoke 時就是緊鄰的上一則）。</summary>
    private static IReadOnlyList<GeminiFunctionToolCall> PrecedingToolCalls(ChatHistory h, int index)
    {
        for (var j = index - 1; j >= 0; j--)
            if (h[j] is GeminiChatMessageContent { ToolCalls.Count: > 0 } m) return m.ToolCalls!;
        return Array.Empty<GeminiFunctionToolCall>();
    }

    /// <summary>模型一次發多個呼叫時，connector 把全部結果放進同一則 tool 訊息；Gemini 要求 functionResponse 的 part 數跟 call 數一樣，不能拆成多則。
    /// 1.80.1-alpha 只公開單一結果的建構子，多結果的是 internal，只好用反射。換版後找不到就不壓（HistoryTrimmerGeminiTests 會紅）。</summary>
    private static readonly ConstructorInfo? MultiResultCtor = typeof(GeminiChatMessageContent).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic, null,
        new[] { typeof(AuthorRole), typeof(string), typeof(string), typeof(IEnumerable<GeminiFunctionToolResult>), typeof(GeminiMetadata) }, null);

    private static string? CompressResult(string functionName, string json)
    {
        try
        {
            switch (functionName)
            {
                case ToolNames.SearchPresets:
                {
                    if (JsonNode.Parse(json) is not JsonObject root || root["results"] is not JsonArray results) return null;
                    // 已壓過的形狀沒有 query（原始結果每一項都有，含錯誤項）：原樣不動，Gemini 的訊息就不必整則重建（檢索時機設計 §4）
                    if (!results.OfType<JsonObject>().Any(r => r.ContainsKey("query"))) return null;
                    var slim = new JsonArray(results.OfType<JsonObject>().Select(r =>
                    {
                        if (r["error"] is not null)
                            return (JsonNode)new JsonObject { ["dimension"] = r["dimension"]?.DeepClone(), ["error"] = r["error"]!.DeepClone() };
                        var hits = r["hits"] as JsonArray ?? new JsonArray();
                        var ids = new JsonArray(hits.OfType<JsonObject>().Select(x => (JsonNode)new JsonObject { ["id"] = x["id"]?.DeepClone(), ["title"] = x["title"]?.DeepClone() }).ToArray());
                        var slimItem = new JsonObject { ["dimension"] = r["dimension"]?.DeepClone() };
                        if (r["facetId"] is JsonNode facetId) slimItem["facetId"] = facetId.DeepClone();   // facet 項目才有；維度項目是 null，不保留
                        slimItem["poolSize"] = r["poolSize"]?.DeepClone();
                        slimItem["hits"] = ids;
                        return slimItem;
                    }).ToArray());
                    return new JsonObject { ["results"] = slim }.ToJsonString(Json);
                }
                case ToolNames.SearchSimilarPrompts:
                {
                    if (JsonNode.Parse(json) is not JsonArray arr) return null;
                    return new JsonArray(arr.Select(x =>
                    {
                        var intent = (x as JsonObject)?["intent"] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";
                        return (JsonNode)new JsonObject { ["intent"] = intent.Length > 40 ? intent[..40] : intent };
                    }).ToArray()).ToJsonString(Json);
                }
                default: return null;
            }
        }
        catch (JsonException) { return null; }
    }

    private static string StripTags(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr) return json;
            foreach (var o in arr) (o as JsonObject)?.Remove("tags");
            return arr.ToJsonString(Json);
        }
        catch (JsonException) { return json; }
    }

    private static string StripAskTags(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr) return json;
            foreach (var ask in arr)
                foreach (var o in (ask as JsonObject)?["options"] as JsonArray ?? new JsonArray()) (o as JsonObject)?.Remove("tags");
            return arr.ToJsonString(Json);
        }
        catch (JsonException) { return json; }
    }

    /// <summary>保留 system（若在 index 0）+ 最近 keepTurns 輪；一輪從一則 user message 起。</summary>
    public static void Truncate(ChatHistory h, int keepTurns)
    {
        if (keepTurns <= 0) return;            // userIdx[^0] 會直接 IndexOutOfRange，把整輪炸掉
        var hasSystem = h.Count > 0 && h[0].Role == AuthorRole.System;
        var userIdx = Enumerable.Range(hasSystem ? 1 : 0, Math.Max(0, h.Count - (hasSystem ? 1 : 0)))
            .Where(i => h[i].Role == AuthorRole.User).ToList();
        if (userIdx.Count <= keepTurns) return;
        var cut = userIdx[^keepTurns];
        for (var i = cut - 1; i >= (hasSystem ? 1 : 0); i--) h.RemoveAt(i);
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
