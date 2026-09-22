using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Endpoints;

public static class ReferenceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Meta");

        app.MapGet("/api/config/facets", (FacetCatalog c) => Results.Ok(new
        {
            dimensions = c.Dimensions.Select(d => new
            {
                key = d, label = c.DimensionLabels[d],
                facets = c.Facets.Values.Where(f => f.Dimension == d).Select(f => new { f.Id, f.Label, f.Hint }),
            }),
            profiles = c.Profiles.ToDictionary(p => p.Key, p => new
            {
                labels = c.Dimensions.Where(d => c.FacetsOf(p.Key, d).Count > 0).ToDictionary(d => d, d => c.DimensionLabel(d, p.Key)),
                dimensions = p.Value,
            }),
        })).WithTags("Reference");

        app.MapGet("/api/presets/{id:long}", async (long id, PresetRepository presets, CancellationToken ct) =>
            await presets.GetAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound()).WithTags("Reference");
    }
}
