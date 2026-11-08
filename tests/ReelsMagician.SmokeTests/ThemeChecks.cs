using System.Globalization;
using System.Text.RegularExpressions;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The light and dark themes (issue #49), read from the app's XAML: both palettes define the same
/// keys, nothing holds on to a theme colour with StaticResource, which would keep the colour of the
/// theme the window opened in, no colour is hard-coded outside the palette, and every text colour
/// reaches 4.5:1 on the backgrounds it sits on.
/// </summary>
public static class ThemeChecks
{
    private const double TextContrast = 4.5;

    public static void Run()
    {
        Check.Section("Light and dark themes: complete, switchable while running, readable (issue #49)");

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var theme = StripComments(File.ReadAllText(Path.Combine(app, "Assets", "Theme.axaml")));
        var block = Regex.Match(theme, @"<ResourceDictionary\.ThemeDictionaries>(.*?)</ResourceDictionary\.ThemeDictionaries>",
            RegexOptions.Singleline).Groups[1].Value;
        var palettes = Regex.Matches(block, "<ResourceDictionary x:Key=\"(Dark|Light)\">(.*?)</ResourceDictionary>",
                RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Check.True(palettes.ContainsKey("Dark") && palettes.ContainsKey("Light"), "Theme.axaml has a Dark and a Light palette");
        if (palettes.Count != 2) return;

        var keys = palettes.ToDictionary(p => p.Key, p => Regex.Matches(p.Value, "x:Key=\"([A-Za-z0-9]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet());
        var onlyDark = keys["Dark"].Except(keys["Light"]).Order().ToList();
        var onlyLight = keys["Light"].Except(keys["Dark"]).Order().ToList();
        Check.True(onlyDark.Count == 0 && onlyLight.Count == 0,
            "both palettes define the same keys" + Listing("only in Dark", onlyDark) + Listing("only in Light", onlyLight));

        StaysDynamic(app, keys["Dark"].Union(keys["Light"]).ToHashSet());
        NoStrayColours(app, theme, block);

        var dark = Colours(palettes["Dark"]);
        var light = Colours(palettes["Light"]);
        Check.Equal("#000000", dark.GetValueOrDefault("Bg"), "the dark window is true black, so OLED pixels switch off");
        Check.True(light.GetValueOrDefault("Bg") is { } lightBg && lightBg != "#FFFFFF",
            "the light window is off-white, so a bright IPS panel doesn't glare");
        foreach (var (name, palette) in new[] { ("Dark", dark), ("Light", light) })
            Readable(name, palette);

        var fallback = Regex.Match(theme, "<Color x:Key=\"FallbackAccentColor\">(#[0-9A-Fa-f]{6})</Color>").Groups[1].Value;
        Check.True(fallback.Length > 0 && Contrast(fallback, "#FFFFFF") >= TextContrast,
            $"white button text on the fallback accent {fallback} reaches {TextContrast}:1 " +
            $"({(fallback.Length > 0 ? Contrast(fallback, "#FFFFFF") : 0):0.00}:1)");
    }

    /// <summary>A theme colour used through StaticResource never changes when the theme does.</summary>
    private static void StaysDynamic(string app, HashSet<string> themeKeys)
    {
        var stale = AppXaml(app)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{StaticResource ([A-Za-z0-9]+)\}")
                .Select(m => m.Groups[1].Value)
                .Where(themeKeys.Contains)
                .Select(k => $"{Path.GetFileName(f)}: {k}"))
            .ToList();
        Check.True(stale.Count == 0, "every theme colour is used through DynamicResource" + Listing("StaticResource", stale));

        var overrides = AppXaml(app)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "x:Key=\"SystemAccentColor"))
            .Select(Path.GetFileName).OfType<string>().ToList();
        Check.True(overrides.Count == 0,
            "no XAML sets Fluent's SystemAccentColor, so the system's accent shows" + Listing("set in", overrides));

        var appXaml = File.ReadAllText(Path.Combine(app, "App.axaml"));
        Check.True(!StripComments(appXaml).Contains("RequestedThemeVariant", StringComparison.Ordinal),
            "App.axaml doesn't fix a theme variant, so the setting decides");
    }

    /// <summary>
    /// Colours live in the palette: the windows name resources only, and Theme.axaml's styles use
    /// fixed values only for Buy Me a Coffee's brand yellow, which is the same in both themes.
    /// </summary>
    private static void NoStrayColours(string app, string theme, string paletteBlock)
    {
        var inWindows = AppXaml(app)
            .Where(f => Path.GetFileName(f) != "Theme.axaml")
            .SelectMany(f => Regex.Matches(StripComments(File.ReadAllText(f)), "#[0-9A-Fa-f]{6,8}\\b")
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Check.True(inWindows.Count == 0, "the windows and dialogs use no colour of their own" + Listing("found", inWindows));

        var rest = theme.Replace(paletteBlock, "");
        rest = Regex.Replace(rest, "<(SolidColorBrush|Color) x:Key=\"[^\"]+\"[^>]*?(/>|>[^<]*</Color>)", "");
        var stray = new List<string>();
        foreach (Match style in Regex.Matches(rest, "<Style Selector=\"([^\"]*)\">(.*?)</Style>", RegexOptions.Singleline))
        {
            if (Regex.IsMatch(style.Groups[2].Value, "#[0-9A-Fa-f]{6}") && !style.Groups[1].Value.Contains("Button.coffee"))
                stray.Add(style.Groups[1].Value);
        }
        var outside = Regex.Matches(Regex.Replace(rest, "<Style Selector=\"[^\"]*\">.*?</Style>", "", RegexOptions.Singleline),
            "#[0-9A-Fa-f]{6}").Select(m => m.Value).ToList();
        Check.True(stray.Count == 0 && outside.Count == 0,
            "Theme.axaml's styles take their colours from the palette, Buy Me a Coffee's brand yellow aside" +
            Listing("styles", stray) + Listing("elsewhere", outside));
    }

    private static void Readable(string name, Dictionary<string, string> palette)
    {
        string[] surfaces = ["Bg", "Panel", "PanelAlt", "InputBg", "CardHover", "CardSelected", "RowSelected"];
        foreach (var text in (string[])["TextBrush", "Muted"])
            Reaches(name, palette, text, surfaces);
        foreach (var status in (string[])["Success", "Error", "Warning", "Info"])
            Reaches(name, palette, status + "Brush", ["Bg", "Panel", "PanelAlt", status + "Bg"]);
        Reaches(name, palette, "SponsorForeground",
            ["SponsorBackground", "SponsorBackgroundPointerOver", "SponsorBackgroundPressed"]);
    }

    private static void Reaches(string theme, Dictionary<string, string> palette, string text, string[] backgrounds)
    {
        if (!palette.TryGetValue(text, out var fg))
        {
            Check.True(false, $"{theme}: {text} is in the palette");
            return;
        }
        var results = backgrounds.Select(bg => palette.TryGetValue(bg, out var c) ? (bg, Contrast(fg, c)) : (bg, 0.0)).ToList();
        var worst = results.MinBy(r => r.Item2);
        Check.True(worst.Item2 >= TextContrast,
            $"{theme}: {text} {fg} reaches {TextContrast}:1 on {string.Join(", ", backgrounds)} " +
            $"(lowest {worst.Item2:0.00}:1 on {worst.bg})");
    }

    private static Dictionary<string, string> Colours(string palette) =>
        Regex.Matches(palette, "<SolidColorBrush x:Key=\"([A-Za-z0-9]+)\" Color=\"(#[0-9A-Fa-f]{6})\"")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());

    /// <summary>WCAG 2's contrast ratio between two opaque sRGB colours.</summary>
    public static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var c = int.Parse(hex.AsSpan(1 + at * 2, 2), NumberStyles.HexNumber) / 255d;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    private static IEnumerable<string> AppXaml(string app) =>
        Directory.EnumerateFiles(app, "*.axaml", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "Icons.axaml"
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string StripComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);

    private static string Listing(string what, List<string> items) =>
        items.Count == 0 ? "" : $" ({what}: {string.Join(", ", items)})";
}
