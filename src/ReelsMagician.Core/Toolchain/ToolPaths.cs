namespace ReelsMagician.Core.Toolchain;

/// <summary>
/// Every path the app owns. Everything lives under %LOCALAPPDATA%\ReelsMagician so the app
/// stays fully self-contained and never needs an installer or admin rights.
/// </summary>
public sealed class ToolPaths
{
    public const string FolderName = "ReelsMagician";

    /// <summary>Written next to an installed whisper.cpp or llama.cpp, naming the build it came from.</summary>
    public const string BackendMarkerName = "reelsmagician-backend.txt";

    // The app was called Selfroller before it became ReelsMagician.
    private const string LegacyFolderName = "Selfroller";
    private const string LegacyBackendMarkerName = "selfroller-backend.txt";

    private static readonly Lazy<string> DefaultRoot = new(() =>
        ResolveRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    public ToolPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? DefaultRoot.Value;
        Tools = Path.Combine(Root, "tools");
        Models = Path.Combine(Root, "models");
        Cache = Path.Combine(Root, "cache");
        Downloads = Path.Combine(Root, "downloads");
        Projects = Path.Combine(Root, "projects");
        Logs = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public string Tools { get; }
    public string Models { get; }
    public string Cache { get; }
    public string Downloads { get; }
    public string Projects { get; }
    public string Logs { get; }

    public string FfmpegDir => Path.Combine(Tools, "ffmpeg");
    public string WhisperDir => Path.Combine(Tools, "whisper");
    public string LlamaDir => Path.Combine(Tools, "llama");
    public string YtDlpDir => Path.Combine(Tools, "yt-dlp");
    public string JsRuntimeDir => Path.Combine(Tools, "deno");

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ManifestFile => Path.Combine(Tools, "installed.json");

    /// <summary>
    /// The app's folder under <paramref name="parent"/>, moving it over from its old name first.
    ///
    /// An install from before the rename keeps gigabytes of tools and models in the old folder, and
    /// moving it is a cheap rename on the same volume. If something still holds a file open in
    /// there, the old folder is used as it is for this run rather than downloading everything again.
    /// </summary>
    public static string ResolveRoot(string parent)
    {
        var root = Path.Combine(parent, FolderName);
        var legacy = Path.Combine(parent, LegacyFolderName);
        if (Directory.Exists(root) || !Directory.Exists(legacy)) return root;

        var resolved = root;
        try { Directory.Move(legacy, root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { resolved = legacy; }

        var paths = new ToolPaths(resolved);
        foreach (var dir in new[] { paths.WhisperDir, paths.LlamaDir })
        {
            var marker = Path.Combine(dir, LegacyBackendMarkerName);
            try { if (File.Exists(marker)) File.Move(marker, Path.Combine(dir, BackendMarkerName)); }
            catch (IOException) { /* the marker only labels the backend on the Setup tab */ }
        }
        return resolved;
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Tools, Models, Cache, Downloads, Projects, Logs })
            Directory.CreateDirectory(d);
    }

    public string WorkDirFor(string projectId)
    {
        var dir = Path.Combine(Cache, projectId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Finds an executable inside a tool directory, searching nested folders.</summary>
    public static string? FindExe(string searchRoot, params string[] candidateNames)
    {
        if (!Directory.Exists(searchRoot)) return null;
        foreach (var name in candidateNames)
        {
            var direct = Path.Combine(searchRoot, name);
            if (File.Exists(direct)) return direct;
            var hit = Directory.EnumerateFiles(searchRoot, name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Looks an executable up on the system PATH.</summary>
    public static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }
}
