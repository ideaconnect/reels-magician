using System.Globalization;
using System.Text.RegularExpressions;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The scroll shadow (issue #51): when each edge of a scrolling area fades, which areas have it,
/// and what they fade from. A scrolling area left without it, or a strip whose style and code
/// name different pseudo-classes, still builds and runs, and only shows as a hard edge again.
/// </summary>
public static class ScrollShadowChecks
{
    public static void Run()
    {
        Check.Section("Scrolling areas fade their edges instead of cutting content off (issue #51)");

        Edges(0, 400, 380, false, false, "content that fits shows no strip");
        Edges(0, 400, 400, false, false, "content exactly the viewport's height shows no strip");
        Edges(0, 400, 1200, false, true, "at the top of a long page, only the bottom edge fades");
        Edges(300, 400, 1200, true, true, "in the middle, both edges fade");
        Edges(800, 400, 1200, true, false, "at the end, only the top edge fades and the bottom strip is gone");
        Edges(799.4, 400, 1200, true, false, "less than a pixel from the end, left there by layout rounding, counts as the end");
        Edges(798, 400, 1200, true, true, "two pixels from the end, there is still more below");
        Edges(0.5, 400, 1200, true, true, "scrolled by any amount, the top edge fades");
        Edges(0, 0, 1200, false, false, "an area not laid out yet shows no strip");

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var window = StripComments(File.ReadAllText(Path.Combine(app, "MainWindow.axaml")));
        var areas = Regex.Matches(window, @"<(ScrollViewer|ListBox)(\s[^>]*)?>")
            .Select(m => (Kind: m.Groups[1].Value, Attributes: m.Groups[2].Value, Line: LineOf(window, m.Index)))
            .ToList();
        // The issue's table: the pages of Setup, Source, Render and Cleanup, the cue list, the
        // proposals and the subject tracking, the caption style and the activity log.
        Check.True(areas.Count >= 9, $"MainWindow.axaml has the nine scrolling areas the issue lists (found {areas.Count})");

        var hardEdged = areas.Where(a => !a.Attributes.Contains("local:ScrollShadow.IsEnabled=\"True\"", StringComparison.Ordinal))
            .Select(a => $"{a.Kind} on line {a.Line}").ToList();
        Check.True(hardEdged.Count == 0, "every ScrollViewer and ListBox in the window has the scroll shadow" + Listing("without", hardEdged));

        // A ListBox draws its own InputBg background, apart from the proposal cards, which sit
        // straight on the page. A fade from the window's background would show there as a band.
        var wrongSurface = areas
            .Where(a => a.Kind == "ListBox" && !Regex.IsMatch(a.Attributes, "Classes=\"[^\"]*\\bcards\\b"))
            .Where(a => !a.Attributes.Contains("local:ScrollShadow.Surface=\"{DynamicResource InputBg}\"", StringComparison.Ordinal))
            .Select(a => $"line {a.Line}").ToList();
        Check.True(wrongSurface.Count == 0, "the lists fade from their own background, InputBg" + Listing("not", wrongSurface));

        var paddings = areas.Where(a => a.Kind == "ScrollViewer")
            .Select(a => (a.Line, Padding: Regex.Match(a.Attributes, "Padding=\"([^\"]+)\"").Groups[1].Value))
            .Where(p => p.Padding.Length > 0)
            .ToList();
        Check.True(paddings.Count >= 5, $"the five card pages pad their content (found {paddings.Count})");
        var flush = paddings.Where(p => Bottom(p.Padding) <= 0).Select(p => $"line {p.Line}: {p.Padding}").ToList();
        Check.True(flush.Count == 0,
            "every padded page has bottom padding, so its last card is not flush with the edge at the end" + Listing("none", flush));

        var theme = StripComments(File.ReadAllText(Path.Combine(app, "Assets", "Theme.axaml")));
        var code = File.ReadAllText(Path.Combine(app, "ScrollShadow.cs"));
        foreach (var (pseudo, strip) in new[] { ("scroll-up", "top"), ("scroll-down", "bottom") })
        {
            Check.True(Regex.IsMatch(theme, $"<Style Selector=\"local\\|ScrollShadowOverlay:{pseudo} > Border\\.{strip}\">\\s*" +
                                            "<Setter Property=\"Opacity\" Value=\"1\" />"),
                $"Theme.axaml shows the {strip} strip on :{pseudo}");
            Check.Contains(code, $"PseudoClasses.Set(\":{pseudo}\"", $"ScrollShadow.cs sets :{pseudo}, the pseudo-class the style waits for");
            Check.Contains(code, $"Classes = {{ \"{strip}\" }}", $"the {strip} strip has the class the style names");
        }
        Check.True(Regex.IsMatch(theme, "<Style Selector=\"local\\|ScrollShadowOverlay > Border\">\\s*<Setter Property=\"Opacity\" Value=\"0\" />"),
            "both strips are hidden unless there is more to scroll to");
        Check.True(Regex.IsMatch(theme, "<DoubleTransition Property=\"Opacity\" Duration=\"0:0:0\\.[0-9]+\" />"),
            "the strips fade in and out with a short transition");
        Check.Contains(theme, "<Setter Property=\"Surface\" Value=\"{DynamicResource Bg}\" />",
            "by default the strips fade from the window's background, in the theme that is showing");

        // The fade replaces a black shadow, which wouldn't show on the true-black dark theme.
        var tints = Regex.Matches(theme, "<SolidColorBrush x:Key=\"ScrollShadowTint\" Color=\"(#[0-9A-Fa-f]{6})\" Opacity=\"([0-9.]+)\" />")
            .Select(m => (Colour: m.Groups[1].Value, Opacity: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
        Check.Equal(2, tints.Count, "both palettes give the strips a tint");
        if (tints.Count == 2)
        {
            Check.Near(0, tints[0].Opacity, 0, "the dark theme's strips fade to the black background with no tint");
            Check.True(tints[1].Opacity is > 0 and <= 0.1,
                $"the light theme's strips add a slight dark tint (opacity {tints[1].Opacity:0.##})");
        }
    }

    private static void Edges(double offset, double viewport, double extent, bool above, bool below, string description)
    {
        var edges = ScrollEdges.Of(offset, viewport, extent);
        Check.True(edges.MoreAbove == above && edges.MoreBelow == below,
            $"{description} (offset {offset}, viewport {viewport}, extent {extent}: " +
            $"top {(edges.MoreAbove ? "fades" : "hard")}, bottom {(edges.MoreBelow ? "fades" : "hard")})");
    }

    private static double Bottom(string padding)
    {
        var parts = padding.Split(',', StringSplitOptions.TrimEntries);
        var value = parts.Length switch { 1 => parts[0], 2 => parts[1], 4 => parts[3], _ => "0" };
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 0;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    // Comments are replaced by as many line breaks as they hold, so line numbers stay true.
    private static string StripComments(string xaml) =>
        Regex.Replace(xaml, "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);

    private static string Listing(string what, List<string> items) =>
        items.Count == 0 ? "" : $" ({what}: {string.Join(", ", items)})";
}
