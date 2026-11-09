using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelsMagician.Core.Transcription;

public sealed record TranscriptWord(double Start, double End, string Text, double Confidence = 1.0)
{
    public double Duration => Math.Max(0, End - Start);
}

/// <summary>One caption cue: a span of speech with its text and, where known, per-word timings.</summary>
public sealed class TranscriptSegment
{
    /// <summary>Below this average word confidence, a cue is one Whisper was unsure about.</summary>
    public const double LowConfidenceThreshold = 0.6;

    /// <summary>The confidence of a word nothing doubts, such as one from subtitles that came with the video.</summary>
    internal const double FullConfidence = 1.0;

    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<TranscriptWord> Words { get; set; } = [];

    public double Duration => Math.Max(0, End - Start);
    public bool HasWordTimings => Words.Count > 0;

    /// <summary>
    /// Whether Whisper was unsure about this cue: its words average below
    /// <see cref="LowConfidenceThreshold"/>. The Subtitles step shows such a cue in amber, so the
    /// review lands on the lines Whisper may have got wrong.
    /// </summary>
    public bool LowConfidence =>
        Words.Count > 0 && Words.Average(w => w.Confidence) < LowConfidenceThreshold;

    public TranscriptSegment Clone() => new()
    {
        Start = Start,
        End = End,
        Text = Text,
        Words = [.. Words],
    };

    /// <summary>
    /// Re-derives word timings after the text was edited. Words that survive the edit keep their
    /// original timing; new or changed words are spread evenly across the gap between the
    /// surviving anchors, so a typo fix never desynchronises the rest of the line.
    ///
    /// Spreading only estimates when a word is said; it says nothing about whether the word is
    /// right. So a spread word keeps the confidence of its text: full confidence for a cue that
    /// had no words yet (subtitles that came with the video, a cue Whisper gave no tokens for),
    /// and for an edit the confidence of the words it replaced. Low confidence stays a mark of
    /// what Whisper was unsure about, never a side effect of the timing (issue #76).
    /// </summary>
    public void RetimeWords()
    {
        var tokens = Tokenize(Text);
        if (tokens.Count == 0)
        {
            Words.Clear();
            return;
        }

        if (Words.Count == 0)
        {
            Words = SpreadEvenly(tokens, Start, End, FullConfidence);
            return;
        }

        var oldWords = Words;
        var matches = MatchTokens(tokens, oldWords);
        var rebuilt = new TranscriptWord?[tokens.Count];

        for (var i = 0; i < tokens.Count; i++)
        {
            var oldIndex = matches[i];
            if (oldIndex >= 0)
            {
                var src = oldWords[oldIndex];
                rebuilt[i] = new TranscriptWord(src.Start, src.End, tokens[i], src.Confidence);
            }
        }

        FillGaps(rebuilt, matches, tokens, oldWords, Start, End);
        Words = [.. rebuilt.Select(w => w!)];
        ClampWords();
    }

    /// <summary>
    /// Spreads the cue's words afresh across the whole cue, for when their timings are no use any
    /// more, as after a timeless cue was folded into this one. What Whisper made of the words
    /// survives as their average confidence, which is what <see cref="LowConfidence"/> reads.
    /// </summary>
    internal void RespreadWords()
    {
        var confidence = AverageConfidence(Words, FullConfidence);
        Words = SpreadEvenly(Tokenize(Text), Start, End, confidence);
    }

    /// <summary>
    /// Keeps word times inside the cue, ordered and non-overlapping.
    ///
    /// The ordering has to be against the previous word's END, not its start: enforcing only
    /// "starts increase" lets a long word swallow the next one, and the caption built from those
    /// words then shows two highlights at once.
    /// </summary>
    public void ClampWords()
    {
        if (Words.Count == 0) return;

        var spans = Words.Select(w => (w.Start, w.End)).ToList();
        TimingSequencer.Sequence(spans, Start, End);

        for (var i = 0; i < Words.Count; i++)
            Words[i] = Words[i] with { Start = spans[i].Start, End = spans[i].End };
    }

    /// <summary>Greedy left-to-right alignment of new tokens onto the old word list.</summary>
    private static int[] MatchTokens(List<string> tokens, List<TranscriptWord> oldWords)
    {
        var result = new int[tokens.Count];
        Array.Fill(result, -1);
        var searchFrom = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var needle = Normalize(tokens[i]);
            if (needle.Length == 0) continue;

            // Look a short distance ahead so an inserted or removed word does not break the chain.
            var limit = Math.Min(oldWords.Count, searchFrom + 6);
            for (var j = searchFrom; j < limit; j++)
            {
                if (Normalize(oldWords[j].Text) != needle) continue;
                result[i] = j;
                searchFrom = j + 1;
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Times each run of new words across the gap its anchors leave. The run takes the average
    /// confidence of the old words it replaced; a run that replaced none, words typed in between,
    /// takes that of the words either side. An edit so carries over how sure Whisper was of that
    /// part of the line.
    /// </summary>
    private static void FillGaps(
        TranscriptWord?[] words, int[] matches, List<string> tokens, List<TranscriptWord> oldWords,
        double start, double end)
    {
        var i = 0;
        while (i < words.Length)
        {
            if (words[i] is not null) { i++; continue; }

            var runStart = i;
            while (i < words.Length && words[i] is null) i++;
            var runEnd = i; // exclusive

            var before = runStart == 0 ? null : words[runStart - 1];
            var after = runEnd >= words.Length ? null : words[runEnd];

            var from = before?.End ?? start;
            var to = after?.Start ?? end;
            if (to <= from) to = from + 0.12 * (runEnd - runStart);

            // The old words between the two anchors are the ones this run replaced.
            var oldFrom = runStart == 0 ? 0 : matches[runStart - 1] + 1;
            var oldTo = runEnd >= words.Length ? oldWords.Count : matches[runEnd];
            var replaced = oldWords.Skip(oldFrom).Take(oldTo - oldFrom);
            var confidence = AverageConfidence(replaced, AverageConfidence([before, after], FullConfidence));

            var filled = SpreadEvenly(tokens.GetRange(runStart, runEnd - runStart), from, to, confidence);
            for (var k = 0; k < filled.Count; k++) words[runStart + k] = filled[k];
        }
    }

    private static double AverageConfidence(IEnumerable<TranscriptWord?> words, double fallback)
    {
        var known = words.OfType<TranscriptWord>().ToList();
        return known.Count > 0 ? known.Average(w => w.Confidence) : fallback;
    }

    /// <summary>
    /// Distributes tokens across a span, weighting each word by its length. The timing is made
    /// up, so the caller says how sure the text is.
    /// </summary>
    private static List<TranscriptWord> SpreadEvenly(List<string> tokens, double start, double end, double confidence)
    {
        var result = new List<TranscriptWord>(tokens.Count);
        if (tokens.Count == 0) return result;

        var totalWeight = tokens.Sum(t => Math.Max(1, t.Length));
        var span = Math.Max(0.05, end - start);
        var cursor = start;

        foreach (var token in tokens)
        {
            var share = span * Math.Max(1, token.Length) / totalWeight;
            result.Add(new TranscriptWord(cursor, cursor + share, token, confidence));
            cursor += share;
        }
        return result;
    }

    public static List<string> Tokenize(string text) =>
        [.. Regex.Split(text.Trim(), @"\s+").Where(t => t.Length > 0)];

    private static string Normalize(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

public enum TranscriptOrigin { Whisper, ProvidedSubtitles, Manual }

public sealed class Transcript
{
    /// <summary>Shortest cue worth keeping — below this a row in the review table is unusable.</summary>
    private const double MinCueSeconds = 0.10;

    /// <summary>At or below this a cue was given no real slot by the recogniser.</summary>
    private const double TimelessCueSeconds = 0.02;

    public List<TranscriptSegment> Segments { get; set; } = [];
    public string Language { get; set; } = "auto";
    public TranscriptOrigin Origin { get; set; } = TranscriptOrigin.Whisper;

    public double Duration => Segments.Count == 0 ? 0 : Segments[^1].End;
    public bool HasWordTimings => Segments.Any(s => s.HasWordTimings);

    public string FullText => string.Join(" ", Segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));

    public int WordCount => Segments.Sum(s => TranscriptSegment.Tokenize(s.Text).Count);

    /// <summary>Sorts cues, drops empties and removes overlaps so downstream stages can assume order.</summary>
    public void Normalize()
    {
        Segments.RemoveAll(s => string.IsNullOrWhiteSpace(s.Text));
        Segments.Sort((a, b) => a.Start.CompareTo(b.Start));

        if (Segments.Count == 0) return;

        foreach (var s in Segments)
        {
            s.Text = Regex.Replace(s.Text.Trim(), @"\s{2,}", " ");
            if (s.End < s.Start) s.End = s.Start;
        }

        FoldTimelessCues(out var folded);
        if (Segments.Count == 0) return;

        // Whisper regularly emits cues whose start and end are identical, and stretching those to
        // a usable length runs them into the cue that follows. Fixing either one in isolation
        // breaks the other — pad first and cues overlap, trim first and cues collapse to nothing.
        // Sequencing them all at once settles both: every cue keeps a visible duration, none
        // overlaps its neighbour, no text is lost, and only the offending cues move at all.
        var spans = Segments.Select(s => (s.Start, s.End)).ToList();
        var windowEnd = Math.Max(
            Segments.Max(s => s.End),
            Segments[0].Start + Segments.Count * MinCueSeconds);

        TimingSequencer.Sequence(spans, Segments[0].Start, windowEnd, MinCueSeconds);

        for (var i = 0; i < Segments.Count; i++)
        {
            Segments[i].Start = spans[i].Start;
            Segments[i].End = spans[i].End;
        }

        // Words are clamped only once every cue boundary is final. Clamping earlier would pin
        // them against a boundary that a later step moves, leaving them outside their own cue.
        // A folded cue's words are spread across its final window first, for the same reason.
        foreach (var segment in Segments)
        {
            if (folded.Contains(segment)) segment.RespreadWords();
            segment.ClampWords();
        }
    }

    /// <summary>
    /// Folds away cues that were given no time at all.
    ///
    /// Whisper lays cues on a coarse grid — 2 second slots in practice — and when a phrase does
    /// not fit it can emit a cue whose start and end are the same instant, wedged between two cues
    /// that already touch. There is no slot to widen: the neighbours own every millisecond around
    /// it. Left alone it becomes a caption that flashes for a couple of frames.
    ///
    /// Its words belong to the sentence it completes, so the text joins the cue before it and the
    /// merged cue's words are respread across that window. The surviving words shift by a fraction
    /// of a second, which is the honest cost of Whisper having lost the timing outright.
    /// </summary>
    /// <param name="folded">The cues that took in another one, whose words still need respreading.</param>
    private void FoldTimelessCues(out HashSet<TranscriptSegment> folded)
    {
        folded = [];
        for (var i = Segments.Count - 1; i >= 0; i--)
        {
            var cue = Segments[i];
            if (cue.Duration > TimelessCueSeconds) continue;

            // Room to simply widen it? Then leave it for the sequencer, which does that better.
            var roomAfter = i + 1 < Segments.Count ? Segments[i + 1].Start - cue.Start : double.MaxValue;
            if (roomAfter > MinCueSeconds) continue;

            // Prefer the cue before: a timeless cue almost always completes its sentence.
            var target = i > 0 ? Segments[i - 1] : i + 1 < Segments.Count ? Segments[i + 1] : null;
            if (target is null) continue;   // the only cue there is; the sequencer gives it a minimum

            if (i > 0)
            {
                target.Text = $"{target.Text.Trim()} {cue.Text.Trim()}".Trim();
                target.End = Math.Max(target.End, cue.End);
                target.Words.AddRange(cue.Words);
            }
            else
            {
                target.Text = $"{cue.Text.Trim()} {target.Text.Trim()}".Trim();
                target.Start = Math.Min(target.Start, cue.Start);
                target.Words.InsertRange(0, cue.Words);
            }

            // The merged text is respread over the whole window once the cue boundaries are final
            // (see Normalize). Keeping the original timings would leave the folded words with
            // nowhere to go but the final instant, which is the flash this exists to remove. The
            // words are kept until then for how sure Whisper was of them.
            folded.Add(target);
            Segments.RemoveAt(i);
        }
    }

    /// <summary>All words in the transcript, in order — the timing source for karaoke captions.</summary>
    public IEnumerable<TranscriptWord> AllWords()
    {
        foreach (var segment in Segments)
        {
            if (segment.HasWordTimings)
            {
                foreach (var w in segment.Words) yield return w;
            }
            else
            {
                // Spread, not heard: the text keeps full confidence, as in RetimeWords.
                var tokens = TranscriptSegment.Tokenize(segment.Text);
                var span = segment.Duration / Math.Max(1, tokens.Count);
                for (var i = 0; i < tokens.Count; i++)
                    yield return new TranscriptWord(
                        segment.Start + i * span, segment.Start + (i + 1) * span, tokens[i],
                        TranscriptSegment.FullConfidence);
            }
        }
    }

    /// <summary>Ensures every cue carries word timings, synthesising them where Whisper gave none.</summary>
    public void EnsureWordTimings()
    {
        foreach (var segment in Segments)
        {
            if (segment.HasWordTimings) continue;
            segment.RetimeWords();
        }
    }

    public string ToSrt()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            sb.Append(i + 1).Append('\n');
            sb.Append(SrtTime(s.Start)).Append(" --> ").Append(SrtTime(s.End)).Append('\n');
            sb.Append(s.Text.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string SrtTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }
}
