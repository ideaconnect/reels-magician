using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// What the step strip shows for each step (issue #47): done once the step's output exists,
/// current for the first step still to do, upcoming for the rest.
/// </summary>
public static class StepProgressChecks
{
    private const StepState D = StepState.Done, C = StepState.Current, U = StepState.Upcoming;

    public static void Run()
    {
        Check.Section("The step strip shows how far the project has got (issue #47)");

        var project = new ReelProject();
        Expect([C, U, U, U, U, U], false, project, "a first start: Setup is the step to do, the rest are not reached");
        Expect([D, C, U, U, U, U], true, project, "with the tools installed, Source is next");

        project.Source = Source();
        Expect([D, D, C, U, U, U], true, project, "a loaded video finishes Source, and Subtitles is next");

        project.Transcript = Transcript();
        Expect([D, D, D, C, U, U], true, project, "a transcript finishes Subtitles, and Reel is next");

        var clip = new ClipRange(1, 9, 0, 0, "Dzień dobry wszystkim");
        var candidate = new ReelCandidate { Title = "Powitanie", Clips = [clip] };
        project.Plan = new ReelPlan { Candidates = [candidate] };
        project.SelectedCandidate = candidate;
        Expect([D, D, D, D, C, U], true, project, "reel proposals finish Reel, and Captions is next");

        project.RenderClips.Add(new RenderClip(clip, new CropPath()));
        Expect([D, D, D, D, D, C], true, project, "a framed reel, which the caption preview is drawn on, leaves Render");

        project.OutputPath = "/reels/Powitanie.mp4";
        Expect([D, D, D, D, D, C], true, project, "an output path alone, with no record of what was rendered, is not this framing's reel");

        project.RenderedClips = [.. project.RenderClips];
        Expect([D, D, D, D, D, D], true, project, "a reel rendered from this framing finishes every step");

        Expect([C, D, D, D, D, D], false, project, "removing the tools later makes Setup the step to do, and keeps the project's checks");

        // Picking another reel clears the framing, as the view model does on a new selection.
        project.RenderClips.Clear();
        Expect([D, D, D, D, C, U], true, project, "another reel picked: Captions is next and the old render no longer counts");

        project.RenderClips.Add(new RenderClip(clip, new CropPath()));
        Expect([D, D, D, D, D, C], true, project, "framing it again leaves Render to do, though a reel was written before");

        var imported = new ReelProject { Transcript = Transcript() };
        Expect([D, C, U, U, U, U], true, imported, "subtitles imported before any video: Source is still next, Subtitles waits for it");

        var states = ReelSteps.Progress(true, new ReelProject());
        Check.Equal(ReelSteps.Count, states.Length, "one state per step");
        Check.Equal(1, states.Count(s => s == C), "exactly one step is the next one");
    }

    private static void Expect(StepState[] expected, bool toolsReady, ReelProject project, string description)
    {
        var actual = ReelSteps.Progress(toolsReady, project);
        Check.Equal(Describe(expected), Describe(actual), description);
    }

    private static string Describe(StepState[] states) =>
        string.Join(" ", states.Select(s => s switch { StepState.Done => "done", StepState.Current => "NEXT", _ => "-" }));

    private static ResolvedSource Source() =>
        new("/videos/rozmowa.mp4", "Rozmowa",
            new MediaInfo("/videos/rozmowa.mp4", 60, 3840, 2160, 30, true, "h264", "aac", 0), null, null);

    private static Transcript Transcript() => new()
    {
        Segments = [new TranscriptSegment { Start = 1, End = 9, Text = "Dzień dobry wszystkim" }],
    };
}
