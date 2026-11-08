using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Toolchain;

/// <summary>Resolved locations of every external binary and model the pipeline needs.</summary>
public sealed class Toolset
{
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public string? YtDlpPath { get; set; }

    /// <summary>JavaScript runtime yt-dlp uses to extract YouTube formats.</summary>
    public string? JsRuntimePath { get; set; }
    public string? WhisperCliPath { get; set; }
    public string? LlamaServerPath { get; set; }
    public string? WhisperModelPath { get; set; }
    public string? LlmModelPath { get; set; }
    public string? FaceModelPath { get; set; }

    /// <summary>The whisper.cpp build found, which <see cref="WhisperBackend"/> names.</summary>
    public Backend WhisperBackendInfo { get; set; } = Backend.None;

    /// <summary>The llama.cpp build found, which <see cref="LlamaBackend"/> names.</summary>
    public Backend LlamaBackendInfo { get; set; } = Backend.None;

    /// <summary>The whisper.cpp build, in the interface's language: "Vulkan (GPU) — whisper-cli".</summary>
    public string WhisperBackend => WhisperBackendInfo.Describe(Lang.Ui);

    public string LlamaBackend => LlamaBackendInfo.Describe(Lang.Ui);

    /// <summary>Set when whisper.cpp is a Fallback build: a lesser build than this machine supports.</summary>
    public FallbackBuild? WhisperFallback { get; set; }

    /// <summary>Set when llama.cpp is a Fallback build: a lesser build than this machine supports.</summary>
    public FallbackBuild? LlamaFallback { get; set; }

    /// <summary>What the Setup tab says under whisper.cpp when it is a Fallback build.</summary>
    public string? WhisperWarning => WhisperFallback?.Warning(Lang.Ui);

    public string? LlamaWarning => LlamaFallback?.Warning(Lang.Ui);

    /// <summary>The kind of whisper.cpp build the app installed, as its marker names it; null for any other.</summary>
    public BuildKind? WhisperBuild { get; set; }

    /// <summary>The kind of llama.cpp build the app installed, as its marker names it; null for any other.</summary>
    public BuildKind? LlamaBuild { get; set; }

    public GpuCapabilities? Gpu { get; set; }

    /// <summary>
    /// What the installed llama-server reports about its devices and options; null until it has
    /// been asked (<see cref="LlamaProbe"/>), or when it could not be.
    /// </summary>
    public LlamaFeatures? LlamaFeatures { get; set; }

    public bool HasFfmpeg => FfmpegPath is not null && FfprobePath is not null;
    public bool HasYtDlp => YtDlpPath is not null;
    public bool HasJsRuntime => JsRuntimePath is not null;
    public bool HasWhisper => WhisperCliPath is not null && WhisperModelPath is not null;
    public bool HasLlm => LlamaServerPath is not null && LlmModelPath is not null;
    public bool HasFaceModel => FaceModelPath is not null;

    /// <summary>Both optional tools a compatible video link needs: yt-dlp and its JavaScript runtime.</summary>
    public bool HasLinkTools => HasYtDlp && HasJsRuntime;

    /// <summary>Everything needed to run the full pipeline end to end.</summary>
    public bool IsComplete => HasFfmpeg && HasWhisper && HasLlm && HasFaceModel;

    public string Ffmpeg => FfmpegPath ?? throw NotInstalled("ffmpeg");
    public string Ffprobe => FfprobePath ?? throw NotInstalled("ffprobe");
    public string YtDlp => YtDlpPath ?? throw NotInstalled("yt-dlp");
    public string WhisperCli => WhisperCliPath ?? throw NotInstalled("whisper.cpp");
    public string LlamaServer => LlamaServerPath ?? throw NotInstalled("llama.cpp");

    private static InvalidOperationException NotInstalled(string tool) =>
        new(Lang.Ui.Format(nameof(Strings.Tool_NotInstalled), tool));

    /// <summary>The required components still absent, in the interface's language. The optional link tools are not listed.</summary>
    public IEnumerable<string> MissingComponents() => MissingComponents(Lang.Ui);

    /// <summary>The same in a language of its own, such as the log's.</summary>
    public IEnumerable<string> MissingComponents(Lang lang)
    {
        if (!HasFfmpeg) yield return "ffmpeg / ffprobe";
        if (WhisperCliPath is null) yield return "whisper.cpp";
        if (WhisperModelPath is null) yield return lang[nameof(Strings.Missing_WhisperModel)];
        if (LlamaServerPath is null) yield return "llama.cpp";
        if (LlmModelPath is null) yield return lang[nameof(Strings.Missing_QwenModel)];
        if (!HasFaceModel) yield return lang[nameof(Strings.Missing_FaceModel)];
    }
}

/// <summary>
/// A whisper.cpp or llama.cpp build as it was found: its program's file name, the kind of build
/// its marker names, or whether it has Vulkan's library beside it when there is no marker.
/// </summary>
public sealed record Backend(string? ExeName, BuildKind? Kind = null, bool HasVulkanRuntime = false)
{
    public static Backend None { get; } = new((string?)null);

    /// <summary>"Vulkan (GPU) — whisper-cli", "custom build — llama-server" or "not installed".</summary>
    public string Describe(Lang lang)
    {
        if (ExeName is null) return lang[nameof(Strings.Backend_NotInstalled)];
        var kind = Kind is { } k ? ToolBuilds.Label(k)
            : HasVulkanRuntime ? ToolBuilds.Label(BuildKind.Vulkan)
            : lang[nameof(Strings.Backend_Custom)];
        return $"{kind} — {ExeName}";
    }
}

/// <summary>A Fallback build: one below the best this machine supports, as it was installed.</summary>
public sealed record FallbackBuild(string Tool, BuildKind Installed, BuildKind Expected)
{
    public string Warning(Lang lang) => lang.Format(nameof(Strings.Fallback_Warning), Tool,
        ToolBuilds.Label(Installed), ToolBuilds.Label(Expected));
}

public enum ComponentState
{
    /// <summary>Required and absent: "Install everything missing" fetches it.</summary>
    RequiredMissing,

    /// <summary>Optional and absent: fetched only if the user agrees to the download.</summary>
    OptionalMissing,

    /// <summary>Downloaded by the app into its data folder.</summary>
    Installed,

    /// <summary>Found already installed on the system, outside the data folder.</summary>
    System,
}

/// <param name="Warning">Shown next to the component when it works but not as well as it could, such as a Fallback build.</param>
public sealed record ComponentStatus(
    string Key, string Name, ComponentState State, string Detail, long ApproxBytes, string? Warning = null)
{
    public bool NeedsDownload => State is ComponentState.RequiredMissing or ComponentState.OptionalMissing;
}
