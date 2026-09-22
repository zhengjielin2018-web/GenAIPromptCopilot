using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

public interface IPromptOrchestrator
{
    IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, CancellationToken ct);
}

public sealed class ProtocolViolationException(string message) : Exception(message);

/// <summary>OutputSafetyFilter 設了 BlockedOutcome 之後，orchestrator 用這個例外走回滾路徑。</summary>
public sealed class OutputBlockedException(string reason) : Exception($"輸出被攔截：{reason}")
{
    public string Reason { get; } = reason;
}
