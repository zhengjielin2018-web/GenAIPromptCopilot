using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace PromptCopilot.Api.Orchestration;

/// <summary>多輪 §6.3。壓縮吃的是 LLM 產的 JSON：形狀不對就整段跳過，絕不丟例外。
/// 壓縮不改語意，只丟掉 ledger 已經有的內容。</summary>
public static class HistoryTrimmer
{
    private static readonly HashSet<string> OptionCarriers = new() { ToolNames.AskUser, ToolNames.Discuss };

    public static void CompressTurn(ChatHistory h, int fromIndex)
    {
        for (var i = fromIndex; i < h.Count; i++)
        {
            foreach (var r in h[i].Items.OfType<FunctionResultContent>().ToList())
            {
                var compressed = CompressResult(r.FunctionName ?? "", r.Result?.ToString() ?? "");
                if (compressed is null) continue;
                h[i].Items.Remove(r);
                h[i].Items.Add(new FunctionResultContent(r.FunctionName, r.PluginName, r.CallId, compressed));
            }
            foreach (var c in h[i].Items.OfType<FunctionCallContent>())
            {
                if (!OptionCarriers.Contains(c.FunctionName) || c.Arguments is null) continue;
                if (c.Arguments.TryGetValue("options", out var o) && o is not null) c.Arguments["options"] = StripTags(o.ToString()!);
                if (c.Arguments.TryGetValue("asks", out var a) && a is not null) c.Arguments["asks"] = StripAskTags(a.ToString()!);
            }
        }
    }

    private static string? CompressResult(string functionName, string json)
    {
        try
        {
            switch (functionName)
            {
                case ToolNames.SearchPresets:
                {
                    if (JsonNode.Parse(json) is not JsonObject root || root["results"] is not JsonArray results) return null;
                    var slim = new JsonArray(results.OfType<JsonObject>().Select(r =>
                    {
                        if (r["error"] is not null)
                            return (JsonNode)new JsonObject { ["dimension"] = r["dimension"]?.DeepClone(), ["error"] = r["error"]!.DeepClone() };
                        var hits = r["hits"] as JsonArray ?? new JsonArray();
                        var ids = new JsonArray(hits.OfType<JsonObject>().Select(x => (JsonNode)new JsonObject { ["id"] = x["id"]?.DeepClone(), ["title"] = x["title"]?.DeepClone() }).ToArray());
                        return new JsonObject { ["dimension"] = r["dimension"]?.DeepClone(), ["poolSize"] = r["poolSize"]?.DeepClone(), ["hits"] = ids };
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
