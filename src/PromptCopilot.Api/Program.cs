using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Npgsql;
using Pgvector.Npgsql;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Endpoints;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var services = builder.Services;

// ---- options ----
services.Configure<LlmOptions>(cfg.GetSection(LlmOptions.Section));
services.Configure<EmbeddingOptions>(cfg.GetSection(EmbeddingOptions.Section));
services.Configure<OrchestratorOptions>(cfg.GetSection(OrchestratorOptions.Section));
services.Configure<DatabaseOptions>(cfg.GetSection(DatabaseOptions.Section));
services.Configure<SafetyOptions>(cfg.GetSection(SafetyOptions.Section));
services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value);

// ---- infra ----
services.AddEndpointsApiExplorer();
services.AddSwaggerGen(c => c.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "PromptCopilot API",
    Version = "v1",
    Description = """
        把中文描述，經由多輪追問與討論，整理成英文的生圖 prompt。

        典型呼叫順序：
        1. `POST /api/sessions` 拿 `sessionId`
        2. 反覆 `POST /api/sessions/{id}/messages`，每次送一句話，讀回一輪的 SSE 事件
        3. 需要時 `GET /api/presets/{id}` 查事件裡提到的 preset
        4. 定稿後（可選）`POST /api/sessions/{id}/save-to-shared` 存進共享庫

        在終端機逐輪試用：見 repo 的 `manual-tests/README.md`。
        """,
}));
services.AddMemoryCache();
services.AddSingleton(sp =>
{
    var b = new NpgsqlDataSourceBuilder(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString);
    b.UseVector();
    return b.Build();
});
services.AddSingleton<PresetRepository>();
services.AddSingleton<HistoryRepository>();
services.AddSingleton<AuditRepository>();
services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<AuditRepository>());
services.AddHttpClient<IEmbeddingClient, GeminiEmbeddingClient>();

// ---- config & sessions ----
services.AddSingleton(_ => FacetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Configuration", "facets.yaml")));
services.AddSingleton(sp => new SessionStore(
    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
    TimeSpan.FromMinutes(sp.GetRequiredService<OrchestratorOptions>().SessionSlidingExpirationMinutes)));
services.AddSingleton(sp => new SystemPromptBuilder(sp.GetRequiredService<FacetCatalog>(), sp.GetRequiredService<OrchestratorOptions>(),
    Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")));

// ---- LLM（真的 Gemini 包在三層重試 decorator 裡）----
services.AddSingleton<IChatCompletionService>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>();
    // 自備 HttpClient 只為了插 GeminiRoleFixHandler（見該類別的註解）；單例服務持有單一 client。
    var http = new HttpClient(new GeminiRoleFixHandler(new HttpClientHandler()));
    return new ResilientChatCompletion(
        new GoogleAIGeminiChatCompletionService(llm.Value.Model, llm.Value.ApiKey, GoogleAIVersion.V1_Beta, http), llm);
});

// ---- safety ----
services.AddSingleton(_ => new Denylist(cfg.GetSection("Safety:Denylist").Get<string[]>() ?? Array.Empty<string>()));
services.AddSingleton<SafetyClassifier>();
services.AddSingleton<SafetyGuard>();

// ---- orchestration ----
services.AddSingleton<AgentKernelFactory>();
services.AddSingleton<IRecommendationService, RecommendationService>();
services.AddSingleton<IPromptOrchestrator>(sp =>
{
    var o = sp.GetRequiredService<OrchestratorOptions>();
    if (!string.Equals(o.Mode, "Agentic", StringComparison.OrdinalIgnoreCase)) return new StateMachineOrchestrator();
    return new AgenticOrchestrator(
        sp.GetRequiredService<IChatCompletionService>(), sp.GetRequiredService<FacetCatalog>(), sp.GetRequiredService<SafetyGuard>(),
        sp.GetRequiredService<SystemPromptBuilder>(), sp.GetRequiredService<IAuditSink>(), o,
        sp.GetRequiredService<SafetyClassifier>(), sp.GetRequiredService<ILogger<AgenticOrchestrator>>(),
        sp.GetRequiredService<IRecommendationService>(),
        kernelFactory: sp.GetRequiredService<AgentKernelFactory>().Create);
});

var app = builder.Build();
// Gemini 的 service 在第一輪才建；key 沒設時整套照樣起來、每一輪卻都 500。至少在啟動時講清楚。
if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<LlmOptions>>().Value.ApiKey))
    app.Logger.LogWarning("Llm:ApiKey 未設定：API 會起來，但每一輪對話都會失敗。docker compose 請在 .env 填 GEMINI_API_KEY；本機開發用 user-secrets（見 manual-tests/README.md）。");
app.UseSwagger();
app.UseSwaggerUI();
SessionEndpoints.Map(app);
ReferenceEndpoints.Map(app);
app.Run();

public partial class Program { }
