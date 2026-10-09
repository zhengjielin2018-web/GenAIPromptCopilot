using System.Collections.Concurrent;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Tests.Fakes;

public sealed class RecordingAudit : IAuditSink
{
    public ConcurrentQueue<AuditEntry> Entries { get; } = new();
    public Task WriteAsync(AuditEntry entry, CancellationToken ct) { Entries.Enqueue(entry); return Task.CompletedTask; }
}
