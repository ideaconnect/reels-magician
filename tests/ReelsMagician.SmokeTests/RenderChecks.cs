using System.Text;
using OpenCvSharp;
using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// Renders a real reel from a synthetic 4K source. This is the check that matters most: it
/// exercises the crop expression quoting, the xfade offsets, the audio crossfade and the ASS
/// burn-in against the actual ffmpeg build, where a filtergraph mistake shows up as a failure
/// rather than as a subtly wrong video.
/// </summary>
public static class RenderChecks
{
    /// <summary>
    /// Renders the caption line once per highlighted word and measures where the text actually
    /// lands, guarding the reported defect: the line jumping sideways as the highlight sweeps.
    ///
    /// This has to be measured from pixels. The cause was \fscx changing the glyph advance width,
    /// which no amount of inspecting the ASS text would reveal — the tags all looked reasonable.
    /// </summary>
    public static async Task CaptionStabilityAsync(string ffmpegPath)
    {
        Check.Section("Captions do not move as the highlight sweeps");

        var work = Path.Combine(Path.GetTempPath(), "reelsmagician-stability");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var sink = NullProgressSink.Instance;
        var ffmpeg = new FFmpegTool(ffmpegPath, sink);
        var ct = CancellationToken.None;

        // Words of deliberately different lengths: a reflow shows up most between a short word
        // and a long one.
        var words = new List<CaptionWord>
        {
            new(0.0, 1.0, "REF"),
            new(1.0, 2.0, "KOMPUTERACH"),
            new(2.0, 3.0, "OSOBISTYCH"),
            new(3.0, 4.0, "ZACZAL"),
        };

        foreach (var style in SubtitleStyle.Presets().Where(s => s.Mode == CaptionMode.WordHighlight))
        {
            var groups = AssBuilder.BuildGroups(words, style);
            var assPath = Path.Combine(work, "s.ass");
            await File.WriteAllTextAsync(assPath, AssBuilder.Build(groups, style),
                new UTF8Encoding(false), ct).ConfigureAwait(false);

            // Only ever compare frames from the SAME caption line. Presets wrap at different
            // word counts, and two different lines legitimately have different widths — measuring
            // across a line break would report a "drift" of a hundred-odd pixels that is really
            // just the next caption being shorter.
            var line = groups.OrderByDescending(g => g.Words.Count).First();
            if (line.Words.Count < 3) continue;

            var lefts = new List<int>();
            var rights = new List<int>();

            // Skip the line's first word: it is highlighted there and is allowed to grow, and it
            // is also the one carrying the entrance animation.
            for (var i = 1; i < line.Words.Count; i++)
            {
                var at = (line.Words[i].Start + line.Words[i].End) / 2;
                var png = Path.Combine(work, $"f{i}.png");

                await ffmpeg.RunAsync(
                [
                    "-f", "lavfi", "-i", $"color=c=0x101018:s={AssBuilder.PlayResX}x{AssBuilder.PlayResY}:d=10:r=30",
                    "-vf", $"ass={Path.GetFileName(assPath)}",
                    "-ss", FFmpegTool.Fmt(at), "-frames:v", "1", png,
                ], 0, "test", "caption frame", ct, workingDirectory: work).ConfigureAwait(false);

                var (left, right) = InkColumns(png);
                lefts.Add(left);
                rights.Add(right);
            }

            var driftLeft = lefts.Max() - lefts.Min();
            var driftRight = rights.Max() - rights.Min();

            Check.True(driftLeft <= 1,
                $"[{style.Name}] the line's left edge is still ({driftLeft} px drift across {lefts.Count} words)");
            Check.True(driftRight <= 1,
                $"[{style.Name}] the line's right edge is still ({driftRight} px drift)");

            // The emphasis must still be visible, or "stable" would just mean "does nothing".
            var ass = AssBuilder.Build(groups, style);
            Check.True(ass.Contains("\\c&H", StringComparison.Ordinal),
                $"[{style.Name}] the spoken word is still recoloured");

            // Look only inside the per-word override block. A line-level \fscx in the entrance
            // animation is fine — it zooms the whole line about its centre — but an \fscx on the
            // highlighted word itself is the reflow that started all this.
            Check.True(!HighlightOverridesContain(ass, "\\fscx"),
                $"[{style.Name}] no horizontal scaling inside the per-word highlight");
        }
    }

    /// <summary>
    /// True when any per-word highlight override — a brace group starting with \c — contains
    /// <paramref name="tag"/>. Line-level overrides are ignored.
    /// </summary>
    public static bool HighlightOverridesContain(string ass, string tag)
    {
        var from = 0;
        while (true)
        {
            var open = ass.IndexOf("{\\c&H", from, StringComparison.Ordinal);
            if (open < 0) return false;
            var close = ass.IndexOf('}', open);
            if (close < 0) return false;
            if (ass.AsSpan(open, close - open).Contains(tag, StringComparison.Ordinal)) return true;
            from = close + 1;
        }
    }

    /// <summary>Leftmost and rightmost inked pixel columns in a rendered caption frame.</summary>
    private static (int Left, int Right) InkColumns(string png)
    {
        using var image = Cv2.ImRead(png, ImreadModes.Color);
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using var mask = new Mat();
        // The backdrop is 0x101018; anything appreciably brighter is text.
        Cv2.Threshold(gray, mask, 40, 255, ThresholdTypes.Binary);
        using var colSums = new Mat();
        Cv2.Reduce(mask, colSums, ReduceDimension.Row, ReduceTypes.Max, MatType.CV_8U);

        int left = -1, right = -1;
        var cols = colSums.Cols;
        for (var x = 0; x < cols; x++)
        {
            if (colSums.At<byte>(0, x) == 0) continue;
            if (left < 0) left = x;
            right = x;
        }
        return (left, right);
    }

    public static async Task RunAsync()
    {
        Check.Section("End-to-end render (real ffmpeg)");

        var ffmpegPath = ToolPaths.FindOnPath(ToolPaths.ExeName("ffmpeg"))
                      ?? ToolPaths.FindExe(new ToolPaths().FfmpegDir, ToolPaths.ExeName("ffmpeg"));
        var ffprobePath = ToolPaths.FindOnPath(ToolPaths.ExeName("ffprobe"))
                       ?? ToolPaths.FindExe(new ToolPaths().FfmpegDir, ToolPaths.ExeName("ffprobe"));

        if (ffmpegPath is null || ffprobePath is null)
        {
            Console.WriteLine("  SKIP  ffmpeg is not installed — run the app's Setup tab first.");
            return;
        }
        Console.WriteLine($"  using {ffmpegPath}");

        await CaptionStabilityAsync(ffmpegPath);
        await FrameGeometryAsync(ffmpegPath, ffprobePath);

        var work = Path.Combine(Path.GetTempPath(), "reelsmagician-smoke");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var sink = new DelegateProgressSink(
            entry => { if (entry.Level >= ReelsMagician.Core.Diagnostics.LogLevel.Warn) Console.WriteLine($"        [{entry.Level}] {entry.Message}"); },
            (_, _, _) => { });

        var ffmpeg = new FFmpegTool(ffmpegPath, sink);
        var ct = CancellationToken.None;

        // A 30-second 4K source with a moving element and a tone, so cropping and audio are real work.
        var source = Path.Combine(work, "source.mp4");
        await ffmpeg.RunAsync(
        [
            "-f", "lavfi", "-i", "testsrc2=size=3840x2160:rate=30:duration=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=30",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-shortest", source,
        ], 30, "test", "Building the synthetic source", ct);

        Check.True(File.Exists(source), "synthetic 4K source was created");

        var probe = new FFprobe(ffprobePath, sink);
        var info = await probe.ProbeAsync(source, ct);
        Check.Equal(3840, info.DisplayWidth, "source probes as 4K wide");
        Check.True(info.Is4K, "source is recognised as 4K");
        Check.True(info.HasAudio, "source has an audio track");

        // Two clips, joined with one short crossfade: 6s + 6s − fade.
        var clips = new List<RenderClip>
        {
            new(new ClipRange(2, 8, 0, 0, "first clip"), MovingPath(info, 0.30, 0.62)),
            new(new ClipRange(15, 21, 1, 1, "second clip"), StaticPath(info)),
        };

        var options = new RenderOptions(CrossfadeSeconds: 0.10, Preset: "ultrafast", Crf: 30);
        var fade = ReelRenderer.ResolveCrossfade(clips, options);
        Check.Near(0.10, fade, 0.001, "the configured crossfade survives clamping");

        var words = new List<CaptionWord>();
        for (var i = 0; i < 24; i++) words.Add(new CaptionWord(i * 0.5, i * 0.5 + 0.45, $"word{i}"));
        var style = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.WordHighlight);
        var captions = AssBuilder.BuildGroups(words, style);

        var output = Path.Combine(work, "reel.mp4");
        var renderer = new ReelRenderer(ffmpeg, sink);

        try
        {
            var result = await renderer.RenderAsync(
                source, info, clips, captions, style, options, work, output, ct);

            Check.True(File.Exists(output), "the reel was rendered");

            var reel = await probe.ProbeAsync(output, ct);
            Check.Equal(1080, reel.DisplayWidth, "reel is 1080 wide");
            Check.Equal(1920, reel.DisplayHeight, "reel is 1920 tall");
            Check.True(reel.HasAudio, "reel kept its audio");
            Check.True(reel.HasSquarePixels, "reel pixels are square");
            Check.True(clips[0].Crop.CropWidth * 1920 == clips[0].Crop.CropHeight * 1080,
                $"the 4K crop {clips[0].Crop.CropWidth}×{clips[0].Crop.CropHeight} is exactly 9:16");

            var expected = clips.Sum(c => c.Range.Duration) - fade;
            Check.Near(expected, reel.DurationSeconds, 0.35,
                "reel duration equals the clip total minus one crossfade");
            Check.Near(expected, result.Duration, 0.01, "the reported duration matches the plan");

            // Audio must stay in step with the picture: a video-only fade would leave the
            // audio one fade longer than the video on every transition.
            var audioDuration = await StreamDurationAsync(ffprobePath, output, "a", ct);
            var videoDuration = await StreamDurationAsync(ffprobePath, output, "v", ct);
            Check.Near(videoDuration, audioDuration, 0.20,
                $"audio and video end together (v={videoDuration:0.###}s, a={audioDuration:0.###}s)");

            Check.True(new FileInfo(output).Length > 50_000, "the reel has real content");
        }
        catch (Exception ex)
        {
            Check.True(false, $"render failed: {ex.Message}");
        }

        // A single-clip reel must not attempt a crossfade at all.
        try
        {
            var single = Path.Combine(work, "single.mp4");
            await renderer.RenderAsync(source, info, [clips[0]], captions, style, options, work, single, ct);
            var probed = await probe.ProbeAsync(single, ct);
            Check.Near(6, probed.DurationSeconds, 0.35, "a one-clip reel keeps its full length");
            Check.Equal(1080, probed.DisplayWidth, "a one-clip reel is still 1080 wide");
        }
        catch (Exception ex)
        {
            Check.True(false, $"single-clip render failed: {ex.Message}");
        }

        // Plain captions go through a different ASS path, so render them too.
        try
        {
            var plainStyle = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.PlainText);
            var plain = Path.Combine(work, "plain.mp4");
            await renderer.RenderAsync(source, info, clips,
                AssBuilder.BuildGroups(words, plainStyle), plainStyle, options, work, plain, ct);
            Check.True(File.Exists(plain), "plain-caption reel rendered");
        }
        catch (Exception ex)
        {
            Check.True(false, $"plain-caption render failed: {ex.Message}");
        }

        // An anamorphic source (1440×1080 stored, 16:9 displayed) must come out as a 9:16 reel of
        // square pixels, planned and cut in the widened geometry rather than the squeezed one.
        try
        {
            var anamorphic = Path.Combine(work, "anamorphic.mp4");
            await ffmpeg.RunAsync(
            [
                "-f", "lavfi", "-i", "testsrc2=size=1440x1080:rate=25:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:duration=4",
                "-vf", "setsar=4/3",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "96k", "-shortest", anamorphic,
            ], 4, "test", "Building the anamorphic source", ct);

            var squeezed = await probe.ProbeAsync(anamorphic, ct);
            Check.Equal(4, squeezed.SarNum, "the anamorphic source probes with a 4:3 pixel aspect");
            Check.Equal(3, squeezed.SarDen, "the anamorphic source probes with a 4:3 pixel aspect");
            Check.Equal(1920, squeezed.DisplayWidth, "the anamorphic source displays 1920 wide");

            var clip = new RenderClip(new ClipRange(0.5, 3.5, 0, 0, "anamorphic clip"), StaticPath(squeezed));
            Check.True(clip.Crop.CropWidth * 1920 == clip.Crop.CropHeight * 1080,
                $"the anamorphic crop {clip.Crop.CropWidth}×{clip.Crop.CropHeight} is exactly 9:16 in display pixels");

            var wide = Path.Combine(work, "anamorphic-reel.mp4");
            await renderer.RenderAsync(anamorphic, squeezed, [clip], captions, style, options, work, wide, ct);
            var probed = await probe.ProbeAsync(wide, ct);
            Check.Equal(1080, probed.DisplayWidth, "the anamorphic reel is 1080 wide");
            Check.Equal(1920, probed.DisplayHeight, "the anamorphic reel is 1920 tall");
            Check.True(probed.HasSquarePixels, "the anamorphic reel has square pixels");
        }
        catch (Exception ex)
        {
            Check.True(false, $"anamorphic render failed: {ex.Message}");
        }

        Console.WriteLine($"  output kept at {work}");
    }

    /// <summary>
    /// Measures, from pixels, that the picture is never stretched: a square drawn on a 16:9
    /// source must still be square in the 9:16 output. Checked for the caption preview before
    /// framing has run (no crop path, the case that used to squeeze the whole frame), for the
    /// preview with a tracked crop, and for a rendered clip.
    /// </summary>
    public static async Task FrameGeometryAsync(string ffmpegPath, string ffprobePath)
    {
        Check.Section("The picture is cropped and scaled, never stretched");

        var work = Path.Combine(Path.GetTempPath(), "reelsmagician-geometry");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var sink = NullProgressSink.Instance;
        var ffmpeg = new FFmpegTool(ffmpegPath, sink);
        var probe = new FFprobe(ffprobePath, sink);
        var ct = CancellationToken.None;

        // A 400×400 white square in the middle of a black 1920×1080 frame.
        var source = Path.Combine(work, "square.mp4");
        await ffmpeg.RunAsync(
        [
            "-f", "lavfi", "-i", "color=c=black:s=1920x1080:d=3:r=30",
            "-vf", "drawbox=x=760:y=340:w=400:h=400:color=white:t=fill",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", source,
        ], 3, "test", "Building the square source", ct);
        var info = await probe.ProbeAsync(source, ct);

        var style = SubtitleStyle.Presets().First();
        var preview = new PreviewRenderer(ffmpeg, sink);

        // No crop path yet: the preview must take a centre cut, so the square keeps its shape
        // and grows by the uniform height ratio 1920/1080.
        var uncropped = await preview.RenderAsync(
            source, info, null, 1.0, [], style, 0, Path.Combine(work, "uncropped"), ct);
        var box = InkBox(uncropped);
        Check.True(Math.Abs(box.W - box.H) <= 2, $"without a crop path the square stays square ({box.W}×{box.H})");
        Check.Near(400 * 1920 / 1080.0, box.W, 3, "without a crop path the square is scaled by the height ratio");

        // With a tracked crop the scale is 1080/603 on both axes.
        var cropped = await preview.RenderAsync(
            source, info, StaticPath(info), 1.0, [], style, 0, Path.Combine(work, "cropped"), ct);
        box = InkBox(cropped);
        Check.True(Math.Abs(box.W - box.H) <= 2, $"with a crop path the square stays square ({box.W}×{box.H})");
        Check.Near(400 * 1080 / 603.0, box.W, 3, "with a crop path the square is scaled by the crop ratio");

        // And in the rendered clip itself.
        var reel = Path.Combine(work, "reel.mp4");
        var clip = new RenderClip(new ClipRange(0.5, 2.5, 0, 0, "square"), StaticPath(info));
        await new ReelRenderer(ffmpeg, sink).RenderAsync(
            source, info, [clip], [], style, new RenderOptions(Preset: "ultrafast", Crf: 30), work, reel, ct);
        var frame = Path.Combine(work, "reel-frame.png");
        await ffmpeg.RunAsync(["-ss", "1", "-i", reel, "-frames:v", "1", frame], 0, "test", "Extracting a reel frame", ct);
        box = InkBox(frame);
        Check.True(Math.Abs(box.W - box.H) <= 2, $"in the rendered reel the square stays square ({box.W}×{box.H})");
        Check.Near(400 * 1080 / 603.0, box.W, 3, "in the rendered reel the square is scaled by the crop ratio");
    }

    /// <summary>Bounding box of the bright pixels in a frame.</summary>
    private static (int W, int H) InkBox(string png)
    {
        using var image = Cv2.ImRead(png, ImreadModes.Grayscale);
        using var mask = new Mat();
        Cv2.Threshold(image, mask, 128, 255, ThresholdTypes.Binary);
        using var points = new Mat();
        Cv2.FindNonZero(mask, points);
        if (points.Empty()) return (0, 0);
        var rect = Cv2.BoundingRect(points);
        return (rect.Width, rect.Height);
    }

    private static CropPath MovingPath(MediaInfo info, double fromFraction, double toFraction)
    {
        var (w, h) = CropPlanner.ComputeCropSize(info.DisplayWidth, info.DisplayHeight, 1080, 1920);
        var maxX = info.DisplayWidth - w;
        return new CropPath
        {
            CropWidth = w,
            CropHeight = h,
            SourceWidth = info.DisplayWidth,
            SourceHeight = info.DisplayHeight,
            DominantSubject = SubjectKind.Face,
            FaceCoverage = 0.9,
            Keyframes =
            [
                new(0, maxX * fromFraction, 0),
                new(3, maxX * (fromFraction + toFraction) / 2, 0),
                new(6, maxX * toFraction, 0),
            ],
        };
    }

    private static CropPath StaticPath(MediaInfo info)
    {
        var (w, h) = CropPlanner.ComputeCropSize(info.DisplayWidth, info.DisplayHeight, 1080, 1920);
        return new CropPath
        {
            CropWidth = w,
            CropHeight = h,
            SourceWidth = info.DisplayWidth,
            SourceHeight = info.DisplayHeight,
            DominantSubject = SubjectKind.Salient,
            Keyframes = [new(0, (info.DisplayWidth - w) / 2.0, 0)],
        };
    }

    private static async Task<double> StreamDurationAsync(
        string ffprobe, string file, string streamType, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(ffprobe,
        [
            "-v", "error",
            "-select_streams", streamType,
            "-show_entries", "stream=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            file,
        ], ct: ct);

        var line = result.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return double.TryParse(line, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
