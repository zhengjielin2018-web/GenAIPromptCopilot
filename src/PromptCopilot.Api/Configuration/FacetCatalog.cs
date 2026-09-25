using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PromptCopilot.Api.Configuration;

public sealed record Facet(string Id, string Label, string Hint, string Dimension);

public sealed class FacetCatalog
{
    public IReadOnlyList<string> Dimensions { get; }
    public IReadOnlyDictionary<string, string> DimensionLabels { get; }
    public IReadOnlyDictionary<string, Facet> Facets { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Profiles { get; }
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _profileLabels;

    private FacetCatalog(
        IReadOnlyList<string> dimensions,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, Facet> facets,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> profiles,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> profileLabels)
    {
        Dimensions = dimensions; DimensionLabels = labels; Facets = facets; Profiles = profiles; _profileLabels = profileLabels;
    }

    public bool IsProfile(string profile) => Profiles.ContainsKey(profile);

    public string DimensionOf(string facetId) => Facets[facetId].Dimension;

    public IReadOnlySet<string> IdsForProfile(string profile) =>
        Profiles[profile].Values.SelectMany(x => x).ToHashSet();

    public IReadOnlyList<string> FacetsOf(string profile, string dimension) =>
        Profiles[profile].TryGetValue(dimension, out var ids) ? ids : Array.Empty<string>();

    /// <summary>該 profile 有 facet 的維度，依 facets.yaml 順序。</summary>
    public IReadOnlyList<string> DimensionsOf(string profile) => Dimensions.Where(d => FacetsOf(profile, d).Count > 0).ToList();

    public string DimensionLabel(string dimension, string profile) =>
        _profileLabels.TryGetValue(profile, out var o) && o.TryGetValue(dimension, out var l) ? l
        : DimensionLabels.GetValueOrDefault(dimension, dimension);

    /// <summary>全部維度與 facet，給 SetProfile 之前的 system prompt。</summary>
    public string PromptListing()
    {
        var lines = new List<string>();
        foreach (var dim in Dimensions)
        {
            lines.Add($"[{dim}] {DimensionLabels[dim]}");
            foreach (var f in Facets.Values.Where(f => f.Dimension == dim))
                lines.Add($"  - {f.Id}：{f.Label}（例：{f.Hint}）");
        }
        return string.Join("\n", lines);
    }

    /// <summary>只列該 profile 適用的維度與 facet；維度名用 profile 的覆寫。</summary>
    public string ProfileListing(string profile)
    {
        var lines = new List<string>();
        foreach (var dim in Dimensions)
        {
            var ids = FacetsOf(profile, dim);
            if (ids.Count == 0) continue;
            lines.Add($"[{dim}] {DimensionLabel(dim, profile)}");
            foreach (var id in ids)
                lines.Add($"  - {id}：{Facets[id].Label}（例：{Facets[id].Hint}）");
        }
        return string.Join("\n", lines);
    }

    // ---- yaml 形狀 ----
    private sealed class Root { public List<DimNode> Dimensions { get; set; } = new(); public Dictionary<string, ProfileNode> Profiles { get; set; } = new(); }
    private sealed class DimNode { public string Key { get; set; } = ""; public string Label { get; set; } = ""; public List<FacetNode> Facets { get; set; } = new(); }
    private sealed class FacetNode { public string Id { get; set; } = ""; public string Label { get; set; } = ""; public string Hint { get; set; } = ""; }
    private sealed class ProfileNode { public Dictionary<string, string>? Labels { get; set; } public Dictionary<string, List<string>> Dimensions { get; set; } = new(); }

    public static FacetCatalog Load(string path)
    {
        var yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();
        var root = yaml.Deserialize<Root>(File.ReadAllText(path));

        var dims = new List<string>();
        var labels = new Dictionary<string, string>();
        var facets = new Dictionary<string, Facet>();
        foreach (var d in root.Dimensions)
        {
            dims.Add(d.Key); labels[d.Key] = d.Label;
            foreach (var f in d.Facets) facets[f.Id] = new Facet(f.Id, f.Label, f.Hint, d.Key);
        }
        var profiles = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
        var profileLabels = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var (name, p) in root.Profiles)
        {
            profiles[name] = p.Dimensions.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.AsReadOnly());
            profileLabels[name] = p.Labels ?? new Dictionary<string, string>();
        }
        var unknown = profiles.Values.SelectMany(d => d.Values).SelectMany(x => x)
            .Where(id => !facets.ContainsKey(id)).Distinct().Order().ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"facets.yaml profiles 引用了不存在的 facet id: {string.Join(", ", unknown)}");
        return new FacetCatalog(dims, labels, facets, profiles, profileLabels);
    }
}
