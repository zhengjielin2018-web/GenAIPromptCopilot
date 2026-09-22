using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Safety;

public sealed record SafetyVerdict(bool Nsfw, bool RealPerson, string? PersonName, bool WantsAutoComplete, string Reason);

public sealed class SafetyClassifier(IChatCompletionService chat, IOptions<LlmOptions> llm)
{
    private const string InputPrompt = """
        你是 AI 生圖提示詞助理的輸入審核。判斷下面這段使用者輸入，回 JSON：
        - nsfw：是否要求色情、裸露、性暗示、未成年性化內容
        - realPerson：是否指名真實存在的公眾人物或私人（虛構角色不算）；是的話 personName 填名字
        - wantsAutoComplete：整句是否在要求系統「直接幫我決定全部／隨便／你看著辦／直接給我」；只針對單一項目說隨便（「鞋子隨便，背景我想一下」）不算
        - reason：一句話
        使用者輸入：
        """;

    private const string OutputPrompt = """
        你是 AI 生圖提示詞助理的輸出審核。下面是要送給使用者的英文 prompt 片段或繁中說明，判斷回 JSON：
        - nsfw：是否含色情、裸露、性暗示、未成年性化內容
        - realPerson：是否描述真實存在的人；是的話 personName 填名字
        - wantsAutoComplete：固定 false
        - reason：一句話
        內容：
        """;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Task<SafetyVerdict> ClassifyInputAsync(string text, CancellationToken ct) => RunAsync(InputPrompt + text, ct);
    public Task<SafetyVerdict> ClassifyOutputAsync(string text, CancellationToken ct) => RunAsync(OutputPrompt + text, ct);

    private async Task<SafetyVerdict> RunAsync(string prompt, CancellationToken ct)
    {
        var history = new ChatHistory();
        history.AddUserMessage(prompt);
        var settings = new GeminiPromptExecutionSettings
        {
            ModelId = llm.Value.Model,
            ResponseMimeType = "application/json",
            ResponseSchema = typeof(SafetyVerdict),
            Temperature = 0,
        };
        var result = await chat.GetChatMessageContentsAsync(history, settings, kernel: null, ct);
        var content = result[0].Content ?? "";
        try
        {
            return JsonSerializer.Deserialize<SafetyVerdict>(content, Json) ?? throw new InvalidOperationException("分類器回 null");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"分類器回了非 JSON：{content[..Math.Min(80, content.Length)]}", e);
        }
    }
}
