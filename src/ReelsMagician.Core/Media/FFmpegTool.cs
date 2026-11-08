using System.Globalization;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Media;

/// <summary>
/// ffmpeg invocations shared by the transcription, framing and rendering stages.
/// Long encodes are driven through <c>-progress pipe:1</c> so the UI shows a real percentage
/// instead of a spinner.
/// </summary>
public sealed class FFmpegTool(string ffmpegPath, IProgressSink sink)
{
    public string Path { get; } = ffmpegPath;

    /// <summary>Runs ffmpeg, translating its progress stream into fractional progress reports.</summary>
    public async Task RunAsync(
        IEnumerable<string> args,
        double expectedDurationSeconds,
        string stage,
        string label,
        CancellationToken ct,
        string? workingDirectory = null)
    {
        var full = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
        if (expectedDurationSeconds > 0) { full.Add("-progress"); full.Add("pipe:1"); full.Add("-nostats"); }
        full.AddRange(args);

        var tail = new Queue<string>();

        var result = await ProcessRunner.RunAsync(Path, full,
            workingDirectory: workingDirectory,
            onStdOutLine: line =>
            {
                if (expectedDurationSeconds <= 0) return;
                if (!line.StartsWith("out_time_ms=", StringComparison.Ordinal)) return;
                if (!long.TryParse(line.AsSpan("out_time_ms=".Length), out var us)) return;
                var done = us / 1_000_000.0;
                var frac = Math.Clamp(done / expectedDurationSeconds, 0, 1);
                sink.Report(stage, $"{label} — {TimeSpan.FromSeconds(done):hh\\:mm\\:ss} / " +
                                   $"{TimeSpan.FromSeconds(expectedDurationSeconds):hh\\:mm\\:ss}", frac);
            },
            onStdErrLine: line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                tail.Enqueue(line);
                while (tail.Count > 25) tail.Dequeue();
                sink.Debug("ffmpeg", line);
            },
            captureOutput: false,
            ct: ct).ConfigureAwait(false);

        if (!result.Success)
            throw new ToolFailedException("ffmpeg", result.ExitCode, string.Join('\n', tail));
    }

    /// <summary>Extracts the 16 kHz mono PCM stream whisper.cpp expects.</summary>
    public async Task<string> ExtractSpeechWavAsync(
        string videoPath, string outputPath, double durationSeconds, CancellationToken ct)
    {
        sink.Report("transcribe", Lang.Ui[nameof(Strings.Media_ExtractingAudio_Running)], 0);
        await RunAsync(
        [
            "-i", videoPath,
            "-vn",
            "-map", "0:a:0",
            "-ac", "1",
            "-ar", "16000",
            "-c:a", "pcm_s16le",
            outputPath,
        ], durationSeconds, "transcribe", Lang.Ui[nameof(Strings.Media_ExtractingAudio)], ct).ConfigureAwait(false);

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1024)
            throw new InvalidOperationException(Lang.Ui[nameof(Strings.Media_NoAudio)]);
        return outputPath;
    }

    /// <summary>
    /// Samples frames from a time range as JPEGs, scaled down for analysis. Sampling a small
    /// window at a low rate keeps subject tracking fast even on a 4K master.
    /// </summary>
    public async Task<IReadOnlyList<(double Time, string File)>> SampleFramesAsync(
        string videoPath, double start, double duration, double fps, int analysisWidth,
        string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.EnumerateFiles(outputDir, "*.jpg")) File.Delete(stale);

        var pattern = System.IO.Path.Combine(outputDir, "f_%06d.jpg");
        await RunAsync(
        [
            "-ss", Fmt(start),
            "-i", videoPath,
            "-t", Fmt(duration),
            // The height follows the *display* aspect (dar), so an anamorphic source is analysed
            // with square pixels and the detector does not see squeezed faces.
            "-vf", $"fps={Fmt(fps)},scale=w={analysisWidth}:h=round({analysisWidth}/dar/2)*2:flags=bilinear",
            "-q:v", "4",
            "-an", "-sn",
            pattern,
        ], duration, "framing", Lang.Ui[nameof(Strings.Media_SamplingFrames)], ct).ConfigureAwait(false);

        var files = Directory.GetFiles(outputDir, "f_*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();
        // Frame N is emitted for the midpoint of its 1/fps slot.
        return [.. files.Select((f, i) => (start + (i + 0.5) / fps, f))];
    }

    /// <summary>Renders a single frame as a preview image.</summary>
    public async Task ExtractStillAsync(
        string videoPath, double time, string outputPath, int width, CancellationToken ct)
    {
        await RunAsync(
        [
            "-ss", Fmt(time),
            "-i", videoPath,
            "-frames:v", "1",
            "-vf", $"scale=w={width}:h=round({width}/dar/2)*2",
            "-q:v", "3",
            outputPath,
        ], 0, "preview", Lang.Ui[nameof(Strings.Media_ExtractingFrame)], ct).ConfigureAwait(false);
    }

    /// <summary>Formats a number for an ffmpeg argument — always invariant, never a comma decimal.</summary>
    public static string Fmt(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Escapes a Windows path for use inside an ffmpeg filtergraph argument.</summary>
    public static string EscapeFilterPath(string path)
    {
        // Inside a filtergraph, backslashes and colons must be escaped, and so must the
        // separators ffmpeg uses between filters and options.
        var normalized = path.Replace('\\', '/');
        return normalized.Replace(":", "\\:").Replace("'", "\\'").Replace(",", "\\,").Replace("[", "\\[").Replace("]", "\\]");
    }
}
