using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class SelfCheckerTests
{
    private static readonly RequirementMatch Hair = new("r1", "銀色雙馬尾", RequirementSources.User, new[] { "silver hair", "twin tails" }, Array.Empty<string>());
    private static readonly RequirementMatch NoHat = new("r2", "不要帽子", RequirementSources.User, Array.Empty<string>(), new[] { "hat" });
    private static readonly RequirementMatch Cat = new("r3", "抱著貓", RequirementSources.User, Array.Empty<string>(), Array.Empty<string>());
    private static readonly GeminiImage Jpeg = new(new byte[] { 1 }, "image/jpeg");
    private static SelfChecker Checker(FakeChatCompletion chat) => new(chat, Options.Create(new LlmOptions()));

    [Fact]
    public async Task No_items_means_no_call()
    {
        var chat = new FakeChatCompletion();   // 被呼叫就丟 script exhausted
        Assert.Empty(await Checker(chat).CheckAsync(Jpeg, Array.Empty<RequirementMatch>(), default));
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task The_prompt_lists_every_requirement_and_the_answer_maps_back_with_an_issue()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text(
            """{"items":[{"id":"r3","verdict":"unmet","reason":"手上沒有東西"},{"id":"r1","verdict":"unmet","reason":"畫成單馬尾"},{"id":"r2","verdict":"met","reason":"沒有戴帽子"}]}"""));
        var result = await Checker(chat).CheckAsync(Jpeg, new[] { Hair, NoHat, Cat }, default);
        Assert.Equal(new[] { ("r1", "unmet", "not_rendered"), ("r2", "met", "none"), ("r3", "unmet", "prompt_missing") },
            result.Select(r => (r.Id, r.Verdict, r.Issue)));
        Assert.Equal(("銀色雙馬尾", "畫成單馬尾"), (result[0].Text, result[0].Reason));
        Assert.Equal(new[] { "hat" }, result[1].NegativeTags);
        var prompt = chat.Calls[0][0].Items.OfType<Microsoft.SemanticKernel.TextContent>().Single().Text!;
        Assert.Contains("r1｜銀色雙馬尾｜silver hair, twin tails", prompt);
        Assert.Contains("r2｜不要帽子｜負向：hat", prompt);
        Assert.Contains("r3｜抱著貓｜（prompt 沒寫）", prompt);
        Assert.Single(chat.Calls[0][0].Items.OfType<Microsoft.SemanticKernel.ImageContent>());
    }

    [Fact]
    public async Task Answers_are_normalized_to_the_items_asked()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text(
            """{"items":[{"id":"r1","verdict":"MET","reason":"有"},{"id":"r1","verdict":"unmet","reason":"重複"},{"id":"r9","verdict":"met","reason":"沒問"},{"id":"r2","verdict":"partial","reason":"亂寫"}]}"""));
        var result = await Checker(chat).CheckAsync(Jpeg, new[] { Hair, NoHat }, default);
        Assert.Equal(new[] { ("r1", "met"), ("r2", "unclear") }, result.Select(r => (r.Id, r.Verdict)));
        Assert.Equal(("模型回的判定看不懂", "unclear"), (result[1].Reason, result[1].Issue));   // 有回、只是判定亂寫：不能說成「沒有回」
    }

    [Fact]
    public async Task A_missing_item_is_unclear()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"items":[{"id":"r1","verdict":"unmet","reason":"短髮"}]}"""));
        var result = await Checker(chat).CheckAsync(Jpeg, new[] { Hair, NoHat }, default);
        Assert.Equal(("unclear", "模型沒有回這一項"), (result[1].Verdict, result[1].Reason));
    }

    [Fact]
    public async Task Non_json_is_an_error()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("sorry"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Checker(chat).CheckAsync(Jpeg, new[] { Hair }, default));
    }
}
