using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Endpoints;

public static class ReferenceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Meta")
            .WithSummary("健康檢查")
            .WithDescription("固定回 `{\"status\": \"ok\"}`。不連資料庫、也不打 Gemini，只證明 API 程式在跑：資料庫或 Gemini 掛了，這支照樣回 ok。");

        app.MapGet("/api/config/facets", (FacetCatalog c) => Results.Ok(new
        {
            dimensions = c.Dimensions.Select(d => new
            {
                key = d, label = c.DimensionLabels[d],
                facets = c.Facets.Values.Where(f => f.Dimension == d).Select(f => new { f.Id, f.Label, f.Hint }),
            }),
            profiles = c.Profiles.ToDictionary(p => p.Key, p => new
            {
                labels = c.Dimensions.Where(d => c.FacetsOf(p.Key, d).Count > 0).ToDictionary(d => d, d => c.DimensionLabel(d, p.Key)),
                dimensions = p.Value,
            }),
        })).WithTags("Reference")
            .WithSummary("維度與 facet 設定")
            .WithDescription("""
                內容來自 `Configuration/facets.yaml`，儀表板靠它把 `dimensions` 事件裡的 facet id 翻成中文。

                - `dimensions`：6 個維度（風格、場景、鏡頭、人物樣貌、人物動作、人物穿著）。每個 facet 有 `id`（如 `style.genre`）、中文 `label`、英文提示詞範例 `hint`。
                - `profiles`：4 種題材（`portrait`、`landscape`、`object`、`vehicle`）各用到哪些維度與 facet。`labels` 是該題材下的維度名稱：同一個維度換了題材可能改叫法（例如載具的 `pose` 叫「運動狀態」）。
                """);

        app.MapGet("/api/config/safety", (IOptions<SafetyOptions> o) => Results.Ok(new { canDisable = o.Value.AllowDisable })).WithTags("Reference")
            .WithSummary("測試用審查開關是否開放")
            .WithDescription("""
                `{"canDisable": true | false}`：後端 `Safety:AllowDisable`（docker compose 用 `.env` 的 `SAFETY_ALLOW_DISABLE`）開著時為 `true`，前端才顯示審查開關，`POST /api/sessions/{id}/messages` 才收 `"safety": "off"`。預設 `false`。
                """)
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/api/presets/{id:long}", async (long id, PresetRepository presets, CancellationToken ct) =>
            await presets.GetAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound()).WithTags("Reference")
            .WithSummary("查知識庫的一筆 preset")
            .WithDescription("""
                回傳完整內容：標題、分類、說明、tags、對應的 facet、`promptSnippet`、`negativeSnippet`、`imageUrl`（指向來源網站的原圖，本服務不轉存）、`sourceRef`（資料來源識別，如 `civitai:12345:0`）、`sourceUrl`（出處頁面；前端顯示圖片時要一併顯示這個連結，見 `docs/資料來源.md`；來源不明時為 null）、`facetTags`（tag → facet 拆分，2026-09-25；尚未回填為 null）。

                對話事件裡出現的 preset id 都能拿來查：`tool_result.presets[].id`、選項的 `presetId`。

                - `404`：沒有這個 id
                """)
            .Produces<PresetDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }
}
