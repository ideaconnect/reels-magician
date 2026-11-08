using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Toolchain;

/// <param name="RuntimeBytes">
/// About how much memory whisper.cpp takes with this model: its weights, its caches and its
/// working buffers. Measured as whisper-cli's peak resident memory with the app's own arguments.
/// </param>
public sealed record WhisperModelSpec(
    string Id, string DisplayName, string FileName, string Url, long ApproxBytes, long RuntimeBytes, string Notes)
{
    /// <summary>The name without the remark after the dash, for sentences: "Whisper large-v3-turbo (f16)".</summary>
    public string ShortName => ModelCatalog.ShortNameOf(DisplayName);

    /// <summary>The name as the interface shows it, in its language. <see cref="DisplayName"/> is the log's.</summary>
    public string UiName => ModelCatalog.UiText(Id, "Name");

    /// <summary>The note under the drop-down, in the interface's language.</summary>
    public string UiNotes => ModelCatalog.UiText(Id, "Notes");

    /// <summary>The short name in the interface's language, for sentences on screen.</summary>
    public string UiShortName => ModelCatalog.ShortNameOf(UiName);

    public override string ToString() => DisplayName;
}

/// <param name="KvBytesPerToken">
/// What each token of context costs in llama.cpp's KV cache at its default f16 precision:
/// layers × KV heads × head size × 2 (K and V) × 2 bytes.
/// </param>
public sealed record LlmModelSpec(
    string Id, string DisplayName, string FileName, string Url, long ApproxBytes,
    int RecommendedContext, long KvBytesPerToken, string Notes)
{
    /// <summary>The name without "Instruct" and the remark after the dash: "Qwen3 8B (Q6_K)".</summary>
    public string ShortName => ModelCatalog.ShortNameOf(DisplayName);

    /// <summary>The name as the interface shows it, in its language. <see cref="DisplayName"/> is the log's.</summary>
    public string UiName => ModelCatalog.UiText(Id, "Name");

    /// <summary>The note under the drop-down, in the interface's language.</summary>
    public string UiNotes => ModelCatalog.UiText(Id, "Notes");

    /// <summary>The short name in the interface's language, for sentences on screen.</summary>
    public string UiShortName => ModelCatalog.ShortNameOf(UiName);

    public override string ToString() => DisplayName;
}

/// <summary>
/// The models the app can fetch on its own. Whisper weights come from the official
/// whisper.cpp model repo; the LLM weights are the official Qwen GGUF releases, which
/// run on llama.cpp's CUDA or Vulkan backend.
/// </summary>
public static class ModelCatalog
{
    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    // Qwen3 4B and 8B both have 36 layers with 8 KV heads of 128 dimensions: 144 KiB per token.
    private const long Qwen3KvBytesPerToken = 36L * 8 * 128 * 2 * 2;

    // whisper-cli's peak resident memory with each model, measured on Linux with whisper.cpp 1.9.5
    // and the app's arguments over a 30 s clip (470, 1122, 797 and 1806 MiB), rounded up.
    public static readonly IReadOnlyList<WhisperModelSpec> WhisperModels =
    [
        new("small-q5", "Whisper small (quantised) — fastest",
            "ggml-small-q5_1.bin", WhisperBase + "ggml-small-q5_1.bin", 190_000_000, 500L << 20,
            "Good for clean speech. Multilingual."),
        new("medium-q5", "Whisper medium (quantised) — balanced",
            "ggml-medium-q5_0.bin", WhisperBase + "ggml-medium-q5_0.bin", 539_000_000, 1200L << 20,
            "Noticeably better punctuation than small."),
        new("large-v3-turbo-q5", "Whisper large-v3-turbo (quantised) — recommended",
            "ggml-large-v3-turbo-q5_0.bin", WhisperBase + "ggml-large-v3-turbo-q5_0.bin", 574_000_000, 850L << 20,
            "Near large-v3 accuracy at a fraction of the cost. Best default."),
        new("large-v3-turbo", "Whisper large-v3-turbo (f16) — most accurate",
            "ggml-large-v3-turbo.bin", WhisperBase + "ggml-large-v3-turbo.bin", 1_620_000_000, 1900L << 20,
            "Full precision turbo. Slower, best word timings."),
    ];

    // What each model needs is stated under its drop-down, worked out by MemoryFit for the
    // context it runs at, so the notes say nothing about memory themselves.
    public static readonly IReadOnlyList<LlmModelSpec> LlmModels =
    [
        new("qwen3-4b-q4", "Qwen3 4B Instruct (Q4_K_M) — light",
            "Qwen3-4B-Q4_K_M.gguf",
            "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
            2_500_000_000, 16384, Qwen3KvBytesPerToken,
            "The lightest choice, for smaller graphics cards."),
        new("qwen3-8b-q4", "Qwen3 8B Instruct (Q4_K_M) — recommended",
            "Qwen3-8B-Q4_K_M.gguf",
            "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf",
            5_030_000_000, 32768, Qwen3KvBytesPerToken,
            "Best segment picks."),
        new("qwen3-8b-q6", "Qwen3 8B Instruct (Q6_K) — highest quality",
            "Qwen3-8B-Q6_K.gguf",
            "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q6_K.gguf",
            6_730_000_000, 32768, Qwen3KvBytesPerToken,
            "Marginal gain over Q4_K_M."),
    ];

    /// <summary>
    /// YuNet, the face detector in OpenCV's model zoo: one small ONNX file, used to keep a speaker
    /// centred in the vertical crop. This version takes frames of any size, as OpenCV 5 needs.
    /// Pinned to the opencv_zoo commit that published it.
    /// </summary>
    public const string FaceModelFileName = "face_detection_yunet_2026may.onnx";
    public const string FaceModelUrl =
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/26cc381e4d2594bb9f47a26eb8fd96c94a13660d/models/face_detection_yunet/face_detection_yunet_2026may.onnx";

    public const long FaceModelApproxBytes = 230_000;

    /// <summary>
    /// The ResNet-10 SSD face detector that earlier versions downloaded. It is a Caffe model, which
    /// OpenCV 5 cannot read, so installing YuNet deletes it.
    /// </summary>
    public static readonly string[] RetiredFaceModelFileNames = ["face_detector.prototxt", "face_detector_res10.caffemodel"];

    /// <summary>
    /// A model's name or note in the interface's language, from the resource <c>Model_{id}_{part}</c>.
    /// The English resources hold the same words as the specs' DisplayName and Notes, which stay
    /// the log's; the logic checks keep the two equal.
    /// </summary>
    public static string UiText(string id, string part) => Lang.Ui[ResourceKey(id, part)];

    public static string ResourceKey(string id, string part) => $"Model_{id}_{part}";

    public static WhisperModelSpec DefaultWhisper => WhisperModels[2];
    public static LlmModelSpec DefaultLlm => LlmModels[1];

    public static WhisperModelSpec ResolveWhisper(string? id) =>
        WhisperModels.FirstOrDefault(m => m.Id == id) ?? DefaultWhisper;

    public static LlmModelSpec ResolveLlm(string? id) =>
        LlmModels.FirstOrDefault(m => m.Id == id) ?? DefaultLlm;

    /// <summary>A display name without the remark after its dash, and without "Instruct".</summary>
    public static string ShortNameOf(string displayName)
    {
        var dash = displayName.IndexOf(" — ", StringComparison.Ordinal);
        var name = dash < 0 ? displayName : displayName[..dash];
        return name.Replace(" Instruct", "", StringComparison.Ordinal).Trim();
    }
}
