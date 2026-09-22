namespace PromptCopilot.Api.Tests;

/// <summary>需要真的 DB 或 Gemini 的測試。沒設 PC_INTEGRATION=1 就 Skip，CI 預設不跑。</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PC_INTEGRATION") != "1")
            Skip = "set PC_INTEGRATION=1 to run (needs docker db / Gemini key)";
    }
}

public static class TestEnv
{
    public static string Db => Environment.GetEnvironmentVariable("PC_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=postgres";
    public static string? GeminiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY");
}
