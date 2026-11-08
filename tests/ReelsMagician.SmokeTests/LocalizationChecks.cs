using System.Globalization;
using System.Text.RegularExpressions;
using ReelsMagician.Core.About;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Localization;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The interface in English and Polish (issue #55). A missing Polish text, a placeholder the
/// translation dropped, a key the XAML names but the resources lack or a text left in the XAML
/// all build and run, and only show as a bracketed key, a wrong number or an English line in a
/// Polish window. These checks hold the two resource pairs (the app's and Core's) to each other
/// and to what uses them, the plural rules to CLDR, and the language setting to settings.json.
/// </summary>
public static class LocalizationChecks
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static void Run()
    {
        Check.Section("Interface languages: English and Polish (issue #55)");

        PluralRules();
        Cultures();
        Setting();
        Languages();
        ProcessCulture();

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        Resources(root, "the app", AppXaml.AppResx(root), AppXaml.AppResx(root, "pl"));
        Resources(root, "Core", AppXaml.CoreResx(root), AppXaml.CoreResx(root, "pl"));
        Xaml(root);
        Code(root);
        CoreTables();
    }

    private static void PluralRules()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        foreach (var (count, expected) in new (double, PluralCategory)[]
                 {
                     (1, PluralCategory.One), (0, PluralCategory.Other), (2, PluralCategory.Other),
                     (21, PluralCategory.Other), (1.5, PluralCategory.Other),
                 })
            Check.Equal(expected, Plural.Category(count, english), $"English: {count} is {expected}");

        foreach (var (count, expected) in new (double, PluralCategory)[]
                 {
                     (1, PluralCategory.One), (2, PluralCategory.Few), (4, PluralCategory.Few), (5, PluralCategory.Many),
                     (0, PluralCategory.Many), (11, PluralCategory.Many), (12, PluralCategory.Many), (14, PluralCategory.Many),
                     (21, PluralCategory.Many), (22, PluralCategory.Few), (24, PluralCategory.Few), (25, PluralCategory.Many),
                     (101, PluralCategory.Many), (102, PluralCategory.Few), (112, PluralCategory.Many), (1001, PluralCategory.Many),
                     (1.5, PluralCategory.Other), (-2, PluralCategory.Few),
                 })
            Check.Equal(expected, Plural.Category(count, Polish), $"Polish: {count} is {expected}");

        var pl = Lang.In(Polish);
        Check.Equal("1 klip · 3 klipy · 5 klipów · 22 klipy · 1,5 klipu",
            string.Join(" · ", new[] { 1, 3, 5, 22, 1.5 }.Select(n => pl.Plural("Count_Clips", n))),
            "a Polish count takes the form its number asks for, the fraction with a decimal comma");
        Check.Equal("1 clip · 3 clips · 1.5 clips",
            string.Join(" · ", new[] { 1, 3, 1.5 }.Select(n => Lang.Log.Plural("Count_Clips", n))),
            "an English one tells one from the rest, a form it lacks falling back to _other");
    }

    private static void Cultures()
    {
        CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);
        foreach (var (language, texts, formats, expectTexts, expectFormats, why) in new[]
                 {
                     (UiLanguage.System, "pl-PL", "pl-PL", "pl-PL", "pl-PL", "a Polish system gets the Polish interface"),
                     (UiLanguage.System, "de-DE", "de-DE", "en-US", "en-US", "a language the app doesn't speak gets English, with English numbers"),
                     (UiLanguage.System, "en-GB", "en-GB", "en-US", "en-GB", "English keeps the system's British dates and numbers"),
                     (UiLanguage.English, "pl-PL", "pl-PL", "en-US", "en-US", "English chosen on a Polish system writes numbers the English way"),
                     (UiLanguage.Polish, "en-US", "en-US", "pl-PL", "pl-PL", "Polish chosen on an English system writes \"22,5 MB\""),
                     (UiLanguage.Polish, "pl-PL", "en-GB", "pl-PL", "pl-PL", "Polish texts never come with English regional settings"),
                 })
        {
            var (t, f) = UiCultures.Resolve(language, C(texts), C(formats));
            Check.True(t.Name == expectTexts && f.Name == expectFormats,
                $"{language} on {texts}/{formats}: texts {expectTexts}, numbers {expectFormats}; {why} (got {t.Name}/{f.Name})");
        }
    }

    private static void Setting()
    {
        Check.Equal(UiLanguage.System, new AppSettings().Language, "a new install speaks the system's language");

        var root = Directory.CreateTempSubdirectory("rm-language-").FullName;
        try
        {
            var paths = new ToolPaths(root);
            new AppSettings { Language = UiLanguage.Polish, OutputDirectory = "/tmp/reels" }.Save(paths);
            Check.Contains(File.ReadAllText(paths.SettingsFile), "\"language\": \"pl\"", "settings.json names the language by its code");
            Check.Equal(UiLanguage.Polish, AppSettings.Load(paths).Language, "the language survives a restart");

            foreach (var (written, expected) in new (string, UiLanguage)[]
                     {
                         ("\"en\"", UiLanguage.English), ("\"PL\"", UiLanguage.Polish), ("\"polski\"", UiLanguage.Polish),
                         ("\"system\"", UiLanguage.System), ("\"de\"", UiLanguage.System), ("1", UiLanguage.System),
                         ("null", UiLanguage.System), ("[\"pl\"]", UiLanguage.System), ("{\"code\": \"pl\"}", UiLanguage.System),
                     })
            {
                File.WriteAllText(paths.SettingsFile, $$"""{ "language": {{written}}, "outputDirectory": "/tmp/kept" }""");
                var loaded = AppSettings.Load(paths);
                Check.True(loaded.Language == expected && loaded.OutputDirectory == "/tmp/kept",
                    $"language {written} reads as {expected}, and the other settings are kept");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // What the log writes and what the screen shows, from the same keys.
    private static void Languages()
    {
        var saved = UiCultures.Current;
        try
        {
            UiCultures.Current = new UiCulture(Polish, Polish);
            Check.Equal("Nie znaleziono pliku multimedialnego.", Lang.Ui[nameof(Strings.Media_FileNotFound)],
                "Core's texts on screen follow the interface's language");
            Check.Equal("Media file not found.", Lang.Log[nameof(Strings.Media_FileNotFound)],
                "while the log keeps writing English");
            Check.Equal("Downloading — 12.5%", Lang.Log.Format(nameof(Strings.Source_Downloading), 12.5),
                "with invariant numbers");
            Check.Equal("Pobieranie — 12,5%", Lang.In(Polish).Format(nameof(Strings.Source_Downloading), 12.5),
                "and Polish numbers on a Polish screen");
            Check.Equal("Ostrzeżenie: Źródło ma 720p.", Notes.AccessibleName(NoteKind.Warning, "Źródło ma 720p."),
                "a screen reader names a note's kind in the interface's language");
            Check.Equal("22,5 MB", AssetDownloader.Humanize(23_592_960), "a size on a Polish screen has a decimal comma");
            Check.Equal("22.5 MB", AssetDownloader.Humanize(23_592_960, Lang.Log), "and a decimal point in the log");
        }
        finally
        {
            UiCultures.Current = saved;
        }
    }

    // The process's own culture, Polish on a Polish system, reaches neither the interface's
    // language, which only the language setting picks, nor anything a machine reads.
    private static void ProcessCulture()
    {
        var words = new List<CaptionWord> { new(0.25, 1.5, "Pół"), new(1.5, 2.75, "litra"), new(2.75, 4.125, "wody") };
        var style = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.WordHighlight && s.Emphasis == HighlightEmphasis.Grow);
        var path = new CropPath
        {
            CropWidth = 1080, CropHeight = 1920, SourceWidth = 3840, SourceHeight = 2160,
            Keyframes = [new(0, 100.5, 0), new(2.25, 812.75, 0), new(4.5, 1400.125, 0)],
        };
        string Machine() => string.Join("\n---\n",
            FFmpegTool.Fmt(0.5), AssBuilder.AssTime(61.25), Transcript.SrtTime(61.25),
            AssBuilder.Build(AssBuilder.BuildGroups(words, style), style), path.BuildExpression(horizontal: true),
            Lang.Log.Format(nameof(Strings.Source_Downloading), 12.5), AssetDownloader.Humanize(23_592_960, Lang.Log));

        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            // The baseline under the invariant culture, whatever the run's own culture is.
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var invariant = Machine();
            CultureInfo.CurrentCulture = Polish;
            CultureInfo.CurrentUICulture = Polish;
            Check.True(Machine() == invariant,
                "ffmpeg numbers, ASS and SRT times, the ASS file, a crop expression and the log are the same under pl-PL");
            Check.True(Lang.Ui[nameof(Strings.Media_FileNotFound)] == "Media file not found." && AssetDownloader.Humanize(23_592_960) == "22.5 MB",
                "and the interface stays English until the language setting is applied, so the checks' output stays English");
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }

    private static void Resources(string root, string what, string englishFile, string polishFile)
    {
        var english = AppXaml.Texts(englishFile);
        var polish = AppXaml.Texts(polishFile);

        static bool PolishOnly(string key) => key.EndsWith("_few", StringComparison.Ordinal) || key.EndsWith("_many", StringComparison.Ordinal);
        var untranslated = english.Keys.Where(k => !polish.ContainsKey(k)).ToList();
        var orphans = polish.Keys.Where(k => !english.ContainsKey(k) && !PolishOnly(k)).ToList();
        Check.True(english.Count > 0 && untranslated.Count == 0 && orphans.Count == 0,
            $"{what}: every one of its {english.Count} English texts has a Polish one, and no Polish text lacks its English"
            + Listing("untranslated", untranslated) + Listing("Polish only", orphans));

        var empty = english.Concat(polish).Where(p => p.Value.Trim().Length == 0).Select(p => p.Key).ToList();
        Check.True(empty.Count == 0, $"{what}: no text is empty" + Listing("empty", empty));

        var placeholders = english.Keys.Where(polish.ContainsKey)
            .Where(k => !Holes(english[k]).SetEquals(Holes(polish[k]))).ToList();
        Check.True(placeholders.Count == 0, $"{what}: each Polish text fills the same placeholders as its English"
            + Listing("different", placeholders));

        var broken = english.Concat(polish).Where(p => !Formats(p.Value)).Select(p => p.Key).ToList();
        Check.True(broken.Count == 0, $"{what}: every text is a valid format string" + Listing("broken", broken));

        var plurals = english.Keys.Where(k => k.EndsWith("_one", StringComparison.Ordinal)).Select(k => k[..^4]).ToList();
        var incomplete = plurals.Where(p => !english.ContainsKey(p + "_other")
                || new[] { "_one", "_few", "_many", "_other" }.Any(f => !polish.ContainsKey(p + f)))
            .ToList();
        Check.True(incomplete.Count == 0,
            $"{what}: each of its {plurals.Count} plurals has one and other in English, one, few, many and other in Polish"
            + Listing("incomplete", incomplete));
    }

    private static void Xaml(string root)
    {
        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var english = AppXaml.Texts(AppXaml.AppResx(root));
        var files = Directory.EnumerateFiles(app, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(app, f), File.ReadAllText);

        var keys = files.SelectMany(f => AppXaml.TrKey.Matches(f.Value).Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(f.Value, @"\{l:Format [^}]*Key=([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value)))
            .ToHashSet();
        var unknown = keys.Where(k => !english.ContainsKey(k)).Order().ToList();
        Check.True(keys.Count > 100 && unknown.Count == 0,
            $"each of the {keys.Count} keys the XAML names has a text" + Listing("unknown", unknown));

        // A text written into the XAML shows in English whatever the language. The brand stays.
        var literal = new Regex(@"\s(Text|Header|Content|Title|ToolTip\.Tip|AutomationProperties\.Name|Watermark|PlaceholderText)=""([^""{][^""]*)""");
        var content = new Regex(@"<(TextBlock|Run|Button|Label|SelectableTextBlock)\b[^>]*>\s*([^<\s][^<]*?)\s*</\1>");
        var written = files.SelectMany(f => literal.Matches(f.Value).Select(m => (f.Key, Text: m.Groups[2].Value))
                .Concat(content.Matches(f.Value).Select(m => (f.Key, Text: m.Groups[2].Value))))
            .Where(t => t.Text != "ReelsMagician" && Regex.IsMatch(t.Text, "[A-Za-z]{2}"))
            .Select(t => $"{t.Key}: {t.Text}").ToList();
        Check.True(written.Count == 0, "no XAML file writes a text of its own" + Listing("written", written));
        Check.True(!files.Values.Any(f => f.Contains("StringFormat", StringComparison.Ordinal)),
            "no binding formats a number in a format of its own: {l:Format} writes it the interface's way");

        var window = files["MainWindow.axaml"];
        Check.True(Regex.IsMatch(window, @"<ComboBox ItemsSource=""\{Binding Languages\}"" SelectedItem=""\{Binding Language\}""\s+ItemTemplate=""\{StaticResource EnumItem\}"""),
            "the header has the language picker, its items named in the interface's language");
        var model = File.ReadAllText(Path.Combine(app, "ViewModels", "MainViewModel.cs"));
        Check.True(model.IndexOf("Localizer.Instance.Apply(_settings.Language)", StringComparison.Ordinal) is var apply and >= 0
                   && apply < model.IndexOf("About = new AboutViewModel", StringComparison.Ordinal),
            "the saved language is applied before the first text is built");

        // The values a picker offers, each named in both languages.
        var polish = AppXaml.Texts(AppXaml.AppResx(root, "pl"));
        foreach (var type in new[] { typeof(UiLanguage), typeof(CaptionMode), typeof(CaptionAnimation), typeof(HighlightEmphasis) })
        {
            var missing = Enum.GetNames(type).Select(n => $"Enum_{type.Name}_{n}").Where(k => !english.ContainsKey(k) || !polish.ContainsKey(k)).ToList();
            Check.True(missing.Count == 0, $"each {type.Name} has its name in a picker" + Listing("missing", missing));
        }

        // The presets are found by their English names, so each has one under its key.
        var presets = SubtitleStyle.Presets().Select(p => (p.Name, Key: "Preset_" + Lang.Slug(p.Name)))
            .Where(p => !english.TryGetValue(p.Key, out var text) || text != p.Name || !polish.ContainsKey(p.Key))
            .Select(p => p.Name).ToList();
        Check.True(presets.Count == 0, "each caption preset has its name in both languages, the English its own" + Listing("missing", presets));
        Check.Contains(window, "{l:TrValue Name, Prefix=Preset_}", "and the preset picker shows it");
    }

    private static void Code(string root)
    {
        var src = Path.Combine(root, "src");
        var sources = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(src, f), File.ReadAllText);

        // A plural's key is a string, not nameof: each must have its forms in its project's resources.
        var app = AppXaml.Texts(AppXaml.AppResx(root));
        var core = AppXaml.Texts(AppXaml.CoreResx(root));
        var plurals = sources.SelectMany(s => Regex.Matches(s.Value, @"\bPlural\(""([A-Za-z0-9_]+)""")
                .Select(m => (Key: m.Groups[1].Value, Texts: s.Key.StartsWith("ReelsMagician.App", StringComparison.Ordinal) ? app : core)))
            .ToList();
        var formless = plurals.Where(p => !p.Texts.ContainsKey(p.Key + "_one") || !p.Texts.ContainsKey(p.Key + "_other"))
            .Select(p => p.Key).Distinct().ToList();
        Check.True(plurals.Count > 5 && formless.Count == 0, $"each of the {plurals.Count} plurals the code counts has its forms" + Listing("formless", formless));

        // The log goes into bug reports as it is: no line of it is written in the interface's language.
        var logged = new Regex(@"(this|sink|_sink|progress)\.(Info|Warn|Error|Debug|Log)\([^;]*(Lang\.Ui|Strings\.|L\.Format|L\.Plural|Localizer)", RegexOptions.Singleline);
        var translatedLogs = sources.Where(s => logged.IsMatch(s.Value)).Select(s => s.Key).ToList();
        Check.True(translatedLogs.Count == 0, "the log stays English: no log line takes an interface text" + Listing("in", translatedLogs));
    }

    // Core's tables keep their English, which the log, the notices and the checks go by; the
    // resources hold the same English and the Polish beside it.
    private static void CoreTables()
    {
        var models = ModelCatalog.WhisperModels.Select(m => (m.Id, m.DisplayName, m.Notes))
            .Concat(ModelCatalog.LlmModels.Select(m => (m.Id, m.DisplayName, m.Notes))).ToList();
        var renamed = models.Where(m =>
                Strings.ResourceManager.GetString($"Model_{m.Id}_Name", CultureInfo.InvariantCulture) != m.DisplayName
                || Strings.ResourceManager.GetString($"Model_{m.Id}_Notes", CultureInfo.InvariantCulture) != m.Notes
                || Strings.ResourceManager.GetString($"Model_{m.Id}_Name", Polish) == m.DisplayName)
            .Select(m => m.Id).ToList();
        Check.True(renamed.Count == 0, $"each of the {models.Count} models has its name and notes in English as the catalogue has them, and in Polish"
            + Listing("different", renamed));

        var components = Licences.Components.Prepend(Licences.App).ToList();
        var purposes = components.Where(c =>
                Strings.ResourceManager.GetString(Licences.PurposeKey(c), CultureInfo.InvariantCulture) != c.Purpose
                || Strings.ResourceManager.GetString(Licences.PurposeKey(c), Polish) is null
                || (Strings.ResourceManager.GetString(Licences.LicenceKey(c), CultureInfo.InvariantCulture) is { } licence && licence != c.Licence)
                || (Strings.ResourceManager.GetString(Licences.NameKey(c), CultureInfo.InvariantCulture) is { } name && name != c.Name))
            .Select(c => c.Name).ToList();
        Check.True(purposes.Count == 0, $"each of the {components.Count} About rows has its purpose and licence in English as the table has them, and in Polish"
            + Listing("different", purposes));

        var groups = Enum.GetValues<LicenceGroup>()
            .Where(g => new[] { "Title", "Description" }.Any(p => Strings.ResourceManager.GetString($"LicenceGroup_{g}_{p}", Polish) is null))
            .ToList();
        Check.True(groups.Count == 0, "each licence group has its heading and description" + Listing("missing", groups.Select(g => g.ToString()).ToList()));

        // The caption preview's line, looked up by the animation's name.
        var animations = Enum.GetValues<CaptionAnimation>()
            .Where(a => Strings.ResourceManager.GetString($"Style_Animation_{a}", Polish) is null).Select(a => a.ToString()).ToList();
        Check.True(animations.Count == 0, "each caption animation is named in the preview's line" + Listing("missing", animations));
        var style = new SubtitleStyle
        {
            Mode = CaptionMode.WordHighlight, Emphasis = HighlightEmphasis.Grow, HighlightScale = 1.15,
            Animation = CaptionAnimation.FadeIn, FontName = "Arial", FontSize = 72, MaxWordsPerCaption = 3,
        };
        Check.Equal("word highlight · grow 1.15× · fadein · Arial 72px · 3 words/line", style.Describe(Lang.Log),
            "the preview's line reads in English as it always has");
        Check.Equal("podświetlanie słów · powiększenie 1,15× · pojawienie się · Arial 72 px · 3 słowa/wiersz",
            style.Describe(Lang.In(Polish)), "and in Polish with its own numbers and plural");
    }

    // The argument numbers a format string fills, such as {0} and {1:0.0}.
    private static HashSet<int> Holes(string text) =>
        Regex.Matches(text.Replace("{{", "").Replace("}}", ""), @"\{(\d+)[^}]*\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

    private static bool Formats(string text)
    {
        var holes = Holes(text);
        if (holes.Count == 0) return !text.Replace("{{", "").Replace("}}", "").Any(c => c is '{' or '}');
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, text, Enumerable.Repeat<object?>(1.5, holes.Max() + 1).ToArray());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Listing(string what, List<string> items) =>
        items.Count == 0 ? "" : $" ({what}: {string.Join(", ", items)})";
}
