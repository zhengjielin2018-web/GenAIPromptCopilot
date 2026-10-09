using PromptCopilot.Api.Rendering;
using SkiaSharp;

namespace PromptCopilot.Api.Tests.Rendering;

public class ImageForGeminiTests
{
    public static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(200, 80, 120));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int W, int H) Size(byte[] bytes)
    {
        using var b = SKBitmap.Decode(bytes);
        return (b.Width, b.Height);
    }

    [Fact]
    public void A_full_size_preview_becomes_a_768_px_jpeg_with_the_same_aspect()
    {
        var png = Png(832, 1216);
        var img = ImageForGemini.Prepare(png);
        Assert.Equal("image/jpeg", img.MimeType);
        Assert.Equal((525, 768), Size(img.Data));
        Assert.True(img.Data.Length < png.Length);
        Assert.Null(img.Error);
    }

    [Fact]
    public void A_small_image_is_not_upscaled()
    {
        var img = ImageForGemini.Prepare(Png(400, 300));
        Assert.Equal("image/jpeg", img.MimeType);
        Assert.Equal((400, 300), Size(img.Data));
    }

    [Fact]
    public void Bytes_that_are_not_an_image_go_out_unchanged_as_png()
    {
        var junk = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var img = ImageForGemini.Prepare(junk);
        Assert.Equal(("image/png", junk), (img.MimeType, img.Data));
        Assert.NotNull(img.Error);
    }
}
