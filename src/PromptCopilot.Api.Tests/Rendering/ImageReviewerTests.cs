using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class ImageReviewerTests
{
    private static ImageReviewer Reviewer(FakeChatCompletion chat) => new(chat, Options.Create(new LlmOptions()));

    [Fact]
    public async Task Parses_the_verdict_and_sends_the_image()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"reason":"風景"}"""));
        var v = await Reviewer(chat).ReviewAsync(new GeminiImage(new byte[] { 1, 2, 3 }, "image/jpeg"), default);
        Assert.Equal((false, false, "風景"), (v.Nsfw, v.RealPerson, v.Reason));
        var items = chat.Calls[0][0].Items;
        Assert.Contains(items, i => i is Microsoft.SemanticKernel.ImageContent img && img.MimeType == "image/jpeg");
    }

    [Theory]
    [InlineData("""{"nsfw":false,"realPerson":false}""")]
    [InlineData("not json")]
    public async Task A_verdict_without_reason_or_json_is_an_error(string content)
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text(content));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reviewer(chat).ReviewAsync(new GeminiImage(new byte[] { 1 }, "image/jpeg"), default));
    }
}
