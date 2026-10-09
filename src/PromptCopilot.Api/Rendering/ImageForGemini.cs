using SkiaSharp;

namespace PromptCopilot.Api.Rendering;

/// <summary>送給 Gemini 看的圖。Error 只在縮圖失敗、退回原圖時有值（給 log 用）。</summary>
public sealed record GeminiImage(byte[] Data, string MimeType, string? Error = null);

/// <summary>審圖與自評用的縮圖（可行性 §9.4）：832×1216、約 1.5 MB 的 PNG，Gemini 審圖要 12–17 秒，
/// 縮成長邊 768 的 JPEG（約 100–200 KB）再送；使用者看到的仍是原圖。比 768 小的不放大。
/// 用 SkiaSharp（MIT）：ImageSharp 4.x 起要授權檔。縮不了（不是圖、原生函式庫載不進來）就退回原圖，不讓整張預覽失敗。</summary>
public static class ImageForGemini
{
    public const int MaxSide = 768;
    public const int JpegQuality = 85;

    public static GeminiImage Prepare(byte[] png)
    {
        try
        {
            using var source = SKBitmap.Decode(png);
            if (source is null) return new GeminiImage(png, "image/png", "不是可解碼的圖");
            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(source.Width, source.Height));
            using var resized = scale < 1
                ? source.Resize(new SKImageInfo((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale)),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                : source.Copy();
            using var image = SKImage.FromBitmap(resized);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return new GeminiImage(jpeg.ToArray(), "image/jpeg");
        }
        catch (Exception e)
        {
            return new GeminiImage(png, "image/png", $"{e.GetType().Name}: {e.Message}");
        }
    }
}
