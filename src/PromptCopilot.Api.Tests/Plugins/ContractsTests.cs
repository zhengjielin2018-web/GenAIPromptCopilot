using System.Text.Json;
using PromptCopilot.Api.Plugins;

namespace PromptCopilot.Api.Tests.Plugins;

public class ContractsTests
{
    [Fact]
    public void SearchQuery_round_trips_camelCase_json()
    {
        var json = """[{"dimension":"style","query":"寫實攝影"},{"dimension":"scene","query":"稻田"}]""";
        var parsed = JsonSerializer.Deserialize<SearchQuery[]>(json)!;
        Assert.Equal(2, parsed.Length);
        Assert.Equal("style", parsed[0].Dimension);
        Assert.Equal("稻田", parsed[1].Query);
        // 序列化用 ASCII 句子：預設 encoder 會把非 ASCII 轉成 \uXXXX，這裡只驗欄位名是 camelCase
        Assert.Equal("""{"dimension":"style","query":"photo","facetId":null}""", JsonSerializer.Serialize(new SearchQuery("style", "photo")));
    }

    /// <summary>facet 層級查詢：項目可以只帶 facetId 不帶 dimension（模型 2026-09-25 自發用的形狀）。</summary>
    [Fact]
    public void SearchQuery_facetId_round_trips_and_dimension_may_be_omitted()
    {
        var parsed = JsonSerializer.Deserialize<SearchQuery[]>("""[{"facetId":"clothing.footwear","query":"拖鞋"},{"dimension":"style","query":"寫實"}]""")!;
        Assert.Null(parsed[0].Dimension);
        Assert.Equal("clothing.footwear", parsed[0].FacetId);
        Assert.Equal("拖鞋", parsed[0].Query);
        Assert.Equal("style", parsed[1].Dimension);
        Assert.Null(parsed[1].FacetId);

        var q = new SearchQuery("clothing", "sandals", "clothing.footwear");
        var json = JsonSerializer.Serialize(q);
        Assert.Equal("""{"dimension":"clothing","query":"sandals","facetId":"clothing.footwear"}""", json);
        Assert.Equal(q, JsonSerializer.Deserialize<SearchQuery>(json));
    }
}
