using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

/// <summary>主規格 §4.10 的降級路徑。只有當 agentic loop 無法穩定跑完「追問 → 定稿」才實作。</summary>
public sealed class StateMachineOrchestrator : IPromptOrchestrator
{
    public IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, CancellationToken ct) =>
        throw new NotImplementedException("Orchestrator:Mode=StateMachine 尚未實作；見主規格 §4.10");
}
