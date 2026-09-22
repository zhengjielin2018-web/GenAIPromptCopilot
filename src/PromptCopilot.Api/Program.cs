using Microsoft.Extensions.Options;
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
services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value);

// ---- infra ----
services.AddEndpointsApiExplorer();
services.AddSwaggerGen();
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
    return new ResilientChatCompletion(new GoogleAIGeminiChatCompletionService(llm.Value.Model, llm.Value.ApiKey), llm);
});

// ---- safety ----
services.AddSingleton(_ => new Denylist(cfg.GetSection("Safety:Denylist").Get<string[]>() ?? Array.Empty<string>()));
services.AddSingleton<SafetyClassifier>();
services.AddSingleton<SafetyGuard>();

// ---- orchestration ----
services.AddSingleton<AgentKernelFactory>();
services.AddSingleton<IPromptOrchestrator>(sp =>
{
    var o = sp.GetRequiredService<OrchestratorOptions>();
    if (!string.Equals(o.Mode, "Agentic", StringComparison.OrdinalIgnoreCase)) return new StateMachineOrchestrator();
    return new AgenticOrchestrator(
        sp.GetRequiredService<IChatCompletionService>(), sp.GetRequiredService<FacetCatalog>(), sp.GetRequiredService<SafetyGuard>(),
        sp.GetRequiredService<SystemPromptBuilder>(), sp.GetRequiredService<IAuditSink>(), o,
        kernelFactory: sp.GetRequiredService<AgentKernelFactory>().Create);
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
SessionEndpoints.Map(app);
ReferenceEndpoints.Map(app);
app.Run();

public partial class Program { }
