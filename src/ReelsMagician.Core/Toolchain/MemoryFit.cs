using System.Globalization;
using ReelsMagician.Core.Localization;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.Core.Toolchain;

/// <summary>Which memory a model runs in: the graphics card's, or the computer's own.</summary>
public enum MemoryKind { Graphics, System }

/// <summary>
/// The memory a model would run in on this computer: its kind, how much there is (null when
/// nothing reports it) and the device it belongs to.
/// </summary>
public sealed record MemoryBudget(MemoryKind Kind, long? Bytes, string? Device = null)
{
    /// <summary>
    /// What a model may take of it before it probably won't fit: all of the graphics memory, but
    /// the computer's own memory less what the system and the app keep for themselves.
    /// </summary>
    public long? Usable => Bytes is not { } total ? null
        : Kind == MemoryKind.System ? Math.Max(0, total - MemoryFit.SystemReserveBytes) : total;
}

/// <summary>A model's estimated need, held against the memory it would run in.</summary>
/// <param name="Model">The model's short name, as sentences use it: "Qwen3 8B (Q6_K)". The log's, in English.</param>
/// <param name="Context">The context the LLM runs with; null for Whisper, which has none to set.</param>
/// <param name="UiModel">The short name in the interface's language, for the texts on screen.</param>
public sealed record FitAssessment(string Model, long NeedBytes, MemoryBudget Budget, int? Context = null,
    string? UiModel = null)
{
    /// <summary>True when the estimate is more than the memory there is. Unknown memory never warns.</summary>
    public bool MayNotFit => Budget.Usable is { } usable && NeedBytes > usable;

    /// <summary>What the model needs, for the line under its drop-down: "Needs about 9.7 GB of graphics memory at a 32k context."</summary>
    public string NeedText => Need(Lang.Ui);

    /// <summary>The same in a language of its own, such as the log's.</summary>
    public string Need(Lang lang) => Context is { } context
        ? lang.Format(Budget.Kind == MemoryKind.Graphics ? nameof(Strings.Fit_Need_Graphics_Context) : nameof(Strings.Fit_Need_System_Context),
            MemoryFit.Format(NeedBytes, lang), MemoryFit.FormatContext(context))
        : lang.Format(Budget.Kind == MemoryKind.Graphics ? nameof(Strings.Fit_Need_Graphics) : nameof(Strings.Fit_Need_System),
            MemoryFit.Format(NeedBytes, lang));

    /// <summary>Why it may not fit, what that risks, and that it can still be tried; null when it fits.</summary>
    public string? Warning => Risk is { } risk ? risk + " " + Lang.Ui[nameof(Strings.Fit_CanStillTry)] : null;

    /// <summary>The warning without its last sentence, for the question asked before a run.</summary>
    public string? Risk => RiskIn(Lang.Ui, UiModel ?? Model);

    /// <summary>The risk in a language of its own, with the model named as that language names it.</summary>
    public string? RiskIn(Lang lang, string model)
    {
        if (!MayNotFit || Budget.Bytes is not { } total) return null;
        var key = (Budget.Kind, Context is null) switch
        {
            (MemoryKind.Graphics, true) => nameof(Strings.Fit_Risk_Graphics),
            (MemoryKind.Graphics, false) => nameof(Strings.Fit_Risk_Graphics_Context),
            (_, true) => nameof(Strings.Fit_Risk_System),
            _ => nameof(Strings.Fit_Risk_System_Context),
        };
        return lang.Format(key, model, MemoryFit.Format(NeedBytes, lang), MemoryFit.Format(total, lang),
            MemoryFit.Format(MemoryFit.SystemReserveBytes, lang));
    }
}

/// <summary>
/// Estimates how much memory a model needs and compares it with what this computer has, so the
/// Setup tab can warn before a model is chosen that probably won't fit (issue #54). It never
/// blocks anything: the warning says what may happen, and the model can still be tried.
///
/// The memory a model runs in depends on the build: llama.cpp's GPU builds use the graphics
/// card, which llama.cpp itself reports once it is installed, and its CPU build and every Linux
/// whisper.cpp use the computer's memory. When nothing reports the memory, nothing warns.
/// </summary>
public static class MemoryFit
{
    /// <summary>
    /// llama.cpp's working buffers beyond the weights and the KV cache. Measured with Qwen3 8B on a
    /// Radeon 760M through Vulkan, they took 143 MiB at a 4k context and 199 MiB at 32k; the
    /// rest of half a GiB covers the context CUDA keeps on an NVIDIA card.
    /// </summary>
    public const long LlamaBufferBytes = 512L << 20;

    /// <summary>What the system, the desktop and the app itself keep of the computer's memory.</summary>
    public const long SystemReserveBytes = 2L << 30;

    /// <summary>Weights, plus the KV cache for the whole context, plus llama.cpp's buffers.</summary>
    public static long LlmNeed(LlmModelSpec model, int context) =>
        model.ApproxBytes + Math.Max(0, context) * model.KvBytesPerToken + LlamaBufferBytes;

    public static FitAssessment AssessLlm(LlmModelSpec model, int context, MemoryBudget budget) =>
        new(model.ShortName, LlmNeed(model, context), budget, context, model.UiShortName);

    public static FitAssessment AssessWhisper(WhisperModelSpec model, MemoryBudget budget) =>
        new(model.ShortName, model.RuntimeBytes, budget, UiModel: model.UiShortName);

    /// <summary>
    /// The memory Qwen runs in. llama.cpp's own list of devices wins: it is the build the analysis
    /// will use, and it counts what Vulkan offers an integrated GPU on top of its own VRAM. A list
    /// with no GPU on it means llama.cpp runs on the processor. Before llama.cpp is installed, or
    /// when it could not be asked, the build this machine gets decides, with the memory the
    /// system reports for the card.
    /// </summary>
    public static MemoryBudget LlmBudget(Toolset tools, long? systemBytes, TargetOs os)
    {
        if (tools.LlamaServerPath is not null && tools.LlamaFeatures?.Devices is { } devices)
            return devices.Count > 0
                ? new MemoryBudget(MemoryKind.Graphics, devices.Sum(d => d.TotalBytes),
                    string.Join(" + ", devices.Select(d => d.Name)))
                : SystemBudget(systemBytes);

        var build = tools.LlamaServerPath is not null && tools.LlamaBuild is { } installed
            ? installed
            : ToolBuilds.ExpectedLlama(tools.Gpu, os);
        return BudgetFor(build, tools.Gpu, systemBytes);
    }

    /// <summary>
    /// The memory Whisper runs in: the graphics card's with a CUDA or Vulkan build, the computer's
    /// with any other, which is every build the app installs on Linux.
    /// </summary>
    public static MemoryBudget WhisperBudget(Toolset tools, long? systemBytes, TargetOs os)
    {
        var build = tools.WhisperCliPath is not null && tools.WhisperBuild is { } installed
            ? installed
            : ToolBuilds.ExpectedWhisper(tools.Gpu, os);
        return BudgetFor(build, tools.Gpu, systemBytes);
    }

    private static MemoryBudget BudgetFor(BuildKind build, GpuCapabilities? gpu, long? systemBytes) => build switch
    {
        BuildKind.Cuda => CudaBudget(gpu),
        BuildKind.Vulkan => VulkanBudget(gpu),
        _ => SystemBudget(systemBytes),
    };

    private static MemoryBudget SystemBudget(long? systemBytes) =>
        new(MemoryKind.System, systemBytes is > 0 ? systemBytes : null, "this computer's memory");

    /// <summary>CUDA spreads a model over every NVIDIA card, so their memory adds up.</summary>
    private static MemoryBudget CudaBudget(GpuCapabilities? gpu)
    {
        var nvidia = gpu?.Adapters.Where(a => a.Vendor == GpuVendor.Nvidia).ToList() ?? [];
        var names = nvidia.Count > 0 ? string.Join(" + ", nvidia.Select(a => a.Name)) : null;
        if (gpu?.Cuda?.TotalMemoryBytes is { } cuda) return new(MemoryKind.Graphics, cuda, names);
        return nvidia.Count > 0 && nvidia.All(a => a.MemoryBytes is not null)
            ? new(MemoryKind.Graphics, nvidia.Sum(a => a.MemoryBytes!.Value), names)
            : new(MemoryKind.Graphics, null, names);
    }

    /// <summary>
    /// Without llama.cpp's own list, the card with a Vulkan driver and the most memory: a desktop
    /// with an integrated and a discrete GPU runs the model on the discrete one.
    /// </summary>
    private static MemoryBudget VulkanBudget(GpuCapabilities? gpu)
    {
        var card = gpu?.Adapters
            .Where(a => a.HasVulkanDriver && a.MemoryBytes is not null)
            .MaxBy(a => a.MemoryBytes);
        return new(MemoryKind.Graphics, card?.MemoryBytes, card?.Name);
    }

    /// <summary>
    /// The computer's memory as .NET sees it, which also respects a container's limit; null when it
    /// can't tell.
    /// </summary>
    public static long? SystemMemoryBytes()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0 ? bytes : null;
    }

    /// <summary>The chosen Whisper model against the memory it would run in on this computer.</summary>
    public static FitAssessment ForWhisper(Toolset tools, AppSettings settings) =>
        AssessWhisper(ModelCatalog.ResolveWhisper(settings.WhisperModelId),
            WhisperBudget(tools, SystemMemoryBytes(), ToolBuilds.Current));

    /// <summary>The chosen Qwen model, at the context it will get, against the memory it would run in.</summary>
    public static FitAssessment ForLlm(Toolset tools, AppSettings settings) =>
        AssessLlm(ModelCatalog.ResolveLlm(settings.LlmModelId), settings.LlamaContextSize,
            LlmBudget(tools, SystemMemoryBytes(), ToolBuilds.Current));

    /// <summary>
    /// A size as a person reads it, in the binary units graphics cards are sold in, so an 8 GiB
    /// card is "8 GB": one decimal below 10 GB, whole numbers above.
    /// </summary>
    public static string Format(long bytes) => Format(bytes, Lang.Ui);

    /// <summary>The same, with the decimal separator of a language: "9,7 GB" in Polish, and the log's "9.7 GB".</summary>
    public static string Format(long bytes, Lang lang)
    {
        var gib = bytes / (double)(1L << 30);
        if (gib >= 9.95) return string.Create(lang.Formats, $"{gib:0} GB");
        if (gib >= 1) return string.Create(lang.Formats, $"{gib:0.#} GB");
        return string.Create(lang.Formats, $"{Math.Max(1, bytes / (double)(1L << 20)):0} MB");
    }

    /// <summary>A context size the way llama.cpp's users name it: 16384 tokens is "16k".</summary>
    public static string FormatContext(int tokens) =>
        tokens >= 1024 && tokens % 1024 == 0
            ? $"{tokens / 1024}k"
            : tokens.ToString("N0", CultureInfo.InvariantCulture);
}
