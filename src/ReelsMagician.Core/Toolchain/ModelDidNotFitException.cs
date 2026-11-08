using System.Text.RegularExpressions;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Toolchain;

/// <summary>
/// A model that ran out of memory while llama.cpp or whisper.cpp was loading or running it. The
/// message is what the user reads instead of the tool's log; the log lines that showed it stay
/// on the exception and in the activity log.
/// </summary>
public sealed partial class ModelDidNotFitException(string message, IReadOnlyList<string> evidence)
    : InvalidOperationException(message)
{
    /// <summary>The lines of the tool's log that said it ran out of memory.</summary>
    public IReadOnlyList<string> Evidence { get; } = evidence;

    /// <summary>The error log gets the lines that showed the shortage, which the message leaves out.</summary>
    public override string ToString() =>
        base.ToString() + Environment.NewLine + "From the tool's log:" + Environment.NewLine +
        string.Join(Environment.NewLine, Evidence.Select(l => "  " + l));

    /// <summary>
    /// The exception for a tool's log, or null when the log shows no shortage of memory. A shortage
    /// on the graphics card and one in the computer's memory are told apart, as each has its own
    /// remedy in the end: a smaller model.
    /// </summary>
    public static ModelDidNotFitException? FromLog(string log)
    {
        var evidence = OutOfMemoryLines(log);
        if (evidence.Count == 0) return null;
        var graphics = !evidence.All(IsHostMemory);
        return new ModelDidNotFitException(graphics ? GraphicsMessage : SystemMessage, evidence);
    }

    /// <summary>What the user reads, in the interface's language, when the graphics memory ran out.</summary>
    public static string GraphicsMessage => Lang.Ui[nameof(Strings.Fit_DidNotFit_Graphics)];

    /// <summary>The same when the computer's own memory ran out.</summary>
    public static string SystemMessage => Lang.Ui[nameof(Strings.Fit_DidNotFit_System)];

    /// <summary>
    /// The lines in which llama.cpp, ggml's Vulkan and CUDA backends, Mesa's Radeon driver or the
    /// C++ runtime report a failed allocation: <c>ErrorOutOfDeviceMemory</c>, <c>failed to allocate
    /// Vulkan0 buffer</c>, <c>CUDA error: out of memory</c>, <c>radv/amdgpu: Not enough memory for
    /// command submission</c>, <c>std::bad_alloc</c>. A lost device on its own is not one: a GPU
    /// that hangs for any other reason ends the same way.
    /// </summary>
    public static IReadOnlyList<string> OutOfMemoryLines(string log) =>
        log.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && OutOfMemoryPattern().IsMatch(l))
            .Distinct()
            .Take(5)
            .ToList();

    // A shortage on the processor's side is one of the computer's own memory.
    private static bool IsHostMemory(string line) => HostMemoryPattern().IsMatch(line);

    [GeneratedRegex(@"ErrorOutOf(Device|Host)Memory|out of memory|not enough memory|failed to allocate|unable to allocate|memory allocation of size \d+ failed|bad_alloc|cannot allocate memory",
        RegexOptions.IgnoreCase)]
    private static partial Regex OutOfMemoryPattern();

    // "CPU buffer", "ggml_backend_cpu_buffer_type": the processor's side, so the computer's memory.
    [GeneratedRegex(@"(?<![a-z])cpu(?![a-z])|ErrorOutOfHostMemory|bad_alloc|cannot allocate memory", RegexOptions.IgnoreCase)]
    private static partial Regex HostMemoryPattern();
}
