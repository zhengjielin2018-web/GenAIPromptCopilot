namespace PromptCopilot.Api.Safety;

public sealed record GuardResult(bool Blocked, string? BlockCode, string? Message, bool WantsAutoComplete)
{
    public static GuardResult Ok(bool wantsAutoComplete) => new(false, null, null, wantsAutoComplete);
}

/// <summary>輸入側（主規格 §6.1）。在 service 層、進 kernel 之前執行。</summary>
public sealed class SafetyGuard(Denylist denylist, SafetyClassifier classifier)
{
    public async Task<GuardResult> CheckAsync(string text, CancellationToken ct)
    {
        if (denylist.Hits(text, out var term))
            return new GuardResult(true, "Blocked_NSFW", $"輸入含不允許的內容（{term}），這一輪不處理。", false);
        var v = await classifier.ClassifyInputAsync(text, ct);
        if (v.Nsfw)
            return new GuardResult(true, "Blocked_NSFW", $"輸入被判定為不當內容：{v.Reason}", false);
        if (v.RealPerson)
            return new GuardResult(true, "Blocked_Celebrity", $"不生成真實人物（{v.PersonName ?? "未具名"}）：{v.Reason}", false);
        return GuardResult.Ok(v.WantsAutoComplete);
    }
}
