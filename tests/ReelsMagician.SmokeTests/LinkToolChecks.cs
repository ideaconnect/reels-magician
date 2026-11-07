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

        Check.True(manager.Describe(settings, installed).All(c => c.Key is not ("ytdlp" or "jsruntime")),
            "the link tools are not listed among the required tools");
        Check.True(!new Toolset().MissingComponents().Any(m => m.Contains("yt-dlp") || m.Contains("JavaScript")),
            "an absent link tool is not reported as missing");

        // A row that said Missing would promise that "Install everything missing" fetches it.
        var absent = manager.DescribeLinkTools(new Toolset());
        Check.Equal(2, absent.Count, "yt-dlp and its runtime are both offered as optional");
        Check.True(absent.All(c => c.State == ComponentState.NotInstalled), "they show as not installed, not missing");

        var present = manager.DescribeLinkTools(new Toolset { YtDlpPath = "yt-dlp.exe", JsRuntimePath = "deno.exe" });
        Check.True(present.All(c => !c.NeedsDownload), "nothing is offered for download once they are present");

        Directory.Delete(root, recursive: true);
    }
}
