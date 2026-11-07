using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ReelsMagician.Core.Toolchain;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Qualcomm }

public sealed record GpuInfo(string Name, GpuVendor Vendor, bool HasVulkanDriver);

/// <summary>
/// What the NVIDIA driver reports through CUDA: the newest CUDA version it runs, and the
/// compute capability (such as 8.9) of the oldest card it drives.
/// </summary>
public sealed record CudaSupport(Version DriverVersion, Version LowestComputeCapability)
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

    public string PrimaryAdapterName => Adapters.Count > 0 ? Adapters[0].Name : "unknown";

    public string Describe()
    {
        var gpus = Adapters.Count == 0 ? "no adapter detected" : string.Join(", ", Adapters.Select(a => a.Name));
        var windows = OperatingSystem.IsWindows();
        var vulkan =
            VulkanAvailable ? $"Vulkan ready ({VulkanIcdCount} driver{(VulkanIcdCount == 1 ? "" : "s")})" :
            HasVulkanLoader
                ? (windows ? "Vulkan loader present but no driver found"
                           : "Vulkan loader present but no driver for this GPU — install mesa-vulkan-drivers, or the NVIDIA driver")
                : (windows ? "no Vulkan loader — update your graphics driver"
                           : "no Vulkan loader — install libvulkan1");
        var cuda = Cuda is null ? "" : $", CUDA {Cuda.DriverVersion} (compute {Cuda.LowestComputeCapability})";
        return $"{gpus} — {vulkan}{cuda}";
    }
}

public static class GpuProbe
{
    public static GpuCapabilities Detect()
    {
        if (OperatingSystem.IsWindows()) return DetectWindows();
        if (OperatingSystem.IsLinux()) return DetectLinux("/sys/class/drm", LinuxIcdManifests(), HasLinuxVulkanLoader());
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

            adapters.Add(new GpuInfo(display, vendor, manifests.Any(m => DriverServes(m, vendor, driver))));
        }

        return new GpuCapabilities(adapters, hasLoader, adapters.Count(a => a.HasVulkanDriver));
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
    /// The driver manifests the Vulkan loader would find: the files named in VK_DRIVER_FILES
    /// or VK_ICD_FILENAMES when set, otherwise the icd.d folders on its search path.
    /// </summary>
    private static List<string> LinuxIcdManifests()
    {
        var overrides = Environment.GetEnvironmentVariable("VK_DRIVER_FILES") ??
                        Environment.GetEnvironmentVariable("VK_ICD_FILENAMES");
        if (!string.IsNullOrWhiteSpace(overrides))
            return overrides.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToList();

        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        string Env(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

        var roots = new List<string> { Env("XDG_CONFIG_HOME", Path.Combine(home, ".config")) };
        roots.AddRange(Env("XDG_CONFIG_DIRS", "/etc/xdg").Split(':', StringSplitOptions.RemoveEmptyEntries));
        roots.Add("/etc");
        roots.Add(Env("XDG_DATA_HOME", Path.Combine(home, ".local", "share")));
        roots.AddRange(Env("XDG_DATA_DIRS", "/usr/local/share:/usr/share").Split(':', StringSplitOptions.RemoveEmptyEntries));

        return roots
            .Select(r => Path.Combine(r, "vulkan", "icd.d"))
            .Distinct()
            .SelectMany(d => SafeFiles(d, "*.json"))
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

    [SupportedOSPlatform("windows")]
    private static CudaSupport? ProbeCuda()
    {
        try
        {
            if (cuInit(0) != 0 || cuDriverGetVersion(out var version) != 0 || cuDeviceGetCount(out var count) != 0)
                return null;

            // With several cards llama.cpp uses them all, so the oldest one decides what can run.
            Version? lowest = null;
            for (var i = 0; i < count; i++)
            {
                if (cuDeviceGet(out var device, i) != 0 ||
                    cuDeviceGetAttribute(out var major, ComputeCapabilityMajor, device) != 0 ||
                    cuDeviceGetAttribute(out var minor, ComputeCapabilityMinor, device) != 0) continue;
                var capability = new Version(major, minor);
                if (lowest is null || capability < lowest) lowest = capability;
            }

            // The driver encodes CUDA 13.4 as 13040.
            return lowest is null ? null : new CudaSupport(new Version(version / 1000, version % 1000 / 10), lowest);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
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

                adapters.Add(new GpuInfo(desc, ClassifyVendor(desc), HasVulkanIcd(key)));
            }
        }
        catch (Exception) { /* registry unavailable — report what we have */ }
        return adapters;
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
