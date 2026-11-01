using System.Runtime.InteropServices;
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
        var vulkan =
            VulkanAvailable ? $"Vulkan ready ({VulkanIcdCount} driver{(VulkanIcdCount == 1 ? "" : "s")})" :
            HasVulkanLoader ? "Vulkan loader present but no driver found" :
            "no Vulkan loader — update your graphics driver";
        var cuda = Cuda is null ? "" : $", CUDA {Cuda.DriverVersion} (compute {Cuda.LowestComputeCapability})";
        return $"{gpus} — {vulkan}{cuda}";
    }
}

public static class GpuProbe
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static GpuCapabilities Detect()
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
