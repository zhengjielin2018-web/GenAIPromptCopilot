using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Sessions;

public class AdoptionComposerTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private static PresetDetail Preset(IReadOnlyDictionary<string, IReadOnlyList<string>>? facetTags) =>
        new(41720, "和風女僕紫和服", "Clothing", "d", new[] { "kimono" }, new[] { "clothing.head", "clothing.upper", "clothing.footwear" },
            "detached sleeves, purple kimono, maid headdress, sandals", null, "https://img", "civitai:9:0", "https://civitai.com/images/9", facetTags);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Tags = new Dictionary<string, IReadOnlyList<string>>
    {
        ["clothing.head"] = new[] { "maid headdress" },
        ["clothing.upper"] = new[] { "purple kimono", "detached sleeves" },
        ["clothing.footwear"] = new[] { "sandals" },
        ["clothing.lower"] = Array.Empty<string>(),
    };

    private static Session Sess(params (string facet, FacetState state)[] states)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(states.ToDictionary(x => x.facet, x => x.state), Catalog);
        return s;
    }

    /// <summary>設計 §6.2：照它的依 facets.yaml 順序、tags 以「, 」相接；保留我的是該維度其餘 facet（notApplicable 除外）。</summary>
    [Fact]
    public void Composes_the_sentence_in_yaml_order_with_kept_facets()
    {
        var s = Sess(("clothing.footwear", FacetState.Covered), ("clothing.material", FacetState.NotApplicable));
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper", "clothing.head" }), Preset(Tags), s, Catalog, 4);
        Assert.Equal("採用〈和風女僕紫和服〉（知識庫 #41720）：頭部配件照它的（maid headdress）、上半身照它的（purple kimono, detached sleeves）；下半身、鞋履、配件飾品保留我的。", c.Text);
        Assert.Equal(4, c.Adoption.TurnIndex);
        Assert.Equal(new[] { "clothing.head", "clothing.upper" }, c.Adoption.Taken.Keys);
        Assert.Equal(new[] { "clothing.lower", "clothing.footwear", "clothing.accessories" }, c.Adoption.Kept);
        Assert.Equal("civitai:9:0", c.Adoption.SourceRef);
        Assert.Equal(41720, c.Adoption.PresetId); Assert.Equal("和風女僕紫和服", c.Adoption.Title); Assert.Equal("clothing", c.Adoption.Dimension);
        Assert.Equal(new[] { "purple kimono", "detached sleeves" }, c.Adoption.Taken["clothing.upper"]);
        Assert.Equal(41720, c.Preset.Id); Assert.Equal("detached sleeves, purple kimono, maid headdress, sandals", c.Preset.PromptSnippet);
        Assert.Equal("https://img", c.Preset.ImageUrl);
        // 字串參數一旦對調（Title／SourceRef／snippet）就會在這裡露餡
        Assert.Equal("civitai:9:0", c.Preset.SourceRef); Assert.Null(c.Preset.NegativeSnippet); Assert.Equal(3, c.Preset.FacetIds.Count);
    }

    [Fact]
    public void Omits_the_kept_clause_when_nothing_is_kept()
    {
        var s = Sess(("clothing.lower", FacetState.NotApplicable), ("clothing.material", FacetState.NotApplicable), ("clothing.accessories", FacetState.NotApplicable));
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.head", "clothing.upper", "clothing.footwear" }), Preset(Tags), s, Catalog, 1);
        Assert.EndsWith("鞋履照它的（sandals）。", c.Text);
        Assert.DoesNotContain("保留我的", c.Text);
        Assert.Empty(c.Adoption.Kept);
    }

    /// <summary>量測用：Filled＝原本 missing／notApplicable；Replaced＝原本 covered／waived。</summary>
    [Fact]
    public void Classifies_taken_facets_into_filled_and_replaced_by_their_previous_state()
    {
        var s = Sess(("clothing.footwear", FacetState.Covered), ("clothing.head", FacetState.Waived), ("clothing.upper", FacetState.NotApplicable));
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.head", "clothing.upper", "clothing.footwear" }), Preset(Tags), s, Catalog, 1);
        Assert.Equal(new[] { "clothing.upper" }, c.Adoption.Filled);                        // notApplicable 也算補上
        Assert.Equal(new[] { "clothing.head", "clothing.footwear" }, c.Adoption.Replaced);
    }

    [Fact]
    public void Dedups_take()
    {
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper", "clothing.upper" }), Preset(Tags), Sess(), Catalog, 1);
        Assert.Single(c.Adoption.Taken);
    }

    [Theory]
    [InlineData("clothing", new[] { "clothing.lower" }, "這套沒有 下半身 的 tag")]          // 有鍵但空
    [InlineData("clothing", new[] { "clothing.accessories" }, "這套沒有 配件飾品 的 tag")]  // 沒鍵
    [InlineData("clothing", new[] { "scene.weather" }, "facet scene.weather 不屬於維度 clothing")]
    [InlineData("clothing", new string[0], "take 不可為空")]
    [InlineData("nope", new[] { "clothing.upper" }, "維度 nope 對 portrait 不適用或不存在")]
    [InlineData(null, new[] { "clothing.upper" }, "dimension 不可為空")]                     // JSON 的 null 要是 400，不是 500
    public void Rejects_take_facet_the_set_has_no_tags_for_and_other_bad_requests(string? dimension, string[] take, string message)
    {
        var e = Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, dimension!, take), Preset(Tags), Sess(), Catalog, 1));
        Assert.StartsWith(message, e.Message);
    }

    [Fact]
    public void Rejects_null_take_preset_without_facet_tags_and_session_without_profile()
    {
        Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", null), Preset(Tags), Sess(), Catalog, 1));
        Assert.Contains("尚未拆分", Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper" }), Preset(null), Sess(), Catalog, 1)).Message);
        Assert.Contains("題材", Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper" }), Preset(Tags), new Session("x"), Catalog, 1)).Message);
    }
}
