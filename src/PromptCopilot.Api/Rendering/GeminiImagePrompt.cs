using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace PromptCopilot.Api.Rendering;

/// <summary>問 Gemini、要它照 schema 回 JSON；可以帶一張圖。圖片放在 user 訊息的 ImageContent，Google connector 轉成 inlineData
/// （GeminiImageRequestTests 釘住）；不進主對話的 ChatHistory（可行性 §4）。送的是 ImageForGemini 縮過的圖。
/// 文字步（整理要求清單，符合度設計 §4.2）不帶圖：清單不能受圖影響。</summary>
internal static class GeminiImagePrompt
{
    public static async Task<string> AskAsync(IChatCompletionService chat, string model, string prompt, GeminiImage? image, Type schema, CancellationToken ct)
    {
        var history = new ChatHistory();
        var items = new ChatMessageContentItemCollection { new TextContent(prompt) };
        if (image is not null) items.Add(new ImageContent(image.Data, image.MimeType));
        history.AddUserMessage(items);
        // 不設 temperature，用模型預設：Google 建議 Gemini 3 系列維持預設，調低可能迴圈或品質下降；
        // 溫度 0 時要求清單偶爾有錯字，同一個輸入也照樣給不同的 tag，沒換到穩定（eval-cases R14–R17、facet 向量實驗）
        var settings = new GeminiPromptExecutionSettings { ModelId = model, ResponseMimeType = "application/json", ResponseSchema = schema };
        var result = await chat.GetChatMessageContentsAsync(history, settings, kernel: null, ct);
        return result[0].Content ?? "";
    }
}
