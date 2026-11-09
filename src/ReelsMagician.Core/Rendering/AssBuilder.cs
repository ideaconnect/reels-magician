using System.Globalization;
using System.Text;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.Core.Rendering;

public sealed record CaptionWord(double Start, double End, string Text);

/// <summary>One caption line: the words shown together, with the timing of each.</summary>
public sealed class CaptionGroup
{
    public double Start { get; set; }
    public double End { get; set; }
    public List<CaptionWord> Words { get; init; } = [];

    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>
/// Writes the ASS subtitle file that ffmpeg burns in.
///
/// Word highlighting is done by emitting one event per word, each carrying the whole line with
/// override tags on the active word. ASS karaoke tags can only swap between two style colours,
/// which cannot scale the word or animate it — one event per word can, and that is what makes
/// the spoken word pop rather than look like a 90s karaoke machine.
/// </summary>
public static class AssBuilder
{
    public const int PlayResX = 1080;
    public const int PlayResY = 1920;

    /// <summary>Shortest event worth emitting — two frames at 30 fps.</summary>
    public const double MinEventSeconds = 1.0 / 15.0;

    /// <summary>Past this, a vertical-only grow reads as a distorted, over-stretched letterform.</summary>
    public const double MaxHighlightScale = 1.35;

    /// <summary>
    /// The floor below which a caption is simply unreadable, whatever else is being traded off.
    /// Netflix enforces 20 frames at delivery; Amazon and the ESIST code both say one second.
    /// </summary>
    public const double HardFloorSeconds = 0.8;

    /// <summary>
    /// How long a finished line lingers after its last word, when nothing follows immediately.
    /// Netflix asks for the out-time to sit at least half a second past the end of the audio.
    /// </summary>
    private const double HoldSeconds = 0.5;

    /// <summary>
    /// A gap shorter than this reads as a flicker rather than a pause, so it is closed up instead.
    /// The BBC's own subtitle validator errors on any non-zero gap below 0.8s — zero is correct,
    /// and so is a real pause, but the range between them is the thing that looks broken.
    /// </summary>
    private const double MinVisibleGapSeconds = 0.8;

    /// <summary>Splits the reel's words into caption lines using the style's line length.</summary>
    /// <param name="reelEnd">
    /// Length of the finished reel, so the closing line can hold to the end instead of vanishing
    /// a third of a second after the last word. Zero means unknown.
    /// </param>
    public static List<CaptionGroup> BuildGroups(
        IEnumerable<CaptionWord> words, SubtitleStyle style, double maxGapSeconds = 0.7, double reelEnd = 0)
    {
        var groups = new List<CaptionGroup>();
        var current = new CaptionGroup();
        var maxWords = Math.Max(1, style.MaxWordsPerCaption);

        void Flush()
        {
            if (current.Words.Count == 0) return;
            current.Start = current.Words[0].Start;
            current.End = Math.Max(current.Words[^1].End, current.Start + 0.2);
            groups.Add(current);
            current = new CaptionGroup();
        }

        CaptionWord? previous = null;
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text)) continue;

            // Break on a pause even mid-line: captions should follow the speech, not a word count.
            if (previous is not null && word.Start - previous.End > maxGapSeconds) Flush();

            current.Words.Add(word);
            previous = word;

            var endsSentence = word.Text.TrimEnd('"', '\'', ')', ']', '”') is { Length: > 0 } t &&
                               t[^1] is '.' or '!' or '?' or '…';

            if (current.Words.Count >= maxWords || (endsSentence && current.Words.Count >= Math.Max(2, maxWords - 2)))
                Flush();
        }
        Flush();

        MergeUnreadableLines(groups, style, reelEnd);

        // Hold each caption open a little so the line does not blink out between words — but
        // never into the next one. Two events share a \pos, so any overlap draws both lines on
        // top of each other, which is exactly the "next subtitle appears before the last one
        // goes" defect.
        var minSeconds = Math.Max(MinEventSeconds, style.MinCaptionSeconds);
        for (var i = 0; i < groups.Count; i++)
            groups[i].End = DisplayEnd(groups, i, reelEnd, minSeconds);

        // A caption whose start was dragged past its neighbour would still collide; settle any
        // remaining conflicts by pushing ends back, never by extending into the next line.
        for (var i = 0; i + 1 < groups.Count; i++)
            if (groups[i].End > groups[i + 1].Start)
                groups[i].End = Math.Max(groups[i].Start, groups[i + 1].Start);

        return groups;
    }

    /// <summary>
    /// When a line leaves the screen, given its neighbours. It lingers after its last word — at
    /// least until it has had its minimum time on screen — but gives way to the next line, and a
    /// gap too short to read as a pause is closed up instead. The closing line has the rest of
    /// the reel to itself, and nothing past the reel's end can be seen.
    ///
    /// The merge pass measures lines with this same rule, so what it judges readable is what is
    /// actually drawn: a short line ahead of a real pause gets its hold, not the whole pause.
    /// </summary>
    private static double DisplayEnd(List<CaptionGroup> groups, int i, double reelEnd, double minSeconds)
    {
        var line = groups[i];
        var hold = Math.Max(line.End + HoldSeconds, line.Start + minSeconds);

        if (i + 1 >= groups.Count)
            return Math.Max(line.Start + MinEventSeconds, reelEnd > 0 ? reelEnd : hold);

        // Either butt this line against the next or leave a gap long enough to read as a pause.
        // A brief blank between two lines is the jerky flicker broadcast validators reject — and
        // the time is better spent leaving the words up.
        var limit = groups[i + 1].Start;
        var gap = limit - hold;
        if (gap > 0 && gap < MinVisibleGapSeconds) hold = limit;

        return Math.Max(line.Start + MinEventSeconds, Math.Min(hold, limit));
    }

    /// <summary>
    /// Joins lines that could never be read in the time available to them.
    ///
    /// A line can only occupy the span between its own first word and the next line's first word:
    /// it must not start early (the highlight would lead the speech) and it must not end late (two
    /// lines would be drawn at once). When that span is too short for the text, the only remedy
    /// left is to have fewer lines — so the line is folded into a neighbour and the pair share the
    /// sum of their windows. Every word keeps its own timing, so the highlight stays exactly in
    /// sync; only the line breaks move.
    /// </summary>
    private static void MergeUnreadableLines(List<CaptionGroup> groups, SubtitleStyle style, double reelEnd)
    {
        if (groups.Count < 2) return;

        var minSeconds = Math.Max(MinEventSeconds, style.MinCaptionSeconds);
        var charsPerSecond = Math.Max(5, style.MaxReadingCharsPerSecond);

        // A cap, so a burst of fast speech cannot collapse into one wall of text. The second
        // is the most a line may grow to rescue one that would otherwise fall below the floor;
        // about three rows of text at the default size, which is all a vertical frame holds.
        var wordCap = Math.Max(2, style.MaxWordsPerCaption) * 2;
        var rescueCap = Math.Max(2, style.MaxWordsPerCaption) * 3;

        double WindowOf(int i) => DisplayEnd(groups, i, reelEnd, minSeconds) - groups[i].Start;

        double NeededBy(CaptionGroup g) =>
            Math.Max(minSeconds, g.Text.Length / charsPerSecond);

        var guard = groups.Count * 2;
        while (guard-- > 0)
        {
            // Worst deficit first, but a line whose neighbours are both full must not stop the
            // pass: skip it and keep going, or one unfixable line strands every other one.
            var starved = Enumerable.Range(0, groups.Count)
                .Select(i => (Index: i, Deficit: NeededBy(groups[i]) - WindowOf(i)))
                .Where(x => x.Deficit > 1e-6)
                .OrderByDescending(x => x.Deficit)
                .ToList();

            var mergedOne = false;

            foreach (var (index, _) in starved)
            {
                // The word cap keeps a fast passage from collapsing into a wall of text, but it
                // must not win over a line that would genuinely flash past. Below the broadcast
                // floor the cap is raised — not removed: a caption of forty words wraps off the
                // frame and is no more readable than the flash it replaced.
                var cap = WindowOf(index) < HardFloorSeconds ? rescueCap : wordCap;

                var previousFits = index > 0 &&
                    groups[index - 1].Words.Count + groups[index].Words.Count <= cap;
                var nextFits = index + 1 < groups.Count &&
                    groups[index].Words.Count + groups[index + 1].Words.Count <= cap;

                // Joining two lines gives the pair the sum of their windows. Prefer a partner that
                // leaves the joined line readable outright, then one on this side of any pause —
                // a line joined across a beat shows its words during the silence and papers over
                // the beat — and between equals fold backwards, since a starved line is usually
                // the tail of the sentence before it and reads more naturally there.
                bool Readable(int a, int b) =>
                    groups[b].Start - groups[a].Start + WindowOf(b) + 1e-6 >= Math.Max(minSeconds,
                        (groups[a].Text.Length + 1 + groups[b].Text.Length) / charsPerSecond);
                bool Paused(int a, int b) => groups[b].Start - groups[a].End >= MinVisibleGapSeconds;

                var previousScore = !previousFits ? 0 :
                    1 + (Readable(index - 1, index) ? 4 : 0) + (Paused(index - 1, index) ? 0 : 2) + 1;
                var nextScore = !nextFits ? 0 :
                    1 + (Readable(index, index + 1) ? 4 : 0) + (Paused(index, index + 1) ? 0 : 2);

                var first = previousScore >= nextScore ? (previousScore > 0 ? index - 1 : -1) : index;
                if (first < 0) continue;   // both neighbours full; leave this one rather than sprawl

                var keep = groups[first];
                var absorbed = groups[first + 1];
                keep.Words.AddRange(absorbed.Words);
                keep.End = Math.Max(keep.End, absorbed.End);
                groups.RemoveAt(first + 1);

                mergedOne = true;
                break;   // indices have shifted; recompute
            }

            if (!mergedOne) break;
        }
    }

    public static string Build(IReadOnlyList<CaptionGroup> groups, SubtitleStyle style)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, style);

        var y = Math.Clamp(style.VerticalPositionPercent, 5, 95) / 100.0 * PlayResY;

        foreach (var group in groups)
        {
            if (group.Words.Count == 0) continue;

            if (style.Mode == CaptionMode.PlainText)
                AppendPlain(sb, group, style, y);
            else
                AppendHighlighted(sb, group, style, y);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Placement and entrance in one block. \move already carries a position, so emitting \pos
    /// alongside it would be a conflicting pair — the slide-up variant supplies its own.
    /// </summary>
    private static string Placement(SubtitleStyle style, double baselineY, bool animate)
    {
        var centre = PlayResX / 2;
        if (!animate) return $"\\an5\\pos({centre},{Fmt(baselineY)})";

        return style.Animation switch
        {
            CaptionAnimation.SlideUp =>
                $"\\an5\\move({centre},{Fmt(baselineY + 55)},{centre},{Fmt(baselineY)},0,180)\\fad(140,0)",

            CaptionAnimation.Pop =>
                $"\\an5\\pos({centre},{Fmt(baselineY)})" +
                "\\fscx82\\fscy82\\t(0,90,\\fscx106\\fscy106)\\t(90,170,\\fscx100\\fscy100)",

            CaptionAnimation.Bounce =>
                $"\\an5\\pos({centre},{Fmt(baselineY)})" +
                "\\fscx70\\fscy70\\t(0,110,\\fscx116\\fscy116)\\t(110,200,\\fscx94\\fscy94)\\t(200,280,\\fscx100\\fscy100)",

            CaptionAnimation.FadeIn => $"\\an5\\pos({centre},{Fmt(baselineY)})\\fad(150,120)",
            _ => $"\\an5\\pos({centre},{Fmt(baselineY)})",
        };
    }

    private static void AppendPlain(StringBuilder sb, CaptionGroup group, SubtitleStyle style, double baselineY)
    {
        var text = Escape(Apply(style, group.Text));
        sb.Append("Dialogue: 0,").Append(AssTime(group.Start)).Append(',').Append(AssTime(group.End))
          .Append(",Caption,,0,0,0,,{").Append(Placement(style, baselineY, animate: true)).Append('}')
          .Append(text).Append('\n');
    }

    /// <summary>
    /// One event per word. Each event shows the whole line, with the word being spoken
    /// recoloured and scaled up, so the highlight lands exactly on the syllable.
    /// </summary>
    private static void AppendHighlighted(
        StringBuilder sb, CaptionGroup group, SubtitleStyle style, double baselineY)
    {
        var highlight = SubtitleStyle.ToOverrideColor(style.HighlightColor);
        var scale = (int)Math.Round(Math.Clamp(style.HighlightScale, 1.0, MaxHighlightScale) * 100);

        var bounds = EventBoundaries(group);

        for (var i = 0; i < group.Words.Count; i++)
        {
            // The event runs from this word until the next begins, so there is never a gap —
            // and, because the boundaries are strictly increasing, never an overlap either.
            var start = bounds[i];
            var end = bounds[i + 1];
            if (end <= start) continue;

            // The entrance animations that scale the line ({\fscx…\t(…)}) fight with a per-word
            // scale override, because {\r} would cancel the animation for the rest of the line.
            // On that one event the highlight changes colour only; scaling resumes next word.
            var animatesScale = i == 0 && style.Animation is CaptionAnimation.Pop or CaptionAnimation.Bounce;

            // Animate only the first word's event, otherwise the line replays its entrance
            // on every syllable.
            sb.Append("Dialogue: 0,").Append(AssTime(start)).Append(',').Append(AssTime(end))
              .Append(",Caption,,0,0,0,,{").Append(Placement(style, baselineY, animate: i == 0)).Append('}')
              .Append(BuildLine(group, i, style, highlight, scale, skipScale: animatesScale))
              .Append('\n');
        }
    }

    /// <summary>
    /// The instants at which the highlight steps from one word to the next: one more value than
    /// there are words, strictly increasing, spanning exactly the caption's own time.
    ///
    /// This is where the per-word events are guaranteed not to collide. Whisper regularly gives
    /// two neighbouring words the same timestamp, and the previous approach — end the event at
    /// max(start + 40 ms, next word's start) — pushed each event past the start of the one after
    /// it, putting two copies of the line on screen together.
    /// </summary>
    private static double[] EventBoundaries(CaptionGroup group)
    {
        var count = group.Words.Count;
        var bounds = new double[count + 1];

        bounds[0] = group.Start;
        for (var i = 1; i < count; i++) bounds[i] = Math.Max(group.Words[i].Start, group.Start);
        bounds[count] = group.End;

        for (var i = 1; i <= count; i++)
            bounds[i] = Math.Max(bounds[i], bounds[i - 1] + MinEventSeconds);

        // Enforcing the minimum can push the last boundary past the caption's end. The line's
        // total screen time is fixed, so share it out evenly rather than letting it run long.
        if (bounds[count] > group.End)
        {
            var step = (group.End - group.Start) / count;
            for (var i = 0; i <= count; i++) bounds[i] = group.Start + i * step;
        }
        return bounds;
    }

    /// <summary>The caption line, with <paramref name="activeIndex"/> marked as being spoken.</summary>
    private static string BuildLine(
        CaptionGroup group, int activeIndex, SubtitleStyle style, string highlight, int scale, bool skipScale)
    {
        var line = new StringBuilder();
        for (var w = 0; w < group.Words.Count; w++)
        {
            if (w > 0) line.Append(' ');
            var text = Escape(Apply(style, group.Words[w].Text));

            if (w != activeIndex) { line.Append(text); continue; }

            line.Append("{\\c").Append(highlight);

            // Deliberately \fscy and never \fscx. Horizontal scaling changes the glyph advance
            // width, so the line gets wider, and a centred line re-centres — every word in it
            // jumps sideways as the highlight passes. Growing only the vertical axis leaves the
            // layout untouched: measured, the rest of the line moves 0 px instead of 19.
            if (!skipScale && style.Emphasis == HighlightEmphasis.Grow)
                line.Append("\\fscy").Append(scale);

            line.Append('}').Append(text).Append("{\\r}");
        }
        return line.ToString();
    }

    /// <summary>
    /// One frozen caption for a still preview: exactly one event, so the frame shows the line as
    /// it looks at a single instant. Rendering the live events into a still would stack every
    /// word's event on top of the others and ghost the text.
    /// </summary>
    public static string BuildStill(CaptionGroup group, int activeWordIndex, SubtitleStyle style)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, style);
        if (group.Words.Count == 0) return sb.ToString();

        var baselineY = Math.Clamp(style.VerticalPositionPercent, 5, 95) / 100.0 * PlayResY;
        var scale = (int)Math.Round(Math.Clamp(style.HighlightScale, 1.0, MaxHighlightScale) * 100);
        var highlight = SubtitleStyle.ToOverrideColor(style.HighlightColor);

        // In plain mode nothing is marked, so no index is active.
        var active = style.Mode == CaptionMode.WordHighlight
            ? Math.Clamp(activeWordIndex, 0, group.Words.Count - 1)
            : -1;

        sb.Append("Dialogue: 0,0:00:00.00,0:00:10.00,Caption,,0,0,0,,{")
          .Append(Placement(style, baselineY, animate: false)).Append('}')
          .Append(BuildLine(group, active, style, highlight, scale, skipScale: false))
          .Append('\n');

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, SubtitleStyle style)
    {
        var primary = SubtitleStyle.ToAssColor(style.PrimaryColor);
        var secondary = SubtitleStyle.ToAssColor(style.HighlightColor);
        var outline = style.BackgroundBox
            ? SubtitleStyle.ToAssColor(style.BackgroundColor, style.BackgroundOpacity)
            : SubtitleStyle.ToAssColor(style.OutlineColor);
        var back = SubtitleStyle.ToAssColor(style.ShadowColor, style.BackgroundBox ? style.BackgroundOpacity : 1.0);

        // BorderStyle 3 draws a filled box behind the text; 1 draws an outline around it.
        var borderStyle = style.BackgroundBox ? 3 : 1;

        sb.Append("[Script Info]\n")
          .Append("; Generated by ReelsMagician\n")
          .Append("ScriptType: v4.00+\n")
          .Append("WrapStyle: 0\n")
          .Append("ScaledBorderAndShadow: yes\n")
          .Append("YCbCr Matrix: TV.709\n")
          .Append($"PlayResX: {PlayResX}\n")
          .Append($"PlayResY: {PlayResY}\n\n")
          .Append("[V4+ Styles]\n")
          .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, ")
          .Append("Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, ")
          .Append("Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n")
          .Append("Style: Caption,")
          .Append(SanitiseFont(style.FontName)).Append(',')
          .Append(Math.Clamp(style.FontSize, 12, 300)).Append(',')
          .Append(primary).Append('&').Append(',')
          .Append(secondary).Append('&').Append(',')
          .Append(outline).Append('&').Append(',')
          .Append(back).Append('&').Append(',')
          .Append(style.Bold ? "-1" : "0").Append(',')
          .Append(style.Italic ? "-1" : "0").Append(",0,0,100,100,")
          .Append(Fmt(style.LetterSpacing)).Append(",0,")
          .Append(borderStyle).Append(',')
          .Append(Fmt(Math.Clamp(style.OutlineWidth, 0, 40))).Append(',')
          .Append(Fmt(Math.Clamp(style.ShadowDepth, 0, 20))).Append(',')
          // Positioning is done per line with \pos, so the margins only bound word wrapping.
          .Append("5,80,80,60,1\n\n")
          .Append("[Events]\n")
          .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
    }

    private static string Apply(SubtitleStyle style, string text) =>
        style.Uppercase ? text.ToUpperInvariant() : text;

    /// <summary>Escapes the characters ASS treats as markup.</summary>
    private static string Escape(string text) => text
        .Replace("\\", "\\\\")
        .Replace("{", "\\{")
        .Replace("}", "\\}")
        .Replace("\r", "")
        .Replace("\n", "\\N");

    /// <summary>Commas would break the style line, so they are stripped from font names.</summary>
    private static string SanitiseFont(string font) =>
        string.IsNullOrWhiteSpace(font) ? "Arial" : font.Replace(",", " ").Trim();

    /// <summary>ASS timestamps are H:MM:SS.cc — centiseconds, not milliseconds.</summary>
    public static string AssTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds / 10);
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
