using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.Core.Toolchain;

/// <summary>
/// Finds — and when missing, downloads and unpacks — every external dependency:
/// ffmpeg/ffprobe, yt-dlp, whisper.cpp, llama.cpp and the model weights.
///
/// Which build of each tool this machine gets is decided by <see cref="ToolBuilds"/>, per
/// Target OS and GPU. When the best build this machine supports cannot be found and a lesser
/// one is installed instead, that Fallback build is reported both during the install and on
/// the Setup tab afterwards, never silently.
///
/// yt-dlp and its JavaScript runtime are deliberately kept apart from all of this. They are
/// third-party tools that ReelsMagician neither includes nor installs by default, so
/// <see cref="EnsureAsync"/> never touches them and <see cref="InstallLinkToolsAsync"/> runs only
/// after the user has agreed to the download.
/// </summary>
public sealed class ToolchainManager(ToolPaths paths, IProgressSink sink)
{
    /// <summary>The <see cref="ComponentStatus.Key"/> of yt-dlp in <see cref="DescribeLinkTools"/>.</summary>
    public const string YtDlpKey = "ytdlp";

    /// <summary>The <see cref="ComponentStatus.Key"/> of the JavaScript runtime in <see cref="DescribeLinkTools"/>.</summary>
    public const string JsRuntimeKey = "jsruntime";

    public ToolPaths Paths => paths;

    /// <summary>Locates already-present tools without touching the network.</summary>
    public Toolset Detect(AppSettings settings)
    {
        paths.EnsureCreated();
        var ts = new Toolset { Gpu = GpuProbe.Detect() };

        // The app's own copies are made runnable again if they lost their execute bit. A folder
        // the user chose is theirs, and is left as it is.
        string[] own = [paths.FfmpegDir, paths.YtDlpDir, paths.JsRuntimeDir, paths.WhisperDir, paths.LlamaDir];
        string? Find(string dir, params string[] names)
        {
            var exe = ToolPaths.FindExe(dir, names);
            return exe is not null && own.Any(o => string.Equals(o, dir, ToolPaths.PathComparison))
                ? ToolPaths.Runnable(exe)
                : exe;
        }

        // ffmpeg: an explicit override wins, then our private copy, then a system install.
        var ffmpeg = ToolPaths.ExeName("ffmpeg");
        var ffprobe = ToolPaths.ExeName("ffprobe");
        foreach (var dir in new[] { settings.CustomFfmpegDir, paths.FfmpegDir })
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            ts.FfmpegPath ??= Find(dir, ffmpeg);
            ts.FfprobePath ??= Find(dir, ffprobe);
        }
        if (settings.PreferSystemFfmpeg && (ts.FfmpegPath is null || ts.FfprobePath is null))
        {
            ts.FfmpegPath ??= ToolPaths.FindOnPath(ffmpeg);
            ts.FfprobePath ??= ToolPaths.FindOnPath(ffprobe);
        }
        // ffprobe without ffmpeg (or the reverse) is unusable — treat the pair as absent.
        if (ts.FfmpegPath is null || ts.FfprobePath is null) { ts.FfmpegPath = null; ts.FfprobePath = null; }

        var ytDlp = ToolPaths.ExeName("yt-dlp");
        var deno = ToolPaths.ExeName("deno");
        ts.YtDlpPath = Find(paths.YtDlpDir, ytDlp) ?? ToolPaths.FindOnPath(ytDlp);
        ts.JsRuntimePath = Find(paths.JsRuntimeDir, deno) ?? ToolPaths.FindOnPath(deno);

        var os = ToolBuilds.Current;

        var whisperDir = Directory.Exists(settings.CustomWhisperDir ?? "") ? settings.CustomWhisperDir! : paths.WhisperDir;
        ts.WhisperCliPath = Find(whisperDir, ToolPaths.ExeName("whisper-cli"), ToolPaths.ExeName("main"));
        ts.WhisperBackend = DescribeBackend(whisperDir, ts.WhisperCliPath);
        if (ts.WhisperCliPath is not null && InstalledAsset(whisperDir) is { } whisperAsset)
            ts.WhisperWarning = ToolBuilds.FallbackWarning("whisper.cpp", whisperAsset, ToolBuilds.ExpectedWhisper(ts.Gpu, os));

        var llamaDir = Directory.Exists(settings.CustomLlamaDir ?? "") ? settings.CustomLlamaDir! : paths.LlamaDir;
        ts.LlamaServerPath = Find(llamaDir, ToolPaths.ExeName("llama-server"));
        ts.LlamaBackend = DescribeBackend(llamaDir, ts.LlamaServerPath);
        if (ts.LlamaServerPath is not null && InstalledAsset(llamaDir) is { } llamaAsset)
            ts.LlamaWarning = ToolBuilds.FallbackWarning("llama.cpp", llamaAsset, ToolBuilds.ExpectedLlama(ts.Gpu, os));

        var whisperModel = ModelCatalog.ResolveWhisper(settings.WhisperModelId);
        var whisperModelPath = Path.Combine(paths.Models, whisperModel.FileName);
        if (File.Exists(whisperModelPath)) ts.WhisperModelPath = whisperModelPath;

        var llm = ModelCatalog.ResolveLlm(settings.LlmModelId);
        var llmPath = Path.Combine(paths.Models, llm.FileName);
        if (File.Exists(llmPath)) ts.LlmModelPath = llmPath;

        var face = Path.Combine(paths.Models, ModelCatalog.FaceModelFileName);
        if (File.Exists(face)) ts.FaceModelPath = face;

        return ts;
    }

    /// <summary>The required components, for the setup screen.</summary>
    public IReadOnlyList<ComponentStatus> Describe(AppSettings settings, Toolset ts)
    {
        var whisperModel = ModelCatalog.ResolveWhisper(settings.WhisperModelId);
        var llm = ModelCatalog.ResolveLlm(settings.LlmModelId);
        var systemFfmpeg = ts.FfmpegPath is not null &&
            !ts.FfmpegPath.StartsWith(paths.Root, ToolPaths.PathComparison);
        var cuda = ToolBuilds.ExpectsLlamaCuda(ts.Gpu, ToolBuilds.Current);

        return
        [
            new("ffmpeg", "ffmpeg + ffprobe",
                ts.HasFfmpeg ? (systemFfmpeg ? ComponentState.System : ComponentState.Installed) : ComponentState.RequiredMissing,
                ts.FfmpegPath ?? "will download the BtbN GPL build", 95_000_000),

            new("whisper", "whisper.cpp",
                ts.WhisperCliPath is not null ? ComponentState.Installed : ComponentState.RequiredMissing,
                ts.WhisperCliPath is null ? "auto-selects the best build for this machine" : ts.WhisperBackend, 30_000_000,
                ts.WhisperWarning),

            // The CUDA build brings its own cudart and cuBLAS, which is most of its size.
            new("llama", "llama.cpp",
                ts.LlamaServerPath is not null ? ComponentState.Installed : ComponentState.RequiredMissing,
                ts.LlamaServerPath is null
                    ? (cuda ? "CUDA build for your NVIDIA card" : "Vulkan build when a driver is present")
                    : ts.LlamaBackend,
                cuda ? 600_000_000 : 130_000_000,
                ts.LlamaWarning),

            new("whisper-model", whisperModel.DisplayName,
                ts.WhisperModelPath is not null ? ComponentState.Installed : ComponentState.RequiredMissing,
                whisperModel.Notes, whisperModel.ApproxBytes),

            new("llm-model", llm.DisplayName,
                ts.LlmModelPath is not null ? ComponentState.Installed : ComponentState.RequiredMissing,
                llm.Notes, llm.ApproxBytes),

            new("face-model", "Face detector (YuNet)",
                ts.HasFaceModel ? ComponentState.Installed : ComponentState.RequiredMissing,
                "Keeps a speaker centred in the vertical crop", ModelCatalog.FaceModelApproxBytes),
        ];
    }

    /// <summary>The optional components that loading a video from a link needs.</summary>
    public IReadOnlyList<ComponentStatus> DescribeLinkTools(Toolset ts) =>
    [
        new(YtDlpKey, "yt-dlp",
            ts.HasYtDlp ? ComponentState.Installed : ComponentState.OptionalMissing,
            ts.YtDlpPath ?? "downloads a video from a link", 23_600_000),

        new(JsRuntimeKey, "JavaScript runtime (Deno)",
            ts.HasJsRuntime ? ComponentState.Installed : ComponentState.OptionalMissing,
            ts.JsRuntimePath ?? "without it yt-dlp drops formats and may miss 4K", 42_900_000),
    ];

    /// <summary>
    /// Downloads and installs whichever required components <see cref="Detect"/> could not find.
    /// Never fetches yt-dlp or its runtime; see <see cref="InstallLinkToolsAsync"/>.
    /// </summary>
    public async Task<Toolset> EnsureAsync(AppSettings settings, CancellationToken ct)
    {
        var final = await InstallAsync(settings, async (ts, dl) =>
        {
            var gpu = ts.Gpu ?? GpuProbe.Detect();
            sink.Info("install", $"GPU: {gpu.Describe()}");

            if (!ts.HasFfmpeg) await InstallFfmpegAsync(dl, ct).ConfigureAwait(false);
            if (ts.WhisperCliPath is null) await InstallWhisperAsync(dl, gpu, ct).ConfigureAwait(false);
            if (ts.LlamaServerPath is null) await InstallLlamaAsync(dl, gpu, ct).ConfigureAwait(false);

            if (ts.WhisperModelPath is null)
            {
                var m = ModelCatalog.ResolveWhisper(settings.WhisperModelId);
                await dl.DownloadAsync(m.Url, Path.Combine(paths.Models, m.FileName), m.DisplayName, ct).ConfigureAwait(false);
            }
            if (ts.LlmModelPath is null)
            {
                var m = ModelCatalog.ResolveLlm(settings.LlmModelId);
                await dl.DownloadAsync(m.Url, Path.Combine(paths.Models, m.FileName), m.DisplayName, ct).ConfigureAwait(false);
            }
            if (!ts.HasFaceModel)
            {
                await dl.DownloadAsync(ModelCatalog.FaceModelUrl,
                    Path.Combine(paths.Models, ModelCatalog.FaceModelFileName), "face detector", ct).ConfigureAwait(false);
                foreach (var retired in ModelCatalog.RetiredFaceModelFileNames)
                    TryDelete(Path.Combine(paths.Models, retired));
            }
        }).ConfigureAwait(false);

        sink.Report("install", "Toolchain ready", 1.0);
        sink.Info("install", $"Whisper backend: {final.WhisperBackend}");
        sink.Info("install", $"llama.cpp backend: {final.LlamaBackend}");
        if (final.WhisperWarning is { } whisperWarning) sink.Warn("install", whisperWarning);
        if (final.LlamaWarning is { } llamaWarning) sink.Warn("install", llamaWarning);
        if (!final.IsComplete)
            sink.Warn("install", "Still missing: " + string.Join(", ", final.MissingComponents()));
        return final;
    }

    /// <summary>
    /// Downloads yt-dlp and its JavaScript runtime from their publishers. Call this only once the
    /// user has agreed to it.
    /// </summary>
    public Task<Toolset> InstallLinkToolsAsync(AppSettings settings, CancellationToken ct) =>
        InstallAsync(settings, async (ts, dl) =>
        {
            if (!ts.HasYtDlp) await InstallYtDlpAsync(dl, ct).ConfigureAwait(false);
            if (!ts.HasJsRuntime) await InstallJsRuntimeAsync(dl, ct).ConfigureAwait(false);
        });

    /// <summary>
    /// What every install shares: the folders exist, what is already present is known, the
    /// downloads go through one client, and the result is detected afresh from disk.
    /// </summary>
    private async Task<Toolset> InstallAsync(AppSettings settings, Func<Toolset, AssetDownloader, Task> install)
    {
        paths.EnsureCreated();
        var present = Detect(settings);
        using var http = AssetDownloader.CreateHttpClient();
        await install(present, new AssetDownloader(http, sink)).ConfigureAwait(false);
        return Detect(settings);
    }

    private async Task InstallFfmpegAsync(AssetDownloader dl, CancellationToken ct)
    {
        var url = ToolBuilds.FfmpegUrl(ToolBuilds.Current);
        var archive = Path.Combine(paths.Downloads, Path.GetFileName(url));
        await dl.DownloadAsync(url, archive, "ffmpeg", ct).ConfigureAwait(false);
        ArchiveExtractor.Extract(archive, paths.FfmpegDir, sink, "ffmpeg");
        TryDelete(archive);
    }

    private async Task InstallYtDlpAsync(AssetDownloader dl, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.YtDlpDir);
        var target = Path.Combine(paths.YtDlpDir, ToolPaths.ExeName("yt-dlp"));
        await dl.DownloadAsync(ToolBuilds.YtDlpUrl(ToolBuilds.Current), target, "yt-dlp", ct).ConfigureAwait(false);
        ToolPaths.MakeExecutable(target);
    }

    /// <summary>
    /// Installs Deno for yt-dlp. Downloads fail softly: a missing runtime only degrades some
    /// compatible video links, so it must never stop the rest of the toolchain from installing.
    /// </summary>
    private async Task InstallJsRuntimeAsync(AssetDownloader dl, CancellationToken ct)
    {
        try
        {
            var url = ToolBuilds.DenoUrl(ToolBuilds.Current);
            var zip = Path.Combine(paths.Downloads, Path.GetFileName(url));
            await dl.DownloadAsync(url, zip, "JavaScript runtime (Deno)", ct).ConfigureAwait(false);
            ArchiveExtractor.Extract(zip, paths.JsRuntimeDir, sink, "Deno");
            if (ToolPaths.FindExe(paths.JsRuntimeDir, ToolPaths.ExeName("deno")) is { } deno) ToolPaths.MakeExecutable(deno);
            TryDelete(zip);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sink.Warn("install",
                $"Could not install the JavaScript runtime ({ex.Message}). Compatible video links will " +
                "still download, but yt-dlp may not see every format.");
        }
    }

    private async Task InstallWhisperAsync(AssetDownloader dl, GpuCapabilities gpu, CancellationToken ct)
    {
        var os = ToolBuilds.Current;
        var (fallbackTag, fallbackAsset) = ToolBuilds.WhisperFallback(os);
        var picked = await ResolveAsync(dl, ToolBuilds.WhisperRepo, ToolBuilds.WhisperRules(gpu, os), 20, ct).ConfigureAwait(false)
            ?? (fallbackTag,
                $"https://github.com/{ToolBuilds.WhisperRepo}/releases/download/{fallbackTag}/{fallbackAsset}",
                fallbackAsset);

        sink.Info("install", $"whisper.cpp {picked.Tag}: {picked.AssetName}");
        var archive = Path.Combine(paths.Downloads, picked.AssetName);
        await dl.DownloadAsync(picked.Url, archive, $"whisper.cpp ({picked.Tag})", ct).ConfigureAwait(false);
        ArchiveExtractor.Extract(archive, paths.WhisperDir, sink, "whisper.cpp");
        File.WriteAllText(Path.Combine(paths.WhisperDir, ToolPaths.BackendMarkerName), picked.AssetName);
        TryDelete(archive);
    }

    private async Task InstallLlamaAsync(AssetDownloader dl, GpuCapabilities gpu, CancellationToken ct)
    {
        var os = ToolBuilds.Current;
        var (fallbackTag, fallbackAsset) = ToolBuilds.LlamaFallback(os);
        var picked = await ResolveAsync(dl, ToolBuilds.LlamaRepo, ToolBuilds.LlamaRules(gpu, os), 6, ct).ConfigureAwait(false)
            ?? (fallbackTag,
                $"https://github.com/{ToolBuilds.LlamaRepo}/releases/download/{fallbackTag}/{fallbackAsset}",
                fallbackAsset);

        sink.Info("install", $"llama.cpp {picked.Tag}: {picked.AssetName}");
        var archive = Path.Combine(paths.Downloads, picked.AssetName);
        await dl.DownloadAsync(picked.Url, archive, $"llama.cpp ({picked.Tag})", ct).ConfigureAwait(false);

        // A CUDA build loads cudart and cuBLAS from its own folder, and without them llama.cpp
        // quietly runs on the CPU. Both archives are downloaded before either is unpacked, so an
        // interrupted install never leaves the build without its runtime.
        string? runtimeZip = null;
        if (ToolBuilds.LlamaCudaVersion(picked.AssetName) is { } cuda)
        {
            var runtime = ToolBuilds.LlamaCudaRuntimeAsset(cuda);
            runtimeZip = Path.Combine(paths.Downloads, runtime);
            await dl.DownloadAsync(
                $"https://github.com/{ToolBuilds.LlamaRepo}/releases/download/{picked.Tag}/{runtime}",
                runtimeZip, $"CUDA {cuda} runtime", ct).ConfigureAwait(false);
        }

        ArchiveExtractor.Extract(archive, paths.LlamaDir, sink, "llama.cpp");
        if (runtimeZip is not null)
        {
            ArchiveExtractor.Extract(runtimeZip, paths.LlamaDir, sink, "CUDA runtime", merge: true);
            TryDelete(runtimeZip);
        }
        File.WriteAllText(Path.Combine(paths.LlamaDir, ToolPaths.BackendMarkerName), picked.AssetName);
        TryDelete(archive);
    }

    private async Task<(string Tag, string Url, string AssetName)?> ResolveAsync(
        AssetDownloader dl, string repo, IReadOnlyList<Func<string, bool>> rules, int pageSize, CancellationToken ct)
    {
        try
        {
            var json = await dl.GetStringAsync(
                $"https://api.github.com/repos/{repo}/releases?per_page={pageSize}", ct).ConfigureAwait(false);
            var releases = GitHubReleases.Parse(json);
            var hit = GitHubReleases.SelectAsset(releases, rules);
            if (hit is { } h) return (h.Release.TagName, h.Asset.Url, h.Asset.Name);
            sink.Warn("install", $"{repo}: no build for this system in the {pageSize} newest releases; using the pinned fallback build.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sink.Warn("install", $"{repo}: release lookup failed ({ex.Message}); using the pinned fallback build.");
        }
        return null;
    }

    /// <summary>The asset an installed whisper.cpp or llama.cpp came from, as its marker records it.</summary>
    private static string? InstalledAsset(string dir)
    {
        var marker = Path.Combine(dir, ToolPaths.BackendMarkerName);
        try { return File.Exists(marker) && File.ReadAllText(marker).Trim() is { Length: > 0 } a ? a : null; }
        catch (IOException) { return null; }
    }

    private static string DescribeBackend(string dir, string? exePath)
    {
        if (exePath is null) return "not installed";
        var kind = InstalledAsset(dir) is { } asset
            ? ToolBuilds.Label(ToolBuilds.KindOf(asset))
            : HasVulkanRuntime(dir) ? "Vulkan (GPU)" : "custom build";
        return $"{kind} — {Path.GetFileName(exePath)}";
    }

    private static bool HasVulkanRuntime(string dir)
    {
        // ggml-vulkan.dll on Windows, libggml-vulkan.so (sometimes versioned) on Linux.
        var pattern = OperatingSystem.IsWindows() ? "*vulkan*.dll" : "*vulkan*.so*";
        try
        {
            return Directory.Exists(dir) &&
                   Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Any();
        }
        catch (IOException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* leave it behind */ }
    }
}
