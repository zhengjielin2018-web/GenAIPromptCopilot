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

    /// <summary>所有參數值串成一段文字，給分類器看。JsonElement 直接取原文。</summary>
    public static string ArgsText(KernelArguments? args) =>
        args is null ? "" : string.Join("\n", args.Values.Select(v => v is JsonElement je ? je.GetRawText() : v?.ToString() ?? ""));

    public static string Summary(KernelArguments? args, int max = 80)
    {
        var t = ArgsText(args).Replace("\n", " ");
        return t.Length <= max ? t : t[..max] + "…";
    }
}
