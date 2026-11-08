using System.Text.Json;
using System.Text.RegularExpressions;
using ReelsMagician.Core.About;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The About tab (issue #52). Its licence rows live in one table, ReelsMagician.Core.About.Licences,
/// and these checks hold that table to what it describes: THIRD-PARTY-NOTICES.md lists the same
/// components in the same groups, every package the projects reference has a row, every licence
/// text kept in the repository is one a row names. Given a self-contained publish (the
/// <c>licences</c> mode, which CI runs on both systems), <see cref="RunOnPublish"/> holds it to every
/// package and licence file in that publish too, so a new dependency can't ship without a row.
/// The rest: the version as the tab shows it, the install states of the downloaded tools, the tab's
/// place in the strip, and the IDCT mark against its SVG.
/// </summary>
public static class LicenceChecks
{
    public static void Run()
    {
        Check.Section("About tab: the licence table, the version, the install states, the IDCT mark (issue #52)");

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        Table();
        Notices(root);
        ProjectPackages(root);
        RepositoryTexts(root);
        Versions(root);
        InstallStates();
        Reading();
        TabPlace(root);
        IdctMark(root);
    }

    private static IEnumerable<LicensedComponent> All => Licences.Components.Prepend(Licences.App);

    private static void Table()
    {
        var names = Licences.Components.Select(c => c.Name).ToList();
        Check.True(names.Distinct().Count() == names.Count, "every component has a row of its own");
        Check.True(All.All(c => c.Name.Length > 0 && c.Purpose.Length > 0 && c.Licence.Length > 0),
            "every row names the component, what it is for and its licence");
        Check.True(All.All(c => c.Source.StartsWith("https://", StringComparison.Ordinal)), "every row links to its source over https");

        var downloaded = Licences.Components.Where(c => c.Group is LicenceGroup.DownloadedByApp or LicenceGroup.DownloadedIfAgreed).ToList();
        Check.True(downloaded.All(c => c.Tool is not null && c.Texts.Count == 0 && c.Packages.Count == 0),
            "a downloaded row names its tool, whose install state the tab shows, and no text or package of a build");
        Check.True(Licences.Components.Except(downloaded).All(c => c.Tool is null && c.Texts.Count > 0),
            "a row of the app or its package names the licence texts the build carries for it");
        var tools = downloaded.Select(c => c.Tool!.Value).ToList();
        Check.True(tools.Order().SequenceEqual(Enum.GetValues<DownloadedTool>()),
            "each downloaded tool has exactly one row");
        Check.True(Licences.Components.Where(c => c.Group == LicenceGroup.DownloadedIfAgreed)
                .Select(c => c.Tool).Order().SequenceEqual([DownloadedTool.YtDlp, DownloadedTool.JsRuntime]),
            "only yt-dlp and Deno are downloaded just if the user agrees");

        Check.True(All.SelectMany(c => c.Texts).All(t => !t.Path.Contains('\\') && !t.Path.StartsWith('/') && !t.Path.Contains("..")),
            "licence paths are relative to the app's folder, with forward slashes");
        Check.True(All.SelectMany(c => c.Texts).Where(t => t.Origin == LicenceOrigin.Package)
                .All(t => t.Package is not null && t.Path.StartsWith($"licenses/{t.Package}/", StringComparison.Ordinal)),
            "a package's own licence sits in licenses/<package>/, where the publish target copies it");
        Check.True(All.SelectMany(c => c.Texts).Where(t => t.Package is not null)
                .All(t => All.Any(c => c.Packages.Contains(t.Package!) && c.Texts.Contains(t))),
            "each package text belongs to the row of its package");

        var windows = Licences.For(TargetOs.Windows).SelectMany(g => g).ToList();
        var linux = Licences.For(TargetOs.Linux).SelectMany(g => g).ToList();
        Check.True(windows.Any(c => c.Name.StartsWith("ANGLE")) && !linux.Any(c => c.Name.StartsWith("ANGLE")),
            "ANGLE, which only Windows builds carry, shows on Windows alone");
        Check.True(linux.Any(c => c.Name.StartsWith("AppImage")) && !windows.Any(c => c.Name.StartsWith("AppImage")),
            "the AppImage runtime shows on Linux alone");
        var skia = Licences.Components.First(c => c.Name.StartsWith("SkiaSharp"));
        Check.True(Licences.TextsFor(skia, TargetOs.Linux).All(t => !t.Path.Contains("Win32"))
                   && Licences.TextsFor(skia, TargetOs.Windows).All(t => !t.Path.Contains(".Linux")),
            "View licence shows a system's own native-library texts, not the other system's");
        var order = Licences.For(TargetOs.Linux).Select(g => g.Key).ToList();
        Check.True(order.SequenceEqual(Enum.GetValues<LicenceGroup>()),
            "the tab shows the groups in THIRD-PARTY-NOTICES.md's order: every build, the package, downloaded, if agreed");
    }

    /// <summary>The table and THIRD-PARTY-NOTICES.md list the same components, each in its group's section.</summary>
    private static void Notices(string root)
    {
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md")).ReplaceLineEndings("\n");
        var sections = Regex.Matches(notices, @"^## ([^\n]+)\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value.Trim(), m => m.Groups[2].Value);
        var groups = new Dictionary<string, LicenceGroup>
        {
            ["Copied into the source"] = LicenceGroup.EveryBuild,
            ["Restored by NuGet at build time"] = LicenceGroup.EveryBuild,
            ["Added by the release packages"] = LicenceGroup.ReleasePackage,
            ["Downloaded by the app at run time"] = LicenceGroup.DownloadedByApp,
            ["Downloaded only if the user agrees"] = LicenceGroup.DownloadedIfAgreed,
        };
        var missing = groups.Keys.Where(k => !sections.ContainsKey(k)).ToList();
        Check.True(missing.Count == 0, "THIRD-PARTY-NOTICES.md has a section for each group" + Listing("missing", missing));
        if (missing.Count > 0) return;

        // A table row of the notices: "| Component | Licence | Source |", not its head or rule.
        var lines = groups.SelectMany(g => sections[g.Key].Split('\n')
                .Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("|---", StringComparison.Ordinal)
                            && !Regex.IsMatch(l, @"^\| (Component|Package) \|"))
                .Select(l => (Group: g.Value, Line: l)))
            .ToList();

        var unlisted = Licences.Components
            .Where(c => !lines.Any(l => l.Group == c.Group && l.Line.Contains(c.Source, StringComparison.Ordinal)))
            .Select(c => c.Name).ToList();
        Check.True(unlisted.Count == 0,
            "THIRD-PARTY-NOTICES.md lists every row's source in its group's section" + Listing("not there", unlisted));
        var noRow = lines
            .Where(l => !Licences.Components.Any(c => c.Group == l.Group && l.Line.Contains(c.Source, StringComparison.Ordinal)))
            .Select(l => l.Line.Split('|')[1].Trim()).ToList();
        Check.True(noRow.Count == 0, "every component THIRD-PARTY-NOTICES.md lists has a row on the About tab" + Listing("no row", noRow));

        string Flat(string text) => Regex.Replace(text, @"\s+", " ");
        Check.Contains(Flat(notices), "not covered by the BSD 3-Clause License",
            "THIRD-PARTY-NOTICES.md says the names and logos are not under the BSD 3-Clause License");
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Check.Contains(Flat(readme), "not covered by the BSD 3-Clause License", "so does the README");

        // Font Awesome's credit names the version Icons.axaml was copied from.
        var icons = File.ReadAllText(Path.Combine(root, "src", "ReelsMagician.App", "Assets", "Icons.axaml"));
        var version = Regex.Match(icons, @"Font Awesome Free (\d+\.\d+\.\d+) icons").Groups[1].Value;
        var fontAwesome = Licences.Components.First(c => c.Source == "https://fontawesome.com");
        Check.True(version.Length > 0 && fontAwesome.Name.EndsWith(version, StringComparison.Ordinal)
                   && Licences.FontAwesomeAttribution.Contains($"Font Awesome Free {version} by @fontawesome", StringComparison.Ordinal)
                   && Licences.FontAwesomeAttribution.Contains("CC BY 4.0", StringComparison.Ordinal),
            $"the About tab credits Font Awesome Free {version}, the version the icons are, under CC BY 4.0");
    }

    /// <summary>Every package the app's projects reference directly has a row; a publish checks the rest.</summary>
    private static void ProjectPackages(string root)
    {
        var referenced = new[] { "ReelsMagician.App", "ReelsMagician.Core" }
            .SelectMany(p => Regex.Matches(File.ReadAllText(Path.Combine(root, "src", p, p + ".csproj")),
                "<PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .ToList();
        Check.True(referenced.Count > 0, "the projects reference packages");
        var noRow = referenced.Where(p => !All.Any(c => c.Packages.Contains(p))).ToList();
        Check.True(noRow.Count == 0, "every package the app and Core reference has a row" + Listing("no row", noRow));
    }

    /// <summary>The texts kept in the repository are there, and each one a row can show.</summary>
    private static void RepositoryTexts(string root)
    {
        var texts = All.SelectMany(c => c.Texts).Append(Licences.Notices).Distinct().ToList();
        var kept = texts.Where(t => t.Origin == LicenceOrigin.Repository).Select(t => t.Path).ToHashSet();
        var appImage = texts.Where(t => t.Origin == LicenceOrigin.AppImage).Select(t => t.Path).ToHashSet();

        var absent = kept.Where(p => !File.Exists(Path.Combine(root, p))).Order().ToList();
        Check.True(absent.Count == 0, "every licence text the table keeps in the repository exists" + Listing("missing", absent));
        var absentAppImage = appImage.Where(p => !File.Exists(Path.Combine(root, "packaging", "linux", p))).Order().ToList();
        Check.True(absentAppImage.Count == 0,
            "every AppImage licence text is in packaging/linux/licenses" + Listing("missing", absentAppImage));

        var inLicenses = Files(Path.Combine(root, "licenses")).Select(f => "licenses/" + f).ToList();
        var unnamed = inLicenses.Where(f => !kept.Contains(f)).ToList();
        Check.True(unnamed.Count == 0, "every text in licenses/ is one a row shows" + Listing("no row", unnamed));
        var inPackaging = Files(Path.Combine(root, "packaging", "linux", "licenses")).Select(f => "licenses/" + f).ToList();
        var unnamedAppImage = inPackaging.Where(f => !appImage.Contains(f)).ToList();
        Check.True(unnamedAppImage.Count == 0,
            "every text in packaging/linux/licenses is one the AppImage runtime's row shows" + Listing("no row", unnamedAppImage));
        Check.True(kept.Contains("LICENSE") && kept.Contains("THIRD-PARTY-NOTICES.md"),
            "the app's own licence and the notices file can be viewed");
    }

    private static void Versions(string root)
    {
        Check.Equal("0.2.0", AppInfo.DisplayVersion("0.2.0+7b16f3e0123456789abcdef"), "a release shows its version without the +commit suffix");
        Check.Equal("0.3.0-beta.1", AppInfo.DisplayVersion("0.3.0-beta.1+7b16f3e"), "a pre-release keeps its label");
        Check.Equal(AppInfo.DevelopmentBuild, AppInfo.DisplayVersion("0.0.0-dev+7b16f3e"), "CI's 0.0.0-dev shows as a development build");
        Check.Equal(AppInfo.DevelopmentBuild, AppInfo.DisplayVersion(null), "a build without a version shows as a development build");
        Check.Equal("Ubuntu 26.10 · x64 · .NET 10.0.14",
            AppInfo.DescribeSystem("Ubuntu 26.10 ", System.Runtime.InteropServices.Architecture.X64, ".NET 10.0.14"),
            "the system line names the system, the architecture and .NET");

        // A local build and CI's untagged builds say they are development builds, not a release.
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Check.Contains(props, $"<Version>{AppInfo.DevelopmentVersion}</Version>",
            $"a build not made from a release tag is {AppInfo.DevelopmentVersion}");
        var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Check.True(Regex.Matches(release, Regex.Escape(AppInfo.DevelopmentVersion)).Count >= 2,
            $"release.yml packages an untagged push as {AppInfo.DevelopmentVersion} on both systems");
        Check.True(Regex.Matches(release, @"-p:Version=\$version").Count >= 2, "and sets a tag's version with -p:Version= on both");
    }

    private static void InstallStates()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\Users\me\AppData\Local\ReelsMagician" : "/home/me/.local/share/ReelsMagician";
        string In(params string[] parts) => Path.Combine(parts.Prepend(root).ToArray());
        var elsewhere = OperatingSystem.IsWindows() ? @"C:\ffmpeg\bin\ffmpeg.exe" : "/usr/bin/ffmpeg";

        var none = new Toolset();
        Check.True(Enum.GetValues<DownloadedTool>().All(t => !DownloadedTerms.Describe(t, none, root).IsPresent),
            "with nothing installed, every downloaded component reads as not installed");
        Check.Equal(ComponentState.OptionalMissing, DownloadedTerms.Describe(DownloadedTool.YtDlp, none, root).State,
            "yt-dlp not downloaded is optional, not missing");

        var ours = new Toolset
        {
            FfmpegPath = In("tools", "ffmpeg", "bin", "ffmpeg"), FfprobePath = In("tools", "ffmpeg", "bin", "ffprobe"),
            WhisperCliPath = In("tools", "whisper", "whisper-cli"), WhisperBuild = BuildKind.Cuda,
            LlamaServerPath = In("tools", "llama", "llama-server"), LlamaBuild = BuildKind.Vulkan,
            YtDlpPath = In("tools", "yt-dlp", "yt-dlp"), LlmModelPath = In("models", "Qwen3-8B-Q4_K_M.gguf"),
        };
        var ffmpeg = DownloadedTerms.Describe(DownloadedTool.Ffmpeg, ours, root);
        Check.True(ffmpeg.State == ComponentState.Installed && ffmpeg.Text.Contains("GPL-3.0"),
            $"the app's ffmpeg is BtbN's GPL build, under GPL-3.0 ('{ffmpeg.Text}')");
        var whisper = DownloadedTerms.Describe(DownloadedTool.Whisper, ours, root);
        Check.True(whisper.IsPresent && whisper.Text.Contains("CUDA EULA"), $"a CUDA whisper.cpp brings NVIDIA's CUDA EULA ('{whisper.Text}')");
        var llama = DownloadedTerms.Describe(DownloadedTool.Llama, ours, root);
        Check.True(llama.IsPresent && !llama.Text.Contains("EULA") && llama.Text.Contains("Vulkan"),
            $"a Vulkan llama.cpp brings no CUDA EULA ('{llama.Text}')");
        var ytDlp = DownloadedTerms.Describe(DownloadedTool.YtDlp, ours, root);
        Check.True(ytDlp.State == ComponentState.Installed && ytDlp.Text.Contains("GPLv3+"),
            $"the app's yt-dlp is the standalone build, under GPLv3+ ('{ytDlp.Text}')");
        Check.True(DownloadedTerms.Describe(DownloadedTool.LlmModel, ours, root).Text.Contains("Qwen3-8B-Q4_K_M.gguf"),
            "an installed model names its file");

        var system = new Toolset { FfmpegPath = elsewhere, FfprobePath = elsewhere, YtDlpPath = elsewhere, WhisperCliPath = elsewhere };
        Check.Equal(ComponentState.System, DownloadedTerms.Describe(DownloadedTool.Ffmpeg, system, root).State,
            "an ffmpeg found on the system is not the app's download, so the GPL build's terms are not claimed");
        Check.True(!DownloadedTerms.Describe(DownloadedTool.Ffmpeg, system, root).Text.Contains("GPL-3.0 applies"),
            "and the tab does not say GPL-3.0 applies to it");
        Check.Equal(ComponentState.System, DownloadedTerms.Describe(DownloadedTool.YtDlp, system, root).State,
            "a yt-dlp on the PATH is the user's own");
        Check.Equal(ComponentState.System, DownloadedTerms.Describe(DownloadedTool.Whisper, system, root).State,
            "a whisper.cpp from settings.json, outside the data folder, is the user's own build");
        Check.True(!DownloadedTerms.IsUnder(root + "Old" + Path.DirectorySeparatorChar + "ffmpeg", root),
            "a folder whose name merely starts like the data folder's is not inside it");

        // Detect reads the build kind from the marker an install writes.
        var temp = Directory.CreateTempSubdirectory("rm-about-").FullName;
        try
        {
            var paths = new ToolPaths(temp);
            paths.EnsureCreated();
            Directory.CreateDirectory(paths.WhisperDir);
            Directory.CreateDirectory(paths.LlamaDir);
            File.WriteAllText(Path.Combine(paths.WhisperDir, ToolPaths.ExeName("whisper-cli")), "");
            File.WriteAllText(Path.Combine(paths.WhisperDir, ToolPaths.BackendMarkerName), "whisper-cublas-12.4.0-bin-x64.zip");
            File.WriteAllText(Path.Combine(paths.LlamaDir, ToolPaths.ExeName("llama-server")), "");
            File.WriteAllText(Path.Combine(paths.LlamaDir, ToolPaths.BackendMarkerName), "llama-b7000-bin-ubuntu-vulkan-x64.tar.gz");
            var detected = new ToolchainManager(paths, NullProgressSink.Instance).Detect(new AppSettings { PreferSystemFfmpeg = false });
            Check.Equal(BuildKind.Cuda, detected.WhisperBuild, "Detect reads an installed whisper.cpp's build from its marker");
            Check.Equal(BuildKind.Vulkan, detected.LlamaBuild, "and llama.cpp's");
            Check.True(DownloadedTerms.Describe(DownloadedTool.Whisper, detected, paths.Root).Text.Contains("CUDA EULA"),
                "so the tab names NVIDIA's CUDA EULA where a CUDA build was installed");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static void Reading()
    {
        var temp = Directory.CreateTempSubdirectory("rm-licences-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "licenses", "Here"));
            File.WriteAllText(Path.Combine(temp, "licenses", "Here", "LICENSE"), "MIT License");
            var read = Licences.Read(temp, [LicenceText.Kept("licenses/Here/LICENSE"), LicenceText.Kept("licenses/Gone/LICENSE")]);
            Check.True(read[0].Found && read[0].Text == "MIT License", "View licence reads a text from the app's folder");
            Check.True(!read[1].Found && read[1].Path == "licenses/Gone/LICENSE",
                "a text the build lacks comes back as not found, rather than failing");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>About is tab 7, after Cleanup, both on the right; the view model's constants agree.</summary>
    private static void TabPlace(string root)
    {
        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var window = AppXaml.Read(root, "MainWindow.axaml");
        var tabs = Regex.Matches(window, @"<TabItem (?:(?!>).)*Header=""([^""]+)""((?:(?!>).)*)>", RegexOptions.Singleline)
            .Select(m => (Header: m.Groups[1].Value, Tag: m.Value)).ToList();
        Check.Equal(8, tabs.Count, "the strip has six steps, Cleanup and About");
        Check.True(tabs.Count == 8 && tabs[6].Header == "Cleanup" && tabs[7].Header == "About",
            "About comes after Cleanup, last in tab order");
        Check.True(tabs.Skip(6).All(t => t.Tag.Contains("Classes=\"aside\"") && t.Tag.Contains("DockPanel.Dock=\"Right\"")),
            "both sit on the right, outlined and unnumbered, apart from the steps");
        Check.Contains(window, "<local:StepStripPanel />", "the strip lays them out in tab order, so About is rightmost");

        var vm = File.ReadAllText(Path.Combine(app, "ViewModels", "MainViewModel.cs"));
        Check.True(vm.Contains("CleanupTab = 6") && vm.Contains("AboutTab = 7"), "the view model's tab constants match");
    }

    /// <summary>The mark in the XAML is assets/idct-mark.svg: the same path, fill rule, colours and plate.</summary>
    private static void IdctMark(string root)
    {
        var svg = File.ReadAllText(Path.Combine(root, "assets", "idct-mark.svg"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "ReelsMagician.App", "Assets", "IdctMark.axaml"));
        var theme = File.ReadAllText(Path.Combine(root, "src", "ReelsMagician.App", "Assets", "Theme.axaml"));
        var window = File.ReadAllText(Path.Combine(root, "src", "ReelsMagician.App", "MainWindow.axaml"));

        var path = Regex.Match(svg, "<path[^>]* d=\"([^\"]+)\"");
        var geometry = Regex.Match(xaml, "<StreamGeometry x:Key=\"IdctMark\">([^<]+)</StreamGeometry>").Groups[1].Value;
        var evenOdd = Regex.IsMatch(svg, "<path[^>]* fill-rule=\"evenodd\"");
        Check.True(path.Success && geometry == (evenOdd ? "F0 " : "F1 ") + path.Groups[1].Value,
            "IdctMark.axaml holds the SVG's path unchanged, with the fill rule it asks for");

        string Brush(string key) => Regex.Match(theme, $"<SolidColorBrush x:Key=\"{key}\" Color=\"(#[0-9A-Fa-f]{{6}})\"").Groups[1].Value.ToUpperInvariant();
        var plate = Regex.Match(svg, "<rect[^>]* fill=\"(#[0-9A-Fa-f]{6})\"").Groups[1].Value.ToUpperInvariant();
        var amber = Regex.Match(svg, "<path[^>]* fill=\"(#[0-9A-Fa-f]{6})\"").Groups[1].Value.ToUpperInvariant();
        Check.True(plate.Length > 0 && Brush("IdctMarkPlate") == plate && amber.Length > 0 && Brush("IdctMarkAmber") == amber,
            $"the mark keeps the SVG's colours, {amber} on {plate}");

        var box = Regex.Match(svg, "viewBox=\"0 0 (\\d+) (\\d+)\"");
        var radius = Regex.Match(svg, "<rect[^>]* rx=\"(\\d+)\"").Groups[1].Value;
        Check.True(box.Success && window.Contains($"<Canvas Width=\"{box.Groups[1].Value}\" Height=\"{box.Groups[2].Value}\">")
                   && window.Contains($"CornerRadius=\"{radius}\" Background=\"{{DynamicResource IdctMarkPlate}}\""),
            "the About tab draws it on the SVG's canvas, with the plate's rounded corners");
    }

    /// <summary>
    /// A self-contained publish (dotnet publish -r RID --self-contained) against the table: every
    /// package in it has a row, every licence file a row names for it is there, and every licence
    /// file in it is one a row names, so View licence can show all of them.
    /// </summary>
    public static void RunOnPublish(string dir)
    {
        Check.Section($"Licences in the publish {dir} (issue #52)");
        var deps = Path.Combine(dir, "ReelsMagician.deps.json");
        Check.True(File.Exists(deps), "the folder is a publish of the app");
        if (!File.Exists(deps)) return;

        using var json = JsonDocument.Parse(File.ReadAllText(deps));
        var libraries = json.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => (Id: p.Name.Split('/')[0], Type: p.Value.GetProperty("type").GetString()))
            .ToList();
        var packages = libraries.Where(l => l.Type == "package").Select(l => l.Id)
            .Concat(libraries.Where(l => l.Type == "runtimepack").Select(l => l.Id["runtimepack.".Length..]))
            .ToHashSet();
        var runtime = packages.FirstOrDefault(p => p.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal));
        Check.True(runtime is not null, "the publish is self-contained, as both release packages are");
        if (runtime is null) return;
        var os = runtime.EndsWith("win-x64", StringComparison.Ordinal) ? TargetOs.Windows : TargetOs.Linux;
        Console.WriteLine($"  ({packages.Count} packages, a {os} publish)");

        var noRow = packages.Where(p => !All.Any(c => c.Packages.Contains(p))).Order().ToList();
        Check.True(noRow.Count == 0, "every package in the publish has a row on the About tab" + Listing("no row", noRow));

        var texts = All.SelectMany(c => c.Texts).Append(Licences.Notices).Distinct().ToList();
        var expected = texts.Where(t => t.Origin == LicenceOrigin.Repository
                                        || (t.Origin == LicenceOrigin.Package && packages.Contains(t.Package!)))
            .Select(t => t.Path).ToHashSet();
        var absent = expected.Where(p => !File.Exists(Path.Combine(dir, p))).Order().ToList();
        Check.True(absent.Count == 0, "every licence file a row names for this publish is in it" + Listing("missing", absent));

        var wrongSystem = texts.Where(t => t.Origin == LicenceOrigin.Package && packages.Contains(t.Package!)
                                           && t.OnlyOn is { } only && only != os).Select(t => t.Path).ToList();
        var notHere = texts.Where(t => t.Origin == LicenceOrigin.Package && t.OnlyOn == os && !packages.Contains(t.Package!))
            .Select(t => t.Path).ToList();
        Check.True(wrongSystem.Count == 0 && notHere.Count == 0,
            $"the texts marked for {os} are the ones its publish carries" + Listing("in it but marked for the other system", wrongSystem) +
            Listing("marked for it but not in it", notHere));

        var shown = Licences.For(os).SelectMany(g => g).ToList();
        var hidden = Licences.Components.Where(c => !c.AppliesTo(os) && c.Packages.Any(packages.Contains)).Select(c => c.Name).ToList();
        var absentRows = shown.Where(c => c.Packages.Count > 0 && !c.Packages.Any(packages.Contains))
            .Select(c => c.Name).ToList();
        Check.True(hidden.Count == 0 && absentRows.Count == 0,
            $"the rows the About tab shows on {os} are the components this publish has" + Listing("in it but hidden", hidden) +
            Listing("shown but not in it", absentRows));

        var files = Files(Path.Combine(dir, "licenses")).Select(f => "licenses/" + f).ToList();
        var named = texts.Select(t => t.Path).ToHashSet();
        var unnamed = files.Where(f => !named.Contains(f)).ToList();
        Check.True(files.Count > 0 && unnamed.Count == 0,
            $"every one of the publish's {files.Count} licence files is one a row shows" + Listing("no row", unnamed));
    }

    private static IEnumerable<string> Files(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal)
            : [];

    private static string Listing(string what, List<string> items) =>
        items.Count == 0 ? "" : $" ({what}: {string.Join(", ", items)})";
}
