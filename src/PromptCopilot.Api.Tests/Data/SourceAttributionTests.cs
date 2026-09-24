using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Tests.Data;

public class SourceAttributionTests
{
    /// <summary>presets 是三段式 civitai:&lt;imageId&gt;:&lt;idx&gt;，histories 是兩段式；兩種都指到同一張圖的頁面。</summary>
    [Theory]
    [InlineData("civitai:12345:0", "https://civitai.com/images/12345")]
    [InlineData("civitai:12345", "https://civitai.com/images/12345")]
    [InlineData("kisegae:1741156656403", "https://github.com/hayde0096/Kisegaeningyou")]
    public void Known_prefixes_map_to_their_source_page(string sourceRef, string expected) =>
        Assert.Equal(expected, SourceAttribution.UrlFor(sourceRef));

    /// <summary>使用者存的紀錄 source_ref 是 NULL；未知前綴不能猜一個網址出來。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("danbooru:99")]
    [InlineData("civitai:")]
    [InlineData("civitai:abc")]
    public void Unknown_or_missing_refs_have_no_url(string? sourceRef) =>
        Assert.Null(SourceAttribution.UrlFor(sourceRef));
}
