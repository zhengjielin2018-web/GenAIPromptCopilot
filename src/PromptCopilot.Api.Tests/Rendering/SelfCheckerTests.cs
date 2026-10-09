using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class SelfCheckerTests
{
    private static readonly SelfCheckItem Hair = new("appearance.hair", "髮型", "long silver hair");
    private static readonly SelfCheckItem Lens = new("camera.focal", "焦段", "85mm");
    private static readonly GeminiImage Jpeg = new(new byte[] { 1 }, "image/jpeg");
    private static SelfChecker Checker(FakeChatCompletion chat) => new(chat, Options.Create(new LlmOptions()));

    [Fact]
    public async Task No_items_means_no_call()
    {
        var chat = new FakeChatCompletion();   // 被呼叫就丟 script exhausted
        Assert.Empty(await Checker(chat).CheckAsync(Jpeg, Array.Empty<SelfCheckItem>(), default));
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task The_prompt_lists_every_item_and_the_answer_maps_back()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text(
            """{"items":[{"facetId":"camera.focal","verdict":"unclear","reason":"看不出焦段"},{"facetId":"appearance.hair","verdict":"present","reason":"銀色長髮"}]}"""));
        var result = await Checker(chat).CheckAsync(Jpeg, new[] { Hair, Lens }, default);
        Assert.Equal(new[] { ("appearance.hair", "present"), ("camera.focal", "unclear") }, result.Select(r => (r.FacetId, r.Verdict)));
        Assert.Equal(("髮型", "long silver hair", "銀色長髮"), (result[0].Label, result[0].Tag, result[0].Reason));
        var prompt = chat.Calls[0][0].Items.OfType<Microsoft.SemanticKernel.TextContent>().Single().Text!;
        Assert.Contains("appearance.hair｜髮型｜long silver hair", prompt);
        Assert.Contains("camera.focal｜焦段｜85mm", prompt);
    }

    [Fact]
    public async Task Answers_are_normalized_to_the_items_asked()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text(
            """{"items":[{"facetId":"appearance.hair","verdict":"PRESENT","reason":"有"},{"facetId":"appearance.hair","verdict":"absent","reason":"重複"},{"facetId":"scene.weather","verdict":"present","reason":"沒問"},{"facetId":"camera.focal","verdict":"maybe","reason":"亂寫"}]}"""));
        var result = await Checker(chat).CheckAsync(Jpeg, new[] { Hair, Lens }, default);
        Assert.Equal(new[] { ("appearance.hair", "present"), ("camera.focal", "unclear") }, result.Select(r => (r.FacetId, r.Verdict)));
        Assert.Equal("模型回的判定看不懂", result[1].Reason);   // 有回、只是判定亂寫：不能說成「沒有回」
    }

    [Fact]
    public async Task A_missing_item_is_unclear()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"items":[{"facetId":"appearance.hair","verdict":"absent","reason":"短髮"}]}"""));
        var result = await Checker(chat).CheckAsync(Jpeg, new[] { Hair, Lens }, default);
        Assert.Equal(("unclear", "模型沒有回這一項"), (result[1].Verdict, result[1].Reason));
    }

    [Fact]
    public async Task Non_json_is_an_error()
    {
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("sorry"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Checker(chat).CheckAsync(Jpeg, new[] { Hair }, default));
    }

    [Fact]
    public void Items_are_covered_facets_with_tags_in_catalog_order()
    {
        var catalog = FacetCatalogTests.Real();
        var s = new Session("s1");
        s.ApplyProfile("portrait", catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState>
        {
            ["appearance.hair"] = FacetState.Covered, ["style.genre"] = FacetState.Covered,
            ["scene.weather"] = FacetState.Covered, ["camera.focal"] = FacetState.Waived,
        }, catalog, new Dictionary<string, string> { ["appearance.hair"] = "long silver hair", ["style.genre"] = "anime" });
        var items = SelfCheckItems.From(s, catalog);
        Assert.Equal(new[] { "style.genre", "appearance.hair" }, items.Select(i => i.FacetId));   // scene.weather 沒 tag、camera.focal 不是 covered
        Assert.Equal(catalog.Facets["appearance.hair"].Label, items[1].Label);
        var order = catalog.Facets.Keys.ToList();
        Assert.True(order.IndexOf("style.genre") < order.IndexOf("appearance.hair"));
    }
}
