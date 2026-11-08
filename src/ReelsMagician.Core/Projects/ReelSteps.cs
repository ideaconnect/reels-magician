namespace ReelsMagician.Core.Projects;

/// <summary>Where one of the six steps stands, as the step strip at the top of the window shows it.</summary>
public enum StepState
{
    /// <summary>Not reached yet: an earlier step's output is still missing.</summary>
    Upcoming,

    /// <summary>The first step whose output is missing: the one to do next.</summary>
    Current,

    /// <summary>The step's output exists.</summary>
    Done,
}

/// <summary>
/// How far the user has got through the six steps, in tab order: Setup, Source, Subtitles, Reel,
/// Captions and Render. A step is done once what it works on exists: the required tools, the
/// loaded video, the transcript, the reel proposals, the framing of the chosen reel (which the
/// caption preview is drawn on), and a reel rendered from that framing.
/// </summary>
/// <remarks>
/// The five project steps build on each other, so each counts as done only while the steps
/// before it are done too: once another reel is picked, the reel rendered before is not this
/// one's. Setup stands apart, because removing the tools later does not undo the project.
/// </remarks>
public static class ReelSteps
{
    public const int Count = 6;

    public const int Setup = 0, Source = 1, Subtitles = 2, Reel = 3, Captions = 4, Render = 5;

    public static StepState[] Progress(bool toolsReady, ReelProject project)
    {
        var done = new bool[Count];
        done[Setup] = toolsReady;
        done[Source] = project.HasSource;
        done[Subtitles] = done[Source] && project.HasTranscript;
        done[Reel] = done[Subtitles] && project.HasPlan;
        done[Captions] = done[Reel] && project.HasFraming;
        done[Render] = done[Captions] && project.HasRender;

        var next = Array.IndexOf(done, false);
        var states = new StepState[Count];
        for (var i = 0; i < Count; i++)
            states[i] = done[i] ? StepState.Done : i == next ? StepState.Current : StepState.Upcoming;
        return states;
    }
}
