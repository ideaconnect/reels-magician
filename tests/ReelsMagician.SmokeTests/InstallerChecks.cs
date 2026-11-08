using System.Text.Json;
using System.Text.RegularExpressions;
using ReelsMagician.Core.About;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The Windows installer (issue #56). It is built by packaging/windows/build-installer.ps1 with
/// Velopack and checked on a Windows runner by check-installer.ps1; these checks hold the parts
/// that would go wrong without failing any build. Above all, the pack id names the folder
/// Setup.exe installs into, %LOCALAPPDATA%\&lt;pack id&gt;, which uninstalling deletes whole, so it
/// must never be the data folder that holds the tools, models and settings.
/// </summary>
public static class InstallerChecks
{
    public static void Run()
    {
        Check.Section("Windows installer: Velopack's pack id, versions, hook and release assets (issue #56)");

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine([root, .. parts])).ReplaceLineEndings("\n");

        var build = Read("packaging", "windows", "build-installer.ps1");
        var check = Read("packaging", "windows", "check-installer.ps1");
        var app = Read("src", "ReelsMagician.App", "ReelsMagician.App.csproj");
        var program = Read("src", "ReelsMagician.App", "Program.cs");
        var release = Read(".github", "workflows", "release.yml");
        var ci = Read(".github", "workflows", "ci.yml");

        PackId(build, check, Read("src", "ReelsMagician.Core", "Toolchain", "ToolPaths.cs"));
        Versions(root, app);
        Hook(program);
        Pack(build, app, Read("Directory.Build.props"));
        Workflows(build, release, ci);
    }

    /// <summary>The install folder is %LOCALAPPDATA%\&lt;pack id&gt;; the data folder is beside it, never it.</summary>
    private static void PackId(string build, string check, string toolPaths)
    {
        var packId = Regex.Match(build, @"^\$PackId = '([^']+)'", RegexOptions.Multiline).Groups[1].Value;
        Check.Equal("IDCT.ReelsMagician", packId, "the installer's pack id is IDCT.ReelsMagician");

        var legacy = Regex.Match(toolPaths, "LegacyFolderName = \"([^\"]+)\"").Groups[1].Value;
        Check.True(legacy.Length > 0, "ToolPaths names the data folder's old name");
        var dataFolders = new[] { ToolPaths.FolderName, legacy };
        Check.True(packId.Length > 0 && !dataFolders.Contains(packId, StringComparer.OrdinalIgnoreCase),
            $"so Setup.exe installs to %LOCALAPPDATA%\\{packId}, not into the data folder ({string.Join(" or ", dataFolders)}), " +
            "which uninstalling would delete");
        Check.True(packId.Length > 0 && !packId.Contains('/') && !packId.Contains('\\') && !packId.Contains(".."),
            "and the pack id is one folder name, so the install folder sits beside the data folder, not around or inside it");

        Check.Contains(build, "--packId $PackId", "build-installer.ps1 packs under that id");
        Check.Contains(check, $"$dataDir = Join-Path $local '{ToolPaths.FolderName}'",
            "check-installer.ps1 guards the app's real data folder through the install and the uninstall");
        Check.Contains(check, "$installDir = Join-Path $local $packId", "and looks for the install where the pack id puts it");
    }

    /// <summary>vpk builds what the Velopack library in the app talks to: their versions must match.</summary>
    private static void Versions(string root, string app)
    {
        var library = Regex.Match(app, "<PackageReference Include=\"Velopack\" Version=\"([^\"]+)\"").Groups[1].Value;
        Check.True(library.Length > 0, "the app references the Velopack package");

        var manifest = Path.Combine(root, ".config", "dotnet-tools.json");
        Check.True(File.Exists(manifest), "the repository pins vpk in .config/dotnet-tools.json");
        if (!File.Exists(manifest)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var tools = json.RootElement.GetProperty("tools");
        Check.True(tools.TryGetProperty("vpk", out var vpk), "the tool manifest lists vpk");
        if (!tools.TryGetProperty("vpk", out vpk)) return;
        Check.Equal(library, vpk.GetProperty("version").GetString(),
            $"vpk is at the Velopack package's version, {library}: the installer and the library are one release");
        Check.True(vpk.TryGetProperty("rollForward", out var roll) && roll.ValueKind == JsonValueKind.False,
            "and is not rolled forward to a newer one");
    }

    /// <summary>The installer runs the app with --veloapp-install and the like, and expects it to exit at once.</summary>
    private static void Hook(string program)
    {
        var main = Regex.Match(program, @"public static int Main\(string\[\] args\)\s*\{(.*?)\n    \}", RegexOptions.Singleline);
        Check.True(main.Success, "Program.cs has Main");
        if (!main.Success) return;
        var statements = Regex.Replace(main.Groups[1].Value, @"^\s*//.*$", "", RegexOptions.Multiline).Trim();
        var first = statements.Split('\n')[0].Trim();
        Check.Equal("if (OperatingSystem.IsWindows()) VelopackApp.Build().Run();", first,
            "Main's first statement is VelopackApp.Build().Run(), on Windows, before the Wayland check and Avalonia");
        Check.True(statements.IndexOf("HasWaylandSession", StringComparison.Ordinal) > 0
                   && statements.IndexOf("StartWithClassicDesktopLifetime", StringComparison.Ordinal) > 0,
            "and the Wayland check and Avalonia's start come after it");
    }

    /// <summary>What vpk is told: the app's exe and icon, its publisher, x64, no second portable zip.</summary>
    private static void Pack(string build, string app, string props)
    {
        var assembly = Regex.Match(app, "<AssemblyName>([^<]+)</AssemblyName>").Groups[1].Value;
        Check.Contains(build, $"--mainExe {assembly}.exe", $"vpk starts {assembly}.exe, the exe the app builds");
        Check.Contains(build, "--runtime win-x64", "the installer is built for x64, like the publish it packs");

        var icon = Regex.Match(app, @"<ApplicationIcon>Assets\\([^<]+)</ApplicationIcon>").Groups[1].Value;
        Check.True(icon.Length > 0 && build.Contains($"'Assets' '{icon}'", StringComparison.Ordinal) && build.Contains("--icon $icon"),
            $"Setup.exe gets the .exe's own icon, {icon}");

        var authors = Regex.Match(props, "<Authors>([^<]+)</Authors>").Groups[1].Value;
        Check.Equal(AppInfo.LegalName, authors, "Directory.Build.props names IDCT Bartosz Pachołek as the author");
        Check.True(build.Contains("Directory.Build.props") && build.Contains("--packAuthors $authors"),
            "and that is the publisher Apps & features shows for the installed app");

        Check.Contains(build, "--packTitle ReelsMagician", "Apps & features and the shortcuts call it ReelsMagician");
        Check.Contains(build, "--shortcuts 'StartMenuRoot,Desktop'", "Setup.exe adds a Start menu and a desktop shortcut");
        Check.Contains(build, "--noPortable", "Velopack's portable zip is left out: the plain zip stays");
        Check.Contains(build, "if ($Version -eq '0.0.0-dev') { '0.0.1-dev' }",
            $"a development build ({AppInfo.DevelopmentVersion}, below vpk's 0.0.1) gets an installer all the same");
    }

    /// <summary>Both workflows build and try the installer; a tag's draft carries it and what updates need.</summary>
    private static void Workflows(string build, string release, string ci)
    {
        var setup = Regex.Match(build, @"^\$SetupName = '([^']+)'", RegexOptions.Multiline).Groups[1].Value;
        Check.Equal("ReelsMagician-win-x64-Setup.exe", setup, "the installer is ReelsMagician-win-x64-Setup.exe, beside ReelsMagician-win-x64.zip");

        foreach (var (name, text) in new[] { ("release.yml", release), ("ci.yml", ci) })
        {
            var packs = text.IndexOf("packaging/windows/build-installer.ps1", StringComparison.Ordinal);
            var checks = text.IndexOf("packaging/windows/check-installer.ps1", StringComparison.Ordinal);
            Check.True(packs > 0 && checks > packs, $"{name} builds the installer, then installs, starts and uninstalls it");
        }

        var draft = Regex.Match(release, @"gh release create .*?(?=\n\n|\z)", RegexOptions.Singleline).Value;
        var assets = new[] { setup, "ReelsMagician-win-x64.zip", "*-full.nupkg", "releases.win.json", "ReelsMagician-x86_64.AppImage" };
        var missing = assets.Where(a => !draft.Contains("artifacts/" + a, StringComparison.Ordinal)).ToList();
        Check.True(missing.Count == 0, "a tag's draft release attaches the installer, the zip, the .nupkg and " +
            "releases.win.json that updates need, and the AppImage" + (missing.Count == 0 ? "" : $" (missing: {string.Join(", ", missing)})"));
        Check.True(release.Contains($"out/installer/{setup}") && release.Contains("out/installer/*-full.nupkg")
                   && release.Contains("out/installer/releases.win.json") && release.Contains("path: out/release/"),
            "and the Windows job hands all three on next to the zip");
    }
}
