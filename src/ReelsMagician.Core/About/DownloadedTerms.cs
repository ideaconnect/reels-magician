using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.About;

/// <summary>Whether a downloaded component is on this machine, and so which terms apply here.</summary>
/// <param name="State">Installed: the app's own download. System: one the app found and uses instead.</param>
public sealed record InstallState(ComponentState State, string Text)
{
    public bool IsPresent => State is ComponentState.Installed or ComponentState.System;
}

/// <summary>
/// The install state the About tab shows for each downloaded component. A tool the app
/// downloaded comes under the terms its row names, a CUDA build under NVIDIA's CUDA EULA as well,
/// and one found on the system under that copy's own terms, which the app can't know.
/// </summary>
public static class DownloadedTerms
{
    public static InstallState Describe(DownloadedTool tool, Toolset tools, string dataRoot) => tool switch
    {
        DownloadedTool.Ffmpeg => Tool(tools.HasFfmpeg ? tools.FfmpegPath : null, dataRoot, required: true,
            "Installed: BtbN's GPL build, so GPL-3.0 applies",
            "Not downloaded: the app uses the ffmpeg on your system, under that build's own terms"),
        DownloadedTool.Whisper => Build(tools.WhisperCliPath, tools.WhisperBuild, dataRoot,
            "Installed: the CUDA build, so NVIDIA's CUDA EULA covers the CUDA libraries in it"),
        DownloadedTool.Llama => Build(tools.LlamaServerPath, tools.LlamaBuild, dataRoot,
            "Installed: the CUDA build with NVIDIA's CUDA runtime, so NVIDIA's CUDA EULA covers those libraries"),
        DownloadedTool.WhisperModel => Model(tools.WhisperModelPath),
        DownloadedTool.LlmModel => Model(tools.LlmModelPath),
        DownloadedTool.FaceModel => Model(tools.FaceModelPath),
        DownloadedTool.YtDlp => Tool(tools.YtDlpPath, dataRoot, required: false,
            "Installed: the standalone build, so GPLv3+ applies",
            "Not downloaded: the app uses the yt-dlp on your PATH, under that copy's own terms"),
        DownloadedTool.JsRuntime => Tool(tools.JsRuntimePath, dataRoot, required: false,
            "Installed",
            "Not downloaded: the app uses the Deno on your PATH"),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    private static InstallState Missing(bool required) => required
        ? new(ComponentState.RequiredMissing, "Not installed")
        : new(ComponentState.OptionalMissing, "Not downloaded");

    private static InstallState Tool(string? path, string dataRoot, bool required, string downloaded, string onSystem) =>
        path is null ? Missing(required)
        : IsUnder(path, dataRoot) ? new(ComponentState.Installed, downloaded)
        : new(ComponentState.System, onSystem);

    private static InstallState Build(string? path, BuildKind? kind, string dataRoot, string cuda) =>
        path is null ? Missing(required: true)
        : !IsUnder(path, dataRoot) ? new(ComponentState.System, "Your own build, set in settings.json, under its own terms")
        : kind == BuildKind.Cuda ? new(ComponentState.Installed, cuda)
        : new(ComponentState.Installed, kind is { } k ? $"Installed: the {ToolBuilds.Label(k)} build" : "Installed");

    private static InstallState Model(string? path) =>
        path is null ? Missing(required: true) : new(ComponentState.Installed, $"Installed: {Path.GetFileName(path)}");

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/>, not merely a name sharing its start.</summary>
    public static bool IsUnder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, ToolPaths.PathComparison);
    }
}
