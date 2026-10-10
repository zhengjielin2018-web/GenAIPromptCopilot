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
        var settings = new GeminiPromptExecutionSettings { ModelId = model, ResponseMimeType = "application/json", ResponseSchema = schema, Temperature = 0 };
        var result = await chat.GetChatMessageContentsAsync(history, settings, kernel: null, ct);
        return result[0].Content ?? "";
    }
}
