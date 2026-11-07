using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelsMagician.Core.Transcription;

/// <summary>
/// Reads SRT and WebVTT sidecar files. YouTube's auto-generated VTT carries inline
/// per-word timestamps, which are preserved so karaoke captions work without re-running Whisper.
/// </summary>
public static partial class SubtitleParser
{
    [GeneratedRegex(@"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})[.,](?<ms>\d{1,3})")]
    private static partial Regex TimeCode();

    [GeneratedRegex(@"^\s*(?<from>[\d:.,]+)\s*-->\s*(?<to>[\d:.,]+)")]
    private static partial Regex CueLine();

    // <00:00:01.234> markers, optionally followed by <c>…</c> spans.
    [GeneratedRegex(@"<(?<t>\d{1,3}:\d{2}:\d{2}[.,]\d{1,3})>")]
    private static partial Regex InlineTime();

    [GeneratedRegex(@"</?c[^>]*>|</?v[^>]*>|\{\\[^}]*\}")]
    private static partial Regex Markup();

    public static Transcript Load(string path)
    {
        var text = File.ReadAllText(path, DetectEncoding(path));
        var transcript = Parse(text);
        transcript.Origin = TranscriptOrigin.ProvidedSubtitles;
        return transcript;
    }

    public static Transcript Parse(string content)
    {
        var transcript = new Transcript { Origin = TranscriptOrigin.ProvidedSubtitles };
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var i = 0;
        while (i < lines.Length)
        {
            var match = CueLine().Match(lines[i]);
            if (!match.Success) { i++; continue; }

            var start = ParseTime(match.Groups["from"].Value);
            var end = ParseTime(match.Groups["to"].Value);
            i++;

            var body = new StringBuilder();
            while (i < lines.Length && lines[i].Length > 0 && !CueLine().IsMatch(lines[i]))
            {
                // YouTube opens each word-timed cue with a line holding a single space. Ending the
                // cue there would drop the only copy of the line that carries word timings.
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    if (body.Length > 0) break;
                    i++;
                    continue;
                }
                if (body.Length > 0) body.Append('\n');
                body.Append(lines[i]);
                i++;
            }

            var raw = body.ToString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var segment = new TranscriptSegment { Start = start, End = end };
            var words = ExtractInlineWords(raw, start, end);
            segment.Text = Clean(Markup().Replace(InlineTime().Replace(raw, " "), " "));
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;

            segment.Words = words;
            if (segment.Words.Count == 0) segment.RetimeWords();
            transcript.Segments.Add(segment);
        }

        DeduplicateRollingCaptions(transcript);
        transcript.Normalize();
        return transcript;
    }

    /// <summary>Pulls per-word timings out of a VTT cue body when the file provides them.</summary>
    private static List<TranscriptWord> ExtractInlineWords(string raw, double cueStart, double cueEnd)
    {
        var matches = InlineTime().Matches(raw);
        if (matches.Count == 0) return [];

        var words = new List<TranscriptWord>();

        // Text before the first marker belongs to the cue start.
        var leading = Clean(Markup().Replace(raw[..matches[0].Index], " "));
        var pendingStart = cueStart;
        foreach (var token in TranscriptSegment.Tokenize(leading))
            words.Add(new TranscriptWord(pendingStart, pendingStart, token));

        for (var m = 0; m < matches.Count; m++)
        {
            var time = ParseTime(matches[m].Groups["t"].Value);
            var from = matches[m].Index + matches[m].Length;
            var to = m + 1 < matches.Count ? matches[m + 1].Index : raw.Length;
            if (to < from) continue;

            var chunk = Clean(Markup().Replace(raw[from..to], " "));
            foreach (var token in TranscriptSegment.Tokenize(chunk))
                words.Add(new TranscriptWord(time, time, token));
        }

        // Each word ends where the next one starts.
        for (var w = 0; w < words.Count; w++)
        {
            var end = w + 1 < words.Count ? words[w + 1].Start : cueEnd;
            if (end <= words[w].Start) end = words[w].Start + 0.08;
            words[w] = words[w] with { End = end };
        }
        return words;
    }

    /// <summary>
    /// YouTube auto-captions repeat the previous line in each new cue to create a rolling
    /// effect, and put a 10 ms snapshot of the finished line between cues. Those duplicates
    /// would be read twice by the analyser and burned in twice, so they are trimmed.
    /// </summary>
    private static void DeduplicateRollingCaptions(Transcript transcript)
    {
        // Each cue is compared with the last one that was kept. The snapshot in between is
        // emptied, so comparing with the cue just before would let the repeat through.
        var prev = "";
        foreach (var cur in transcript.Segments)
        {
            if (prev.Length > 0 && StartsWithWords(cur.Text, prev))
            {
                var remainder = cur.Text[prev.Length..].Trim();
                if (remainder.Length == 0) { cur.Text = ""; continue; }
                cur.Text = remainder;
                if (cur.Words.Count > 0)
                {
                    var keep = TranscriptSegment.Tokenize(remainder).Count;
                    if (keep < cur.Words.Count) cur.Words.RemoveRange(0, cur.Words.Count - keep);
                    if (cur.Words.Count > 0) cur.Start = cur.Words[0].Start;
                }
            }

            if (!string.IsNullOrWhiteSpace(cur.Text)) prev = cur.Text.Trim();
        }
        transcript.Segments.RemoveAll(s => string.IsNullOrWhiteSpace(s.Text));
    }

    /// <summary>True when <paramref name="text"/> opens with <paramref name="prefix"/> as whole words.</summary>
    private static bool StartsWithWords(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (text.Length == prefix.Length || char.IsWhiteSpace(text[prefix.Length]));

    private static string Clean(string s) =>
        Regex.Replace(WebUtilityDecode(s), @"\s{2,}", " ").Trim();

    private static string WebUtilityDecode(string s) =>
        s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
         .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");

    public static double ParseTime(string value)
    {
        var m = TimeCode().Match(value);
        if (!m.Success)
        {
            // WebVTT allows MM:SS.mmm with no hour component.
            var shortForm = Regex.Match(value, @"(?<m>\d{1,3}):(?<s>\d{2})[.,](?<ms>\d{1,3})");
            if (!shortForm.Success) return 0;
            return int.Parse(shortForm.Groups["m"].Value) * 60
                 + int.Parse(shortForm.Groups["s"].Value)
                 + PadMs(shortForm.Groups["ms"].Value);
        }
        return int.Parse(m.Groups["h"].Value) * 3600
             + int.Parse(m.Groups["m"].Value) * 60
             + int.Parse(m.Groups["s"].Value)
             + PadMs(m.Groups["ms"].Value);
    }

    private static double PadMs(string ms) =>
        double.Parse(ms.PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000.0;

    private static Encoding DetectEncoding(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[3];
        var read = fs.Read(bom);
        if (read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
        return new UTF8Encoding(false);
    }
}
