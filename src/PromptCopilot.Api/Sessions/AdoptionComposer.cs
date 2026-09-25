using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Sessions;

/// <summary>POST /messages 的 adopt 欄位（設計 §6.1）。Take：照它的 facet；該維度其餘 facet 視為保留我的。</summary>
public sealed record AdoptRequest(long PresetId, string Dimension, IReadOnlyList<string>? Take);

/// <summary>請求本身不成立（端點回 400）。retrieval off 與 profile 未設是 409，端點先擋。</summary>
public sealed class AdoptValidationException(string message) : Exception(message);

public sealed record ComposedAdoption(string Text, Adoption Adoption, LedgerEntry Preset);

/// <summary>採用句由伺服器組（設計 §6.2）：模型每次看到的形狀一致、tag 一定是資料庫的原字。純函式，不動 session。</summary>
public static class AdoptionComposer
{
    public const string Prefix = "採用〈";

    public static ComposedAdoption Compose(AdoptRequest req, PresetDetail preset, Session s, FacetCatalog catalog, int turnIndex)
    {
        if (s.Profile is null) throw new AdoptValidationException("尚未判定題材，還不能採用組合");
        if (preset.FacetTags is null) throw new AdoptValidationException("這筆片段尚未拆分 facet，無法採用");
        var facets = catalog.FacetsOf(s.Profile, req.Dimension);
        if (facets.Count == 0) throw new AdoptValidationException($"維度 {req.Dimension} 對 {s.Profile} 不適用或不存在");
        var take = (req.Take ?? Array.Empty<string>()).Distinct().ToList();
        if (take.Count == 0) throw new AdoptValidationException("take 不可為空：至少一個 facet 照它的");
        foreach (var f in take)
        {
            if (!facets.Contains(f)) throw new AdoptValidationException($"facet {f} 不屬於維度 {req.Dimension}");
            if ((preset.FacetTags.GetValueOrDefault(f)?.Count ?? 0) == 0) throw new AdoptValidationException($"這套沒有 {catalog.Facets[f].Label} 的 tag");
        }

        var ordered = facets.Where(take.Contains).ToList();                                   // 依 facets.yaml 順序
        var taken = ordered.ToDictionary(f => f, f => preset.FacetTags[f]);
        FacetState StateOf(string f) => s.FacetStates.GetValueOrDefault(f, FacetState.Missing);
        var kept = facets.Where(f => !take.Contains(f) && StateOf(f) != FacetState.NotApplicable).ToList();
        var filled = ordered.Where(f => StateOf(f) is FacetState.Missing or FacetState.NotApplicable).ToList();
        var replaced = ordered.Where(f => !filled.Contains(f)).ToList();

        var takeText = string.Join("、", ordered.Select(f => $"{catalog.Facets[f].Label}照它的（{string.Join(", ", taken[f])}）"));
        var keptText = kept.Count == 0 ? "" : $"；{string.Join("、", kept.Select(f => catalog.Facets[f].Label))}保留我的";
        var text = $"{Prefix}{preset.Title}〉（知識庫 #{preset.Id}）：{takeText}{keptText}。";

        var adoption = new Adoption(turnIndex, preset.Id, preset.Title, preset.SourceRef, req.Dimension, taken, kept, filled, replaced);
        var entry = new LedgerEntry
        {
            Id = preset.Id, Title = preset.Title, PromptSnippet = preset.PromptSnippet, NegativeSnippet = preset.NegativeSnippet,
            FacetIds = preset.FacetIds, ImageUrl = preset.ImageUrl, SourceRef = preset.SourceRef,
        };
        return new ComposedAdoption(text, adoption, entry);
    }
}
