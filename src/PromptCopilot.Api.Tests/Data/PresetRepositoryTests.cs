using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Tests.Data;

public class PresetRepositoryTests
{
    [Fact]
    public void ParseFacetTags_reads_jsonb_text_and_null_stays_null()
    {
        var d = PresetRepository.ParseFacetTags("""{"clothing.footwear":["sandals","platform footwear"],"clothing.upper":[]}""")!;
        Assert.Equal(new[] { "sandals", "platform footwear" }, d["clothing.footwear"]);
        Assert.Empty(d["clothing.upper"]);
        Assert.Null(PresetRepository.ParseFacetTags(null));
        Assert.Empty(PresetRepository.ParseFacetTags("{}")!);
    }

    /// <summary>LIKE 的 % 與 _ 是萬用字元：錨 tag 已正規化（底線變空白），但保險起見兩個都跳脫。</summary>
    [Fact]
    public void EscapeLike_escapes_wildcards()
    {
        Assert.Equal(@"100\% wool\_blend", PresetRepository.EscapeLike("100% wool_blend"));
        Assert.Equal("sandals", PresetRepository.EscapeLike("sandals"));
    }
}
