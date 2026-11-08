using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Toolchain;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Qualcomm }

/// <param name="MemoryBytes">
/// The memory a model can use on this card, as the system reports it before llama.cpp is
/// installed; null when it doesn't say. llama.cpp's own report replaces it once it is there.
/// </param>
/// <param name="Integrated">
/// An integrated GPU, whose memory is the computer's own. Set where the system tells it apart
/// from a discrete card, which is amdgpu's APUs on Linux; false wherever it can't tell.
/// </param>
public sealed record GpuInfo(string Name, GpuVendor Vendor, bool HasVulkanDriver, long? MemoryBytes = null,
    bool Integrated = false);

/// <summary>
/// What the NVIDIA driver reports through CUDA: the newest CUDA version it runs, the
/// compute capability (such as 8.9) of the oldest card it drives, and the memory of all of its
/// cards together, which llama.cpp splits a model across.
/// </summary>
public sealed record CudaSupport(Version DriverVersion, Version LowestComputeCapability, long? TotalMemoryBytes = null)
{
    public bool CanRun(Version cudaBuild) => DriverVersion >= new Version(cudaBuild.Major, cudaBuild.Minor);
}

/// <summary>
/// Detects display adapters and whether a usable Vulkan driver is installed, so the toolchain
/// can pick GPU-accelerated builds instead of CPU ones.
/// </summary>
public sealed record GpuCapabilities(
    IReadOnlyList<GpuInfo> Adapters,
    bool HasVulkanLoader,
    int VulkanIcdCount,
    CudaSupport? Cuda = null)
{
    public bool VulkanAvailable => HasVulkanLoader && VulkanIcdCount > 0;
    public bool HasNvidia => Adapters.Any(a => a.Vendor == GpuVendor.Nvidia);
    public bool HasAmd => Adapters.Any(a => a.Vendor == GpuVendor.Amd);

    /// <summary>
    /// Whether llama.cpp's Vulkan build runs on an integrated GPU here: every adapter with a
    /// Vulkan driver is one. llama.cpp loads a model onto the discrete GPUs when there are any,
    /// and onto an integrated one only when there are none (src/llama.cpp, build 12189), though
    /// <c>--list-devices</c> lists both.
    /// </summary>
    public bool VulkanRunsOnIntegrated =>
        Adapters.Any(a => a.HasVulkanDriver) && Adapters.Where(a => a.HasVulkanDriver).All(a => a.Integrated);

    public string PrimaryAdapterName => Adapters.Count > 0 ? Adapters[0].Name : "unknown";

    /// <summary>The header's line about the graphics card, in the interface's language.</summary>
    public string Describe() => Describe(Lang.Ui);

    /// <summary>The same in a language of its own, such as the log's English.</summary>
    public string Describe(Lang lang)
    {
        var gpus = Adapters.Count == 0 ? lang[nameof(Strings.Gpu_NoAdapter)] : string.Join(", ", Adapters.Select(a => a.Name));
        var windows = OperatingSystem.IsWindows();
        var vulkan =
            VulkanAvailable ? lang.Plural("Gpu_VulkanReady", VulkanIcdCount) :
            HasVulkanLoader
                ? lang[windows ? nameof(Strings.Gpu_NoDriver_Windows) : nameof(Strings.Gpu_NoDriver_Linux)]
                : lang[windows ? nameof(Strings.Gpu_NoLoader_Windows) : nameof(Strings.Gpu_NoLoader_Linux)];
        var cuda = Cuda is null ? "" : lang.Format(nameof(Strings.Gpu_Cuda), Cuda.DriverVersion, Cuda.LowestComputeCapability);
        return $"{gpus} — {vulkan}{cuda}";
    }
}

public static class GpuProbe
{
    public static GpuCapabilities Detect()
    {
        if (OperatingSystem.IsWindows()) return DetectWindows();
        if (OperatingSystem.IsLinux())
            return DetectLinux("/sys/class/drm", LinuxIcdManifests(Environment.GetEnvironmentVariable), HasLinuxVulkanLoader());
        return new GpuCapabilities([], HasVulkanLoader: false, VulkanIcdCount: 0);
    }

    // ── Linux ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the GPUs from sysfs and matches each one to an installed Vulkan driver manifest.
    /// Mesa installs the manifests for every vendor at once, so a manifest on its own says
    /// nothing about the hardware: only one matching an adapter's vendor counts as a driver.
    /// Linux gets no CUDA builds, so CUDA is never probed here.
    /// </summary>
    /// <param name="drmClassDir">Normally <c>/sys/class/drm</c>.</param>
    /// <param name="icdManifests">Paths or file names of the Vulkan driver manifests installed.</param>
    /// <param name="hasLoader">Whether <c>libvulkan.so.1</c> can be loaded.</param>
    public static GpuCapabilities DetectLinux(string drmClassDir, IEnumerable<string> icdManifests, bool hasLoader)
    {
        var manifests = icdManifests
            .Select(m => Path.GetFileName(m).ToLowerInvariant())
            .Where(m => !m.Contains("i686") && !m.Contains("icd32")) // 32-bit drivers serve 32-bit programs only
            .ToList();

        var adapters = new List<GpuInfo>();
        foreach (var card in SafeDirectories(drmClassDir, "card*").Order(StringComparer.Ordinal))
        {
            // card0, card1 … are GPUs; card1-eDP-1 and the like are their connectors.
            var name = Path.GetFileName(card);
            if (name.Length <= 4 || !name[4..].All(char.IsAsciiDigit)) continue;

            var device = Path.Combine(card, "device");
            // Firmware framebuffers such as simpledrm have no PCI vendor and are not GPUs.
            if (ReadTrimmed(Path.Combine(device, "vendor")) is not { Length: > 2 } vendorId) continue;

            var vendor = VendorFromPciId(vendorId);
            var deviceId = ReadTrimmed(Path.Combine(device, "device"));
            var driver = ReadDriver(device);
            var display = $"{VendorName(vendor)} GPU [{Hex(vendorId)}:{Hex(deviceId)}]" +
                          (driver is null ? "" : $" ({driver})");
            if (adapters.Any(a => a.Name == display)) continue;

            var (memory, integrated) = driver == "amdgpu" ? AmdgpuMemory(device) : (null, false);
            adapters.Add(new GpuInfo(display, vendor, manifests.Any(m => DriverServes(m, vendor, driver)),
                memory, integrated));
        }

        return new GpuCapabilities(adapters, hasLoader, adapters.Count(a => a.HasVulkanDriver));
    }

    /// <summary>
    /// The memory amdgpu offers a model, from its sysfs counters. An integrated Radeon's VRAM is
    /// a slice of the system's memory set aside by the firmware, and Mesa's Vulkan driver lets
    /// llama.cpp use GTT, more of the system's memory, on top of it, so both count: on a Radeon
    /// 760M with 6 GiB of VRAM and 12.6 GiB of GTT llama.cpp reports 19,062 MiB, the sum. amdgpu
    /// shows mem_info_vram_vendor only when it knows who made the card's memory chips, which an
    /// APU doesn't have; a card that shows it is taken for a discrete one, where only VRAM counts,
    /// as llama.cpp's Vulkan backend reports only the card's own memory there. The same test tells
    /// an integrated card; one without the counters isn't taken for integrated.
    /// </summary>
    private static (long? Bytes, bool Integrated) AmdgpuMemory(string device)
    {
        if (!long.TryParse(ReadTrimmed(Path.Combine(device, "mem_info_vram_total")), out var vram) || vram <= 0) return (null, false);
        if (File.Exists(Path.Combine(device, "mem_info_vram_vendor"))) return (vram, false);
        var gtt = long.TryParse(ReadTrimmed(Path.Combine(device, "mem_info_gtt_total")), out var g) && g > 0 ? g : 0;
        return (vram + gtt, true);
    }

    /// <summary>
    /// Whether a Vulkan driver manifest, by its file name, can drive this card. Each Vulkan driver
    /// works only on top of one kernel driver: Mesa's Radeon driver needs amdgpu, its NVIDIA
    /// driver needs nouveau, and NVIDIA's own needs nvidia. Mesa installs all of its manifests
    /// everywhere, so the kernel driver is what tells a usable one apart. When the kernel driver
    /// is unknown, the vendor alone decides.
    /// </summary>
    private static bool DriverServes(string manifest, GpuVendor vendor, string? kernelDriver)
    {
        bool On(params string[] drivers) => kernelDriver is null || drivers.Contains(kernelDriver);

        return vendor switch
        {
            GpuVendor.Amd => (manifest.StartsWith("radeon_icd") || manifest.StartsWith("amd_icd")) && On("amdgpu"),
            GpuVendor.Intel => (manifest.StartsWith("intel_icd") && On("i915", "xe")) ||
                               (manifest.StartsWith("intel_hasvk_icd") && On("i915")),
            GpuVendor.Nvidia => (manifest.StartsWith("nvidia_icd") && On("nvidia")) ||
                                (manifest.StartsWith("nouveau_icd") && On("nouveau")),
            GpuVendor.Qualcomm => manifest.StartsWith("freedreno_icd") && On("msm"),
            _ => false,
        };
    }

    /// <summary>
    /// The driver manifests the Vulkan loader would find, read the way the loader reads them.
    /// VK_DRIVER_FILES, or the older VK_ICD_FILENAMES when it is not set, replaces the search
    /// and may name manifest files or folders of them. Otherwise the manifests named in
    /// VK_ADD_DRIVER_FILES come first, then the icd.d folders on the loader's search path.
    /// </summary>
    /// <param name="env">Reads an environment variable; the probe passes the process's own.</param>
    public static List<string> LinuxIcdManifests(Func<string, string?> env)
    {
        string? Var(string name) => env(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v : null;

        // Lists are separated as on PATH: by ':' on Linux.
        string[] Split(string list) =>
            list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<string> Listed(string list)
        {
            foreach (var entry in Split(list))
            {
                if (Directory.Exists(entry)) foreach (var file in SafeFiles(entry, "*.json")) yield return file;
                else if (File.Exists(entry)) yield return entry;
            }
        }

        if ((Var("VK_DRIVER_FILES") ?? Var("VK_ICD_FILENAMES")) is { } overrides)
            return Listed(overrides).ToList();

        var home = env("HOME") ?? "";
        var roots = new List<string> { Var("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config") };
        roots.AddRange(Split(Var("XDG_CONFIG_DIRS") ?? "/etc/xdg"));
        roots.Add("/etc");
        roots.Add(Var("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"));
        roots.AddRange(Split(Var("XDG_DATA_DIRS") ?? string.Join(Path.PathSeparator, "/usr/local/share", "/usr/share")));

        var added = Var("VK_ADD_DRIVER_FILES") is { } extra ? Listed(extra) : [];
        return added
            .Concat(roots
                .Select(r => Path.Combine(r, "vulkan", "icd.d"))
                .Distinct()
                .SelectMany(d => SafeFiles(d, "*.json")))
            .ToList();
    }

    private static bool HasLinuxVulkanLoader()
    {
        if (!NativeLibrary.TryLoad("libvulkan.so.1", out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static GpuVendor VendorFromPciId(string id) => id.ToLowerInvariant() switch
    {
        "0x10de" => GpuVendor.Nvidia,
        "0x1002" or "0x1022" => GpuVendor.Amd,
        "0x8086" => GpuVendor.Intel,
        "0x5143" => GpuVendor.Qualcomm,
        _ => GpuVendor.Unknown,
    };

    private static string VendorName(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Qualcomm => "Qualcomm",
        _ => "Unknown",
    };

    private static string Hex(string? id) =>
        id is null ? "?" : id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? id[2..] : id;

    /// <summary>The kernel driver bound to the device, from its uevent file (DRIVER=amdgpu).</summary>
    private static string? ReadDriver(string device)
    {
        try
        {
            var uevent = Path.Combine(device, "uevent");
            if (!File.Exists(uevent)) return null;
            return File.ReadLines(uevent)
                .FirstOrDefault(l => l.StartsWith("DRIVER=", StringComparison.Ordinal))?["DRIVER=".Length..].Trim() is { Length: > 0 } d
                ? d : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadTrimmed(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> SafeDirectories(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ── Windows ──────────────────────────────────────────────────────────────────────────

    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    [SupportedOSPlatform("windows")]
    private static GpuCapabilities DetectWindows()
    {
        var adapters = ReadAdapters();

        var loader = File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-1.dll"));

        // Current AMD, NVIDIA and Intel drivers register their ICD manifest on the adapter's own
        // registry key. The legacy Khronos hive is checked too, because older drivers and
        // software rasterisers (lavapipe, SwiftShader) still use it.
        var icds = adapters.Count(a => a.HasVulkanDriver) + CountLegacyIcds();
        var cuda = adapters.Any(a => a.Vendor == GpuVendor.Nvidia) ? ProbeCuda() : null;

        return new GpuCapabilities(adapters, loader, icds, cuda);
    }

    // The CUDA driver API ships with every NVIDIA driver. Asking it directly answers both
    // questions a CUDA build depends on, with no table of driver numbers to keep current.
    private const string CudaDriver = "nvcuda.dll";
    private const int ComputeCapabilityMajor = 75; // CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR
    private const int ComputeCapabilityMinor = 76;

    [DllImport(CudaDriver), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int cuInit(uint flags);

    [DllImport(CudaDriver), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int cuDriverGetVersion(out int version);

    [DllImport(CudaDriver), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int cuDeviceGetCount(out int count);

    [DllImport(CudaDriver), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int cuDeviceGet(out int device, int ordinal);

    [DllImport(CudaDriver), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int cuDeviceGetAttribute(out int value, int attribute, int device);

    [DllImport(CudaDriver), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int cuDeviceTotalMem_v2(out nuint bytes, int device);

    [SupportedOSPlatform("windows")]
    private static CudaSupport? ProbeCuda()
    {
        try
        {
            if (cuInit(0) != 0 || cuDriverGetVersion(out var version) != 0 || cuDeviceGetCount(out var count) != 0)
                return null;

            // With several cards llama.cpp uses them all, so the oldest one decides what can run,
            // and their memory adds up.
            Version? lowest = null;
            long memory = 0;
            for (var i = 0; i < count; i++)
            {
                if (cuDeviceGet(out var device, i) != 0 ||
                    cuDeviceGetAttribute(out var major, ComputeCapabilityMajor, device) != 0 ||
                    cuDeviceGetAttribute(out var minor, ComputeCapabilityMinor, device) != 0) continue;
                var capability = new Version(major, minor);
                if (lowest is null || capability < lowest) lowest = capability;
                if (TotalMemory(device) is { } bytes) memory += bytes;
            }

            // The driver encodes CUDA 13.4 as 13040.
            return lowest is null ? null
                : new CudaSupport(new Version(version / 1000, version % 1000 / 10), lowest, memory > 0 ? memory : null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static long? TotalMemory(int device)
    {
        try { return cuDeviceTotalMem_v2(out var bytes, device) == 0 && bytes > 0 ? (long)bytes : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static List<GpuInfo> ReadAdapters()
    {
        var adapters = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return adapters;

            foreach (var sub in cls.GetSubKeyNames())
            {
                // Adapter instances are four-digit keys; skip Configuration and Properties.
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;

                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc || string.IsNullOrWhiteSpace(desc)) continue;
                if (adapters.Any(a => a.Name.Equals(desc, StringComparison.OrdinalIgnoreCase))) continue;

                var vendor = ClassifyVendor(desc);
                adapters.Add(new GpuInfo(desc, vendor, HasVulkanIcd(key), DedicatedMemory(key, desc, vendor)));
            }
        }
        catch (Exception) { /* registry unavailable — report what we have */ }
        return adapters;
    }

    /// <summary>
    /// The card's own memory, as its driver records it on the adapter's key: qwMemorySize, a
    /// QWORD, on current drivers, and MemorySize, which stops at 4 GB, on older ones. Intel's
    /// integrated graphics record only the small slice set aside for them, while llama.cpp can use
    /// shared system memory on top, so a warning from that figure would be a false one; it is left
    /// unknown, as is any value the driver doesn't record.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static long? DedicatedMemory(RegistryKey adapterKey, string name, GpuVendor vendor)
    {
        if (vendor == GpuVendor.Intel && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return null;

        var bytes = adapterKey.GetValue("HardwareInformation.qwMemorySize") switch
        {
            long q => q,
            byte[] { Length: 8 } raw => BitConverter.ToInt64(raw),
            _ => adapterKey.GetValue("HardwareInformation.MemorySize") switch
            {
                int d => (long)(uint)d,
                byte[] { Length: 4 } raw => BitConverter.ToUInt32(raw),
                _ => 0L,
            },
        };
        return bytes > 0 ? bytes : null;
    }

    /// <summary>True when the adapter advertises a Vulkan ICD manifest that is actually on disk.</summary>
    [SupportedOSPlatform("windows")]
    private static bool HasVulkanIcd(RegistryKey adapterKey)
    {
        foreach (var name in new[] { "VulkanDriverName", "VulkanDriverNameWoW" })
        {
            var value = adapterKey.GetValue(name);

            // The value is REG_SZ on some drivers and REG_MULTI_SZ on others.
            var paths = value switch
            {
                string single when single.Length > 0 => [single],
                string[] many => many,
                _ => Array.Empty<string>(),
            };

            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return true;
            }
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static int CountLegacyIcds()
    {
        var count = 0;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var drivers = baseKey.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\Drivers");
                if (drivers is null) continue;

                // Each value name is an ICD manifest path; the data is 0 when enabled.
                count += drivers.GetValueNames().Count(n => !string.IsNullOrWhiteSpace(n) && File.Exists(n));
            }
            catch (Exception) { /* ignore this view */ }
        }
        return count;
    }

    private static GpuVendor ClassifyVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx")) return GpuVendor.Nvidia;
        if (n.Contains("amd") || n.Contains("radeon") || n.Contains("firepro")) return GpuVendor.Amd;
        if (n.Contains("intel") || n.Contains("arc ")) return GpuVendor.Intel;
        if (n.Contains("qualcomm") || n.Contains("adreno")) return GpuVendor.Qualcomm;
        return GpuVendor.Unknown;
    }
}
