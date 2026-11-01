using System.Text;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;

namespace ReelsMagician.Core.Rendering;

/// <summary>
/// Renders a single 1080×1920 still with the captions burned in, so the caption table can show
/// exactly what the style will look like over real footage instead of an approximation.
/// </summary>
public sealed class PreviewRenderer(FFmpegTool ffmpeg, IProgressSink sink)
{
    /// <summary>
    /// Renders the frame at <paramref name="reelTime"/> of the planned reel. Falls back to a
    /// flat backdrop when no source frame is available yet, so the style is still previewable
    /// before the framing stage has run.
    /// </summary>
    public async Task<string> RenderAsync(
        string? sourcePath,
        MediaInfo? sourceInfo,
        CropPath? crop,
        double sourceTime,
        IReadOnlyList<CaptionGroup> captions,
        SubtitleStyle style,
        double reelTime,
        string workDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);

        // A still shows one instant, so exactly one caption line is drawn, with the word that is
        // being spoken at that moment marked.
        var group = captions.FirstOrDefault(g => reelTime >= g.Start - 0.001 && reelTime <= g.End + 0.001)
                 ?? captions.FirstOrDefault();

        // Entrance animations would render mid-flight in a still, so the preview shows the
        // settled state instead.
        var stillStyle = style.Clone();
        stillStyle.Animation = CaptionAnimation.None;

        var assPath = Path.Combine(workDir, "preview.ass");
        var ass = group is null
            ? AssBuilder.Build([], stillStyle)
            : AssBuilder.BuildStill(group, ActiveWordIndex(group, reelTime), stillStyle);

        await File.WriteAllTextAsync(assPath, ass, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var output = Path.Combine(workDir, "preview.png");
        var args = new List<string>();
        var filters = new List<string>();

        if (sourcePath is not null && sourceInfo is not null && File.Exists(sourcePath))
        {
            args.AddRange(["-ss", FFmpegTool.Fmt(sourceTime), "-i", sourcePath]);
            // Before the framing stage has run there is no crop path; the chain then takes a
            // centre cut of the frame rather than squeezing the whole picture into 9:16.
            filters.AddRange(VerticalFilterChain.Build(sourceInfo, crop, AssBuilder.PlayResX, AssBuilder.PlayResY));
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i",
                $"color=c=0x1B1B22:s={AssBuilder.PlayResX}x{AssBuilder.PlayResY}:d=1"]);
        }

        filters.Add($"ass={Path.GetFileName(assPath)}");
        args.AddRange(["-vf", string.Join(',', filters), "-frames:v", "1", output]);

        await ffmpeg.RunAsync(args, 0, "preview", "Rendering caption preview", ct, workingDirectory: workDir)
            .ConfigureAwait(false);

        if (!File.Exists(output))
            throw new InvalidOperationException("The preview frame could not be rendered.");

        sink.Debug("preview", $"Preview rendered at reel time {reelTime:0.##}s");
        return output;
    }

    /// <summary>
    /// Which word of the line is marked at <paramref name="time"/>. This mirrors the rendered
    /// events, where a word stays marked until the next one begins: in the pause between two
    /// words the preview must show the word just spoken, not the one still to come.
    /// </summary>
    public static int ActiveWordIndex(CaptionGroup group, double time)
    {
        for (var i = 0; i + 1 < group.Words.Count; i++)
        {
            if (time < group.Words[i + 1].Start) return i;
        }
        return Math.Max(0, group.Words.Count - 1);
    }

    /// <summary>
    /// Picks a good instant to preview: the middle of the longest caption line, where the most
    /// text is on screen.
    /// </summary>
    public static double ChoosePreviewTime(IReadOnlyList<CaptionGroup> captions)
    {
        if (captions.Count == 0) return 0;
        var best = captions.OrderByDescending(g => g.Words.Count).ThenBy(g => g.Start).First();
        return best.Words.Count > 1
            ? (best.Words[best.Words.Count / 2].Start + best.Words[best.Words.Count / 2].End) / 2
            : (best.Start + best.End) / 2;
    }

    /// <summary>Maps a reel timestamp back to the source clip and time that produced it.</summary>
    public static (int ClipIndex, double SourceTime) ToSource(
        IReadOnlyList<RenderClip> clips, double fade, double reelTime)
    {
        double offset = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            var duration = clips[i].Range.Duration;
            if (reelTime <= offset + duration || i == clips.Count - 1)
                return (i, clips[i].Range.Start + Math.Clamp(reelTime - offset, 0, duration));
            offset += duration - fade;
        }
        return (0, clips.Count > 0 ? clips[0].Range.Start : 0);
    }
}
