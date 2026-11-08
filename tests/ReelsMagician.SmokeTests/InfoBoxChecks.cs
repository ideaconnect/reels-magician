using System.Globalization;
using System.Text.RegularExpressions;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The note boxes (issue #53): every explanatory note and warning in the window is an InfoBox,
/// the data shown in the hint style stays plain, the old notice and warn styles are gone, each
/// kind has its own icon and colours, and a screen reader hears the kind before the text. A note
/// left as a hint, or a kind whose style names a pseudo-class the control never sets, still
/// builds and runs, and only shows as a grey line or a blue box where an amber one belongs.
/// </summary>
public static class InfoBoxChecks
{
    private const double TextContrast = 4.5;

    // The notes the issue lists, each by the start of its text or the binding that holds it, and
    // the two the About tab (#52) added since.
    private static readonly (string Tab, string Note)[] InfoNotes =
    [
        ("Setup", "Everything installs into the app's own data folder"),
        ("Setup", "Only needed to load a video from a link"),
        ("Setup", "{Binding ModelsHint}"),
        ("Source", "Choose an MP4 or paste a compatible video link"),
        ("Source", "Taken from the video title"),
        ("Subtitles", "Amber text marks cues Whisper was unsure about"),
        ("Reel", "Each clip is sampled frame by frame"),
        ("Reel", "Choosing a different reel clears the framing"),
        ("Captions", "Plain text draws one static line per caption"),
        ("Captions", "Colour recolours the spoken word"),
        ("Captions", "A line that cannot get this much time"),
        ("Captions", "<Binding Path=\"CrossfadeHint\" />"),
        ("Captions", "The audio is crossfaded by the same amount"),
        ("Render", "Renders 1080×1920 H.264 at 30 fps"),
        ("Cleanup", "Only this folder is touched"),
        ("About", "Each opens in your browser"),
        ("About", "ReelsMagician and every component it uses come under their own licences"),
    ];

    // The source warning and a component's warning, such as a Fallback build.
    private static readonly string[] WarningNotes = ["{Binding SourceWarning}", "{Binding Warning}"];

    // Data, not notes: summaries, states, sizes and times keep the small muted hint style.
    private static readonly string[] DataHints =
    [
        "{Binding TranscriptSummary}", "{Binding PlanSummary}", "{Binding TimeLabel}", "{Binding SizeText}",
        "{Binding MissingText}", "{Binding BackendText}", "{Binding StateText}",
    ];

    public static void Run()
    {
        Check.Section("Notes and warnings in bordered boxes with an icon (issue #53)");

        Check.Equal("Information: Taken from the video title.", Notes.AccessibleName(NoteKind.Info, "Taken from the video title."),
            "a screen reader hears an explanation as \"Information: …\"");
        Check.Equal("Warning: Source is 720p.", Notes.AccessibleName(NoteKind.Warning, "Source is 720p."),
            "a screen reader hears a warning as \"Warning: …\"");
        Check.Equal("Error: It failed.", Notes.AccessibleName(NoteKind.Error, "It failed."), "and an error as \"Error: …\"");
        Check.Equal("Warning: Two lines.\nThe second.", Notes.AccessibleName(NoteKind.Warning, "  Two lines.\nThe second.\n"),
            "the name keeps the text's own line break and drops the whitespace round it");
        Check.True(Notes.AccessibleName(NoteKind.Info, null) == "Information" && Notes.AccessibleName(NoteKind.Error, " ") == "Error",
            "a note with no text, whose content is other controls, is named by its kind alone");

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var window = StripComments(File.ReadAllText(Path.Combine(app, "MainWindow.axaml")));
        var boxes = Regex.Matches(window, @"<local:InfoBox\b(?:[^>]*?/>|.*?</local:InfoBox>)", RegexOptions.Singleline)
            .Select(m => m.Value).ToList();
        var hints = Regex.Matches(window, @"<TextBlock\b[^>]*Classes=""hint""[^>]*/?>").Select(m => m.Value).ToList();

        var missing = InfoNotes.Where(n => !boxes.Any(b => b.Contains(n.Note, StringComparison.Ordinal)))
            .Select(n => $"{n.Tab}: {n.Note}").ToList();
        Check.True(missing.Count == 0, $"each of the {InfoNotes.Length} notes is in an InfoBox" + Listing("not", missing));
        var asWarnings = InfoNotes.Where(n => boxes.Any(b => b.Contains(n.Note, StringComparison.Ordinal) && !IsKind(b, "Info")))
            .Select(n => $"{n.Tab}: {n.Note}").ToList();
        Check.True(asWarnings.Count == 0, "the explanations are of the Info kind" + Listing("another kind", asWarnings));
        var stillHints = InfoNotes.Where(n => hints.Any(h => h.Contains(n.Note, StringComparison.Ordinal)))
            .Select(n => $"{n.Tab}: {n.Note}").ToList();
        Check.True(stillHints.Count == 0, "no note is left as a grey hint as well" + Listing("still a hint", stillHints));
        var crossfade = boxes.Where(b => b.Contains("CrossfadeHint", StringComparison.Ordinal)).ToList();
        Check.True(crossfade.Count == 1 && crossfade[0].Contains("The audio is crossfaded", StringComparison.Ordinal),
            "the crossfade's two lines share one box");

        foreach (var warning in WarningNotes)
            Check.True(boxes.Any(b => b.Contains($"Text=\"{warning}\"", StringComparison.Ordinal) && IsKind(b, "Warning")),
                $"the warning {warning} is an InfoBox of the Warning kind");
        Check.True(!Regex.IsMatch(window, @"Classes=""[^""]*\b(notice|warn)\b"),
            "the window uses neither the old notice box nor the bare warn text");

        var dataBoxed = DataHints.Where(d => boxes.Any(b => b.Contains(d, StringComparison.Ordinal))).ToList();
        var dataPlain = DataHints.Where(d => !hints.Any(h => h.Contains(d, StringComparison.Ordinal))).ToList();
        Check.True(dataBoxed.Count == 0 && dataPlain.Count == 0,
            "data such as summaries, states and sizes keeps the hint style" + Listing("boxed", dataBoxed) + Listing("not a hint", dataPlain));

        var theme = StripComments(File.ReadAllText(Path.Combine(app, "Assets", "Theme.axaml")));
        Check.True(!Regex.IsMatch(theme, @"Selector=""(Border\.notice|TextBlock\.warn)"""),
            "Theme.axaml drops the old notice and warn styles");
        BoxTheme(theme);

        var code = File.ReadAllText(Path.Combine(app, "InfoBox.cs"));
        foreach (var pseudo in (string[])[":info", ":warning", ":error"])
            Check.Contains(code, $"PseudoClasses.Set(\"{pseudo}\"", $"InfoBox.cs sets {pseudo}, which its style waits for");
        Check.Contains(code, "Notes.AccessibleName(Kind, Text)", "InfoBox names itself by its kind and its text");

        Palettes(theme);
    }

    /// <summary>The control's theme: the box, each kind's icon and colours, and the compact form.</summary>
    private static void BoxTheme(string theme)
    {
        var block = Regex.Match(theme, @"<ControlTheme x:Key=""\{x:Type local:InfoBox\}"".*?</ControlTheme>", RegexOptions.Singleline).Value;
        Check.True(block.Length > 0, "Theme.axaml has a control theme for InfoBox");
        if (block.Length == 0) return;

        var setters = Setters(Regex.Replace(block, @"<Style\b.*?</Style>", "", RegexOptions.Singleline));
        Check.Equal("{DynamicResource InfoBg}", setters.GetValueOrDefault("Background"), "a note's box is tinted with its kind's colour");
        Check.Equal("{DynamicResource InfoBorder}", setters.GetValueOrDefault("BorderBrush"), "and bordered in it");
        Check.Equal("1", setters.GetValueOrDefault("BorderThickness"), "the border is 1 px");
        Check.Equal("6", setters.GetValueOrDefault("CornerRadius"), "with a corner radius of 6");
        var padding = setters.GetValueOrDefault("Padding") ?? "";
        Check.True(padding.Split(',').All(p => double.TryParse(p, CultureInfo.InvariantCulture, out var v) && v is >= 10 and <= 12),
            $"the box pads its text by 10 to 12 px (Padding {padding})");
        Check.Equal("{DynamicResource TextBrush}", setters.GetValueOrDefault("Foreground"),
            "the text is at the normal text colour, not muted, so it reads at full contrast");
        Check.True(Regex.IsMatch(block, @"<TextBlock Name=""PART_Text""[^>]*TextWrapping=""Wrap""[^>]*AutomationProperties\.AccessibilityView=""Raw"""),
            "the text wraps, and is hidden from screen readers, which hear it in the box's name instead");

        foreach (var (kind, selector, icon) in new[]
                 {
                     ("Info", "^ /template/ local|FaIcon#PART_Icon", "fa-circle-info"),
                     ("Warning", "^:warning /template/ local|FaIcon#PART_Icon", "fa-triangle-exclamation"),
                     ("Error", "^:error /template/ local|FaIcon#PART_Icon", "fa-circle-xmark"),
                 })
        {
            var style = Setters(Style(block, selector));
            Check.True(style.GetValueOrDefault("Data") == $"{{StaticResource {icon}}}" &&
                       style.GetValueOrDefault("Foreground") == $"{{DynamicResource {kind}Brush}}",
                $"{kind} notes show {icon} in {kind}Brush");
            if (kind == "Info") continue;
            var box = Setters(Style(block, $"^:{kind.ToLowerInvariant()}"));
            Check.True(box.GetValueOrDefault("Background") == $"{{DynamicResource {kind}Bg}}" &&
                       box.GetValueOrDefault("BorderBrush") == $"{{DynamicResource {kind}Border}}",
                $"{kind} notes are tinted with {kind}Bg and bordered with {kind}Border");
        }

        var compact = Setters(Style(block, "^.compact"));
        Check.True(compact.GetValueOrDefault("Padding") == "0" && compact.GetValueOrDefault("BorderThickness") == "0" &&
                   compact.GetValueOrDefault("Background") == "Transparent",
            "the compact form keeps the icon and the text, with no box and no padding");
    }

    /// <summary>
    /// Each kind's colours in both themes: the border is the kind's colour at about half opacity,
    /// the text reaches 4.5:1 on the tint, and the box's edge stands out from the window and from
    /// a card at least as clearly as a card's own border does, true black included.
    /// </summary>
    private static void Palettes(string theme)
    {
        var block = Regex.Match(theme, @"<ResourceDictionary\.ThemeDictionaries>(.*?)</ResourceDictionary\.ThemeDictionaries>",
            RegexOptions.Singleline).Groups[1].Value;
        foreach (Match palette in Regex.Matches(block, "<ResourceDictionary x:Key=\"(Dark|Light)\">(.*?)</ResourceDictionary>",
                     RegexOptions.Singleline))
        {
            var name = palette.Groups[1].Value;
            var brushes = Regex.Matches(palette.Groups[2].Value,
                    "<SolidColorBrush x:Key=\"([A-Za-z0-9]+)\" Color=\"(#[0-9A-Fa-f]{6})\"(?: Opacity=\"([0-9.]+)\")? />")
                .ToDictionary(m => m.Groups[1].Value,
                    m => (Colour: m.Groups[2].Value.ToUpperInvariant(),
                          Opacity: m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1));
            string[] needed = ["Bg", "Panel", "BorderBrush2", "TextBrush"];
            if (needed.Any(k => !brushes.ContainsKey(k)))
            {
                Check.True(false, $"{name}: the palette has {string.Join(", ", needed)}");
                continue;
            }
            var cardEdge = Math.Min(ThemeChecks.Contrast(brushes["BorderBrush2"].Colour, brushes["Bg"].Colour),
                ThemeChecks.Contrast(brushes["BorderBrush2"].Colour, brushes["Panel"].Colour));

            foreach (var kind in (string[])["Info", "Warning", "Error"])
            {
                if (!brushes.TryGetValue(kind + "Brush", out var colour) || !brushes.TryGetValue(kind + "Bg", out var tint) ||
                    !brushes.TryGetValue(kind + "Border", out var border))
                {
                    Check.True(false, $"{name}: the palette has {kind}Brush, {kind}Bg and {kind}Border");
                    continue;
                }
                Check.True(border.Colour == colour.Colour && border.Opacity is >= 0.4 and <= 0.6,
                    $"{name}: {kind}Border is {kind}Brush {colour.Colour} at about half opacity ({border.Colour} at {border.Opacity:0.##})");
                var text = ThemeChecks.Contrast(brushes["TextBrush"].Colour, tint.Colour);
                Check.True(text >= TextContrast,
                    $"{name}: the text of {kind} notes reaches {TextContrast}:1 on their tint {tint.Colour} ({text:0.00}:1)");
                var edge = Blend(border.Colour, border.Opacity, tint.Colour);
                var shows = Math.Min(ThemeChecks.Contrast(edge, brushes["Bg"].Colour), ThemeChecks.Contrast(edge, brushes["Panel"].Colour));
                Check.True(shows >= cardEdge,
                    $"{name}: the edge of {kind} notes shows on the window and on a card at least as clearly as a card's border " +
                    $"({shows:0.00}:1 against {cardEdge:0.00}:1)");
            }
        }
    }

    private static bool IsKind(string element, string kind)
    {
        var tag = Regex.Match(element, @"<local:InfoBox\b[^>]*>").Value;
        var set = Regex.Match(tag, @"\bKind=""([A-Za-z]+)""");
        return (set.Success ? set.Groups[1].Value : "Info") == kind;
    }

    private static string Style(string block, string selector) =>
        Regex.Match(block, $"<Style Selector=\"{Regex.Escape(selector)}\">(.*?)</Style>", RegexOptions.Singleline).Groups[1].Value;

    private static Dictionary<string, string> Setters(string xaml)
    {
        var setters = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(xaml, @"<Setter Property=""([A-Za-z.]+)"" Value=""([^""]*)"" />"))
            setters.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return setters;
    }

    /// <summary>A colour at an opacity laid over an opaque one, as a 1 px border over the box's tint.</summary>
    private static string Blend(string top, double opacity, string under)
    {
        int Channel(string hex, int at) => int.Parse(hex.AsSpan(1 + at * 2, 2), NumberStyles.HexNumber);
        return "#" + string.Concat(Enumerable.Range(0, 3)
            .Select(i => ((int)Math.Round(Channel(top, i) * opacity + Channel(under, i) * (1 - opacity))).ToString("X2")));
    }

    private static string StripComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);

    private static string Listing(string what, List<string> items) =>
        items.Count == 0 ? "" : $" ({what}: {string.Join(", ", items)})";
}
