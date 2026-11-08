using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Analysis;

/// <summary>One continuous stretch of the source that will become a clip in the reel.</summary>
public sealed record ClipRange(double Start, double End, int FromParagraph, int ToParagraph, string Text)
{
    public double Duration => Math.Max(0, End - Start);

    public string TimeLabel =>
        $"{TimeSpan.FromSeconds(Start):h\\:mm\\:ss} → {TimeSpan.FromSeconds(End):h\\:mm\\:ss}";
}

/// <summary>A proposed reel: an ordered set of clips plus the model's pitch for it.</summary>
public sealed class ReelCandidate
{
    public string Title { get; set; } = "";
    public string Hook { get; set; } = "";
    public string Reason { get; set; } = "";
    public double Score { get; set; }
    public List<ClipRange> Clips { get; set; } = [];

    public double TotalDuration => Clips.Sum(c => c.Duration);
    public string Transcript => string.Join(" ", Clips.Select(c => c.Text));

    /// <summary>The proposal's length, clips and score, in the interface's language.</summary>
    public string Summary => SummaryIn(Lang.Ui);

    /// <summary>The same in a language of its own, such as the log's.</summary>
    public string SummaryIn(Lang lang) =>
        lang.Format(nameof(Strings.Candidate_Summary), TotalDuration, lang.Plural("Count_Clips", Clips.Count), Score);
}

public sealed class ReelPlan
{
    public string Topic { get; set; } = "";
    public List<ReelCandidate> Candidates { get; set; } = [];
    public bool UsedFallback { get; set; }
    public string? Notes { get; set; }
}

/// <summary>The per-paragraph judgement returned by the scoring pass.</summary>
public sealed record ParagraphScore(int Index, double Score, string Reason)
{
    public static ParagraphScore Neutral(int index) => new(index, 5, "");
}

public sealed record ReelPlanOptions(
    double TargetSeconds = 45,
    double TolerancePercent = 0.18,
    int CandidateCount = 3,
    int MaxClipsPerReel = 4,
    double MinClipSeconds = 4,
    double LeadInSeconds = 0.25,
    double TailSeconds = 0.35)
{
    public double MinSeconds => TargetSeconds * (1 - TolerancePercent);
    public double MaxSeconds => TargetSeconds * (1 + TolerancePercent);
}
