using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

public static class LinkToolChecks
{
    /// <summary>
    /// yt-dlp and its runtime are downloaded only after the user agrees, so the default install
    /// must never fetch them or count them as something missing. With every required component
    /// seeded, the install has nothing left to do and needs no network.
    /// </summary>
    public static void Run()
    {
        Check.Section("Link tools are opt-in");

        var root = Path.Combine(Path.GetTempPath(), "reelsmagician-linktools");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

        var paths = new ToolPaths(root);
        paths.EnsureCreated();
        var settings = new AppSettings();

        void Seed(string dir, string name)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), []);
        }

        Seed(paths.FfmpegDir, ToolPaths.ExeName("ffmpeg"));
        Seed(paths.FfmpegDir, ToolPaths.ExeName("ffprobe"));
        Seed(paths.WhisperDir, ToolPaths.ExeName("whisper-cli"));
        Seed(paths.LlamaDir, ToolPaths.ExeName("llama-server"));
        Seed(paths.Models, ModelCatalog.ResolveWhisper(settings.WhisperModelId).FileName);
        Seed(paths.Models, ModelCatalog.ResolveLlm(settings.LlmModelId).FileName);
        Seed(paths.Models, ModelCatalog.FaceProtoFileName);
        Seed(paths.Models, ModelCatalog.FaceWeightsFileName);

        var manager = new ToolchainManager(paths, NullProgressSink.Instance);
        var installed = manager.EnsureAsync(settings, CancellationToken.None).GetAwaiter().GetResult();

        Check.True(installed.IsComplete, "the required tools alone make the toolchain complete");
        Check.True(ToolPaths.FindExe(paths.YtDlpDir, ToolPaths.ExeName("yt-dlp")) is null, "the default install does not fetch yt-dlp");
        Check.True(ToolPaths.FindExe(paths.JsRuntimeDir, ToolPaths.ExeName("deno")) is null, "the default install does not fetch Deno");

        string[] linkKeys = [ToolchainManager.YtDlpKey, ToolchainManager.JsRuntimeKey];
        Check.True(manager.Describe(settings, installed).All(c => !linkKeys.Contains(c.Key)),
            "the link tools are not listed among the required tools");

        // Compared without reading any label: having the link tools or not changes nothing that
        // counts as missing.
        var withLinkTools = new Toolset { YtDlpPath = "yt-dlp", JsRuntimePath = "deno" };
        Check.True(new Toolset().MissingComponents().SequenceEqual(withLinkTools.MissingComponents()),
            "an absent link tool is not reported as missing");
        Check.True(withLinkTools.HasLinkTools && !new Toolset { YtDlpPath = "yt-dlp" }.HasLinkTools,
            "the link tools count as present only as a pair");

        // A row that said Missing would promise that "Install everything missing" fetches it.
        var absent = manager.DescribeLinkTools(new Toolset());
        Check.True(absent.Select(c => c.Key).SequenceEqual(linkKeys), "yt-dlp and its runtime are both offered as optional");
        Check.True(absent.All(c => c.State == ComponentState.OptionalMissing), "they show as optional, not as required and missing");
        Check.True(absent.All(c => c.NeedsDownload), "and as something that can be downloaded");

        var present = manager.DescribeLinkTools(withLinkTools);
        Check.True(present.All(c => !c.NeedsDownload), "nothing is offered for download once they are present");

        Directory.Delete(root, recursive: true);
    }
}
