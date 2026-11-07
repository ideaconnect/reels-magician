using ReelsMagician.Core.Transcription;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// Rolling auto-captions repeat the previous line in each new cue; those repeats must go. Speech
/// that genuinely repeats must stay, in full and at its own time (issues #1 and #4).
/// </summary>
public static class CaptionParsingChecks
{
    public static void Run()
    {
        Check.Section("Rolling captions are trimmed, repeated speech is kept");

        // ── Speech that repeats is not a rolling repeat ──────────────────────────────────

        var later = SubtitleParser.Parse(Srt((1, 2, "Thank you"), (42, 43, "Thank you")));
        Check.Equal(2, later.Segments.Count, "the same words said again later are both kept");

        var longer = SubtitleParser.Parse(Srt((1, 3, "Thank you"), (3, 5, "Thank you very much")));
        Check.Equal(2, longer.Segments.Count, "a cue that starts with the previous words is kept");
        Check.Equal("Thank you very much", longer.Segments[1].Text, "in full, not trimmed to its tail");
        Check.Near(3.0, longer.Segments[1].Start, 0.001, "and at its own start");

        // Word-timed cues open with a line holding one space, then the spoken words.
        string Timed(double from, double to, string first, string second) =>
            $"{T(from)} --> {T(to)} align:start position:0%\n \n{first}<{T(from + 0.5)}><c> {second}</c>\n\n";
        string Snapshot(double at, string text) => $"{T(at)} --> {T(at + 0.01)} align:start position:0%\n{text}\n \n\n";

        var vttAgain = SubtitleParser.Parse("WEBVTT\n\n" +
            Timed(10, 12, "no", "tak") + Snapshot(12, "no tak") + Timed(52, 54, "no", "tak"));
        Check.Equal(2, vttAgain.Segments.Count, "word-timed speech repeated later is kept, past its snapshot");
        Check.True(vttAgain.Segments.All(s => s.Text == "no tak"), "both times in full");

        var vttLonger = SubtitleParser.Parse("WEBVTT\n\n" +
            Timed(10, 12, "no", "tak") + Snapshot(12, "no tak") + Timed(52, 54, "no", "tak jest"));
        Check.Equal(2, vttLonger.Segments.Count, "a later word-timed cue opening with the same words is kept");
        Check.Equal("no tak jest", vttLonger.Segments[^1].Text, "in full");
        Check.Near(52.0, vttLonger.Segments[^1].Start, 0.001, "at its own start");

        // ── A real rolling repeat is still trimmed ───────────────────────────────────────

        var rolling = SubtitleParser.Parse("WEBVTT\n\n" +
            Timed(0, 2.5, "one", "two") + Snapshot(2.5, "one two") +
            $"{T(2.51)} --> {T(5)} align:start position:0%\none two\nthree<{T(3)}><c> four</c>\n\n" +
            Snapshot(5, "three four"));
        Check.Equal("one two|three four", string.Join("|", rolling.Segments.Select(s => s.Text)),
            "the repeated line and the snapshots go, every spoken word stays once");
        Check.Near(2.51, rolling.Segments[1].Start, 0.001, "the rolling cue starts with its first new word");

        // ── A rolling cue with no word timings keeps its start (issue #4) ───────────────

        var oneWord = SubtitleParser.Parse("WEBVTT\n\n" +
            $"{T(142.0)} --> {T(144.88)}\nogladamy to w\n\n" +
            $"{T(144.88)} --> {T(146.39)}\nogladamy to w\ntelewizji.\n\n");
        Check.Equal("telewizji.", oneWord.Segments[^1].Text, "a one-word rolling line keeps only the new word");
        Check.Near(144.88, oneWord.Segments[^1].Start, 0.001,
            "and starts with its cue, not where the repeated words were spread to");
        var word = oneWord.Segments[^1].Words.Single();
        Check.True(word.Start >= 144.88 - 0.001 && word.End <= 146.39 + 0.001,
            "its word is spread across the whole cue");
    }

    private static string Srt(params (double From, double To, string Text)[] cues) =>
        string.Join("\n", cues.Select((c, i) =>
            $"{i + 1}\n{T(c.From).Replace('.', ',')} --> {T(c.To).Replace('.', ',')}\n{c.Text}\n"));

    private static string T(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}
