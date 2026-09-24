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
        Assert.Equal("""{"dimension":"style","query":"photo"}""", JsonSerializer.Serialize(new SearchQuery("style", "photo")));
    }
}
