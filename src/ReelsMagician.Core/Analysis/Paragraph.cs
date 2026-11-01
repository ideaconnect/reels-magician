using System.Text;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.Core.Analysis;

/// <summary>
/// A coherent block of speech built from several caption cues. The analyser reasons over these
/// rather than over individual cues, so it judges complete thoughts instead of stray words.
/// </summary>
public sealed class Paragraph
{
    public int Index { get; init; }
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<int> SegmentIndices { get; init; } = [];

    public double Duration => Math.Max(0, End - Start);
    public int WordCount => TranscriptSegment.Tokenize(Text).Count;

    public override string ToString() =>
        $"[{Index}] {TimeSpan.FromSeconds(Start):mm\\:ss}-{TimeSpan.FromSeconds(End):mm\\:ss} {Text}";
}

public sealed record ParagraphOptions(
    double MaxDurationSeconds = 30,
    double MinDurationSeconds = 6,
    int MaxCharacters = 700,
    double SentenceBreakPauseSeconds = 0.55,
    double HardBreakPauseSeconds = 1.4);

/// <summary>
/// Groups cues into paragraphs on sentence endings and speech pauses, capped so no single
/// block is too long to reason about or too short to stand on its own as a clip.
/// </summary>
public static class ParagraphBuilder
{
    public static List<Paragraph> Build(Transcript transcript, ParagraphOptions? options = null)
    {
        var opt = options ?? new ParagraphOptions();
        var paragraphs = new List<Paragraph>();
        if (transcript.Segments.Count == 0) return paragraphs;

        var buffer = new StringBuilder();
        var indices = new List<int>();
        double start = transcript.Segments[0].Start, end = start;

        void Flush()
        {
            var text = buffer.ToString().Trim();
            if (text.Length == 0) { buffer.Clear(); indices.Clear(); return; }
            paragraphs.Add(new Paragraph
            {
                Index = paragraphs.Count,
                Start = start,
                End = end,
                Text = text,
                SegmentIndices = [.. indices],
            });
            buffer.Clear();
            indices.Clear();
        }

        for (var i = 0; i < transcript.Segments.Count; i++)
        {
            var seg = transcript.Segments[i];
            var segText = seg.Text.Trim();
            if (segText.Length == 0) continue;

            if (indices.Count == 0) start = seg.Start;
            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(segText);
            indices.Add(i);
            end = seg.End;

            var gapToNext = i + 1 < transcript.Segments.Count
                ? transcript.Segments[i + 1].Start - seg.End
                : double.MaxValue;

            var duration = end - start;
            var endsSentence = EndsSentence(segText);
            var tooLong = duration >= opt.MaxDurationSeconds || buffer.Length >= opt.MaxCharacters;
            var longEnough = duration >= opt.MinDurationSeconds;

            var shouldBreak =
                tooLong ||
                (longEnough && endsSentence && gapToNext >= opt.SentenceBreakPauseSeconds) ||
                (longEnough && gapToNext >= opt.HardBreakPauseSeconds) ||
                i == transcript.Segments.Count - 1;

            if (shouldBreak) Flush();
        }
        Flush();

        // Fold a tiny trailing block back into its predecessor rather than leaving a stub.
        if (paragraphs.Count >= 2 && paragraphs[^1].Duration < opt.MinDurationSeconds / 2)
        {
            var last = paragraphs[^1];
            var prev = paragraphs[^2];
            prev.End = last.End;
            prev.Text = (prev.Text + " " + last.Text).Trim();
            prev.SegmentIndices.AddRange(last.SegmentIndices);
            paragraphs.RemoveAt(paragraphs.Count - 1);
        }

        return paragraphs;
    }

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd('"', '\'', ')', ']', '»', '”');
        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or '…' or ':' or ';';
    }

    /// <summary>Formats paragraphs for the prompt: index, timing and text, one per line.</summary>
    public static string Render(IEnumerable<Paragraph> paragraphs)
    {
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            sb.Append('[').Append(p.Index).Append("] (")
              .Append(TimeSpan.FromSeconds(p.Start).ToString(@"h\:mm\:ss")).Append(", ")
              .Append(p.Duration.ToString("0")).Append("s) ")
              .Append(p.Text.Replace('\n', ' '))
              .Append('\n');
        }
        return sb.ToString();
    }
}
