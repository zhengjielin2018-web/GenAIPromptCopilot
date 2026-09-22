using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

/// <summary>多輪 §4.3。只修剪與過濾，不改語意；理由全部回傳，呼叫端寫 audit。</summary>
public static class AskCleaner
{
    public const int MinOptions = 2;
    public const int MaxOptions = 4;

    public static CleanResult<AskItem> CleanAsks(IReadOnlyList<AskItem> asks, Session session, FacetCatalog catalog, int maxAsks)
    {
        var kept = new List<AskItem>();
        var rejected = new List<string>();
        if (asks.Count > maxAsks)
            rejected.Add($"asks 有 {asks.Count} 則，只留前 {maxAsks}");
        foreach (var a in asks.Take(maxAsks))
        {
            var options = a.Options;
            if (options.Count > MaxOptions) { rejected.Add($"[{a.Dimension}] options 有 {options.Count} 個，只留前 {MaxOptions}"); options = options.Take(MaxOptions).ToList(); }
            if (options.Count < MinOptions) { rejected.Add($"[{a.Dimension}] options 少於 {MinOptions} 個，整則移除"); continue; }

            var facets = new List<string>();
            foreach (var fid in a.MissingFacetIds)
            {
                if (!catalog.Facets.ContainsKey(fid)) { rejected.Add($"[{a.Dimension}] {fid} 不存在，過濾"); continue; }
                if (catalog.DimensionOf(fid) != a.Dimension) { rejected.Add($"[{a.Dimension}] {fid} 不屬於此維度，過濾"); continue; }
                if (session.FacetStates.GetValueOrDefault(fid, FacetState.NotApplicable) != FacetState.Missing) { rejected.Add($"[{a.Dimension}] {fid} 不是 missing，過濾"); continue; }
                facets.Add(fid);
            }
            if (facets.Count == 0) { rejected.Add($"[{a.Dimension}] 過濾後沒有 missing facet，整則移除"); continue; }
            kept.Add(a with { MissingFacetIds = facets, Options = options });
        }
        return new CleanResult<AskItem>(kept, rejected);
    }

    public static CleanResult<OptionItem> CleanOptions(IReadOnlyList<OptionItem> options, PresetLedger ledger, int maxOptions)
    {
        var rejected = new List<string>();
        var kept = new List<OptionItem>();
        if (options.Count > maxOptions) rejected.Add($"options 有 {options.Count} 個，只留前 {maxOptions}");
        foreach (var o in options.Take(maxOptions))
        {
            if (o.PresetId is { } id && !ledger.Contains(id))
            {
                rejected.Add($"選項「{o.Label}」的 presetId {id} 不在 ledger，降級為無來源");
                kept.Add(o with { PresetId = null });
            }
            else kept.Add(o);
        }
        return new CleanResult<OptionItem>(kept, rejected);
    }
}
