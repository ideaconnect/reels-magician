using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// Which download of each tool every kind of machine gets, on each Target OS, checked against
/// the asset lists of real releases. A wrong pick is silent: the CPU build of a tool still
/// works, only slower, so nothing else would ever fail.
/// </summary>
public static class ToolBuildChecks
{
    public static void Run()
    {
        WhisperBuilds();
        LlamaLinuxBuilds();
        FallbackBuilds();
        Downloads();
    }

    private static GhRelease Release(string tag, params string[] assets) =>
        new() { TagName = tag, Assets = assets.Select(n => new GhAsset { Name = n }).ToList() };

    // whisper.cpp b5454, after the CUDA builds were renamed.
    private static readonly GhRelease WhisperB5454 = Release("b5454",
        "whisper-b5454-xcframework.zip", "whisper-bin-ubuntu-arm64.tar.gz", "whisper-bin-ubuntu-x64.tar.gz",
        "whisper-bin-win-cpu-arm64.zip", "whisper-bin-win-cuda-11.8.0-x64.zip", "whisper-bin-win-cuda-12.4.0-x64.zip",
        "whisper-bin-win-cuda-13.4-arm64.zip", "whisper-bin-win-opencl-adreno-arm64.zip", "whisper-bin-Win32.zip",
        "whisper-bin-x64.zip", "whisper-blas-bin-Win32.zip", "whisper-blas-bin-x64.zip");

    // whisper.cpp b5130, the last release with the old names.
    private static readonly GhRelease WhisperB5130 = Release("b5130",
        "whisper-bin-ubuntu-x64.tar.gz", "whisper-bin-x64.zip", "whisper-blas-bin-x64.zip",
        "whisper-cublas-11.8.0-bin-x64.zip", "whisper-cublas-12.4.0-bin-x64.zip");

    private static CudaSupport Nvidia(string driver, string compute) => new(Version.Parse(driver), Version.Parse(compute));

    private static GpuCapabilities Gpu(CudaSupport? cuda = null, bool vulkan = true) =>
        new([], vulkan, vulkan ? 1 : 0, cuda);

    private static string? Pick(GhRelease release, IReadOnlyList<Func<string, bool>> rules) =>
        GitHubReleases.SelectAsset([release], rules)?.Asset.Name;

    private static void WhisperBuilds()
    {
        Check.Section("whisper.cpp build selection");

        var rtx = Gpu(Nvidia("13.4", "8.9"));
        Check.Equal("whisper-bin-win-cuda-12.4.0-x64.zip", Pick(WhisperB5454, ToolBuilds.WhisperRules(rtx, TargetOs.Windows)),
            "an NVIDIA card gets the CUDA build under its new name");
        Check.Equal("whisper-cublas-12.4.0-bin-x64.zip", Pick(WhisperB5130, ToolBuilds.WhisperRules(rtx, TargetOs.Windows)),
            "and still under the old one");
        Check.Equal("whisper-bin-win-cuda-11.8.0-x64.zip",
            Pick(WhisperB5454, ToolBuilds.WhisperRules(Gpu(Nvidia("12.2", "8.6")), TargetOs.Windows)),
            "a driver too old for CUDA 12.4 gets the CUDA 11.8 build");
        Check.Equal("whisper-blas-bin-x64.zip", Pick(WhisperB5454, ToolBuilds.WhisperRules(Gpu(), TargetOs.Windows)),
            "AMD and Intel on Windows get OpenBLAS");
        Check.Equal("whisper-bin-x64.zip",
            Pick(Release("b1", "whisper-bin-x64.zip"), ToolBuilds.WhisperRules(Gpu(), TargetOs.Windows)),
            "and plain CPU when a release has no OpenBLAS build");

        Check.Equal("whisper-bin-ubuntu-x64.tar.gz", Pick(WhisperB5454, ToolBuilds.WhisperRules(rtx, TargetOs.Linux)),
            "Linux gets the Ubuntu x64 build, never a Windows CUDA one");
        Check.Equal("whisper-bin-ubuntu-x64.tar.gz", Pick(WhisperB5454, ToolBuilds.WhisperRules(Gpu(vulkan: false), TargetOs.Linux)),
            "with or without a GPU");

        Check.Equal(new Version(12, 4, 0), ToolBuilds.WhisperCudaVersion("whisper-bin-win-cuda-12.4.0-x64.zip"),
            "the CUDA version is read from the new name");
        Check.True(ToolBuilds.WhisperCudaVersion("whisper-bin-win-cuda-13.4-arm64.zip") is null,
            "an ARM CUDA build is never taken for x64");

        foreach (var os in new[] { TargetOs.Windows, TargetOs.Linux })
        {
            var (tag, asset) = ToolBuilds.WhisperFallback(os);
            Check.True(tag == "v1.9.2" && asset.EndsWith(os == TargetOs.Windows ? ".zip" : ".tar.gz"),
                $"the pinned whisper.cpp fallback is a {os} build");
        }
    }

    private static void LlamaLinuxBuilds()
    {
        Check.Section("llama.cpp build selection on Linux");

        // The x64 Linux and Windows assets of llama.cpp b12189.
        var release = Release("b12189",
            "cudart-llama-b12189-bin-ubuntu-cuda-12.8-x64.tar.gz", "cudart-llama-bin-win-cuda-12.4-x64.zip",
            "llama-b12189-bin-ubuntu-cuda-12.8-x64.tar.gz", "llama-b12189-bin-ubuntu-cuda-13.5-x64.tar.gz",
            "llama-b12189-bin-ubuntu-openvino-2026.4.1-x64.tar.gz", "llama-b12189-bin-ubuntu-rocm-10.0-x64.tar.gz",
            "llama-b12189-bin-ubuntu-sycl-fp16-x64.tar.gz", "llama-b12189-bin-ubuntu-vulkan-arm64.tar.gz",
            "llama-b12189-bin-ubuntu-vulkan-x64.tar.gz", "llama-b12189-bin-ubuntu-x64.tar.gz",
            "llama-b12189-bin-win-cpu-x64.zip", "llama-b12189-bin-win-cuda-12.4-x64.zip",
            "llama-b12189-bin-win-cuda-13.5-x64.zip", "llama-b12189-bin-win-vulkan-x64.zip");

        Check.Equal("llama-b12189-bin-ubuntu-vulkan-x64.tar.gz", Pick(release, ToolBuilds.LlamaRules(Gpu(), TargetOs.Linux)),
            "a Vulkan GPU on Linux gets the Vulkan build");
        Check.Equal("llama-b12189-bin-ubuntu-vulkan-x64.tar.gz",
            Pick(release, ToolBuilds.LlamaRules(Gpu(Nvidia("13.4", "8.9")), TargetOs.Linux)),
            "so does an NVIDIA card, as Linux gets no CUDA builds");
        Check.Equal("llama-b12189-bin-ubuntu-x64.tar.gz", Pick(release, ToolBuilds.LlamaRules(Gpu(vulkan: false), TargetOs.Linux)),
            "no Vulkan driver gets the CPU build, not the Vulkan one by prefix");
        Check.Equal("llama-b12189-bin-win-cuda-13.5-x64.zip",
            Pick(release, ToolBuilds.LlamaRules(Gpu(Nvidia("13.6", "8.9")), TargetOs.Windows)),
            "the same release still gives Windows its CUDA build");
        Check.True(!ToolBuilds.ExpectsLlamaCuda(Gpu(Nvidia("13.4", "8.9")), TargetOs.Linux),
            "Linux never expects the larger CUDA download");

        foreach (var os in new[] { TargetOs.Windows, TargetOs.Linux })
        {
            var (tag, asset) = ToolBuilds.LlamaFallback(os);
            Check.True(asset.StartsWith($"llama-{tag}-bin-") && asset.Contains("vulkan-x64") &&
                       asset.EndsWith(os == TargetOs.Windows ? ".zip" : ".tar.gz"),
                $"the pinned llama.cpp fallback is the {os} Vulkan build");
        }
    }

    /// <summary>A lesser build than the machine supports must be reported, never installed silently.</summary>
    private static void FallbackBuilds()
    {
        Check.Section("Fallback builds are reported");

        Check.Equal(BuildKind.Cuda, ToolBuilds.KindOf("whisper-cublas-12.4.0-bin-x64.zip"), "cuBLAS is a CUDA build, not OpenBLAS");
        Check.Equal(BuildKind.Blas, ToolBuilds.KindOf("whisper-blas-bin-x64.zip"), "OpenBLAS is recognised");
        Check.Equal(BuildKind.Cpu, ToolBuilds.KindOf("llama-b12189-bin-ubuntu-x64.tar.gz"), "a plain build is CPU");

        var rtx = Gpu(Nvidia("13.4", "8.9"));
        var whisper = ToolBuilds.FallbackWarning("whisper.cpp", "whisper-blas-bin-x64.zip", ToolBuilds.ExpectedWhisper(rtx, TargetOs.Windows));
        Check.True(whisper is not null && whisper.Contains("OpenBLAS (CPU)") && whisper.Contains("CUDA (GPU)"),
            "an NVIDIA card left on OpenBLAS is reported, naming both builds");
        Check.True(ToolBuilds.FallbackWarning("whisper.cpp", "whisper-bin-win-cuda-12.4.0-x64.zip",
                ToolBuilds.ExpectedWhisper(rtx, TargetOs.Windows)) is null,
            "the CUDA build on an NVIDIA card is not");
        Check.True(ToolBuilds.FallbackWarning("whisper.cpp", "whisper-blas-bin-x64.zip",
                ToolBuilds.ExpectedWhisper(Gpu(), TargetOs.Windows)) is null,
            "OpenBLAS on an AMD card is the best whisper.cpp offers, so it is not");
        Check.True(ToolBuilds.FallbackWarning("whisper.cpp", "whisper-bin-ubuntu-x64.tar.gz",
                ToolBuilds.ExpectedWhisper(rtx, TargetOs.Linux)) is null,
            "the CPU build on Linux is the only one whisper.cpp offers, so it is not");

        Check.True(ToolBuilds.FallbackWarning("llama.cpp", "llama-b12189-bin-ubuntu-x64.tar.gz",
                ToolBuilds.ExpectedLlama(Gpu(), TargetOs.Linux)) is not null,
            "a Vulkan GPU left on the CPU build of llama.cpp is reported");
        Check.True(ToolBuilds.FallbackWarning("llama.cpp", "llama-b12189-bin-win-vulkan-x64.zip",
                ToolBuilds.ExpectedLlama(rtx, TargetOs.Windows)) is not null,
            "so is a CUDA-capable card left on Vulkan");
        Check.True(ToolBuilds.FallbackWarning("llama.cpp", "llama-b12189-bin-ubuntu-x64.tar.gz",
                ToolBuilds.ExpectedLlama(Gpu(vulkan: false), TargetOs.Linux)) is null,
            "the CPU build on a machine without Vulkan is not");
    }

    private static void Downloads()
    {
        Check.Section("Downloads for each system");

        Check.True(ToolBuilds.FfmpegUrl(TargetOs.Windows).EndsWith("-win64-gpl.zip") &&
                   ToolBuilds.FfmpegUrl(TargetOs.Linux).EndsWith("-linux64-gpl.tar.xz"),
            "ffmpeg comes as BtbN's GPL build for each system");
        Check.True(ToolBuilds.YtDlpUrl(TargetOs.Windows).EndsWith("/yt-dlp.exe") &&
                   ToolBuilds.YtDlpUrl(TargetOs.Linux).EndsWith("/yt-dlp_linux"),
            "yt-dlp comes as each system's single-file build");
        Check.True(ToolBuilds.DenoUrl(TargetOs.Windows).EndsWith("-pc-windows-msvc.zip") &&
                   ToolBuilds.DenoUrl(TargetOs.Linux).EndsWith("-unknown-linux-gnu.zip"),
            "Deno comes as each system's x64 build");
    }
}
