using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Npgsql;
using Pgvector.Npgsql;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Safety;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.Section));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));
builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection(OrchestratorOptions.Section));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(sp =>
{
    var cs = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
    var b = new NpgsqlDataSourceBuilder(cs);
    b.UseVector();
    return b.Build();
});
builder.Services.AddSingleton<PresetRepository>();
builder.Services.AddSingleton<HistoryRepository>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<AuditRepository>());
builder.Services.AddHttpClient<IEmbeddingClient, GeminiEmbeddingClient>();
builder.Services.AddSingleton<IChatCompletionService>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>();
    var raw = new GoogleAIGeminiChatCompletionService(llm.Value.Model, llm.Value.ApiKey);
    return new ResilientChatCompletion(raw, llm);
});
builder.Services.AddSingleton(sp => new Denylist(builder.Configuration.GetSection("Safety:Denylist").Get<string[]>() ?? Array.Empty<string>()));
builder.Services.AddSingleton<SafetyClassifier>();
builder.Services.AddSingleton<SafetyGuard>();
builder.Services.AddSingleton(sp => FacetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Configuration", "facets.yaml")));
builder.Services.AddSingleton(sp => new SystemPromptBuilder(
    sp.GetRequiredService<FacetCatalog>(),
    sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value,
    Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value);
builder.Services.AddSingleton<AgentKernelFactory>();
builder.Services.AddSingleton<IPromptOrchestrator>(sp => new AgenticOrchestrator(
    sp.GetRequiredService<IChatCompletionService>(), sp.GetRequiredService<FacetCatalog>(), sp.GetRequiredService<SafetyGuard>(),
    sp.GetRequiredService<SystemPromptBuilder>(), sp.GetRequiredService<IAuditSink>(), sp.GetRequiredService<OrchestratorOptions>(),
    kernelFactory: sp.GetRequiredService<AgentKernelFactory>().Create));

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program { }
