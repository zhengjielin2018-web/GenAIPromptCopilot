namespace PromptCopilot.Api.Sessions;

/// <summary>確認輪留下的待確認（先確認再動手設計 §3.4）。只有最新一筆可以按；動手輪（含採用輪）開始時清掉。
/// AutoComplete：確認輪的輸入分類器判定「隨便／直接給我」；按下確認後才打開 AutoFill。</summary>
public sealed record PendingConfirmation(int TurnIndex, string Message, IReadOnlyList<string> Choices, bool AutoComplete);

/// <summary>POST /messages 的 confirm 欄位。Choice：沒有選項的提案卡是 null。</summary>
public sealed record ConfirmRequest(int TurnIndex, int? Choice = null);

/// <summary>按下的那張卡與選的解讀。Text 是使用者泡泡與 history 裡的那句：沒有選項是接受句，有選項是那個選項的原文。</summary>
public sealed record ConfirmedInput(PendingConfirmation Pending, int? Choice)
{
    public string? ChosenText => Choice is { } c ? Pending.Choices[c] : null;
    public string Text => ChosenText ?? ConfirmValidator.AcceptText;
}

/// <summary>Status：端點要回的 HTTP 狀態碼（409 或 400）。</summary>
public sealed class ConfirmValidationException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public static class ConfirmValidator
{
    public const string AcceptText = "對，就這樣";

    /// <summary>設計 §3.5：沒有待確認、不是最新一張 → 409；choice 跟卡片對不上 → 400。純函式，不動 session。</summary>
    public static ConfirmedInput Validate(Session s, ConfirmRequest req)
    {
        var p = s.PendingConfirmation ?? throw new ConfirmValidationException(409, "沒有待確認的內容");
        if (p.TurnIndex != req.TurnIndex) throw new ConfirmValidationException(409, "只有最新一張確認卡可以按");
        if (p.Choices.Count == 0)
        {
            if (req.Choice is not null) throw new ConfirmValidationException(400, "這張確認卡沒有選項，choice 要是 null");
        }
        else if (req.Choice is not { } c || c < 0 || c >= p.Choices.Count)
            throw new ConfirmValidationException(400, $"choice 要是 0 到 {p.Choices.Count - 1}");
        return new ConfirmedInput(p, req.Choice);
    }
}
