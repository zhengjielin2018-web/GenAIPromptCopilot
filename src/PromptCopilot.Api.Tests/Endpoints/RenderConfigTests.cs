using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Tests.Endpoints;

public class RenderConfigTests
{
    private static async Task<bool> EnabledWith(Dictionary<string, string?> config)
    {
        await using var f = new EndpointTests.Factory();
        var client = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config))).CreateClient();
        var doc = await client.GetFromJsonAsync<JsonElement>("/api/config/render");
        return doc.GetProperty("enabled").GetBoolean();
    }

    [Fact]
    public async Task Render_is_off_until_both_endpoint_and_key_are_set()
    {
        Assert.False(await EnabledWith(new()));
        Assert.False(await EnabledWith(new() { ["Render:EndpointId"] = "ep" }));
        Assert.False(await EnabledWith(new() { ["Render:ApiKey"] = "k" }));
        Assert.True(await EnabledWith(new() { ["Render:EndpointId"] = "ep", ["Render:ApiKey"] = "k" }));
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var o = new RenderOptions();
        Assert.Equal((10, 200, 60, 180, 1000, 10), (o.PerSessionLimit, o.DailyLimit, o.MaxEstimatedWaitSeconds, o.JobTimeoutSeconds, o.PollIntervalMs, o.DefaultImageSeconds));
        Assert.False(o.Enabled);
        Assert.Equal(2, o.SeedsBeforeRewrite);
    }
}
