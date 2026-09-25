namespace PromptCopilot.Api.Configuration;

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public string Provider { get; set; } = "Gemini";
    public string Model { get; set; } = "gemini-3.5-flash-lite";
    public string ApiKey { get; set; } = "";
    public int TransportRetries { get; set; } = 3;
    public int UnusableRetries { get; set; } = 3;
    public int ContentBlockRetries { get; set; } = 1;
    /// <summary>傳輸重試的第一次退避；之後 ×2。</summary>
    public int TransportBackoffMs { get; set; } = 1000;
}

public sealed class EmbeddingOptions
{
    public const string Section = "Embedding";
    public string Model { get; set; } = "gemini-embedding-001";
    public int Dimensions { get; set; } = 768;
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}

public sealed class OrchestratorOptions
{
    public const string Section = "Orchestrator";
    public string Mode { get; set; } = "Agentic";
    public int MaxAskCount { get; set; } = 2;
    public int MaxDiscussStreak { get; set; } = 8;
    public int MaxToolCallsPerTurn { get; set; } = 16;
    public int TurnTimeoutSeconds { get; set; } = 120;
    public int HistoryTurns { get; set; } = 10;
    public int OfferedOptionsLimit { get; set; } = 24;
    public int MaxAsksPerCall { get; set; } = 3;
    public int SessionSlidingExpirationMinutes { get; set; } = 120;
    /// <summary>整套組合推薦：每個維度幾套（設計 §5.3）。</summary>
    public int RecommendationTake { get; set; } = 3;
    /// <summary>推薦自己的逾時：它在 final 宣告之後才跑，不能掛在整輪的 token 上（那會走回滾）。</summary>
    public int RecommendationTimeoutSeconds { get; set; } = 20;
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=postgres";
}
