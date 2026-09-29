using Microsoft.Extensions.Logging;

namespace PromptCopilot.Api.Tests.Fakes;

/// <summary>把寫出去的 log 收成 (等級, 已套用參數的訊息) 清單，驗 log 行的格式用。</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
    }
}
