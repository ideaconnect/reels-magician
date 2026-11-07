using System.Formats.Tar;
using System.IO.Compression;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Toolchain;
using SharpCompress.Compressors.Xz;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// What differs between Windows and Linux: executable names and permissions, whether paths
/// ignore letter case, how GPUs are found, and the tar archives the Linux builds come in.
/// Every check runs on both systems, so a change made on one cannot quietly break the other.
/// </summary>
public static class PlatformChecks
{
    public static void Run()
    {
        Executables();
        PathCase();
        LinuxGpuDetection();
        TarArchives();
    }

    private static void Executables()
    {
        Check.Section("Executables on this system");

        var windows = OperatingSystem.IsWindows();
        Check.Equal(windows ? "ffmpeg.exe" : "ffmpeg", ToolPaths.ExeName("ffmpeg"),
            "an executable gets this system's file name");

        var root = Fresh("reelsmagician-exec");
        var tool = Path.Combine(root, ToolPaths.ExeName("tool"));
        File.WriteAllText(tool, "#!/bin/sh\necho ok\n");
        if (!windows) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // A downloaded single file has no execute bit on Linux, and starting it would fail.
        Check.Equal(windows, ToolPaths.IsExecutable(tool),
            windows ? "Windows runs a file by its extension" : "a file without an execute bit cannot be run");

        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", root);
            Check.Equal<string?>(windows ? tool : null, ToolPaths.FindOnPath(ToolPaths.ExeName("tool")),
                windows ? "a program on PATH is found" : "a PATH entry that cannot be run is skipped");

            ToolPaths.MakeExecutable(tool);
            Check.True(ToolPaths.IsExecutable(tool), "a downloaded program is made runnable");
            Check.Equal<string?>(tool, ToolPaths.FindOnPath(ToolPaths.ExeName("tool")), "and is then found on PATH");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// The cleanup guard decides what may be deleted. Where the file system tells letter case
    /// apart, a differently cased path is somewhere else and must be refused.
    /// </summary>
    private static void PathCase()
    {
        Check.Section("Paths and letter case");

        var root = Fresh("reelsmagician-case");
        var paths = new ToolPaths(Path.Combine(root, "Data"));
        paths.EnsureCreated();
        var cleaner = new DataFolderCleaner(paths, NullProgressSink.Instance);

        var ignoresCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Check.Equal(ignoresCase, cleaner.IsInsideDataFolder(Path.Combine(root, "DATA", "models")),
            ignoresCase
                ? "a differently cased path is the same folder on this system"
                : "a differently cased path is a different folder on this system, so it is refused");
        Check.True(cleaner.IsInsideDataFolder(paths.Models), "the exact path is still allowed");

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Linux GPUs come from sysfs. Mesa installs a Vulkan manifest for every vendor whatever the
    /// hardware, so only a manifest matching the card's vendor may count as its driver.
    /// </summary>
    private static void LinuxGpuDetection()
    {
        Check.Section("GPU detection on Linux");

        var root = Fresh("reelsmagician-sysfs");

        string Sysfs(string machine, string card, string? vendor, string? device = null, string? driver = null)
        {
            var dir = Path.Combine(root, machine, card, "device");
            Directory.CreateDirectory(dir);
            if (vendor is not null) File.WriteAllText(Path.Combine(dir, "vendor"), vendor + "\n");
            if (device is not null) File.WriteAllText(Path.Combine(dir, "device"), device + "\n");
            if (driver is not null) File.WriteAllText(Path.Combine(dir, "uevent"), $"DRIVER={driver}\nPCI_SLOT_NAME=0000:65:00.0\n");
            return Path.Combine(root, machine);
        }

        // Suseł: a firmware framebuffer, the Radeon 760M, and one of its connectors.
        var susel = Sysfs("susel", "card0", vendor: null);
        Sysfs("susel", "card1", "0x1002", "0x15bf", "amdgpu");
        Directory.CreateDirectory(Path.Combine(susel, "card1-eDP-1"));

        // Mesa's manifests as installed on Suseł (Ubuntu 26.10): every vendor's, on any machine.
        string[] mesa =
        [
            .. new[] { "asahi", "freedreno", "gfxstream_vk", "intel_hasvk", "intel", "lvp", "nouveau", "radeon", "virtio" }
                .Select(n => $"/usr/share/vulkan/icd.d/{n}_icd.json"),
        ];

        var amd = GpuProbe.DetectLinux(susel, mesa, hasLoader: true);
        Check.Equal(1, amd.Adapters.Count, "only the GPU is listed, not the framebuffer or a connector");
        Check.True(amd.HasAmd, "an AMD card is recognised by its PCI vendor");
        Check.Equal("AMD GPU [1002:15bf] (amdgpu)", amd.PrimaryAdapterName, "it is named by vendor, device and driver");
        Check.True(amd.VulkanAvailable, "Mesa's Radeon manifest is its Vulkan driver");
        Check.Equal(1, amd.VulkanIcdCount, "one GPU has a driver");

        Check.True(!GpuProbe.DetectLinux(susel, mesa, hasLoader: false).VulkanAvailable,
            "without libvulkan there is no Vulkan, whatever the manifests say");
        Check.True(!GpuProbe.DetectLinux(susel, ["/usr/share/vulkan/icd.d/radeon_icd.i686.json"], hasLoader: true).VulkanAvailable,
            "a 32-bit driver alone does not count");

        var oldRadeon = Sysfs("old-radeon", "card0", "0x1002", "0x6798", "radeon");
        Check.True(!GpuProbe.DetectLinux(oldRadeon, mesa, hasLoader: true).VulkanAvailable,
            "an old Radeon on the legacy radeon kernel driver has no Vulkan, though the manifest is there");

        // Mesa's NVIDIA driver is installed everywhere but runs only on the nouveau kernel driver.
        var nvidia = Sysfs("nvidia", "card0", "0x10de", "0x2684", "nvidia");
        Check.True(!GpuProbe.DetectLinux(nvidia, mesa, hasLoader: true).VulkanAvailable,
            "Mesa's manifests do not make a card on NVIDIA's own kernel driver Vulkan-ready");
        var withDriver = GpuProbe.DetectLinux(nvidia, [.. mesa, "/usr/share/vulkan/icd.d/nvidia_icd.json"], hasLoader: true);
        Check.True(withDriver.HasNvidia && withDriver.VulkanAvailable, "NVIDIA's own manifest does");
        Check.True(withDriver.Cuda is null, "CUDA is never probed on Linux, which gets no CUDA builds");

        var nouveau = Sysfs("nouveau", "card0", "0x10de", "0x2684", "nouveau");
        Check.True(GpuProbe.DetectLinux(nouveau, mesa, hasLoader: true).VulkanAvailable,
            "an NVIDIA card on the nouveau kernel driver gets Vulkan from Mesa");

        var none = GpuProbe.DetectLinux(Path.Combine(root, "no-drm"), mesa, hasLoader: true);
        Check.True(none.Adapters.Count == 0 && !none.VulkanAvailable,
            "with no GPU in sysfs there is no adapter and no Vulkan");

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// The Linux builds come as .tar.gz (whisper.cpp, llama.cpp) and .tar.xz (ffmpeg). They must
    /// keep their execute bits and symbolic links, and lose their top folder like a zip does.
    /// </summary>
    private static void TarArchives()
    {
        Check.Section("Tar archives");

        var root = Fresh("reelsmagician-tar");
        var unix = !OperatingSystem.IsWindows();
        const UnixFileMode Rwxrxrx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                     UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                     UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        var gz = Path.Combine(root, "llama-bin-ubuntu-x64.tar.gz");
        using (var file = File.Create(gz))
        using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(zip, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "build/") { Mode = Rwxrxrx });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "build/llama-server")
            {
                Mode = Rwxrxrx,
                DataStream = new MemoryStream("#!/bin/sh\necho ok\n"u8.ToArray()),
            });
        }

        var llama = Path.Combine(root, "llama");
        ArchiveExtractor.Extract(gz, llama, NullProgressSink.Instance, "llama.cpp");
        var server = Path.Combine(llama, "llama-server");
        Check.True(File.Exists(server), "a .tar.gz unpacks, losing its single top folder");
        if (unix) Check.True(ToolPaths.IsExecutable(server), "and keeps the execute bit");
        Check.True(!Directory.Exists(llama + ".staging"), "no staging folder is left behind");

        var xz = Path.Combine(root, "ffmpeg-linux64-gpl.tar.xz");
        File.WriteAllBytes(xz, Convert.FromBase64String(XzFixture));
        if (unix)
        {
            var ffmpeg = Path.Combine(root, "ffmpeg");
            ArchiveExtractor.Extract(xz, ffmpeg, NullProgressSink.Instance, "ffmpeg");
            Check.True(ToolPaths.IsExecutable(Path.Combine(ffmpeg, "bin", "tool")), "a .tar.xz unpacks with its execute bits");
            Check.True(File.Exists(Path.Combine(ffmpeg, "README.txt")), "and its plain files");
            var link = new FileInfo(Path.Combine(ffmpeg, "lib", "libtool.so"));
            Check.True(link.LinkTarget == "libtool.so.1" && File.ReadAllText(link.FullName) == "library\n",
                "and a versioned library stays reachable through its link");
        }
        else
        {
            // Creating symbolic links needs extra rights on Windows, which never unpacks the
            // Linux builds anyway, so only the xz decoding is checked here.
            using var file = File.OpenRead(xz);
            using var reader = new TarReader(new XZStream(file));
            var names = new List<string>();
            while (reader.GetNextEntry() is { } entry) names.Add(entry.Name);
            Check.True(names.Contains("tool-linux64/bin/tool") && names.Contains("tool-linux64/lib/libtool.so"),
                "a .tar.xz decodes and lists its entries");
        }

        var unknown = Path.Combine(root, "build.rar");
        File.WriteAllBytes(unknown, [1, 2, 3]);
        var refused = false;
        try { ArchiveExtractor.Extract(unknown, Path.Combine(root, "rar"), NullProgressSink.Instance, "rar"); }
        catch (NotSupportedException) { refused = true; }
        Check.True(refused, "an archive type the app does not know is refused");
        Check.True(!Directory.Exists(Path.Combine(root, "rar.staging")), "and leaves no staging folder behind");

        Directory.Delete(root, recursive: true);
    }

    private static string Fresh(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // tool-linux64/{bin/tool (755), README.txt (644), lib/libtool.so.1, lib/libtool.so -> libtool.so.1},
    // made with GNU tar -cJ, as BtbN packs ffmpeg.
    private const string XzFixture =
        "/Td6WFoAAATm1rRGBMD6AYBQIQEWAAAAAAAAAGglAs/gJ/8A8l0AOhvs2C0qzVvH/+c9Y9Tb2bHkBG0cMRSK6IWKib2D5kz9hesA4VtQkMBm" +
        "VCOTNjzSjmQKgK1cfqIKYFf02Twpy1OznpeuUdSikBQ1zcp3B4obUlc9XuhWHXojC058uLo/cEEpD697iTz6bVjZ/kp9+iBxYAAu6plLR0j2" +
        "ljjLiG5ve+SMmd++lo9pu3c6wgbxrYtqyjMCCOQGFFdMNC1LKaQFKjuLR7HuAS1W7r4W0psUCEismGRAOSWMeM2IY+QBhfueklFJkyponoAF" +
        "REX1msU++vxk9cAfm0xZcfRBYaYre0uSNyTYABVF6GxWzparpgAAAADQhfTD5EzawAABlgKAUAAAaOpP87HEZ/sCAAAAAARZWg==";
}
