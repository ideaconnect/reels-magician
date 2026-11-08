using System.Globalization;
using System.Text;
using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Rendering;

public sealed record RenderOptions(
    int Width = 1080,
    int Height = 1920,
    /// <summary>
    /// Transition length. Kept very short on purpose: long enough to soften the cut, short
    /// enough that the matching audio crossfade is inaudible.
    /// </summary>
    double CrossfadeSeconds = 0.10,
    int Fps = 30,
    int Crf = 19,
    string Preset = "medium",
    int AudioBitrateKbps = 192,
    bool BurnSubtitles = true,
    bool NormalizeAudio = true)
{
    public const double MaxSafeCrossfade = 0.35;
}

/// <summary>What one clip contributes to the finished reel.</summary>
public sealed record RenderClip(ClipRange Range, CropPath Crop);

public sealed record RenderResult(string OutputPath, double Duration, string SubtitlePath);

/// <summary>
/// Encodes the reel in two passes: each clip is cropped, tracked and normalised on its own,
/// then the clips are joined with a short cross dissolve and the captions are burned in.
///
/// Video and audio are crossfaded with the *same* duration. xfade consumes its overlap from the
/// timeline, so fading only the picture would slide the audio later by one transition length
/// each time; matching them keeps sound and picture locked together, and at ~0.1s the audio
/// blend is below the threshold of notice.
/// </summary>
public sealed class ReelRenderer(FFmpegTool ffmpeg, IProgressSink sink)
{
    public async Task<RenderResult> RenderAsync(
        string sourcePath,
        MediaInfo sourceInfo,
        IReadOnlyList<RenderClip> clips,
        IReadOnlyList<CaptionGroup> captions,
        SubtitleStyle style,
        RenderOptions options,
        string workDir,
        string outputPath,
        CancellationToken ct)
    {
        if (clips.Count == 0)
            throw new InvalidOperationException(Lang.Ui[nameof(Strings.Render_NoClips)]);

        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var fade = ResolveCrossfade(clips, options);
        var hasAudio = sourceInfo.HasAudio;
        if (!hasAudio) sink.Warn("render", "The source has no audio track — the reel will be silent.");

        // Pass 1: one normalised vertical clip per range.
        var clipFiles = new List<string>(clips.Count);
        for (var i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(workDir, $"clip_{i:00}.mp4");
            sink.Report("render", Lang.Ui.Format(nameof(Strings.Render_Clip), i + 1, clips.Count), (double)i / (clips.Count + 1));
            await RenderClipAsync(sourcePath, sourceInfo, clips[i], file, options, hasAudio, ct).ConfigureAwait(false);
            clipFiles.Add(file);
        }

        // Pass 2: join, burn captions, mux.
        var subtitlePath = Path.Combine(workDir, "captions.ass");
        await File.WriteAllTextAsync(subtitlePath,
            AssBuilder.Build(captions, style), new UTF8Encoding(false), ct).ConfigureAwait(false);

        var total = clips.Sum(c => c.Range.Duration) - fade * (clips.Count - 1);
        sink.Report("render", Lang.Ui[nameof(Strings.Render_Joining)], (double)clips.Count / (clips.Count + 1));

        await JoinAsync(clipFiles, subtitlePath, outputPath, options, fade, total, hasAudio, workDir, ct)
            .ConfigureAwait(false);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException(Lang.Ui[nameof(Strings.Render_NoOutput)]);

        sink.Report("render", Lang.Ui[nameof(Strings.Render_Complete)], 1);
        sink.Info("render", $"Wrote {outputPath} ({AssetSize(outputPath)}, {total:0.0}s)");
        return new RenderResult(outputPath, total, subtitlePath);
    }

    /// <summary>
    /// Crops and scales one range to the vertical target, following its tracked crop path. The
    /// picture is only ever cropped and uniformly scaled; see <see cref="VerticalFilterChain"/>.
    /// </summary>
    private async Task RenderClipAsync(
        string sourcePath, MediaInfo sourceInfo, RenderClip clip, string outputPath,
        RenderOptions options, bool hasAudio, CancellationToken ct)
    {
        var filters = VerticalFilterChain.Build(sourceInfo, clip.Crop, options.Width, options.Height);
        filters.Add($"fps={options.Fps}");
        filters.Add("format=yuv420p");

        var args = new List<string>
        {
            "-ss", FFmpegTool.Fmt(clip.Range.Start),
            "-i", sourcePath,
            "-t", FFmpegTool.Fmt(clip.Range.Duration),
            "-vf", string.Join(',', filters),
            "-c:v", "libx264",
            // The intermediate is re-encoded once more in the join pass, so it is kept
            // visually lossless to avoid stacking compression artefacts.
            "-crf", "16",
            "-preset", "veryfast",
            "-pix_fmt", "yuv420p",
            "-map", "0:v:0",
        };

        if (hasAudio)
        {
            args.AddRange(["-map", "0:a:0", "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2"]);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(["-movflags", "+faststart", outputPath]);
        await ffmpeg.RunAsync(args, clip.Range.Duration, "render",
            Lang.Ui.Format(nameof(Strings.Render_ClipLabel), Path.GetFileNameWithoutExtension(outputPath)), ct).ConfigureAwait(false);
    }

    /// <summary>Chains the clips with matched video and audio crossfades, then burns the captions.</summary>
    private async Task JoinAsync(
        List<string> clipFiles,
        string subtitlePath,
        string outputPath,
        RenderOptions options,
        double fade,
        double expectedDuration,
        bool hasAudio,
        string workDir,
        CancellationToken ct)
    {
        var args = new List<string>();
        foreach (var file in clipFiles) { args.Add("-i"); args.Add(file); }

        var graph = new StringBuilder();
        var durations = new List<double>();
        foreach (var file in clipFiles) durations.Add(await ProbeDurationAsync(file, ct).ConfigureAwait(false));

        // xfade needs a common timebase or it silently mistimes the overlap.
        for (var i = 0; i < clipFiles.Count; i++)
            graph.Append($"[{i}:v]settb=AVTB,fps={options.Fps}[v{i}];");

        string videoLabel;
        if (clipFiles.Count == 1)
        {
            videoLabel = "v0";
        }
        else
        {
            var previous = "v0";
            double elapsed = durations[0];

            for (var i = 1; i < clipFiles.Count; i++)
            {
                // Each transition starts one fade length before the running edit point.
                var offset = elapsed - fade;
                var label = $"x{i}";
                graph.Append($"[{previous}][v{i}]xfade=transition=fade:duration={FFmpegTool.Fmt(fade)}" +
                             $":offset={FFmpegTool.Fmt(Math.Max(0, offset))}[{label}];");
                previous = label;
                elapsed += durations[i] - fade;
            }
            videoLabel = previous;
        }

        if (options.BurnSubtitles && File.Exists(subtitlePath))
        {
            // Referenced by bare filename with ffmpeg's cwd set to workDir, which avoids
            // having to escape a Windows drive letter inside the filtergraph.
            graph.Append($"[{videoLabel}]ass={Path.GetFileName(subtitlePath)}[vout];");
            videoLabel = "vout";
        }

        string? audioLabel = null;
        if (hasAudio)
        {
            for (var i = 0; i < clipFiles.Count; i++)
                graph.Append($"[{i}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a{i}];");

            if (clipFiles.Count == 1)
            {
                audioLabel = "a0";
            }
            else
            {
                var previous = "a0";
                for (var i = 1; i < clipFiles.Count; i++)
                {
                    var label = $"ax{i}";
                    // Triangular curves over ~100 ms: enough to kill the click at the splice,
                    // short enough that no word is audibly blended.
                    graph.Append($"[{previous}][a{i}]acrossfade=d={FFmpegTool.Fmt(fade)}:c1=tri:c2=tri[{label}];");
                    previous = label;
                }
                audioLabel = previous;
            }

            if (options.NormalizeAudio)
            {
                graph.Append($"[{audioLabel}]loudnorm=I=-14:TP=-1.5:LRA=11[aout];");
                audioLabel = "aout";
            }
        }

        var graphText = graph.ToString().TrimEnd(';');
        args.AddRange(["-filter_complex", graphText, "-map", $"[{videoLabel}]"]);
        if (audioLabel is not null) args.AddRange(["-map", $"[{audioLabel}]"]);

        args.AddRange([
            "-c:v", "libx264",
            "-crf", options.Crf.ToString(CultureInfo.InvariantCulture),
            "-preset", options.Preset,
            "-profile:v", "high",
            "-level", "4.2",
            "-pix_fmt", "yuv420p",
            "-r", options.Fps.ToString(CultureInfo.InvariantCulture),
        ]);

        if (audioLabel is not null)
            args.AddRange(["-c:a", "aac", "-b:a", $"{options.AudioBitrateKbps}k", "-ar", "48000", "-ac", "2"]);
        else
            args.Add("-an");

        args.AddRange(["-movflags", "+faststart", "-shortest", outputPath]);

        await ffmpeg.RunAsync(args, expectedDuration, "render", Lang.Ui[nameof(Strings.Render_FinalEncode)], ct, workingDirectory: workDir)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Clamps the transition so it can never eat a whole clip: xfade consumes its duration from
    /// both sides, and a fade longer than the shortest clip produces a broken edit.
    /// </summary>
    public static double ResolveCrossfade(IReadOnlyList<RenderClip> clips, RenderOptions options)
    {
        if (clips.Count < 2) return 0;
        var shortest = clips.Min(c => c.Range.Duration);
        var limit = Math.Min(RenderOptions.MaxSafeCrossfade, shortest / 3);
        return Math.Clamp(options.CrossfadeSeconds, 0.02, Math.Max(0.02, limit));
    }

    /// <summary>Reads a rendered intermediate's real duration, which drives the xfade offsets.</summary>
    private async Task<double> ProbeDurationAsync(string path, CancellationToken ct)
    {
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg.Path)!, Toolchain.ToolPaths.ExeName("ffprobe"));
        if (File.Exists(probe))
        {
            try
            {
                var result = await ProcessRunner.RunAsync(probe,
                [
                    "-v", "error", "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1", path,
                ], ct: ct).ConfigureAwait(false);

                if (double.TryParse(result.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                    return d;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sink.Debug("render", $"Could not probe {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(Lang.Ui.Format(nameof(Strings.Render_NoDuration), Path.GetFileName(path)));
    }

    private static string AssetSize(string path) =>
        Toolchain.AssetDownloader.Humanize(new FileInfo(path).Length);
}
