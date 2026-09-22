using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace PromptCopilot.Api.Orchestration;

/// <summary>多輪 §6.3。壓縮不改語意，只丟掉 ledger 已經有的內容。</summary>
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
                    var root = JsonNode.Parse(json)?.AsObject();
                    var hits = root?["hits"]?.AsArray();
                    if (hits is null) return null;
                    var slim = new JsonArray(hits.Select(x => (JsonNode)new JsonObject { ["id"] = x!["id"]?.DeepClone(), ["title"] = x["title"]?.DeepClone() }).ToArray());
                    return new JsonObject { ["dimension"] = root!["dimension"]?.DeepClone(), ["poolSize"] = root["poolSize"]?.DeepClone(), ["hits"] = slim }.ToJsonString(Json);
                }
                case ToolNames.SearchSimilarPrompts:
                {
                    var arr = JsonNode.Parse(json)?.AsArray();
                    if (arr is null) return null;
                    return new JsonArray(arr.Select(x =>
                    {
                        var intent = x?["intent"]?.GetValue<string>() ?? "";
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
            var arr = JsonNode.Parse(json)?.AsArray();
            if (arr is null) return json;
            foreach (var o in arr) o?.AsObject().Remove("tags");
            return arr.ToJsonString(Json);
        }
        catch (JsonException) { return json; }
    }

    private static string StripAskTags(string json)
    {
        try
        {
            var arr = JsonNode.Parse(json)?.AsArray();
            if (arr is null) return json;
            foreach (var ask in arr)
                foreach (var o in ask?["options"]?.AsArray() ?? new JsonArray()) o?.AsObject().Remove("tags");
            return arr.ToJsonString(Json);
        }
        catch (JsonException) { return json; }
    }

    /// <summary>保留 system（若在 index 0）+ 最近 keepTurns 輪；一輪從一則 user message 起。</summary>
    public static void Truncate(ChatHistory h, int keepTurns)
    {
        var hasSystem = h.Count > 0 && h[0].Role == AuthorRole.System;
        var userIdx = Enumerable.Range(hasSystem ? 1 : 0, Math.Max(0, h.Count - (hasSystem ? 1 : 0)))
            .Where(i => h[i].Role == AuthorRole.User).ToList();
        if (userIdx.Count <= keepTurns) return;
        var cut = userIdx[^keepTurns];
        for (var i = cut - 1; i >= (hasSystem ? 1 : 0); i--) h.RemoveAt(i);
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
