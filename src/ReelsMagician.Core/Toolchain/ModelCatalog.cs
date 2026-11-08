namespace ReelsMagician.Core.Toolchain;

public sealed record WhisperModelSpec(
    string Id, string DisplayName, string FileName, string Url, long ApproxBytes, string Notes)
{
    public override string ToString() => DisplayName;
}

public sealed record LlmModelSpec(
    string Id, string DisplayName, string FileName, string Url, long ApproxBytes,
    int RecommendedContext, string Notes)
{
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

    public static readonly IReadOnlyList<WhisperModelSpec> WhisperModels =
    [
        new("small-q5", "Whisper small (quantised) — fastest",
            "ggml-small-q5_1.bin", WhisperBase + "ggml-small-q5_1.bin", 190_000_000,
            "Good for clean speech. Multilingual."),
        new("medium-q5", "Whisper medium (quantised) — balanced",
            "ggml-medium-q5_0.bin", WhisperBase + "ggml-medium-q5_0.bin", 539_000_000,
            "Noticeably better punctuation than small."),
        new("large-v3-turbo-q5", "Whisper large-v3-turbo (quantised) — recommended",
            "ggml-large-v3-turbo-q5_0.bin", WhisperBase + "ggml-large-v3-turbo-q5_0.bin", 574_000_000,
            "Near large-v3 accuracy at a fraction of the cost. Best default."),
        new("large-v3-turbo", "Whisper large-v3-turbo (f16) — most accurate",
            "ggml-large-v3-turbo.bin", WhisperBase + "ggml-large-v3-turbo.bin", 1_620_000_000,
            "Full precision turbo. Slower, best word timings."),
    ];

    public static readonly IReadOnlyList<LlmModelSpec> LlmModels =
    [
        new("qwen3-4b-q4", "Qwen3 4B Instruct (Q4_K_M) — light",
            "Qwen3-4B-Q4_K_M.gguf",
            "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
            2_500_000_000, 16384,
            "Runs comfortably on 8 GB of VRAM."),
        new("qwen3-8b-q4", "Qwen3 8B Instruct (Q4_K_M) — recommended",
            "Qwen3-8B-Q4_K_M.gguf",
            "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf",
            5_030_000_000, 32768,
            "Best segment picks. Wants ~6 GB of VRAM."),
        new("qwen3-8b-q6", "Qwen3 8B Instruct (Q6_K) — highest quality",
            "Qwen3-8B-Q6_K.gguf",
            "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q6_K.gguf",
            6_730_000_000, 32768,
            "Marginal gain over Q4_K_M; needs ~8 GB of VRAM."),
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

    public static WhisperModelSpec DefaultWhisper => WhisperModels[2];
    public static LlmModelSpec DefaultLlm => LlmModels[1];

    public static WhisperModelSpec ResolveWhisper(string? id) =>
        WhisperModels.FirstOrDefault(m => m.Id == id) ?? DefaultWhisper;

    public static LlmModelSpec ResolveLlm(string? id) =>
        LlmModels.FirstOrDefault(m => m.Id == id) ?? DefaultLlm;
}
