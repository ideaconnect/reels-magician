using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelsMagician.Core.Localization;
using ReelsMagician.Core.Media;

namespace ReelsMagician.Core.Toolchain;

/// <summary>A device llama.cpp lists, with its memory.</summary>
public sealed record DeviceMemory(string Name, long TotalBytes, long? FreeBytes = null);

/// <summary>
/// What an installed llama-server says about itself: the devices it would run on, with their
/// memory, and whether it can fit a model to them on its own.
/// </summary>
/// <param name="Devices">
/// The GPUs from <c>--list-devices</c>: empty when it found none and runs on the processor, null
/// when the build could not list them.
/// </param>
/// <param name="SupportsFit">
/// Whether <c>--help</c> lists <c>--fit</c>, with which llama.cpp moves layers to the processor
/// when a model is too big for the GPU, instead of failing.
/// </param>
public sealed record LlamaFeatures(IReadOnlyList<DeviceMemory>? Devices, bool SupportsFit);

/// <summary>
/// Asks llama-server for its devices and options, once per build: the answers are kept in the
/// data folder, under the executable's path, size and time and the GPUs the probe found, so a
/// new build or a new card asks again and nothing else does.
/// </summary>
public sealed partial class LlamaProbe(string cacheFile)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly Lock Gate = new();

    // "  Vulkan0: AMD Radeon 760M Graphics (RADV PHOENIX) (19062 MiB, 18838 MiB free)". The name
    // may hold brackets of its own, so the sizes are the last pair on the line.
    [GeneratedRegex(@"^\s*(?<id>[A-Za-z][\w-]*\d+):\s*(?<name>.*?)\s*\((?<total>\d+)\s*MiB,\s*(?<free>\d+)\s*MiB free\)\s*$")]
    private static partial Regex DeviceLine();

    // The option itself, not --fit-target or --fit-ctx.
    [GeneratedRegex(@"(^|[\s,])--fit(?![\w-])", RegexOptions.Multiline)]
    private static partial Regex FitOption();

    /// <summary>
    /// The devices in <c>llama-server --list-devices</c> output: null when it has no
    /// "Available devices:" header, which a build without the option never prints.
    /// </summary>
    public static IReadOnlyList<DeviceMemory>? ParseDevices(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        var header = Array.FindIndex(lines, l => l.Trim().Equals("Available devices:", StringComparison.OrdinalIgnoreCase));
        if (header < 0) return null;

        var devices = new List<DeviceMemory>();
        foreach (var line in lines.Skip(header + 1))
        {
            var m = DeviceLine().Match(line);
            if (!m.Success) continue;
            var name = m.Groups["name"].Value.Trim();
            devices.Add(new DeviceMemory(
                name.Length > 0 ? name : m.Groups["id"].Value,
                Mib(m.Groups["total"].Value),
                Mib(m.Groups["free"].Value)));
        }
        return devices;
    }

    /// <summary>Whether <c>llama-server --help</c> lists <c>--fit</c>.</summary>
    public static bool HelpListsFit(string help) => FitOption().IsMatch(help);

    /// <summary>The GPUs the system reports, so a new card or driver asks llama-server again.</summary>
    public static string GpuKey(GpuCapabilities? gpu) => gpu?.Describe(Lang.Log) ?? "";

    private static long Mib(string digits) =>
        long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) * (1L << 20);

    /// <summary>What was found for this build and these GPUs before, or null if it hasn't been asked.</summary>
    public LlamaFeatures? Cached(string exe, string gpuKey)
    {
        var key = KeyFor(exe, gpuKey);
        if (key is null) return null;
        lock (Gate) return Load().FirstOrDefault(e => e.Key == key)?.ToFeatures();
    }

    /// <summary>
    /// Runs <c>--list-devices</c> and <c>--help</c> and keeps the answers. Null when llama-server
    /// could not be run at all; nothing is kept then, so the next detection asks again.
    /// </summary>
    public async Task<LlamaFeatures?> ProbeAsync(string exe, string gpuKey, CancellationToken ct)
    {
        if (Cached(exe, gpuKey) is { } known) return known;

        var devices = await RunAsync(exe, "--list-devices", ct).ConfigureAwait(false);
        var help = await RunAsync(exe, "--help", ct).ConfigureAwait(false);
        if (devices is null && help is null) return null;

        var features = new LlamaFeatures(devices is null ? null : ParseDevices(devices), help is not null && HelpListsFit(help));
        if (KeyFor(exe, gpuKey) is { } key)
        {
            lock (Gate)
            {
                // A few builds are remembered, so switching to a custom build and back asks nothing.
                var entries = Load().Where(e => e.Key != key).Take(3).Prepend(Entry.From(key, features)).ToList();
                Save(entries);
            }
        }
        return features;
    }

    private static async Task<string?> RunAsync(string exe, string option, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var result = await ProcessRunner.RunAsync(exe, [option],
                workingDirectory: Path.GetDirectoryName(exe), ct: timeout.Token).ConfigureAwait(false);
            // Older builds print an unknown option's error and exit non-zero; that output says
            // nothing about devices, and the parsers find nothing in it.
            return result.StdOut + "\n" + result.StdErr;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static string? KeyFor(string exe, string gpuKey)
    {
        try
        {
            var file = new FileInfo(exe);
            return file.Exists
                ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{gpuKey}"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private List<Entry> Load()
    {
        try
        {
            return File.Exists(cacheFile)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(cacheFile), Json) ?? []
                : [];
        }
        catch (Exception) { return []; }
    }

    private void Save(List<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception) { /* the next detection asks again */ }
    }

    private sealed record Entry(string Key, List<DeviceMemory>? Devices, bool SupportsFit)
    {
        public static Entry From(string key, LlamaFeatures f) => new(key, f.Devices?.ToList(), f.SupportsFit);
        public LlamaFeatures ToFeatures() => new(Devices, SupportsFit);
    }
}
