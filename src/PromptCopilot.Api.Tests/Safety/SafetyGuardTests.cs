using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Safety;

public class SafetyGuardTests
{
    private static (SafetyGuard guard, FakeChatCompletion chat) Make(params string[] deny)
    {
        var chat = new FakeChatCompletion();
        var guard = new SafetyGuard(new Denylist(deny), new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        return (guard, chat);
    }

    private static string Verdict(bool nsfw = false, bool real = false, string? name = null, bool auto = false) =>
        $$"""{"nsfw":{{nsfw.ToString().ToLower()}},"realPerson":{{real.ToString().ToLower()}},"personName":{{(name is null ? "null" : $"\"{name}\"")}},"wantsAutoComplete":{{auto.ToString().ToLower()}},"reason":"r"}""";

    [Fact]
    public void Denylist_normalizes_punctuation_and_matches_whole_english_tokens()
    {
        var d = new Denylist(new[] { "nude", "裸" });
        Assert.True(d.Hits("a half-NUDE figure", out var t)); Assert.Equal("nude", t);
        Assert.True(d.Hits("underwear_nude", out _));
        Assert.False(d.Hits("nudeness", out _));          // 不是整個 token
        Assert.True(d.Hits("全裸的人", out _));            // CJK 用子字串
    }

    [Fact]
    public async Task Denylist_hit_blocks_without_calling_classifier()
    {
        var (guard, chat) = Make("nude");
        var r = await guard.CheckAsync("a nude girl", default);
        Assert.True(r.Blocked); Assert.Equal("Blocked_NSFW", r.BlockCode);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task Classifier_nsfw_blocks()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text(Verdict(nsfw: true)));
        var r = await guard.CheckAsync("x", default);
        Assert.True(r.Blocked); Assert.Equal("Blocked_NSFW", r.BlockCode);
    }

    [Fact]
    public async Task Classifier_real_person_blocks_with_celebrity_code()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text(Verdict(real: true, name: "某某")));
        var r = await guard.CheckAsync("x", default);
        Assert.True(r.Blocked); Assert.Equal("Blocked_Celebrity", r.BlockCode);
        Assert.Contains("某某", r.Message);
    }

    [Fact]
    public async Task Clean_input_passes_through_wantsAutoComplete()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text(Verdict(auto: true)));
        var r = await guard.CheckAsync("隨便你決定", default);
        Assert.False(r.Blocked); Assert.True(r.WantsAutoComplete);
    }

    [Fact]
    public async Task Unparseable_verdict_throws()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text("not json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CheckAsync("x", default));
    }
}
