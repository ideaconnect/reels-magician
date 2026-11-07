using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.Core.Toolchain;

/// <summary>
/// Finds — and when missing, downloads and unpacks — every external dependency:
/// ffmpeg/ffprobe, yt-dlp, whisper.cpp, llama.cpp and the model weights.
///
/// Backend selection is GPU-aware. llama.cpp publishes official Windows CUDA and Vulkan builds:
/// an NVIDIA card gets CUDA, which uses it fully, and every other GPU gets Vulkan, so the Qwen
/// model runs on the GPU on any vendor. whisper.cpp currently publishes only CPU,
/// OpenBLAS and cuBLAS Windows builds, so it falls back in that order — but the preference
/// list is matched by pattern, so a Vulkan asset is adopted automatically if one appears,
/// and <see cref="AppSettings.CustomWhisperDir"/> lets a hand-built Vulkan binary be used today.
///
/// yt-dlp and its JavaScript runtime are deliberately kept apart from all of this. They are
/// third-party tools that ReelsMagician neither includes nor installs by default, so
/// <see cref="EnsureAsync"/> never touches them and <see cref="InstallLinkToolsAsync"/> runs only
/// after the user has agreed to the download.
/// </summary>
public sealed class ToolchainManager(ToolPaths paths, IProgressSink sink)
{
    private const string WhisperRepo = "ggml-org/whisper.cpp";
    private const string LlamaRepo = "ggml-org/llama.cpp";
    private const string FfmpegZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    // yt-dlp needs a JavaScript runtime to extract YouTube formats; without one it warns that
    // extraction is deprecated and quietly drops formats, sometimes every 4K variant.
    private const string DenoUrl =
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    // Used when the GitHub API is unreachable or rate limited (60 anonymous calls/hour).
    private const string WhisperFallbackTag = "v1.9.2";
    private const string LlamaFallbackTag = "b10760";

    public ToolPaths Paths => paths;

    /// <summary>Locates already-present tools without touching the network.</summary>
    public Toolset Detect(AppSettings settings)
    {
        paths.EnsureCreated();
        var ts = new Toolset { Gpu = GpuProbe.Detect() };

        // ffmpeg: an explicit override wins, then our private copy, then a system install.
        foreach (var dir in new[] { settings.CustomFfmpegDir, paths.FfmpegDir })
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            ts.FfmpegPath ??= ToolPaths.FindExe(dir, "ffmpeg.exe");
            ts.FfprobePath ??= ToolPaths.FindExe(dir, "ffprobe.exe");
        }
        if (settings.PreferSystemFfmpeg && (ts.FfmpegPath is null || ts.FfprobePath is null))
        {
            ts.FfmpegPath ??= ToolPaths.FindOnPath("ffmpeg.exe");
            ts.FfprobePath ??= ToolPaths.FindOnPath("ffprobe.exe");
        }
        // ffprobe without ffmpeg (or the reverse) is unusable — treat the pair as absent.
        if (ts.FfmpegPath is null || ts.FfprobePath is null) { ts.FfmpegPath = null; ts.FfprobePath = null; }

        ts.YtDlpPath = ToolPaths.FindExe(paths.YtDlpDir, "yt-dlp.exe") ?? ToolPaths.FindOnPath("yt-dlp.exe");
        ts.JsRuntimePath = ToolPaths.FindExe(paths.JsRuntimeDir, "deno.exe") ?? ToolPaths.FindOnPath("deno.exe");

        var whisperDir = Directory.Exists(settings.CustomWhisperDir ?? "") ? settings.CustomWhisperDir! : paths.WhisperDir;
        ts.WhisperCliPath = ToolPaths.FindExe(whisperDir, "whisper-cli.exe", "main.exe");
        ts.WhisperBackend = DescribeBackend(whisperDir, ts.WhisperCliPath);

        var llamaDir = Directory.Exists(settings.CustomLlamaDir ?? "") ? settings.CustomLlamaDir! : paths.LlamaDir;
        ts.LlamaServerPath = ToolPaths.FindExe(llamaDir, "llama-server.exe");
        ts.LlamaBackend = DescribeBackend(llamaDir, ts.LlamaServerPath);

        var whisperModel = ModelCatalog.ResolveWhisper(settings.WhisperModelId);
        var whisperModelPath = Path.Combine(paths.Models, whisperModel.FileName);
        if (File.Exists(whisperModelPath)) ts.WhisperModelPath = whisperModelPath;

        var llm = ModelCatalog.ResolveLlm(settings.LlmModelId);
        var llmPath = Path.Combine(paths.Models, llm.FileName);
        if (File.Exists(llmPath)) ts.LlmModelPath = llmPath;

        var proto = Path.Combine(paths.Models, ModelCatalog.FaceProtoFileName);
        if (File.Exists(proto)) ts.FaceProtoPath = proto;
        var weights = Path.Combine(paths.Models, ModelCatalog.FaceWeightsFileName);
        if (File.Exists(weights)) ts.FaceWeightsPath = weights;

        return ts;
    }

    /// <summary>The required components, for the setup screen.</summary>
    public IReadOnlyList<ComponentStatus> Describe(AppSettings settings, Toolset ts)
    {
        var whisperModel = ModelCatalog.ResolveWhisper(settings.WhisperModelId);
        var llm = ModelCatalog.ResolveLlm(settings.LlmModelId);
        var systemFfmpeg = ts.FfmpegPath is not null &&
            !ts.FfmpegPath.StartsWith(paths.Root, StringComparison.OrdinalIgnoreCase);

        return
        [
            new("ffmpeg", "ffmpeg + ffprobe",
                ts.HasFfmpeg ? (systemFfmpeg ? ComponentState.System : ComponentState.Installed) : ComponentState.Missing,
                ts.FfmpegPath ?? "will download the BtbN GPL build", 95_000_000),

            new("whisper", "whisper.cpp",
                ts.WhisperCliPath is not null ? ComponentState.Installed : ComponentState.Missing,
                ts.WhisperCliPath is null ? "auto-selects the best Windows build" : ts.WhisperBackend, 30_000_000),

            // The CUDA build brings its own cudart and cuBLAS, which is most of its size.
            new("llama", "llama.cpp",
                ts.LlamaServerPath is not null ? ComponentState.Installed : ComponentState.Missing,
                ts.LlamaServerPath is null
                    ? (ExpectsLlamaCuda(ts.Gpu) ? "CUDA build for your NVIDIA card" : "Vulkan build when a driver is present")
                    : ts.LlamaBackend,
                ExpectsLlamaCuda(ts.Gpu) ? 600_000_000 : 130_000_000),

            new("whisper-model", whisperModel.DisplayName,
                ts.WhisperModelPath is not null ? ComponentState.Installed : ComponentState.Missing,
                whisperModel.Notes, whisperModel.ApproxBytes),

            new("llm-model", llm.DisplayName,
                ts.LlmModelPath is not null ? ComponentState.Installed : ComponentState.Missing,
                llm.Notes, llm.ApproxBytes),

            new("face-model", "Face detector (OpenCV SSD)",
                ts.HasFaceModel ? ComponentState.Installed : ComponentState.Missing,
                "Keeps a speaker centred in the vertical crop", ModelCatalog.FaceModelApproxBytes),
        ];
    }

    /// <summary>The optional components that loading a video from a link needs.</summary>
    public IReadOnlyList<ComponentStatus> DescribeLinkTools(Toolset ts) =>
    [
        new("ytdlp", "yt-dlp",
            ts.HasYtDlp ? ComponentState.Installed : ComponentState.NotInstalled,
            ts.YtDlpPath ?? "downloads a video from a link", 23_600_000),

        new("jsruntime", "JavaScript runtime (Deno)",
            ts.HasJsRuntime ? ComponentState.Installed : ComponentState.NotInstalled,
            ts.JsRuntimePath ?? "without it yt-dlp drops formats and may miss 4K", 42_900_000),
    ];

    /// <summary>
    /// Downloads and installs whichever required components <see cref="Detect"/> could not find.
    /// Never fetches yt-dlp or its runtime; see <see cref="InstallLinkToolsAsync"/>.
    /// </summary>
    public async Task<Toolset> EnsureAsync(AppSettings settings, CancellationToken ct)
    {
        paths.EnsureCreated();
        var ts = Detect(settings);
        var gpu = ts.Gpu ?? GpuProbe.Detect();
        sink.Info("install", $"GPU: {gpu.Describe()}");

        using var http = AssetDownloader.CreateHttpClient();
        var dl = new AssetDownloader(http, sink);

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
            await dl.DownloadAsync(ModelCatalog.FaceProtoUrl,
                Path.Combine(paths.Models, ModelCatalog.FaceProtoFileName), "face detector graph", ct).ConfigureAwait(false);
            await dl.DownloadAsync(ModelCatalog.FaceWeightsUrl,
                Path.Combine(paths.Models, ModelCatalog.FaceWeightsFileName), "face detector weights", ct).ConfigureAwait(false);
        }

        var final = Detect(settings);
        sink.Report("install", "Toolchain ready", 1.0);
        sink.Info("install", $"Whisper backend: {final.WhisperBackend}");
        sink.Info("install", $"llama.cpp backend: {final.LlamaBackend}");
        if (!final.IsComplete)
            sink.Warn("install", "Still missing: " + string.Join(", ", final.MissingComponents()));
        return final;
    }

    /// <summary>
    /// Downloads yt-dlp and its JavaScript runtime from their publishers. Call this only once the
    /// user has agreed to it.
    /// </summary>
    public async Task<Toolset> InstallLinkToolsAsync(AppSettings settings, CancellationToken ct)
    {
        paths.EnsureCreated();
        var ts = Detect(settings);

        using var http = AssetDownloader.CreateHttpClient();
        var dl = new AssetDownloader(http, sink);

        if (!ts.HasYtDlp) await InstallYtDlpAsync(dl, ct).ConfigureAwait(false);
        if (!ts.HasJsRuntime) await InstallJsRuntimeAsync(dl, ct).ConfigureAwait(false);

        return Detect(settings);
    }

    private async Task InstallFfmpegAsync(AssetDownloader dl, CancellationToken ct)
    {
        var zip = Path.Combine(paths.Downloads, "ffmpeg-win64-gpl.zip");
        await dl.DownloadAsync(FfmpegZipUrl, zip, "ffmpeg", ct).ConfigureAwait(false);
        ArchiveExtractor.ExtractZip(zip, paths.FfmpegDir, sink, "ffmpeg");
        TryDelete(zip);
    }

    private async Task InstallYtDlpAsync(AssetDownloader dl, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.YtDlpDir);
        await dl.DownloadAsync(YtDlpUrl, Path.Combine(paths.YtDlpDir, "yt-dlp.exe"), "yt-dlp", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs Deno for yt-dlp. Downloads fail softly: a missing runtime only degrades YouTube
    /// extraction, so it must never stop the rest of the toolchain from installing.
    /// </summary>
    private async Task InstallJsRuntimeAsync(AssetDownloader dl, CancellationToken ct)
    {
        try
        {
            var zip = Path.Combine(paths.Downloads, "deno-win-x64.zip");
            await dl.DownloadAsync(DenoUrl, zip, "JavaScript runtime (Deno)", ct).ConfigureAwait(false);
            ArchiveExtractor.ExtractZip(zip, paths.JsRuntimeDir, sink, "Deno");
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
        // Preference order. Vulkan is listed first so a future official build is adopted with no code change.
        var rules = new List<Func<string, bool>>();
        if (gpu.VulkanAvailable) rules.Add(n => n.Contains("vulkan") && n.Contains("x64") && n.EndsWith(".zip"));
        if (gpu.HasNvidia) rules.Add(n => n.Contains("cublas") && n.Contains("12.") && n.Contains("bin-x64.zip"));
        rules.Add(n => n == "whisper-blas-bin-x64.zip");
        rules.Add(n => n == "whisper-bin-x64.zip");

        var picked = await ResolveAsync(dl, WhisperRepo, rules, 20, ct).ConfigureAwait(false)
            ?? (WhisperFallbackTag,
                $"https://github.com/{WhisperRepo}/releases/download/{WhisperFallbackTag}/whisper-blas-bin-x64.zip",
                "whisper-blas-bin-x64.zip");

        sink.Info("install", $"whisper.cpp {picked.Tag}: {picked.AssetName}");
        var zip = Path.Combine(paths.Downloads, picked.AssetName);
        await dl.DownloadAsync(picked.Url, zip, $"whisper.cpp ({picked.Tag})", ct).ConfigureAwait(false);
        ArchiveExtractor.ExtractZip(zip, paths.WhisperDir, sink, "whisper.cpp");
        File.WriteAllText(Path.Combine(paths.WhisperDir, ToolPaths.BackendMarkerName), picked.AssetName);
        TryDelete(zip);
    }

    private async Task InstallLlamaAsync(AssetDownloader dl, GpuCapabilities gpu, CancellationToken ct)
    {
        // The pinned fallback is the Vulkan build, because it runs on every vendor's GPU.
        var picked = await ResolveAsync(dl, LlamaRepo, LlamaRules(gpu), 6, ct).ConfigureAwait(false)
            ?? (LlamaFallbackTag,
                $"https://github.com/{LlamaRepo}/releases/download/{LlamaFallbackTag}/llama-{LlamaFallbackTag}-bin-win-vulkan-x64.zip",
                $"llama-{LlamaFallbackTag}-bin-win-vulkan-x64.zip");

        sink.Info("install", $"llama.cpp {picked.Tag}: {picked.AssetName}");
        var zip = Path.Combine(paths.Downloads, picked.AssetName);
        await dl.DownloadAsync(picked.Url, zip, $"llama.cpp ({picked.Tag})", ct).ConfigureAwait(false);

        // A CUDA build loads cudart and cuBLAS from its own folder, and without them llama.cpp
        // quietly runs on the CPU. Both archives are downloaded before either is unpacked, so an
        // interrupted install never leaves the build without its runtime.
        string? runtimeZip = null;
        if (LlamaCudaVersion(picked.AssetName) is { } cuda)
        {
            var runtime = LlamaCudaRuntimeAsset(cuda);
            runtimeZip = Path.Combine(paths.Downloads, runtime);
            await dl.DownloadAsync(
                $"https://github.com/{LlamaRepo}/releases/download/{picked.Tag}/{runtime}",
                runtimeZip, $"CUDA {cuda} runtime", ct).ConfigureAwait(false);
        }

        ArchiveExtractor.ExtractZip(zip, paths.LlamaDir, sink, "llama.cpp");
        if (runtimeZip is not null)
        {
            ArchiveExtractor.ExtractZip(runtimeZip, paths.LlamaDir, sink, "CUDA runtime", merge: true);
            TryDelete(runtimeZip);
        }
        File.WriteAllText(Path.Combine(paths.LlamaDir, ToolPaths.BackendMarkerName), picked.AssetName);
        TryDelete(zip);
    }

    // The CUDA versions llama.cpp publishes Windows builds for, newest first, with the oldest
    // card each still supports: CUDA 13 dropped Maxwell, Pascal and Volta.
    private static readonly (int Major, Version MinCompute)[] LlamaCudaLines = [(13, new(7, 5)), (12, new(5, 0))];

    /// <summary>
    /// llama.cpp builds in order of preference for this machine. An NVIDIA card gets the newest
    /// CUDA build its driver and card can run; any other GPU, or an NVIDIA setup too old for
    /// every CUDA build, gets Vulkan when a driver is present and the CPU build otherwise.
    /// </summary>
    public static IReadOnlyList<Func<string, bool>> LlamaRules(GpuCapabilities gpu)
    {
        var rules = new List<Func<string, bool>>();
        if (gpu.Cuda is { } support)
        {
            foreach (var (major, minCompute) in LlamaCudaLines)
            {
                if (support.LowestComputeCapability < minCompute) continue;
                rules.Add(n => LlamaCudaVersion(n) is { } v && v.Major == major && support.CanRun(v));
            }
        }
        if (gpu.VulkanAvailable) rules.Add(n => n.Contains("bin-win-vulkan-x64.zip"));
        rules.Add(n => n.Contains("bin-win-cpu-x64.zip"));
        return rules;
    }

    /// <summary>The CUDA version of a llama.cpp CUDA build, or null for any other asset.</summary>
    public static Version? LlamaCudaVersion(string assetName)
    {
        const string marker = "-bin-win-cuda-", suffix = "-x64.zip";
        var name = assetName.ToLowerInvariant();

        // The cudart-llama-… runtime archives share the pattern but hold no llama-server.
        if (!name.StartsWith("llama-") || !name.EndsWith(suffix)) return null;
        var at = name.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        var start = at + marker.Length;
        var end = name.Length - suffix.Length;
        return start < end && Version.TryParse(name[start..end], out var version) ? version : null;
    }

    /// <summary>The archive that carries cudart and cuBLAS for a CUDA build, in the same release.</summary>
    public static string LlamaCudaRuntimeAsset(Version cuda) =>
        $"cudart-llama-bin-win-cuda-{cuda.Major}.{cuda.Minor}-x64.zip";

    /// <summary>Whether this machine will most likely get a CUDA build of llama.cpp.</summary>
    public static bool ExpectsLlamaCuda(GpuCapabilities? gpu) =>
        gpu?.Cuda is { } support &&
        LlamaCudaLines.Any(l => support.LowestComputeCapability >= l.MinCompute && support.CanRun(new Version(l.Major, 0)));

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
            sink.Warn("install", $"{repo}: no matching Windows asset in the {pageSize} newest releases.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sink.Warn("install", $"{repo}: release lookup failed ({ex.Message}); using the pinned fallback build.");
        }
        return null;
    }

    private static string DescribeBackend(string dir, string? exePath)
    {
        if (exePath is null) return "not installed";
        var marker = Path.Combine(dir, ToolPaths.BackendMarkerName);
        var asset = File.Exists(marker) ? File.ReadAllText(marker).Trim().ToLowerInvariant() : "";

        var kind =
            asset.Contains("vulkan") ? "Vulkan (GPU)" :
            asset.Contains("cublas") || asset.Contains("cuda") ? "CUDA (GPU)" :
            asset.Contains("blas") ? "OpenBLAS (CPU)" :
            asset.Length > 0 ? "CPU" :
            HasVulkanRuntime(dir) ? "Vulkan (GPU)" : "custom build";

        return $"{kind} — {Path.GetFileName(exePath)}";
    }

    private static bool HasVulkanRuntime(string dir)
    {
        try
        {
            return Directory.Exists(dir) &&
                   Directory.EnumerateFiles(dir, "*vulkan*.dll", SearchOption.AllDirectories).Any();
        }
        catch (IOException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* leave it behind */ }
    }
}
