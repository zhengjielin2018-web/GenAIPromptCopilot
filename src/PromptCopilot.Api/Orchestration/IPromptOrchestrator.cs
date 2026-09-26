using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

/// <summary>一輪的輸入：使用者原文；或伺服器組好的採用句加上要記帳的採用與片段（設計 §6）。
/// SafetyOn=false：這一輪關掉程式端審查（測試用；端點只在 Safety:AllowDisable 開著時收）。Gemini 自己的攔截不受影響。</summary>
public sealed record TurnInput(string Text, Adoption? Adoption = null, LedgerEntry? AdoptedPreset = null, bool SafetyOn = true);

public interface IPromptOrchestrator
{
    IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, TurnInput input, CancellationToken ct);
}

public sealed class ProtocolViolationException(string message) : Exception(message);

/// <summary>OutputSafetyFilter 設了 BlockedOutcome 之後，orchestrator 用這個例外走回滾路徑。</summary>
public sealed class OutputBlockedException(string reason) : Exception($"輸出被攔截：{reason}")
{
    public string Reason { get; } = reason;
}
