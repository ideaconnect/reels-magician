using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelsMagician.Core.Toolchain;

public sealed class GhAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class GhRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GhAsset> Assets { get; set; } = [];
}

/// <summary>
/// Picks the right prebuilt binary out of a GitHub release. Asset names change between
/// builds, so selection is pattern based with an explicit preference order; that also means
/// a Vulkan build is picked up automatically as soon as a project starts publishing one.
/// </summary>
public static class GitHubReleases
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static List<GhRelease> Parse(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('['))
            return JsonSerializer.Deserialize<List<GhRelease>>(json, Options) ?? [];
        var single = JsonSerializer.Deserialize<GhRelease>(json, Options);
        return single is null ? [] : [single];
    }

    /// <summary>
    /// Walks releases newest-first and returns the first asset matching the highest-priority
    /// predicate available in that release.
    /// </summary>
    public static (GhRelease Release, GhAsset Asset, int MatchedRule)? SelectAsset(
        IEnumerable<GhRelease> releases,
        IReadOnlyList<Func<string, bool>> preferenceOrder)
    {
        foreach (var release in releases)
        {
            for (var rule = 0; rule < preferenceOrder.Count; rule++)
            {
                var asset = release.Assets.FirstOrDefault(a => preferenceOrder[rule](a.Name.ToLowerInvariant()));
                if (asset is not null) return (release, asset, rule);
            }
        }
        return null;
    }

    public static Func<string, bool> Contains(params string[] needles) =>
        name => needles.All(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
}
