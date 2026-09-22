using System.Text.Encodings.Web;
using System.Text.Json;
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

    public static string Summary(KernelArguments? args, int max = 80)
    {
        var t = ArgsText(args).Replace("\n", " ");
        return t.Length <= max ? t : t[..max] + "…";
    }
}
