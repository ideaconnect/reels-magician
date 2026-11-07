using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// How a typed or pasted source is read (issue #2), and what the cleanup screen says it would
/// delete (issue #7).
/// </summary>
public static class SourceAndCleanupChecks
{
    public static void Run()
    {
        SourceInput();
        CleanupDescription();
    }

    private static void SourceInput()
    {
        Check.Section("A pasted source is read the same everywhere");

        Check.Equal("https://host/v", SourceResolver.NormaliseInput("  \"https://host/v\" "), "quotes around a link are dropped");
        Check.True(SourceResolver.LooksLikeUrl(SourceResolver.NormaliseInput("\"https://host/v\"")),
            "so a quoted link counts as a link, and is checked for ffmpeg and asked about like any other");
        Check.Equal(@"C:\videos\clip one.mp4", SourceResolver.NormaliseInput("\"C:\\videos\\clip one.mp4\""),
            "a path copied from Explorer loses its double quotes");
        Check.Equal("/home/u/clip one.mp4", SourceResolver.NormaliseInput("'/home/u/clip one.mp4'"),
            "a path copied from a Linux terminal loses its single quotes");
        Check.Equal("", SourceResolver.NormaliseInput(null), "nothing typed reads as empty");
    }

    private static void CleanupDescription()
    {
        Check.Section("Free up space names only what was downloaded");

        var root = Path.Combine(Path.GetTempPath(), "reelsmagician-cleanup-text");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var paths = new ToolPaths(root);
        paths.EnsureCreated();

        string Detail() => new DataFolderCleaner(paths, NullProgressSink.Instance).Survey()
            .First(i => i.Key == DataFolderCleaner.ToolsKey).Detail;

        void Seed(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "tool"), [1]);
        }

        Check.True(!Detail().Contains("yt-dlp"), "with nothing downloaded, yt-dlp is not claimed");

        Seed(paths.FfmpegDir);
        Seed(paths.WhisperDir);
        Seed(paths.LlamaDir);
        Check.Equal("ffmpeg, whisper.cpp and llama.cpp.", Detail(), "the required tools are named once installed");

        Seed(paths.YtDlpDir);
        Seed(paths.JsRuntimeDir);
        Check.Equal("ffmpeg, whisper.cpp, llama.cpp, yt-dlp and the JavaScript runtime.", Detail(),
            "the link tools are named only once the user has downloaded them");

        Directory.Delete(root, recursive: true);
    }
}
