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
    public string? FaceProtoPath { get; set; }
    public string? FaceWeightsPath { get; set; }

    public string WhisperBackend { get; set; } = "not installed";
    public string LlamaBackend { get; set; } = "not installed";

    /// <summary>Set when whisper.cpp is a Fallback build: a lesser build than this machine supports.</summary>
    public string? WhisperWarning { get; set; }

    /// <summary>Set when llama.cpp is a Fallback build: a lesser build than this machine supports.</summary>
    public string? LlamaWarning { get; set; }

    public GpuCapabilities? Gpu { get; set; }

    public bool HasFfmpeg => FfmpegPath is not null && FfprobePath is not null;
    public bool HasYtDlp => YtDlpPath is not null;
    public bool HasJsRuntime => JsRuntimePath is not null;
    public bool HasWhisper => WhisperCliPath is not null && WhisperModelPath is not null;
    public bool HasLlm => LlamaServerPath is not null && LlmModelPath is not null;
    public bool HasFaceModel => FaceProtoPath is not null && FaceWeightsPath is not null;

    /// <summary>Both optional tools a compatible video link needs: yt-dlp and its JavaScript runtime.</summary>
    public bool HasLinkTools => HasYtDlp && HasJsRuntime;

    /// <summary>Everything needed to run the full pipeline end to end.</summary>
    public bool IsComplete => HasFfmpeg && HasWhisper && HasLlm && HasFaceModel;

    public string Ffmpeg => FfmpegPath ?? throw new InvalidOperationException("ffmpeg is not installed.");
    public string Ffprobe => FfprobePath ?? throw new InvalidOperationException("ffprobe is not installed.");
    public string YtDlp => YtDlpPath ?? throw new InvalidOperationException("yt-dlp is not installed.");
    public string WhisperCli => WhisperCliPath ?? throw new InvalidOperationException("whisper.cpp is not installed.");
    public string LlamaServer => LlamaServerPath ?? throw new InvalidOperationException("llama.cpp is not installed.");

    /// <summary>The required components still absent. The optional link tools are not listed.</summary>
    public IEnumerable<string> MissingComponents()
    {
        if (!HasFfmpeg) yield return "ffmpeg / ffprobe";
        if (WhisperCliPath is null) yield return "whisper.cpp";
        if (WhisperModelPath is null) yield return "Whisper model";
        if (LlamaServerPath is null) yield return "llama.cpp";
        if (LlmModelPath is null) yield return "Qwen model";
        if (!HasFaceModel) yield return "face detector model";
    }
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
