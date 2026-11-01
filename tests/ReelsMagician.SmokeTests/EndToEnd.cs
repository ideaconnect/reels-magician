using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// Exercises the pipeline against the real toolchain: installs the tools, then runs a generated
/// speech video all the way through Whisper, Qwen, subject tracking and the final render.
/// </summary>
public static class EndToEnd
{
    private static IProgressSink CreateSink() => new DelegateProgressSink(
        entry =>
        {
            if (entry.Level == LogLevel.Debug) return;
            Console.WriteLine($"    [{entry.Stage}] {entry.Message}");
        },
        (stage, message, fraction) =>
        {
            var pct = fraction is null ? "" : $" {fraction.Value:P0}";
            Console.Write($"\r    {stage}: {message}{pct}".PadRight(110)[..110]);
            if (fraction is >= 1) Console.WriteLine();
        });

    public static async Task InstallAsync()
    {
        Check.Section("Toolchain install (real downloads)");

        var paths = new ToolPaths();
        var sink = CreateSink();
        var settings = AppSettings.Load(paths);
        var manager = new ToolchainManager(paths, sink);

        var gpu = GpuProbe.Detect();
        Console.WriteLine($"    GPU: {gpu.Describe()}");
        Check.True(gpu.Adapters.Count > 0, "at least one display adapter was detected");

        var tools = await manager.EnsureAsync(settings, includeYtDlp: true, CancellationToken.None);
        Console.WriteLine();

        Check.True(tools.HasFfmpeg, $"ffmpeg resolved ({tools.FfmpegPath})");
        Check.True(tools.HasYtDlp, $"yt-dlp installed ({tools.YtDlpPath})");
        Check.True(tools.WhisperCliPath is not null, $"whisper.cpp installed ({tools.WhisperCliPath})");
        Check.True(tools.LlamaServerPath is not null, $"llama.cpp installed ({tools.LlamaServerPath})");
        Check.True(tools.WhisperModelPath is not null, "Whisper weights downloaded");
        Check.True(tools.LlmModelPath is not null, "Qwen weights downloaded");
        Check.True(tools.HasFaceModel, "face detector downloaded");
        Check.True(tools.IsComplete, "toolchain reports itself complete");

        Console.WriteLine($"    Whisper backend : {tools.WhisperBackend}");
        Console.WriteLine($"    llama.cpp backend: {tools.LlamaBackend}");

        // The whole point of the GPU preference: an NVIDIA card CUDA can drive must get the CUDA
        // build, and any other Vulkan-capable machine the Vulkan one.
        if (ToolchainManager.ExpectsLlamaCuda(gpu))
            Check.True(tools.LlamaBackend.Contains("CUDA"),
                $"llama.cpp picked the CUDA build on an NVIDIA card (got '{tools.LlamaBackend}')");
        else if (gpu.VulkanAvailable)
            Check.True(tools.LlamaBackend.Contains("Vulkan"),
                $"llama.cpp picked the Vulkan build on a Vulkan-capable GPU (got '{tools.LlamaBackend}')");
    }

    /// <summary>
    /// Resolves a real YouTube link through the app's own code path. This is the check that
    /// catches a bad yt-dlp flag, a format selector that stops finding 4K, or a silent loss of
    /// progress reporting — none of which the offline tests can see.
    /// </summary>
    public static async Task YouTubeAsync(string url)
    {
        Check.Section($"YouTube source resolution — {url}");

        var paths = new ToolPaths();
        var sink = CreateSink();
        var settings = AppSettings.Load(paths);
        var tools = new ToolchainManager(paths, sink).Detect(settings);

        if (!tools.HasFfmpeg || !tools.HasYtDlp)
        {
            Console.WriteLine("  SKIP  ffmpeg or yt-dlp missing — run with 'install' first.");
            return;
        }

        Check.True(tools.HasJsRuntime, $"a JavaScript runtime is installed ({tools.JsRuntimePath ?? "none"})");

        var work = Path.Combine(Path.GetTempPath(), "reelsmagician-yt");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        // Track that progress actually reaches the UI during a long download.
        var progressReports = 0;
        double maxFraction = 0;
        var tracking = new DelegateProgressSink(
            entry =>
            {
                if (entry.Level == LogLevel.Debug) return;
                Console.WriteLine($"    [{entry.Stage}] {entry.Message}");
            },
            (stage, message, fraction) =>
            {
                if (stage == "source" && fraction is { } f) { progressReports++; maxFraction = Math.Max(maxFraction, f); }
                Console.Write($"\r    {message}".PadRight(110)[..110]);
            });

        var resolver = new SourceResolver(tools, tracking);
        var started = DateTime.UtcNow;
        var source = await resolver.ResolveAsync(url, work, 2160, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"    downloaded in {(DateTime.UtcNow - started).TotalSeconds:0}s");

        Check.True(File.Exists(source.VideoPath), "a video file was downloaded");
        Check.True(source.CameFromLink, "the source is recorded as coming from a link");
        Check.True(source.Title.Length > 0 && source.Title != "Untitled video",
            $"the real video title was read ({source.Title})");

        Console.WriteLine($"    title  : {source.Title}");
        Console.WriteLine($"    media  : {source.Info.Describe()}");
        Console.WriteLine($"    file   : {source.VideoPath} " +
                          $"({AssetDownloader.Humanize(new FileInfo(source.VideoPath).Length)})");
        Console.WriteLine($"    subs   : {source.ProvidedSubtitlePath ?? "none"}");

        Check.True(source.Info.IsAtLeast1080p,
            $"the download meets the 1080p floor (got {source.Info.QualityLabel})");
        Check.True(source.Info.Is4K, $"the download is 4K (got {source.Info.QualityLabel})");
        Check.True(source.Info.HasAudio, "audio was merged in");
        Check.True(source.Info.DurationSeconds > 60, "the full video was downloaded, not a fragment");

        // Progress reporting is the difference between a working app and one that looks hung
        // for several minutes on a gigabyte-scale download.
        Check.True(progressReports > 5, $"download progress was reported ({progressReports} updates)");
        Check.True(maxFraction > 0.9, $"progress reached completion (peaked at {maxFraction:P0})");
    }

    /// <summary>
    /// Rebuilds captions from every transcript left in the cache and asserts the on-screen
    /// invariants against real speech timings — the data that actually exposed the overlap bug,
    /// which synthetic word lists did not.
    /// </summary>
    public static void Recaption()
    {
        Check.Section("Captions rebuilt from cached real transcripts");

        var cache = new ToolPaths().Cache;
        if (!Directory.Exists(cache))
        {
            Console.WriteLine("  SKIP  no cache directory yet — run the pipeline first.");
            return;
        }

        var sources = Directory.EnumerateFiles(cache, "transcript.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(cache, "*.vtt", SearchOption.AllDirectories))
            .OrderBy(f => f)
            .ToList();

        if (sources.Count == 0)
        {
            Console.WriteLine("  SKIP  no cached transcripts found.");
            return;
        }

        foreach (var path in sources)
        {
            Transcript transcript;
            try
            {
                transcript = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? WhisperTranscriber.ParseWhisperJson(File.ReadAllText(path))
                    : SubtitleParser.Load(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  skip {Path.GetFileName(path)}: {ex.Message}");
                continue;
            }

            transcript.Normalize();
            transcript.EnsureWordTimings();
            if (transcript.Segments.Count == 0) continue;

            var name = $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";
            var words = transcript.AllWords().ToList();

            var wordSpans = words.Select(w => (w.Start, w.End)).ToList();
            Check.True(TimingSequencer.IsSequenced(wordSpans),
                $"[{name}] {words.Count} transcript words are ordered and non-overlapping");

            // A controlled comparison on the same words: with the dwell-time guarantees off, and
            // with them on. Anything else would confuse a change in the transcript for a change
            // in the code — whisper.cpp is not deterministic run to run on this machine.
            var probe = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.WordHighlight);
            var caption = words.Select(w => new CaptionWord(w.Start, w.End, w.Text)).ToList();

            var off = probe.Clone();
            off.MinCaptionSeconds = 0;
            off.MaxReadingCharsPerSecond = 1000;

            var before = AssBuilder.BuildGroups(caption, off);
            var after = AssBuilder.BuildGroups(caption, probe);

            static (double Min, int Brief, int Rushed, int Lines) Stats(
                List<CaptionGroup> g, double minSeconds, double cps)
            {
                var d = g.Select(x => x.End - x.Start).ToList();
                return (d.Count == 0 ? 0 : d.Min(),
                        d.Count(x => x < minSeconds - 1e-6),
                        g.Count(x => x.Text.Length / Math.Max(0.01, x.End - x.Start) > cps + 1),
                        g.Count);
            }

            var b = Stats(before, probe.MinCaptionSeconds, probe.MaxReadingCharsPerSecond);
            var a = Stats(after, probe.MinCaptionSeconds, probe.MaxReadingCharsPerSecond);

            Console.WriteLine(
                $"    [{name}] guarantees off: {b.Lines} lines, shortest {b.Min:0.00}s, " +
                $"{b.Brief} too brief, {b.Rushed} too fast");
            Console.WriteLine(
                $"    [{name}] guarantees on : {a.Lines} lines, shortest {a.Min:0.00}s, " +
                $"{a.Brief} too brief, {a.Rushed} too fast");

            // How far merging stretched the lines, and how often it reached across a real pause
            // (a line whose words are separated by a beat shows its second half during silence).
            var longest = after.MaxBy(g => g.Text.Length)!;
            var acrossPause = after.Count(g => g.Words.Zip(g.Words.Skip(1))
                .Any(p => p.Second.Start - p.First.End >= 0.8 - 1e-6));
            Console.WriteLine(
                $"    [{name}] longest line {longest.Words.Count} words / {longest.Text.Length} chars; " +
                $"{acrossPause} lines span a pause of 0.8s or more");
            for (var i = 0; i < after.Count; i++)
            {
                var g = after[i];
                var beat = g.Words.Zip(g.Words.Skip(1)).FirstOrDefault(p => p.Second.Start - p.First.End >= 0.8 - 1e-6);
                if (beat.First is null) continue;
                Console.WriteLine(
                    $"      spans a pause: {g.Start:0.00}-{g.End:0.00} '{g.Text}' " +
                    $"({beat.Second.Start - beat.First.End:0.00}s before '{beat.Second.Text}')" +
                    (i > 0 ? $" | prev {after[i - 1].Words.Count}w ends {after[i - 1].End:0.00}" : "") +
                    (i + 1 < after.Count ? $" | next {after[i + 1].Words.Count}w starts {after[i + 1].Start:0.00}" : ""));
            }

            // Name any line that is still short or fast, with its neighbours' shape, so a failure
            // below can be read straight off the log.
            for (var i = 0; i < after.Count; i++)
            {
                var g = after[i];
                var seconds = g.End - g.Start;
                var cps = g.Text.Length / Math.Max(0.01, seconds);
                if (seconds >= probe.MinCaptionSeconds - 1e-6 && cps <= probe.MaxReadingCharsPerSecond + 1) continue;

                static string Shape(CaptionGroup n) => $"{n.Words.Count}w {n.Start:0.00}-{n.End:0.00}";
                Console.WriteLine(
                    $"      short/fast {g.Start:0.00}-{g.End:0.00} ({seconds:0.00}s, {cps:0} cps) '{g.Text}'" +
                    (i > 0 ? $" | prev {Shape(after[i - 1])}" : "") +
                    (i + 1 < after.Count ? $" | next {Shape(after[i + 1])}" : ""));
            }

            // The absolute guarantee: nothing flashes past. A line whose neighbours are both full
            // can still land a little under the preferred minimum, which is a preference rather
            // than a spec — so that is asserted as a small fraction, not as zero.
            Check.True(a.Min >= AssBuilder.HardFloorSeconds - 1e-6,
                $"[{name}] no line falls below the {AssBuilder.HardFloorSeconds:0.0}s broadcast floor " +
                $"(shortest is {a.Min:0.00}s, was {b.Min:0.00}s)");
            Check.True(a.Brief <= Math.Max(1, a.Lines / 100),
                $"[{name}] lines under the preferred minimum: {a.Brief} of {a.Lines} (was {b.Brief})");
            Check.Equal(0, a.Rushed, $"[{name}] no line is left above the reading-rate limit");
            Check.True(a.Brief < b.Brief && a.Min > b.Min,
                $"[{name}] the guarantees measurably improved dwell time");
            Check.True(a.Lines <= b.Lines, $"[{name}] the guarantees only ever join lines, never add them");
            Check.Equal(
                string.Join(" ", before.SelectMany(g => g.Words).Select(w => w.Text)),
                string.Join(" ", after.SelectMany(g => g.Words).Select(w => w.Text)),
                $"[{name}] the same words survive, in the same order");

            foreach (var style in SubtitleStyle.Presets())
            {
                var groups = AssBuilder.BuildGroups(
                    words.Select(w => new CaptionWord(w.Start, w.End, w.Text)), style);
                var ass = AssBuilder.Build(groups, style);

                var events = new List<(double Start, double End)>();
                foreach (var line in ass.Split('\n'))
                {
                    if (!line.StartsWith("Dialogue:", StringComparison.Ordinal)) continue;
                    var fields = line.Split(',', 10);
                    if (fields.Length < 10) continue;
                    events.Add((SubtitleParser.ParseTime(fields[1]), SubtitleParser.ParseTime(fields[2])));
                }

                var overlaps = events.Zip(events.Skip(1)).Count(p => p.Second.Start < p.First.End - 1e-6);
                var lineOverlaps = groups.Zip(groups.Skip(1)).Count(p => p.Second.Start < p.First.End - 1e-6);

                Check.Equal(0, overlaps, $"[{name} / {style.Name}] {events.Count} events, none overlapping");
                Check.Equal(0, lineOverlaps, $"[{name} / {style.Name}] {groups.Count} lines, none overlapping");
            }
        }
    }

    public static async Task RunAsync(string? existingVideo)
    {
        Check.Section("End-to-end pipeline (Whisper + Qwen + framing + render)");

        var paths = new ToolPaths();
        var sink = CreateSink();
        var settings = AppSettings.Load(paths);
        var tools = new ToolchainManager(paths, sink).Detect(settings);

        if (!tools.IsComplete)
        {
            Console.WriteLine("  SKIP  toolchain incomplete — run with 'install' first.");
            Console.WriteLine("        missing: " + string.Join(", ", tools.MissingComponents()));
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "reelsmagician-e2e");
        Directory.CreateDirectory(work);
        var ct = CancellationToken.None;

        var video = existingVideo ?? Path.Combine(work, "Why Small Habits Beat Big Goals.mp4");
        if (!File.Exists(video))
        {
            Console.WriteLine($"  Video not found: {video}");
            Console.WriteLine("  Generate one first with tools/make-speech-video.ps1, or pass a path.");
            Check.True(false, "an input video is available");
            return;
        }

        var project = new ReelProject { SourceInput = video };
        var pipeline = new ReelPipeline(tools, settings, paths, sink);

        // 1 — source
        await pipeline.ResolveSourceAsync(project, 2160, ct);
        Check.True(project.HasSource, "source resolved");
        Console.WriteLine($"    topic  : {project.Topic}");
        Console.WriteLine($"    media  : {project.Source!.Info.Describe()}");
        Check.True(project.Topic.Length > 0, "a topic was derived from the filename");

        // 2 — transcription
        var started = DateTime.UtcNow;
        await pipeline.TranscribeAsync(project, preferProvidedSubtitles: false, ct);
        Check.True(project.HasTranscript, "Whisper produced a transcript");

        var transcript = project.Transcript!;
        Console.WriteLine($"    whisper: {transcript.Segments.Count} cues, {transcript.WordCount} words " +
                          $"in {(DateTime.UtcNow - started).TotalSeconds:0}s");
        Check.True(transcript.WordCount > 20, $"transcript has real content ({transcript.WordCount} words)");
        Check.True(transcript.HasWordTimings, "transcript carries word-level timings");
        Check.True(transcript.Segments.All(s => s.End > s.Start), "every cue has a positive duration");
        Check.True(transcript.AllWords().All(w => w.End >= w.Start), "no word ends before it starts");

        Console.WriteLine("    first cues:");
        foreach (var cue in transcript.Segments.Take(3))
            Console.WriteLine($"      {cue.Start,6:0.0}s  {cue.Text}");

        // 3 — analysis
        project.PlanOptions = project.PlanOptions with { TargetSeconds = 25, CandidateCount = 2 };
        started = DateTime.UtcNow;
        await pipeline.AnalyzeAsync(project, ct);
        Console.WriteLine($"    qwen   : planned in {(DateTime.UtcNow - started).TotalSeconds:0}s");

        Check.True(project.Paragraphs.Count > 0, $"paragraphs were built ({project.Paragraphs.Count})");
        Check.True(project.HasPlan, "a reel plan was produced");
        Check.True(project.SelectedCandidate is not null, "a candidate was selected");

        var candidate = project.SelectedCandidate!;
        Console.WriteLine($"    reel   : \"{candidate.Title}\" — {candidate.Summary}");
        foreach (var clip in candidate.Clips)
            Console.WriteLine($"      {clip.TimeLabel}  ({clip.Duration:0.0}s)");

        Check.True(candidate.Clips.Count > 0, "the reel has at least one clip");
        Check.True(candidate.Clips.All(c => c.Duration > 0), "every clip has a positive duration");
        Check.True(candidate.Clips.All(c => c.End <= project.Source.Info.DurationSeconds + 1),
            "no clip runs past the end of the source");
        Check.True(candidate.TotalDuration is > 8 and < 60,
            $"the reel length is in a sane range ({candidate.TotalDuration:0.0}s)");

        // 4 — framing
        await pipeline.PlanFramingAsync(project, ct);
        Check.True(project.HasFraming, "framing was planned for every clip");
        Check.Equal(candidate.Clips.Count, project.RenderClips.Count, "one crop path per clip");

        foreach (var rc in project.RenderClips)
        {
            Console.WriteLine($"    crop   : {rc.Crop.CropWidth}×{rc.Crop.CropHeight} — {rc.Crop.Describe()}");
            Check.True(rc.Crop.CropWidth > 0 && rc.Crop.CropHeight > 0, "crop has a real size");
            Check.True(rc.Crop.Keyframes.Count > 0, "crop path has keyframes");
            Check.True(rc.Crop.Keyframes.All(k =>
                    k.X >= -0.001 && k.X <= rc.Crop.SourceWidth - rc.Crop.CropWidth + 0.001 &&
                    k.Y >= -0.001 && k.Y <= rc.Crop.SourceHeight - rc.Crop.CropHeight + 0.001),
                "the crop window stays inside the frame");
        }

        // 5 — captions and render
        project.Style = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.WordHighlight);
        var captions = ReelPipeline.BuildCaptions(project);
        Check.True(captions.Count > 0, $"captions were generated ({captions.Count} lines)");
        Console.WriteLine("    captions:");
        foreach (var group in captions.Take(4))
            Console.WriteLine($"      {group.Start,6:0.00}s  {group.Text}");

        var output = Path.Combine(work, "reel.mp4");
        var result = await pipeline.RenderAsync(project, output, ct);
        Console.WriteLine();

        Check.True(File.Exists(result.OutputPath), "the reel file was written");

        var probe = new FFprobe(tools.Ffprobe, sink);
        var reel = await probe.ProbeAsync(result.OutputPath, ct);
        Check.Equal(1080, reel.DisplayWidth, "reel is 1080 wide");
        Check.Equal(1920, reel.DisplayHeight, "reel is 1920 tall");
        Check.True(reel.HasAudio, "reel has audio");
        Check.Near(candidate.TotalDuration -
                   ReelRenderer.ResolveCrossfade(project.RenderClips, project.RenderOptions) *
                   (project.RenderClips.Count - 1),
            reel.DurationSeconds, 0.6, "reel duration matches the plan");

        Console.WriteLine($"    output : {result.OutputPath} ({reel.Describe()})");
    }
}
