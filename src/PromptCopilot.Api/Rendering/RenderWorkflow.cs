using System.Text.Json.Nodes;

namespace PromptCopilot.Api.Rendering;

/// <summary>ComfyUI 的 API 格式 workflow 範本（render/workflows/txt2img-sdxl.json，csproj 連結進輸出目錄）。
/// 要改的節點寫死編號並檢查 class_type：範本換了節點編號，送出去才發現會白花一次冷啟動（同 scripts/render_spike.py）。</summary>
public sealed class RenderWorkflow
{
    public const string FileName = "txt2img-sdxl.json";

    /// <summary>每張都帶的負向詞，同 scripts/render_spike.py 的 NEGATIVE。nsfw 在最前：動漫模型就算提示詞乾淨也可能生出不當內容（可行性 §8）。</summary>
    public const string BaseNegative = "nsfw, lowres, bad anatomy, bad hands, text, error, missing fingers, extra digit, fewer digits, cropped, "
        + "worst quality, low quality, jpeg artifacts, signature, watermark, username, blurry";

    private static readonly (string Role, string Id, string ClassType)[] Nodes =
        { ("positive", "6", "CLIPTextEncode"), ("negative", "7", "CLIPTextEncode"), ("sampler", "3", "KSampler") };

    private readonly JsonObject _template;

    public RenderWorkflow(JsonObject template)
    {
        foreach (var (role, id, classType) in Nodes)
        {
            var actual = template[id]?["class_type"]?.GetValue<string>();
            if (actual != classType) throw new InvalidOperationException($"workflow 的節點 {id}（{role}）應該是 {classType}，實際是 {actual ?? "（沒有）"}");
        }
        _template = template;
    }

    public static RenderWorkflow Load(string path) =>
        new(JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidOperationException($"{path} 不是 JSON 物件"));

    public string? CheckpointName => _template.Select(kv => kv.Value).OfType<JsonObject>()
        .FirstOrDefault(n => n["class_type"]?.GetValue<string>() == "CheckpointLoaderSimple")?["inputs"]?["ckpt_name"]?.GetValue<string>();

    public JsonObject Build(string positive, string negative, long seed)
    {
        var wf = (JsonObject)_template.DeepClone();
        wf["6"]!["inputs"]!["text"] = positive;
        wf["7"]!["inputs"]!["text"] = WithBaseNegative(negative);
        wf["3"]!["inputs"]!["seed"] = seed;
        return wf;
    }

    /// <summary>定稿的負向詞在前、固定詞補在後；逗號切開後不分大小寫去重。</summary>
    public static string WithBaseNegative(string negative)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var p in $"{negative},{BaseNegative}".Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (seen.Add(p)) parts.Add(p);
        return string.Join(", ", parts);
    }
}
