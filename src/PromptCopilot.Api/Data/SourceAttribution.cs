namespace PromptCopilot.Api.Data;

/// <summary>source_ref 前綴 → 出處頁面。docs/資料來源.md 要求顯示 image_url 時一併顯示出處；
/// 規則放伺服器，前端只顯示。來源多一種只改這裡。</summary>
public static class SourceAttribution
{
    public const string KisegaeRepo = "https://github.com/hayde0096/Kisegaeningyou";

    public static string? UrlFor(string? sourceRef)
    {
        if (string.IsNullOrWhiteSpace(sourceRef)) return null;
        var parts = sourceRef.Split(':');
        return parts[0] switch
        {
            "civitai" when parts.Length >= 2 && parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit)
                => $"https://civitai.com/images/{parts[1]}",
            "kisegae" => KisegaeRepo,
            _ => null,
        };
    }
}
