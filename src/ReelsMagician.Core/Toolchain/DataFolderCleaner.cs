using ReelsMagician.Core.Diagnostics;

namespace ReelsMagician.Core.Toolchain;

/// <summary>One removable part of the app's data folder, with what it currently costs on disk.</summary>
public sealed record DataFolderItem(
    string Key,
    string Name,
    string Detail,
    string Path,
    long Bytes,
    bool Exists,
    bool SelectedByDefault)
{
    public string SizeText => Exists ? AssetDownloader.Humanize(Bytes) : "—";
}

public sealed record CleanupResult(long BytesFreed, int ItemsRemoved, IReadOnlyList<string> Failures)
{
    public bool Complete => Failures.Count == 0;
}

/// <summary>
/// Measures and clears what the app has downloaded into its own data folder.
///
/// Every path is checked to sit under that folder before anything is deleted. The check is not
/// ceremony: the categories are built from <see cref="ToolPaths"/>, and a future change there —
/// or a settings override pointing somewhere else — could otherwise turn a cleanup button into a
/// recursive delete of a directory the user cares about.
/// </summary>
public sealed class DataFolderCleaner(ToolPaths paths, IProgressSink sink)
{
    public const string ModelsKey = "models";
    public const string ToolsKey = "tools";
    public const string WorkingKey = "working";
    public const string LogsKey = "logs";
    public const string SettingsKey = "settings";

    /// <summary>What is on disk right now, largest first among the optional entries.</summary>
    public IReadOnlyList<DataFolderItem> Survey() =>
    [
        Describe(ModelsKey, "Downloaded models",
            "Whisper, Qwen and the face detector. The big one — several gigabytes.",
            paths.Models, selectedByDefault: false),

        Describe(ToolsKey, "Downloaded tools", ToolsDetail(), paths.Tools, selectedByDefault: false),

        Describe(WorkingKey, "Working files",
            "Extracted audio, sampled frames, clip intermediates and half-finished downloads. " +
            "Safe to clear at any time; the app recreates them.",
            paths.Cache, extraPaths: [paths.Downloads, paths.Projects], selectedByDefault: true),

        Describe(LogsKey, "Logs", "Error logs written when something went wrong.",
            paths.Logs, selectedByDefault: false),

        Describe(SettingsKey, "Settings",
            "Your model choices, output folder and recent sources. Removing this resets the app.",
            paths.SettingsFile, selectedByDefault: false),
    ];

    /// <summary>
    /// Names the tools actually on disk. yt-dlp and its runtime are downloaded only if the user
    /// agreed, so naming them unconditionally would claim a download that never happened.
    /// </summary>
    private string ToolsDetail()
    {
        var present = new (string Dir, string Name)[]
            {
                (paths.FfmpegDir, "ffmpeg"), (paths.WhisperDir, "whisper.cpp"), (paths.LlamaDir, "llama.cpp"),
                (paths.YtDlpDir, "yt-dlp"), (paths.JsRuntimeDir, "the JavaScript runtime"),
            }
            .Where(t => HasContent(t.Dir))
            .Select(t => t.Name)
            .ToList();

        return present.Count switch
        {
            0 => "ffmpeg, whisper.cpp and llama.cpp, once they are downloaded.",
            1 => present[0] + ".",
            _ => string.Join(", ", present[..^1]) + " and " + present[^1] + ".",
        };
    }

    private static bool HasContent(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception) { return false; }
    }

    private DataFolderItem Describe(
        string key, string name, string detail, string path,
        IReadOnlyList<string>? extraPaths = null, bool selectedByDefault = false)
    {
        var all = new List<string> { path };
        if (extraPaths is not null) all.AddRange(extraPaths);

        long bytes = 0;
        var exists = false;
        foreach (var p in all)
        {
            bytes += MeasureBytes(p);
            exists |= Directory.Exists(p) || File.Exists(p);
        }

        return new DataFolderItem(key, name, detail, path, bytes, exists, selectedByDefault);
    }

    /// <summary>Total size of a file or a directory tree, ignoring anything unreadable.</summary>
    public static long MeasureBytes(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception) { /* vanished or locked mid-scan */ }
            }
            return total;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Everything the data folder currently occupies.</summary>
    public long TotalBytes() => MeasureBytes(paths.Root);

    /// <summary>
    /// Deletes the selected categories. Anything still in use is reported rather than forced,
    /// so a running ffmpeg or llama-server produces a clear message instead of a half-deleted
    /// tools folder.
    /// </summary>
    public CleanupResult Remove(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var wanted = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        long freed = 0;
        var removed = 0;

        foreach (var item in Survey())
        {
            ct.ThrowIfCancellationRequested();
            if (!wanted.Contains(item.Key) || !item.Exists) continue;

            var targets = item.Key == WorkingKey
                ? new[] { paths.Cache, paths.Downloads, paths.Projects }
                : [item.Path];

            var before = targets.Sum(MeasureBytes);
            var ok = true;

            foreach (var target in targets)
            {
                if (!TryRemove(target, failures)) ok = false;
            }

            var after = targets.Sum(MeasureBytes);
            freed += Math.Max(0, before - after);
            if (ok) removed++;
        }

        // Keep the skeleton so the next run does not have to reason about missing directories.
        paths.EnsureCreated();

        sink.Info("cleanup",
            $"Freed {AssetDownloader.Humanize(freed)} from the data folder" +
            (failures.Count == 0 ? "." : $"; {failures.Count} item(s) were in use."));

        return new CleanupResult(freed, removed, failures);
    }

    private bool TryRemove(string path, List<string> failures)
    {
        if (!IsInsideDataFolder(path))
        {
            // Refuse rather than delete something outside the folder this button is meant to clear.
            failures.Add($"{path} is outside the data folder and was left alone.");
            sink.Warn("cleanup", $"Refused to delete {path}: outside {paths.Root}.");
            return false;
        }

        try
        {
            if (File.Exists(path)) { File.Delete(path); return true; }
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); return true; }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            sink.Warn("cleanup", $"Could not delete {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>True only when the path really sits inside the app's data folder.</summary>
    public bool IsInsideDataFolder(string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            // The root itself is not a valid target: this clears parts, never the whole tree.
            if (string.Equals(full, root, ToolPaths.PathComparison)) return false;

            // Ignoring case where the file system doesn't would let a differently cased path,
            // which on Linux is a different folder, pass as one inside the data folder.
            return full.StartsWith(root + Path.DirectorySeparatorChar, ToolPaths.PathComparison);
        }
        catch (Exception) { return false; }
    }
}
