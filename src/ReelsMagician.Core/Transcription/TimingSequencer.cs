namespace ReelsMagician.Core.Transcription;

/// <summary>
/// Forces a run of timed spans to be ordered, non-overlapping and contained in a window.
///
/// Every caption defect of the "two lines on screen at once" kind traces back to spans that were
/// allowed to overlap somewhere upstream: Whisper can emit two tokens with the same offset, a word
/// clamped to its cue's end piles up against its neighbours, and joining clips can put two words at
/// the same instant. Rather than patching each site, they all funnel through here.
/// </summary>
public static class TimingSequencer
{
    /// <summary>Shortest span worth showing — two frames at 30 fps, below which a caption flashes.</summary>
    public const double MinSpanSeconds = 1.0 / 15.0;

    /// <summary>
    /// Rewrites the spans in place so that each starts no earlier than the previous one ended,
    /// each lasts at least <paramref name="minSpan"/>, and all of them fit inside
    /// [<paramref name="windowStart"/>, <paramref name="windowEnd"/>].
    ///
    /// Original durations are preserved wherever they are already legal, because the gaps between
    /// words are what caption grouping uses to detect pauses. Only violations are moved.
    /// </summary>
    public static void Sequence(
        IList<(double Start, double End)> spans,
        double windowStart,
        double windowEnd,
        double minSpan = MinSpanSeconds)
    {
        var count = spans.Count;
        if (count == 0) return;

        var window = windowEnd - windowStart;
        if (window <= 0)
        {
            // Degenerate window: give every span the same zero-length instant rather than
            // inventing time that the clip does not have.
            for (var i = 0; i < count; i++) spans[i] = (windowStart, windowStart);
            return;
        }

        // Too many spans to honour the minimum — share the window out evenly instead. Everything
        // is short, but nothing overlaps, which is the property that matters on screen.
        if (window < count * minSpan)
        {
            var step = window / count;
            for (var i = 0; i < count; i++)
                spans[i] = (windowStart + i * step, windowStart + (i + 1) * step);
            return;
        }

        // Forward pass: pin each start after the previous span, while reserving enough of the
        // window for every span still to come.
        //
        // Deliberately built from Math.Min/Math.Max rather than Math.Clamp: when the window is
        // only just large enough, rounding can leave the reserved tail a few femtoseconds beyond
        // the cursor, and Math.Clamp throws outright when its lower bound exceeds its upper one.
        var cursor = windowStart;
        for (var i = 0; i < count; i++)
        {
            var remaining = count - i - 1;
            var latestStart = Math.Max(cursor, windowEnd - (remaining + 1) * minSpan);
            var start = Math.Min(Math.Max(spans[i].Start, cursor), latestStart);
            spans[i] = (start, spans[i].End);
            cursor = start + minSpan;
        }

        // Second pass: keep each original end where it is legal, but never let it reach into the
        // next span or past the window.
        for (var i = 0; i < count; i++)
        {
            var start = spans[i].Start;
            var limit = i + 1 < count ? spans[i + 1].Start : windowEnd;
            var end = Math.Max(spans[i].End, start + minSpan);
            if (end > limit) end = Math.Max(limit, start);
            spans[i] = (start, end);
        }
    }

    /// <summary>True when the spans are ordered, non-overlapping and long enough. For tests and asserts.</summary>
    public static bool IsSequenced(
        IReadOnlyList<(double Start, double End)> spans, double tolerance = 1e-6)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            if (spans[i].End < spans[i].Start - tolerance) return false;
            if (i > 0 && spans[i].Start < spans[i - 1].End - tolerance) return false;
        }
        return true;
    }
}
