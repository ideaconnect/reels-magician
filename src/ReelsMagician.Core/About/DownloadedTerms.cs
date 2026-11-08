using ReelsMagician.Core.Localization;
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
            Ui[nameof(Strings.Terms_Ffmpeg_Installed)], Ui[nameof(Strings.Terms_Ffmpeg_System)]),
        DownloadedTool.Whisper => Build(tools.WhisperCliPath, tools.WhisperBuild, dataRoot,
            Ui[nameof(Strings.Terms_Whisper_Cuda)]),
        DownloadedTool.Llama => Build(tools.LlamaServerPath, tools.LlamaBuild, dataRoot,
            Ui[nameof(Strings.Terms_Llama_Cuda)]),
        DownloadedTool.WhisperModel => Model(tools.WhisperModelPath),
        DownloadedTool.LlmModel => Model(tools.LlmModelPath),
        DownloadedTool.FaceModel => Model(tools.FaceModelPath),
        DownloadedTool.YtDlp => Tool(tools.YtDlpPath, dataRoot, required: false,
            Ui[nameof(Strings.Terms_YtDlp_Installed)], Ui[nameof(Strings.Terms_YtDlp_System)]),
        DownloadedTool.JsRuntime => Tool(tools.JsRuntimePath, dataRoot, required: false,
            Ui[nameof(Strings.Terms_Installed)], Ui[nameof(Strings.Terms_Deno_System)]),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    // The states are read on the About tab, in the interface's language.
    private static Lang Ui => Lang.Ui;

    private static InstallState Missing(bool required) => required
        ? new(ComponentState.RequiredMissing, Ui[nameof(Strings.Terms_NotInstalled)])
        : new(ComponentState.OptionalMissing, Ui[nameof(Strings.Terms_NotDownloaded)]);

    private static InstallState Tool(string? path, string dataRoot, bool required, string downloaded, string onSystem) =>
        path is null ? Missing(required)
        : IsUnder(path, dataRoot) ? new(ComponentState.Installed, downloaded)
        : new(ComponentState.System, onSystem);

    private static InstallState Build(string? path, BuildKind? kind, string dataRoot, string cuda) =>
        path is null ? Missing(required: true)
        : !IsUnder(path, dataRoot) ? new(ComponentState.System, Ui[nameof(Strings.Terms_OwnBuild)])
        : kind == BuildKind.Cuda ? new(ComponentState.Installed, cuda)
        : new(ComponentState.Installed, kind is { } k
            ? Ui.Format(nameof(Strings.Terms_InstalledBuild), ToolBuilds.Label(k))
            : Ui[nameof(Strings.Terms_Installed)]);

    private static InstallState Model(string? path) =>
        path is null ? Missing(required: true)
            : new(ComponentState.Installed, Ui.Format(nameof(Strings.Terms_InstalledFile), Path.GetFileName(path)));

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/>, not merely a name sharing its start.</summary>
    public static bool IsUnder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, ToolPaths.PathComparison);
    }
}
