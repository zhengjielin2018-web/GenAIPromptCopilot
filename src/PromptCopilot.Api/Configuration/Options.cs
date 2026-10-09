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
    /// <summary>保留最近幾則使用者訊息的輪次。2026-10-05 由 10 改 20：每個要求多一則「對，就這樣」，維持原本記得的要求數（先確認再動手設計 §2）。</summary>
    public int HistoryTurns { get; set; } = 20;
    public int OfferedOptionsLimit { get; set; } = 24;
    public int MaxAsksPerCall { get; set; } = 3;
    public int SessionSlidingExpirationMinutes { get; set; } = 120;
    /// <summary>整套組合推薦：每個維度幾套（設計 §5.3）。</summary>
    public int RecommendationTake { get; set; } = 3;
    /// <summary>推薦自己的逾時：它在 final 宣告之後才跑，不能掛在整輪的 token 上（那會走回滾）。</summary>
    public int RecommendationTimeoutSeconds { get; set; } = 20;
    /// <summary>推薦的近似錨（facet 向量設計 §6.4）：字面錨不到 2 筆時，facet 向量距離在這個門檻內的才算「接近你講的」。
    /// 2026-09-30 驗收由 0.23 放寬：單一詞對單一詞的同義詞落在 0.28–0.34（flip-flops→sandals 0.294、clogs→crocs 0.277），
    /// 0.23 以內只剩字面相同、字面錨早就抓到的那筆；0.30 起開始混進 shoes、hat 這類泛稱。</summary>
    public double RecommendationSimilarMaxDist { get; set; } = 0.30;
    /// <summary>推薦組法（2026-09-30 設計 §3.1）：定稿卡每層候選取幾筆。純向量那層走 HNSW，hnsw.ef_search 預設 40，
    /// 調到 40 以上要一起調 ef_search，否則回不滿（設計 §8）。</summary>
    public int RecommendationPoolSize { get; set; } = 30;
    /// <summary>看過一次，有效名次往後加幾名。P=10、τ=5 時看過一次權重剩 e^-2 ≈ 13.5%，兩次 1.8%。</summary>
    public double RecommendationSeenPenalty { get; set; } = 10;
    /// <summary>名次轉機率的溫度 τ：權重 exp(−有效名次/τ)。越大越往深處抽；使用者要的是看過的退得夠多，不是抽很深。</summary>
    public double RecommendationTemperature { get; set; } = 5;
}

public sealed class SafetyOptions
{
    public const string Section = "Safety";
    /// <summary>true 才收 messages 的 safety: off（測試用的審查開關）。預設關：誰都能打 API，不能一個欄位就關掉審查。
    /// Denylist 同一節，但在 Program.cs 直接讀。</summary>
    public bool AllowDisable { get; set; }
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=postgres";
}

/// <summary>定稿後生成預覽（預覽設計 §4.1）。EndpointId 或 ApiKey 有一個是空的就當沒開：/api/config/render 回 false、POST /renders 回 404。</summary>
public sealed class RenderOptions
{
    public const string Section = "Render";
    public string EndpointId { get; set; } = "";
    /// <summary>只從環境變數 Render__ApiKey 讀（docker compose 由 .env 的 RUNPOD_API_KEY 帶入），不寫進任何設定檔：repo 是公開的。</summary>
    public string ApiKey { get; set; } = "";
    public int PerSessionLimit { get; set; } = 10;
    public int DailyLimit { get; set; } = 200;
    /// <summary>預估等待超過就不收（可行性 §11 第 3 項）。</summary>
    public int MaxEstimatedWaitSeconds { get; set; } = 60;
    /// <summary>從送出 RunPod 起算。閒置很久後的冷啟動實測 91 秒（排隊 74.5＋執行 16.8，可行性 §9.4），原本的 90 秒擋不住，
    /// 2026-10-10 調成 180 秒（使用者同意）。</summary>
    public int JobTimeoutSeconds { get; set; } = 180;
    public int PollIntervalMs { get; set; } = 1000;
    /// <summary>審圖、自評各自的上限（含 ResilientChatCompletion 的重試）。縮圖後實測審圖中位數 1.9 秒、自評 3.1 秒（可行性 §9.4），
    /// 這個上限只是防 Gemini 卡住時佔著佇列不放：審圖逾時當沒通過（不給圖），自評逾時標 unavailable。</summary>
    public int GeminiTimeoutSeconds { get; set; } = 60;
    /// <summary>還沒有實測資料時，預估等待用的每張秒數。</summary>
    public int DefaultImageSeconds { get; set; } = 10;
    public bool Enabled => !string.IsNullOrWhiteSpace(EndpointId) && !string.IsNullOrWhiteSpace(ApiKey);
}
