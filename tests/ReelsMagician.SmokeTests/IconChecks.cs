using System.Text.RegularExpressions;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The Font Awesome icons the app draws (src/ReelsMagician.App/Assets/Icons.axaml) against the
/// script that copies them, the app's XAML that uses them and the notices that credit them. A key
/// the XAML names but Icons.axaml lacks still compiles, and only fails when the window loads.
/// </summary>
public static class IconChecks
{
    public static void Run()
    {
        Check.Section("Font Awesome icons match their script, their uses and their credit");

        var root = FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var icons = File.ReadAllText(Path.Combine(app, "Assets", "Icons.axaml"));
        var geometries = Regex.Matches(icons, "<StreamGeometry x:Key=\"(fa-[a-z0-9-]+)\">([^<]*)</StreamGeometry>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Check.True(geometries.Count > 0, "Icons.axaml holds the icons");
        Check.True(geometries.Values.All(g => g.StartsWith("F1 M", StringComparison.Ordinal)),
            "every icon is one path with the nonzero fill rule (F1) its SVG is drawn for");

        var used = Directory.EnumerateFiles(app, "*.axaml", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "Icons.axaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{StaticResource (fa-[a-z0-9-]+)\}").Select(m => m.Groups[1].Value))
            .ToHashSet();
        var missing = used.Except(geometries.Keys).Order().ToList();
        Check.True(missing.Count == 0, "every icon the XAML uses is in Icons.axaml" + Listing("missing", missing));
        var unused = geometries.Keys.Except(used).Order().ToList();
        Check.True(unused.Count == 0, "every icon in Icons.axaml is used" + Listing("unused", unused));

        // Icons.axaml is generated: the script's list and the file must say the same.
        var script = File.ReadAllText(Path.Combine(root, "tools", "update-icons.cs"));
        var list = Regex.Match(script, @"string\[\] Icons =\s*\[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        var listed = Regex.Matches(list, "\"(?:[a-z]+/)?([a-z0-9-]+)\"").Select(m => "fa-" + m.Groups[1].Value).ToHashSet();
        Check.True(listed.SetEquals(geometries.Keys),
            "Icons.axaml holds exactly the icons tools/update-icons.cs lists, so it was generated from that list");

        var version = Regex.Match(icons, @"Font Awesome Free (\d+\.\d+\.\d+) icons").Groups[1].Value;
        Check.True(version.Length > 0, "Icons.axaml names the Font Awesome version it was copied from");
        Check.Contains(script, $"DefaultVersion = \"{version}\"", $"the script's default version is {version} too");
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        Check.Contains(notices, $"Font Awesome Free {version}", $"THIRD-PARTY-NOTICES.md credits Font Awesome Free {version}");

        var licence = Path.Combine(root, "licenses", "FontAwesome", "LICENSE.txt");
        Check.True(File.Exists(licence) && File.ReadAllText(licence).Contains("CC BY 4.0", StringComparison.Ordinal),
            "licenses/FontAwesome/LICENSE.txt carries the CC BY 4.0 icon licence");
    }

    private static string Listing(string what, List<string> keys) =>
        keys.Count == 0 ? "" : $" ({what}: {string.Join(", ", keys)})";

    internal static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReelsMagician.slnx"))) return dir.FullName;
        return null;
    }
}
