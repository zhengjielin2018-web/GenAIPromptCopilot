using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Orchestration;

namespace PromptCopilot.Api.Filters;

public static class TurnContextExtensions
{
    public const string DataKey = "turn";

    /// <summary>每輪的 kernel 在 Data 裡帶 TurnContext；filter 由此拿到本輪狀態。</summary>
    public static TurnContext Turn(this Kernel k) =>
        k.Data.TryGetValue(DataKey, out var t) && t is TurnContext turn ? turn
        : throw new InvalidOperationException("kernel.Data 缺少 TurnContext；AgentKernelFactory 應該已放入");

    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>所有參數值串成一段文字，給分類器看。
    /// JsonElement 不能用 GetRawText()：那是 Gemini 回來的原文，非 ASCII 全被 escape 成 \uXXXX，
    /// 分類器會拿到六個 ASCII 字元一組的跳脫序列，而不是「少女」兩個字。輸出側是繁中訊息
    /// （preamble、Discuss 的 message、tips）送到使用者眼前之前唯一的閘門，不該讓它多做一層解碼。
    /// 字串元素直接取值（連引號都不要），
    /// 物件與陣列重新序列化成不 escape 的 JSON，巢狀的 option 標籤才看得懂。</summary>
    public static string ArgsText(KernelArguments? args) =>
        args is null ? "" : string.Join("\n", args.Values.Select(Render));

    private static string Render(object? v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        JsonElement je => JsonSerializer.Serialize(je, Unescaped),
        _ => v?.ToString() ?? "",
    };

    /// <summary>送進輸出側分類器的文字：只取這個 tool 真的會出現在使用者眼前的欄位。
    /// 不能整包參數下去——SD 的 negativePrompt 本來就長成「nsfw, nude, naked」（那是排除清單），
    /// 分類器看到會照實判成 NSFW，於是每一次定稿都被自己的排除詞擋掉。
    /// 主規格 §6.2 把 FinalizePrompt 的檢查範圍寫成 positivePrompt；facetStates 是機器狀態，也不進來。</summary>
    public static string OutputTextFor(string functionName, KernelArguments? args)
    {
        if (args is null) return "";
        var parts = new List<string>();
        switch (functionName)
        {
            case ToolNames.FinalizePrompt:
                parts.Add(Field(args, "positivePrompt"));
                parts.Add(Field(args, "tips"));
                parts.Add(Field(args, "intentSummary"));
                break;
            case ToolNames.Discuss:
                parts.Add(Field(args, "message"));
                AddOptions(parts, Node(args, "options"));
                break;
            case ToolNames.AskUser:
                parts.Add(Field(args, "preamble"));
                foreach (var ask in Node(args, "asks") as JsonArray ?? new JsonArray())
                {
                    if (ask is not JsonObject o) continue;
                    parts.Add(Text(o["question"]));
                    AddOptions(parts, o["options"]);
                }
                break;
            // 之後若多了別的終止型 tool，寧可多看一點（誤攔）也不要漏看
            default: return ArgsText(args);
        }
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static void AddOptions(List<string> parts, JsonNode? options)
    {
        foreach (var opt in options as JsonArray ?? new JsonArray())
        {
            if (opt is not JsonObject o) continue;
            parts.Add(Text(o["label"]));
            parts.Add(Text(o["tags"]));
        }
    }

    private static string Field(KernelArguments args, string name) =>
        args.TryGetValue(name, out var v) ? Render(v) : "";

    private static JsonNode? Node(KernelArguments args, string name) =>
        args.TryGetValue(name, out var v) ? ToNode(v) : null;

    /// <summary>參數值可能是 Gemini 回來的 JsonElement，也可能是測試或強制定稿路徑上的 POCO；
    /// 兩者都攤平成 JsonNode 才能一致地掘出巢狀的 option 標籤。</summary>
    private static JsonNode? ToNode(object? v)
    {
        try
        {
            return v switch
            {
                null => null,
                JsonNode n => n,
                JsonElement je => JsonNode.Parse(je.GetRawText()),
                string s => JsonValue.Create(s),
                _ => JsonSerializer.SerializeToNode(v, Unescaped),
            };
        }
        catch (JsonException) { return null; }
    }

    private static string Text(JsonNode? n) =>
        n is null ? "" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString(Unescaped);

    public static string Summary(KernelArguments? args, int max = 80)
    {
        var t = ArgsText(args).Replace("\n", " ");
        return t.Length <= max ? t : t[..max] + "…";
    }
}
