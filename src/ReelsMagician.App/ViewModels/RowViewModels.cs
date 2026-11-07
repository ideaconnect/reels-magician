using System.Globalization;
using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// One editable subtitle cue. Times are edited as text so the user can type "1:02.5" or
/// "62.5"; the parsed value flows straight back into the underlying transcript segment.
/// </summary>
public sealed class SubtitleRowViewModel(TranscriptSegment segment) : ViewModelBase
{
    public TranscriptSegment Segment { get; } = segment;

    private bool _edited;
    public bool Edited { get => _edited; private set => Set(ref _edited, value); }

    public string StartText
    {
        get => FormatTime(Segment.Start);
        set
        {
            if (!TryParseTime(value, out var seconds)) { Raise(); return; }
            Segment.Start = Math.Max(0, seconds);
            if (Segment.End <= Segment.Start) Segment.End = Segment.Start + 0.3;
            Segment.ClampWords();
            MarkEdited();
        }
    }

    public string EndText
    {
        get => FormatTime(Segment.End);
        set
        {
            if (!TryParseTime(value, out var seconds)) { Raise(); return; }
            Segment.End = Math.Max(Segment.Start + 0.1, seconds);
            Segment.ClampWords();
            MarkEdited();
        }
    }

    public string Text
    {
        get => Segment.Text;
        set
        {
            if (Segment.Text == value) return;
            Segment.Text = value ?? "";
            // Re-derive word timings so the karaoke highlight still lands correctly.
            Segment.RetimeWords();
            MarkEdited();
        }
    }

    public string DurationText => $"{Segment.Duration:0.0}s";
    public int WordCount => Segment.Words.Count;

    /// <summary>Flags a cue that Whisper was unsure about, so review effort can be aimed at it.</summary>
    public bool LowConfidence =>
        Segment.Words.Count > 0 && Segment.Words.Average(w => w.Confidence) < 0.6;

    private void MarkEdited()
    {
        Edited = true;
        Raise(nameof(StartText));
        Raise(nameof(EndText));
        Raise(nameof(DurationText));
        Raise(nameof(Text));
        Raise(nameof(WordCount));
        Raise(nameof(LowConfidence));
    }

    public void RefreshAll() => MarkEditedSilently();

    private void MarkEditedSilently()
    {
        Raise(nameof(StartText));
        Raise(nameof(EndText));
        Raise(nameof(DurationText));
        Raise(nameof(Text));
        Raise(nameof(WordCount));
        Raise(nameof(LowConfidence));
    }

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:000}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }

    /// <summary>Accepts "h:mm:ss.fff", "mm:ss.f" or a plain number of seconds.</summary>
    public static bool TryParseTime(string? value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim().Replace(',', '.');
        var parts = text.Split(':');
        try
        {
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var component))
                    return false;
                total = total * 60 + component;
            }
            seconds = total;
            return seconds >= 0;
        }
        catch (OverflowException) { return false; }
    }
}

/// <summary>A toolchain component row on the setup screen.</summary>
public sealed class ComponentRowViewModel(ComponentStatus status) : ViewModelBase
{
    public string Name => status.Name;
    public string Detail => status.Detail;
    public string StateText => status.State switch
    {
        ComponentState.Installed => "Installed",
        ComponentState.System => "Found on system",
        ComponentState.NotInstalled => "Not installed",
        _ => "Will download",
    };
    public bool IsReady => status.State is ComponentState.Installed or ComponentState.System;
    public string SizeText => status.NeedsDownload ? AssetDownloader.Humanize(status.ApproxBytes) : "—";
}

/// <summary>One reel proposal from the analyser.</summary>
public sealed class CandidateViewModel(ReelCandidate candidate) : ViewModelBase
{
    public ReelCandidate Candidate { get; } = candidate;

    public string Title => Candidate.Title;
    public string Hook => Candidate.Hook;
    public string Reason => Candidate.Reason;
    public string Summary => Candidate.Summary;

    public string ClipList => string.Join("   ", Candidate.Clips.Select(c => c.TimeLabel));
    public string Preview =>
        Candidate.Transcript.Length <= 320 ? Candidate.Transcript : Candidate.Transcript[..320] + "…";
}

/// <summary>The framing decision made for one clip, shown as read-only feedback.</summary>
public sealed class ClipFramingViewModel(int index, ClipRange range, CropPath crop) : ViewModelBase
{
    public string Label => $"Clip {index + 1}";
    public string TimeLabel => range.TimeLabel;
    public string DurationText => $"{range.Duration:0.0}s";
    public string CropText => $"{crop.CropWidth}×{crop.CropHeight}";
    public string TrackingText => crop.Describe();
    public string SubjectText => crop.DominantSubject switch
    {
        SubjectKind.Face => "Speaker",
        SubjectKind.Person => "Person",
        SubjectKind.Salient => "Visual subject",
        _ => "Centred",
    };
}
