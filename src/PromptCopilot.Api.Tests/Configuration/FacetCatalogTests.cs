using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Tests.Configuration;

public class FacetCatalogTests
{
    public static FacetCatalog Real() =>
        FacetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Configuration", "facets.yaml"));

    [Fact]
    public void Loads_six_dimensions_in_yaml_order()
    {
        var c = Real();
        Assert.Equal(new[] { "style", "scene", "camera", "appearance", "pose", "clothing" }, c.Dimensions);
        Assert.Equal("風格", c.DimensionLabels["style"]);
    }

    [Fact]
    public void DimensionOf_maps_facet_to_its_dimension() =>
        Assert.Equal("pose", Real().DimensionOf("pose.gaze"));

    [Fact]
    public void FacetsOf_returns_empty_for_inapplicable_dimension()
    {
        var c = Real();
        Assert.Empty(c.FacetsOf("landscape", "clothing"));
        Assert.Equal(new[] { "pose.motion_state", "pose.terrain" }, c.FacetsOf("vehicle", "pose"));
    }

    [Fact]
    public void IdsForProfile_portrait_has_31_ids_and_no_vehicle_facets()
    {
        var ids = Real().IdsForProfile("portrait");
        Assert.Equal(31, ids.Count);
        Assert.DoesNotContain("pose.motion_state", ids);
    }

    [Fact]
    public void DimensionLabel_prefers_profile_override()
    {
        var c = Real();
        Assert.Equal("主體外觀", c.DimensionLabel("appearance", "object"));
        Assert.Equal("人物樣貌", c.DimensionLabel("appearance", "portrait"));
    }

    [Fact]
    public void ProfileListing_only_includes_applicable_facets()
    {
        var text = Real().ProfileListing("landscape");
        Assert.Contains("scene.season", text);
        Assert.DoesNotContain("clothing.", text);
    }

    [Fact]
    public void Load_rejects_profile_referencing_unknown_facet()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            dimensions:
              - key: style
                label: 風格
                facets:
                  - { id: style.genre, label: g, hint: h }
            profiles:
              portrait:
                dimensions:
                  style: [style.genre, style.nope]
            """);
        Assert.Throws<InvalidDataException>(() => FacetCatalog.Load(path));
    }
}
