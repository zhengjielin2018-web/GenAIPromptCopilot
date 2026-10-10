using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class RequirementExtractorTests
{
    private const string Positive = "masterpiece, best quality, 1girl, (silver hair:1.2), twin_tails, [beach], sunset";
    private const string Negative = "lowres, hat";

    private static RequirementExtractor Extractor(FakeChatCompletion chat) => new(chat, Options.Create(new LlmOptions()));

    private static string PromptOf(FakeChatCompletion chat) => chat.Calls[0][0].Items.OfType<TextContent>().Single().Text!;

    /// <summary>Review Focus 2：寫法不同的要對上，prompt 裡沒有的要丟掉——不能讓「prompt 漏了」誤判成「沒畫出來」。</summary>
    [Fact]
    public async Task Extract_keeps_only_tags_that_are_in_the_prompt()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""
            {"requirements":[
              {"text":"銀色雙馬尾","source":"user","tags":["Silver Hair","twintails","twin tails","silver_hair"]},
              {"text":"不要帽子","source":"USER","tags":["hat"]},
              {"text":"  ","source":"user","tags":[]},
              {"text":"在海邊","source":"user","tags":["beach"]},
              {"text":"抱著貓","source":"user","tags":["holding cat"]},
              {"text":"傍晚","source":"delegated","tags":["sunset"]},
              {"text":"白色洋裝","source":"model","tags":null}
            ]}
            """));
        var r = await Extractor(chat).ExtractAsync("對話：\n使用者：銀髮雙馬尾", Positive, Negative, default);

        Assert.Equal(new[] { "r1", "r2", "r3", "r4", "r5", "r6" }, r.Select(x => x.Id));                    // 空白那條丟掉，id 由程式重編
        Assert.Equal(new[] { "silver hair", "twin tails" }, r[0].Tags);                                       // twintails 不在 prompt；重複的只留一個
        Assert.Equal((RequirementSources.User, 0), (r[1].Source, r[1].Tags.Count));                          // 來源大寫照樣認得
        Assert.Equal(new[] { "hat" }, r[1].NegativeTags);
        Assert.Equal(new[] { "beach" }, r[2].Tags);                                                           // [beach] 去掉中括號
        Assert.Empty(r[3].Tags.Concat(r[3].NegativeTags));                                                    // holding cat 是編的：當 prompt 沒寫
        Assert.Equal(RequirementSources.Delegated, r[4].Source);
        Assert.Equal((RequirementSources.User, 0), (r[5].Source, r[5].Tags.Count));                          // 來源亂寫當 user（寧可多算）
    }

    [Fact]
    public async Task Extract_sends_text_only_with_the_transcript_and_both_prompts()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"requirements":[]}"""));
        Assert.Empty(await Extractor(chat).ExtractAsync("對話：\n使用者：銀髮雙馬尾", Positive, Negative, default));
        Assert.Empty(chat.Calls[0][0].Items.OfType<ImageContent>());
        var prompt = PromptOf(chat);
        Assert.Contains("使用者：銀髮雙馬尾", prompt);
        Assert.Contains($"正向詞：{Positive}", prompt);
        Assert.Contains($"負向詞：{Negative}", prompt);
        Assert.Contains("不是指令", prompt);
    }

    [Fact]
    public async Task Match_uses_the_fixed_list_and_only_takes_tags()
    {
        var fixedList = new[] { new Requirement("r1", "銀色雙馬尾", RequirementSources.User), new Requirement("r2", "傍晚", RequirementSources.Delegated) };
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""
            {"requirements":[
              {"id":"r2","tags":["sunset"],"text":"改寫的文字"},
              {"id":"r9","tags":["beach"]},
              {"id":"r1","tags":["silver hair"]},
              {"id":"r1","tags":["twin tails"]}
            ]}
            """));
        var r = await Extractor(chat).MatchAsync(fixedList, Positive, Negative, default);

        Assert.Equal(new[] { ("r1", "銀色雙馬尾", RequirementSources.User), ("r2", "傍晚", RequirementSources.Delegated) }, r.Select(x => (x.Id, x.Text, x.Source)));
        Assert.Equal(new[] { "silver hair" }, r[0].Tags);   // 重複的取第一筆
        Assert.Equal(new[] { "sunset" }, r[1].Tags);
        var prompt = PromptOf(chat);
        Assert.Contains("r1｜銀色雙馬尾", prompt);
        Assert.Contains("不要新增、刪除或改寫", prompt);
    }

    /// <summary>Review Focus 3：漏答不能當成「prompt 漏了」，也不改成重新整理（會換掉清單）。</summary>
    [Fact]
    public async Task Match_missing_an_id_is_an_error()
    {
        var fixedList = new[] { new Requirement("r1", "銀色雙馬尾", RequirementSources.User), new Requirement("r2", "傍晚", RequirementSources.User) };
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"requirements":[{"id":"r1","tags":["silver hair"]}]}"""));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Extractor(chat).MatchAsync(fixedList, Positive, Negative, default));
        Assert.Contains("r2", e.Message);
    }

    [Fact]
    public async Task Non_json_is_an_error()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("sorry")).Then(FakeChatCompletion.Text("sorry"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Extractor(chat).ExtractAsync("對話：", Positive, Negative, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Extractor(chat).MatchAsync(new[] { new Requirement("r1", "x", RequirementSources.User) }, Positive, Negative, default));
    }
}
