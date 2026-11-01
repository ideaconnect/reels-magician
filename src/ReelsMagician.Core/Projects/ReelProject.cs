using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.Core.Projects;

/// <summary>
/// The state carried through the app, one stage at a time. Each stage fills in the next slot,
/// and every stage can be re-run on its own after the user changes something upstream.
/// </summary>
public sealed class ReelProject
{
    public string Id { get; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");

    public string SourceInput { get; set; } = "";
    public ResolvedSource? Source { get; set; }

    /// <summary>The topic hint given to the analyser: the video title, or the cleaned-up filename.</summary>
    public string Topic { get; set; } = "";

    public Transcript? Transcript { get; set; }
    public List<Paragraph> Paragraphs { get; set; } = [];
    public ReelPlan? Plan { get; set; }
    public ReelCandidate? SelectedCandidate { get; set; }
    public List<RenderClip> RenderClips { get; set; } = [];

    public ReelPlanOptions PlanOptions { get; set; } = new();
    public FramingOptions FramingOptions { get; set; } = new();
    public RenderOptions RenderOptions { get; set; } = new();
    public SubtitleStyle Style { get; set; } = SubtitleStyle.Presets()[1];

    public string? OutputPath { get; set; }
    public string WorkDir { get; set; } = "";

    public bool HasSource => Source is not null;
    public bool HasTranscript => Transcript is { Segments.Count: > 0 };
    public bool HasPlan => Plan is { Candidates.Count: > 0 };
    public bool HasFraming => RenderClips.Count > 0;
}
