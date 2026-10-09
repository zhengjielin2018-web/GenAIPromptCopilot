using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Llm;

/// <summary>可行性 §4 只讀過 connector 原始碼確認 ImageContent 會變成 inlineData，這裡用真的 connector 釘住（不打網路）。</summary>
public class GeminiImageRequestTests
{
    [Fact]
    public async Task The_image_goes_out_as_inline_png_data()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = """{"nsfw":false,"realPerson":false,"personName":null,"reason":"風景"}""" } }, role = "model" }, finishReason = "STOP", index = 0 } },
            usageMetadata = new { promptTokenCount = 1, candidatesTokenCount = 1, totalTokenCount = 2 },
        })));
        var chat = new GoogleAIGeminiChatCompletionService("gemini-3.5-flash-lite", "test-key", GoogleAIVersion.V1_Beta, new HttpClient(handler));
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

        var v = await new ImageReviewer(chat, Options.Create(new LlmOptions())).ReviewAsync(png, default);

        Assert.Equal("風景", v.Reason);
        var body = handler.Bodies.Single();
        Assert.Contains("\"inlineData\"", body);
        Assert.Contains("\"image/png\"", body);
        Assert.Contains(Convert.ToBase64String(png), body);
    }
}
