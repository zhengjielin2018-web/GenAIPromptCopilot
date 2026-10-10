using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Llm;

/// <summary>回 JSON 的四個呼叫（安全分類、看圖審查、整理要求清單、逐條判圖）不設 temperature，用模型預設值。
/// Google 建議 Gemini 3 系列維持預設：調低可能迴圈或品質下降；實機看到溫度 0 時清單偶爾有錯字，也沒換到穩定（eval-cases R14–R17）。</summary>
public class GeminiTemperatureTests
{
    private static (GoogleAIGeminiChatCompletionService Chat, StubHttpHandler Handler) Reply(string text)
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text } }, role = "model" }, finishReason = "STOP", index = 0 } },
            usageMetadata = new { promptTokenCount = 1, candidatesTokenCount = 1, totalTokenCount = 2 },
        })));
        return (new GoogleAIGeminiChatCompletionService("gemini-3.5-flash-lite", "test-key", GoogleAIVersion.V1_Beta, new HttpClient(handler)), handler);
    }

    private static void AssertNoTemperature(StubHttpHandler handler)
    {
        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        var hasConfig = doc.RootElement.TryGetProperty("generationConfig", out var config);
        Assert.True(!hasConfig || !config.TryGetProperty("temperature", out var t) || t.ValueKind == JsonValueKind.Null, handler.Bodies.Single());
    }

    private static readonly IOptions<LlmOptions> Llm = Options.Create(new LlmOptions());
    private static GeminiImage Image => ImageForGemini.Prepare(Rendering.ImageForGeminiTests.Png(64, 64));

    [Fact]
    public async Task Safety_classifier_uses_the_model_default()
    {
        var (chat, handler) = Reply("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"風景"}""");
        await new SafetyClassifier(chat, Llm).ClassifyInputAsync("一個女生站在海邊", default);
        AssertNoTemperature(handler);
    }

    [Fact]
    public async Task Image_review_uses_the_model_default()
    {
        var (chat, handler) = Reply("""{"nsfw":false,"realPerson":false,"personName":null,"reason":"風景"}""");
        await new ImageReviewer(chat, Llm).ReviewAsync(Image, default);
        AssertNoTemperature(handler);
    }

    [Fact]
    public async Task Requirement_steps_use_the_model_default()
    {
        var (chat, handler) = Reply("""{"requirements":[{"text":"銀髮","source":"user","tags":["silver hair"]}]}""");
        await new RequirementExtractor(chat, Llm).ExtractAsync("對話：\n使用者：銀髮", "1girl, silver hair", "lowres", default);
        AssertNoTemperature(handler);

        var (chat2, handler2) = Reply("""{"requirements":[{"id":"r1","tags":["silver hair"]}]}""");
        await new RequirementExtractor(chat2, Llm).MatchAsync(new[] { new Requirement("r1", "銀髮", RequirementSources.User) }, "1girl, silver hair", "lowres", default);
        AssertNoTemperature(handler2);
    }

    [Fact]
    public async Task Self_check_uses_the_model_default()
    {
        var (chat, handler) = Reply("""{"items":[{"id":"r1","verdict":"met","reason":"銀髮"}]}""");
        await new SelfChecker(chat, Llm).CheckAsync(Image,
            new[] { new RequirementMatch("r1", "銀髮", RequirementSources.User, new[] { "silver hair" }, Array.Empty<string>()) }, default);
        AssertNoTemperature(handler);
    }
}
