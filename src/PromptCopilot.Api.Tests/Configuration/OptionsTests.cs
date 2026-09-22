using Microsoft.Extensions.Configuration;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Tests.Configuration;

public class OptionsTests
{
    private static IConfiguration Config(params (string key, string value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.key, p => (string?)p.value)).Build();

    [Fact]
    public void Orchestrator_defaults_match_spec()
    {
        var o = new OrchestratorOptions();
        Assert.Equal(2, o.MaxAskCount);
        Assert.Equal(8, o.MaxDiscussStreak);
        Assert.Equal(8, o.MaxToolCallsPerTurn);
        Assert.Equal(120, o.TurnTimeoutSeconds);
        Assert.Equal(10, o.HistoryTurns);
        Assert.Equal(24, o.OfferedOptionsLimit);
    }

    [Fact]
    public void Llm_retry_defaults_match_spec()
    {
        var o = new LlmOptions();
        Assert.Equal(3, o.TransportRetries);
        Assert.Equal(3, o.UnusableRetries);
        Assert.Equal(1, o.ContentBlockRetries);
        Assert.Equal("gemini-3.5-flash-lite", o.Model);
    }

    [Fact]
    public void Options_bind_from_configuration_sections()
    {
        var cfg = Config(("Llm:ApiKey", "k"), ("Llm:TransportRetries", "5"), ("Embedding:Dimensions", "768"),
                         ("Orchestrator:MaxDiscussStreak", "3"), ("Database:ConnectionString", "Host=x"));
        var llm = cfg.GetSection(LlmOptions.Section).Get<LlmOptions>()!;
        var emb = cfg.GetSection(EmbeddingOptions.Section).Get<EmbeddingOptions>()!;
        var orch = cfg.GetSection(OrchestratorOptions.Section).Get<OrchestratorOptions>()!;
        var db = cfg.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>()!;
        Assert.Equal("k", llm.ApiKey);
        Assert.Equal(5, llm.TransportRetries);
        Assert.Equal(768, emb.Dimensions);
        Assert.Equal(3, orch.MaxDiscussStreak);
        Assert.Equal("Host=x", db.ConnectionString);
    }
}
