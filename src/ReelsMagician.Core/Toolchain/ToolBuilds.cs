namespace ReelsMagician.Core.Toolchain;

/// <summary>An operating system the app ships for.</summary>
public enum TargetOs { Windows, Linux }

/// <summary>The hardware a Tool build runs on, from least to most capable.</summary>
public enum BuildKind { Cpu, Blas, Vulkan, Cuda }

/// <summary>
/// Which download of each Toolchain program this machine gets, on each Target OS.
///
/// Asset names change between releases, so builds are matched by pattern, in order of
/// preference. A build listed ahead of what a project publishes today (a Vulkan whisper.cpp,
/// say) is adopted with no code change once it appears. Every rule takes the Target OS
/// explicitly, so the rules for both systems can be checked on either.
///
/// Linux gets no CUDA builds: llama.cpp runs on any GPU there through Vulkan, and whisper.cpp
/// publishes only a CPU build for Linux.
/// </summary>
public static class ToolBuilds
{
    public static TargetOs Current =>
        OperatingSystem.IsWindows() ? TargetOs.Windows :
        OperatingSystem.IsLinux() ? TargetOs.Linux :
        throw new PlatformNotSupportedException("ReelsMagician runs on Windows and Linux.");

    // ── Single-download tools ────────────────────────────────────────────────────────────

    private const string BtbN = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";

    public static string FfmpegUrl(TargetOs os) => os == TargetOs.Windows
        ? BtbN + "ffmpeg-master-latest-win64-gpl.zip"
        : BtbN + "ffmpeg-master-latest-linux64-gpl.tar.xz";

    public static string YtDlpUrl(TargetOs os) => os == TargetOs.Windows
        ? "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
        : "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux";

    // yt-dlp needs a JavaScript runtime for some compatible video links; without one it warns
    // that extraction is deprecated and quietly drops formats, sometimes every 4K variant.
    public static string DenoUrl(TargetOs os) => os == TargetOs.Windows
        ? "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip"
        : "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip";

    // ── whisper.cpp ──────────────────────────────────────────────────────────────────────

    public const string WhisperRepo = "ggml-org/whisper.cpp";

    // The CUDA lines whisper.cpp builds for Windows x64, newest first, with the oldest card each
    // still supports. A line it does not publish today simply never matches.
    private static readonly (int Major, Version MinCompute)[] WhisperCudaLines =
        [(13, new(7, 5)), (12, new(5, 0)), (11, new(3, 5))];

    /// <summary>
    /// whisper.cpp builds in order of preference. An NVIDIA card on Windows gets the newest CUDA
    /// build its driver and card can run, then OpenBLAS, then plain CPU.
    /// </summary>
    public static IReadOnlyList<Func<string, bool>> WhisperRules(GpuCapabilities gpu, TargetOs os)
    {
        var rules = new List<Func<string, bool>>();
        if (os == TargetOs.Windows)
        {
            if (gpu.VulkanAvailable) rules.Add(n => n.Contains("vulkan") && n.EndsWith("-x64.zip"));
            if (gpu.Cuda is { } support)
            {
                foreach (var (major, minCompute) in WhisperCudaLines)
                {
                    if (support.LowestComputeCapability < minCompute) continue;
                    rules.Add(n => WhisperCudaVersion(n) is { } v && v.Major == major && support.CanRun(v));
                }
            }
            rules.Add(n => n == "whisper-blas-bin-x64.zip");
            rules.Add(n => n == "whisper-bin-x64.zip");
        }
        else
        {
            if (gpu.VulkanAvailable) rules.Add(n => n.Contains("vulkan") && n.Contains("ubuntu") && n.EndsWith("-x64.tar.gz"));
            rules.Add(n => n == "whisper-bin-ubuntu-x64.tar.gz");
        }
        return rules;
    }

    /// <summary>
    /// The CUDA version of a whisper.cpp Windows x64 CUDA build, or null for any other asset.
    /// Releases up to b5130 named it whisper-cublas-12.4.0-bin-x64.zip; from b5454 it is
    /// whisper-bin-win-cuda-12.4.0-x64.zip. Both are recognised.
    /// </summary>
    public static Version? WhisperCudaVersion(string assetName)
    {
        var name = assetName.ToLowerInvariant();
        string? version = null;
        if (name.StartsWith("whisper-bin-win-cuda-") && name.EndsWith("-x64.zip"))
            version = name["whisper-bin-win-cuda-".Length..^"-x64.zip".Length];
        else if (name.StartsWith("whisper-cublas-") && name.EndsWith("-bin-x64.zip"))
            version = name["whisper-cublas-".Length..^"-bin-x64.zip".Length];
        return version is not null && Version.TryParse(version, out var v) ? v : null;
    }

    /// <summary>The pinned build used when the GitHub release list cannot be read.</summary>
    public static (string Tag, string Asset) WhisperFallback(TargetOs os) =>
        ("v1.9.2", os == TargetOs.Windows ? "whisper-blas-bin-x64.zip" : "whisper-bin-ubuntu-x64.tar.gz");

    /// <summary>The best whisper.cpp build this machine can expect its publisher to provide.</summary>
    public static BuildKind ExpectedWhisper(GpuCapabilities? gpu, TargetOs os)
    {
        if (os != TargetOs.Windows) return BuildKind.Cpu;

        // whisper.cpp publishes Windows x64 CUDA builds for 12.4 and 11.8.
        var cuda = gpu?.Cuda is { } s &&
                   ((s.LowestComputeCapability >= new Version(5, 0) && s.CanRun(new Version(12, 4))) ||
                    (s.LowestComputeCapability >= new Version(3, 5) && s.CanRun(new Version(11, 8))));
        return cuda ? BuildKind.Cuda : BuildKind.Blas;
    }

    // ── llama.cpp ────────────────────────────────────────────────────────────────────────

    public const string LlamaRepo = "ggml-org/llama.cpp";

    // The CUDA versions llama.cpp publishes Windows builds for, newest first, with the oldest
    // card each still supports: CUDA 13 dropped Maxwell, Pascal and Volta.
    private static readonly (int Major, Version MinCompute)[] LlamaCudaLines = [(13, new(7, 5)), (12, new(5, 0))];

    /// <summary>
    /// llama.cpp builds in order of preference. On Windows an NVIDIA card gets the newest CUDA
    /// build its driver and card can run. Any other GPU, or an NVIDIA setup too old for every CUDA
    /// build, gets Vulkan when a driver is present and the CPU build otherwise. Linux has only
    /// Vulkan and CPU.
    /// </summary>
    public static IReadOnlyList<Func<string, bool>> LlamaRules(GpuCapabilities gpu, TargetOs os)
    {
        var rules = new List<Func<string, bool>>();
        if (os == TargetOs.Windows)
        {
            if (gpu.Cuda is { } support)
            {
                foreach (var (major, minCompute) in LlamaCudaLines)
                {
                    if (support.LowestComputeCapability < minCompute) continue;
                    rules.Add(n => LlamaCudaVersion(n) is { } v && v.Major == major && support.CanRun(v));
                }
            }
            if (gpu.VulkanAvailable) rules.Add(n => n.StartsWith("llama-") && n.EndsWith("-bin-win-vulkan-x64.zip"));
            rules.Add(n => n.StartsWith("llama-") && n.EndsWith("-bin-win-cpu-x64.zip"));
        }
        else
        {
            if (gpu.VulkanAvailable) rules.Add(n => n.StartsWith("llama-") && n.EndsWith("-bin-ubuntu-vulkan-x64.tar.gz"));
            rules.Add(n => n.StartsWith("llama-") && n.EndsWith("-bin-ubuntu-x64.tar.gz"));
        }
        return rules;
    }

    /// <summary>The CUDA version of a llama.cpp Windows x64 CUDA build, or null for any other asset.</summary>
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
    public static bool ExpectsLlamaCuda(GpuCapabilities? gpu, TargetOs os) =>
        os == TargetOs.Windows && gpu?.Cuda is { } support &&
        LlamaCudaLines.Any(l => support.LowestComputeCapability >= l.MinCompute && support.CanRun(new Version(l.Major, 0)));

    /// <summary>The pinned build used when the GitHub release list cannot be read. Vulkan, because it runs on every vendor's GPU.</summary>
    public static (string Tag, string Asset) LlamaFallback(TargetOs os) =>
        ("b10760", os == TargetOs.Windows ? "llama-b10760-bin-win-vulkan-x64.zip" : "llama-b10760-bin-ubuntu-vulkan-x64.tar.gz");

    /// <summary>The best llama.cpp build this machine can expect its publisher to provide.</summary>
    public static BuildKind ExpectedLlama(GpuCapabilities? gpu, TargetOs os) =>
        ExpectsLlamaCuda(gpu, os) ? BuildKind.Cuda :
        gpu?.VulkanAvailable == true ? BuildKind.Vulkan :
        BuildKind.Cpu;

    // ── Fallback builds ──────────────────────────────────────────────────────────────────

    /// <summary>What hardware a build runs on, read from its asset name.</summary>
    public static BuildKind KindOf(string assetName)
    {
        var n = assetName.ToLowerInvariant();
        if (n.Contains("cuda") || n.Contains("cublas")) return BuildKind.Cuda;
        if (n.Contains("vulkan")) return BuildKind.Vulkan;
        if (n.Contains("blas")) return BuildKind.Blas;
        return BuildKind.Cpu;
    }

    public static string Label(BuildKind kind) => kind switch
    {
        BuildKind.Cuda => "CUDA (GPU)",
        BuildKind.Vulkan => "Vulkan (GPU)",
        BuildKind.Blas => "OpenBLAS (CPU)",
        _ => "CPU",
    };

    /// <summary>
    /// The warning for a Fallback build: an installed build below the best one this machine
    /// supports. Null when the installed build is as good as expected, or better.
    /// </summary>
    public static string? FallbackWarning(string tool, string installedAsset, BuildKind expected)
    {
        var got = KindOf(installedAsset);
        if (got >= expected) return null;
        return $"{tool} is running as the {Label(got)} build, because the {Label(expected)} build this machine " +
               "supports was not available when it was installed. Remove it under Free up space and install it " +
               "again to retry.";
    }
}
