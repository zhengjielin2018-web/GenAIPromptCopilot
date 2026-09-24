using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PromptCopilot.Api.Tests.Endpoints;

/// <summary>docker compose 的人忘了填 GEMINI_API_KEY：整套照樣起來，每一輪卻都 500。
/// 至少啟動時要在 log 講清楚是哪裡沒設。</summary>
public class StartupWarningTests
{
    private sealed class CapturingProvider : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Warnings = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) owner.Warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    private static IReadOnlyList<string> WarningsAtStartup(string apiKey)
    {
        var logs = new CapturingProvider();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // 明確給值：本機的 user-secrets 可能有 key，CI 沒有；測試不能跟著環境變
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:ApiKey"] = apiKey }));
            b.ConfigureLogging(l => l.AddProvider(logs));
        });
        using var _ = factory.CreateClient();       // 啟動 host
        return logs.Warnings.ToList();
    }

    [Fact]
    public void Blank_api_key_logs_where_to_set_it()
    {
        var warnings = WarningsAtStartup("");
        Assert.Contains(warnings, w => w.Contains("Llm:ApiKey") && w.Contains("GEMINI_API_KEY"));
    }

    [Fact]
    public void Configured_api_key_logs_nothing_about_it() =>
        Assert.DoesNotContain(WarningsAtStartup("test-key"), w => w.Contains("Llm:ApiKey"));
}
