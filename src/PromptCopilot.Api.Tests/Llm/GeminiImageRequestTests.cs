using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Llm;

/// <summary>可行性 §4 只讀過 connector 原始碼確認 ImageContent 會變成 inlineData，這裡用真的 connector 釘住（不打網路）。</summary>
public class GeminiImageRequestTests
{
    [Fact]
    public async Task The_downscaled_image_goes_out_as_inline_jpeg_data()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = """{"nsfw":false,"realPerson":false,"personName":null,"reason":"風景"}""" } }, role = "model" }, finishReason = "STOP", index = 0 } },
            usageMetadata = new { promptTokenCount = 1, candidatesTokenCount = 1, totalTokenCount = 2 },
        })));
        var chat = new GoogleAIGeminiChatCompletionService("gemini-3.5-flash-lite", "test-key", GoogleAIVersion.V1_Beta, new HttpClient(handler));
        var image = ImageForGemini.Prepare(Rendering.ImageForGeminiTests.Png(832, 1216));

        var v = await new ImageReviewer(chat, Options.Create(new LlmOptions())).ReviewAsync(image, default);

        Assert.Equal("風景", v.Reason);
        var body = handler.Bodies.Single();
        Assert.Contains("\"inlineData\"", body);
        Assert.Contains("\"image/jpeg\"", body);
        Assert.Contains(Convert.ToBase64String(image.Data), body);
    }

    [Fact]
    public async Task The_requirements_step_sends_no_image()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = """{"requirements":[{"text":"銀髮","source":"user","tags":["silver hair"]}]}""" } }, role = "model" }, finishReason = "STOP", index = 0 } },
            usageMetadata = new { promptTokenCount = 1, candidatesTokenCount = 1, totalTokenCount = 2 },
        })));
        var chat = new GoogleAIGeminiChatCompletionService("gemini-3.5-flash-lite", "test-key", GoogleAIVersion.V1_Beta, new HttpClient(handler));

        var r = await new RequirementExtractor(chat, Options.Create(new LlmOptions())).ExtractAsync("對話：\n使用者：銀髮", "1girl, silver hair", "lowres", default);

        Assert.Equal(new[] { "silver hair" }, Assert.Single(r).Tags);
        Assert.DoesNotContain("\"inlineData\"", handler.Bodies.Single());
    }
}
