using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.Core.Projects;

/// <summary>
/// Runs the reel pipeline one stage at a time. Stages are separate methods rather than a single
/// batch so the UI can stop for the subtitle review and the caption styling, and so any stage
/// can be repeated after the user changes its inputs.
/// </summary>
public sealed class ReelPipeline(Toolset tools, AppSettings settings, ToolPaths paths, IProgressSink sink)
{
    private FFmpegTool Ffmpeg => new(tools.Ffmpeg, sink);

    /// <summary>Stage 1 — fetch or open the source and read its properties.</summary>
    public async Task ResolveSourceAsync(
        ReelProject project, int maxHeight, CancellationToken ct, bool fetchSubtitles = true)
    {
        project.WorkDir = paths.WorkDirFor(project.Id);
        var resolver = new SourceResolver(tools, sink);
        project.Source = await resolver
            .ResolveAsync(project.SourceInput, project.WorkDir, maxHeight, ct, fetchSubtitles)
            .ConfigureAwait(false);
        project.Topic = project.Source.Title;
    }

    /// <summary>
    /// Stage 2 — obtain a word-timed transcript, either from the subtitles that came with the
    /// video or by running Whisper.
    /// </summary>
    public async Task TranscribeAsync(ReelProject project, bool preferProvidedSubtitles, CancellationToken ct)
    {
        var source = project.Source ?? throw new InvalidOperationException("Resolve the source first.");

        if (preferProvidedSubtitles && source.ProvidedSubtitlePath is { } subtitlePath && File.Exists(subtitlePath))
        {
            sink.Info("transcribe", $"Using the supplied subtitles: {Path.GetFileName(subtitlePath)}");
            var provided = SubtitleParser.Load(subtitlePath);
            provided.EnsureWordTimings();

            if (provided.Segments.Count > 0)
            {
                project.Transcript = provided;
                sink.Report("transcribe",
                    $"Loaded {provided.Segments.Count} cues from the supplied subtitles", 1);
                return;
            }
            sink.Warn("transcribe", "The supplied subtitles were empty — running Whisper instead.");
        }
        else if (preferProvidedSubtitles && source.CameFromLink && source.ProvidedSubtitlePath is null)
        {
            // Most likely the box was ticked only after the download, which is when captions
            // are fetched. Say so, rather than silently running Whisper.
            sink.Info("transcribe",
                "No subtitles were downloaded with this video, so Whisper will transcribe it. " +
                "Tick the box before loading the source to fetch them.");
        }

        var whisper = new WhisperTranscriber(tools, settings, sink);
        var fit = MemoryFit.ForWhisper(tools, settings);
        ReportFit("transcribe", fit);
        ModelRunMarker.Begin(paths.ModelRunMarkerFile,
            ModelRun.Of(ModelStage.Transcription, ModelCatalog.ResolveWhisper(settings.WhisperModelId).Id, fit));
        try
        {
            project.Transcript = await whisper
                .TranscribeAsync(source.VideoPath, source.Info.DurationSeconds, project.WorkDir, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ModelRunMarker.End(paths.ModelRunMarkerFile);
        }
    }

    /// <summary>
    /// Whether transcribing will run Whisper, rather than take the subtitles that came with the
    /// video; only then does the Whisper model's memory matter.
    /// </summary>
    public static bool WillRunWhisper(ReelProject project, bool preferProvidedSubtitles) =>
        !(preferProvidedSubtitles && project.Source?.ProvidedSubtitlePath is { } path && File.Exists(path));

    /// <summary>What the model needs, in the log, and a warning there too when it may not fit.</summary>
    private void ReportFit(string stage, FitAssessment fit)
    {
        var has = fit.Budget.Bytes is { } total ? $"; {fit.Budget.Device ?? "the computer"} has {MemoryFit.Format(total)}" : "";
        sink.Info(stage, $"{fit.Model}: {fit.NeedText.TrimEnd('.')}{has}.");
        if (fit.Risk is { } risk) sink.Warn(stage, risk);
    }

    /// <summary>Stage 3 — group the reviewed transcript into paragraphs and let Qwen pick the reel.</summary>
    public async Task AnalyzeAsync(ReelProject project, CancellationToken ct)
    {
        var transcript = project.Transcript ?? throw new InvalidOperationException("Transcribe first.");
        transcript.Normalize();
        transcript.EnsureWordTimings();

        project.Paragraphs = ParagraphBuilder.Build(transcript);
        sink.Info("analyze",
            $"Built {project.Paragraphs.Count} paragraphs from {transcript.Segments.Count} cues " +
            $"(topic: {project.Topic}).");

        if (project.Paragraphs.Count == 0)
            throw new InvalidOperationException("The transcript produced no paragraphs to analyse.");

        // Asked once per build: the devices for the memory estimate, and whether it can fit the
        // model to them, which decides how many layers it is told to put on the GPU.
        if (tools.LlamaFeatures is null && tools.LlamaServerPath is { } llamaServer)
            tools.LlamaFeatures = await new LlamaProbe(paths.LlamaProbeFile)
                .ProbeAsync(llamaServer, LlamaProbe.GpuKey(tools.Gpu), ct).ConfigureAwait(false);

        var fit = MemoryFit.ForLlm(tools, settings);
        ReportFit("analyze", fit);
        ModelRunMarker.Begin(paths.ModelRunMarkerFile,
            ModelRun.Of(ModelStage.Analysis, ModelCatalog.ResolveLlm(settings.LlmModelId).Id, fit));
        try
        {
            await using var server = await LlamaServerSession
                .StartAsync(tools, settings, settings.LlamaContextSize, sink, ct).ConfigureAwait(false);

            var analyzer = new QwenAnalyzer(server, sink);
            project.Plan = await analyzer.PlanAsync(project.Topic, project.Paragraphs, project.PlanOptions, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ModelRunMarker.End(paths.ModelRunMarkerFile);
        }

        project.SelectedCandidate = project.Plan.Candidates.FirstOrDefault();
        foreach (var candidate in project.Plan.Candidates)
            sink.Info("analyze", $"“{candidate.Title}” — {candidate.Summary}");
    }

    /// <summary>
    /// Stage 4 — work out where to point the vertical crop for every clip, by sampling frames
    /// and tracking the subject through them.
    /// </summary>
    public async Task PlanFramingAsync(ReelProject project, CancellationToken ct)
    {
        var source = project.Source ?? throw new InvalidOperationException("Resolve the source first.");
        var candidate = project.SelectedCandidate ?? throw new InvalidOperationException("Pick a reel first.");

        var options = project.FramingOptions;
        var ffmpeg = Ffmpeg;
        using var detector = new SubjectDetector(tools.FaceModelPath, sink);

        project.RenderClips.Clear();

        for (var i = 0; i < candidate.Clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var clip = candidate.Clips[i];
            var label = $"clip {i + 1} of {candidate.Clips.Count}";
            sink.Report("framing", $"Analysing framing for {label}…", (double)i / candidate.Clips.Count);

            var frameDir = Path.Combine(project.WorkDir, $"frames_{i:00}");
            var frames = await ffmpeg.SampleFramesAsync(
                source.VideoPath, clip.Start, clip.Duration,
                options.SampleFps, options.AnalysisWidth, frameDir, ct).ConfigureAwait(false);

            detector.BeginClip();
            var hits = new List<SubjectHit>(frames.Count);
            for (var f = 0; f < frames.Count; f++)
            {
                ct.ThrowIfCancellationRequested();
                hits.Add(detector.Analyze(frames[f].File, frames[f].Time));

                if (f % 10 == 0)
                    sink.Report("framing",
                        $"Tracking the subject in {label} — frame {f + 1}/{frames.Count}",
                        (i + (double)f / Math.Max(1, frames.Count)) / candidate.Clips.Count);
            }

            var path = CropPlanner.Plan(hits, source.Info, clip.Duration,
                project.RenderOptions.Width, project.RenderOptions.Height, options);

            sink.Info("framing", $"{label}: {path.Describe()}");
            project.RenderClips.Add(new RenderClip(clip, path));

            TryCleanFrames(frameDir);
        }

        sink.Report("framing", "Framing planned", 1);
    }

    /// <summary>Stage 5 — encode the reel with the chosen caption style.</summary>
    public async Task<RenderResult> RenderAsync(ReelProject project, string outputPath, CancellationToken ct)
    {
        var source = project.Source ?? throw new InvalidOperationException("Resolve the source first.");
        if (project.RenderClips.Count == 0) throw new InvalidOperationException("Plan the framing first.");

        var captions = BuildCaptions(project);
        sink.Info("render", $"{captions.Count} caption lines, style “{project.Style.Name}”.");

        var renderer = new ReelRenderer(Ffmpeg, sink);
        var result = await renderer.RenderAsync(
            source.VideoPath, source.Info, project.RenderClips, captions,
            project.Style, project.RenderOptions, project.WorkDir, outputPath, ct).ConfigureAwait(false);

        project.OutputPath = result.OutputPath;
        project.RenderedClips = [.. project.RenderClips];
        return result;
    }

    /// <summary>
    /// Maps the reviewed transcript onto the reel's own timeline and groups it into caption lines.
    ///
    /// Because each transition consumes one crossfade length from the timeline, a clip's position
    /// in the reel is the sum of the preceding clip lengths minus one fade per transition.
    /// </summary>
    public static List<CaptionGroup> BuildCaptions(ReelProject project)
    {
        var transcript = project.Transcript;
        if (transcript is null || project.RenderClips.Count == 0) return [];

        var fade = ReelRenderer.ResolveCrossfade(project.RenderClips, project.RenderOptions);
        var words = transcript.AllWords().ToList();
        var mapped = new List<CaptionWord>();

        double offset = 0;
        for (var i = 0; i < project.RenderClips.Count; i++)
        {
            var range = project.RenderClips[i].Range;

            foreach (var word in words)
            {
                // Keep a word if its midpoint falls inside this clip, so a word straddling the
                // cut is assigned to the clip that actually shows most of it.
                var mid = (word.Start + word.End) / 2;
                if (mid < range.Start || mid >= range.End) continue;

                var start = Math.Max(range.Start, word.Start) - range.Start + offset;
                var end = Math.Min(range.End, word.End) - range.Start + offset;
                if (end <= start) end = start + 0.06;

                mapped.Add(new CaptionWord(start, end, word.Text));
            }

            offset += range.Duration - (i < project.RenderClips.Count - 1 ? fade : 0);
        }

        mapped.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Clipping and joining can land two words at the same instant — a word clamped to a clip
        // boundary, or the last word of one clip meeting the first of the next. Settle the whole
        // reel timeline once here so the caption builder is handed clean, ordered spans.
        if (mapped.Count > 0)
        {
            var spans = mapped.Select(w => (w.Start, w.End)).ToList();
            var reelEnd = Math.Max(spans[^1].End, offset);
            TimingSequencer.Sequence(spans, Math.Min(0, spans[0].Start), reelEnd);

            for (var i = 0; i < mapped.Count; i++)
                mapped[i] = mapped[i] with { Start = spans[i].Start, End = spans[i].End };
        }

        // After the loop, offset is the finished reel's length: the clip durations less one
        // crossfade per join. Handing it through lets the closing caption hold to the end
        // instead of vanishing a third of a second after the final word.
        return AssBuilder.BuildGroups(mapped, project.Style, reelEnd: Math.Max(0, offset));
    }

    /// <summary>Sampled frames are large and single-use; drop them once the path is planned.</summary>
    private void TryCleanFrames(string frameDir)
    {
        try
        {
            if (Directory.Exists(frameDir)) Directory.Delete(frameDir, recursive: true);
        }
        catch (IOException ex)
        {
            sink.Debug("framing", $"Could not clean {frameDir}: {ex.Message}");
        }
    }
}
