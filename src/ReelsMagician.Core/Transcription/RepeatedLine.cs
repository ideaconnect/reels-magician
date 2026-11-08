using System.Text;
using System.Text.RegularExpressions;

namespace ReelsMagician.Core.Transcription;

/// <summary>
/// One line that a transcript gives again and again in a row: the sign that Whisper got stuck on
/// a sentence. whisper.cpp hands each 30 s window the text it has just written, so a sentence it
/// made up over music is carried on into the next window, and the next, for minutes (#75).
/// </summary>
/// <param name="Text">The line, as its first cue gives it.</param>
/// <param name="Count">How many cues in a row give it.</param>
/// <param name="Start">Where the first of those cues starts, in seconds.</param>
/// <param name="End">Where the last of them ends.</param>
public sealed partial record RepeatedLine(string Text, int Count, double Start, double End)
{
    /// <summary>How many times in a row one line has to come for the transcript to look stuck.</summary>
    public const int StuckAt = 5;

    /// <summary>The longest run of one line in the cues, when it is at least <paramref name="atLeast"/> long; otherwise null.</summary>
    public static RepeatedLine? Longest(IEnumerable<TranscriptSegment> segments, int atLeast = StuckAt)
    {
        var watch = new RepeatWatch(atLeast);
        foreach (var segment in segments) watch.Add(segment.Text, segment.Start, segment.End);
        return watch.Longest;
    }

    /// <summary>
    /// What two lines are compared by: their words, in lower case, without punctuation. Null for a
    /// line that is no speech: a sound Whisper describes, such as "[Music]", "(applause)",
    /// "*music*" or "♪", or one with no words at all. Whisper writes one of those for each window of
    /// a long piece of music, which is the music, not a transcript that is stuck.
    /// </summary>
    public static string? Key(string text)
    {
        if (SoundOnly().IsMatch(text)) return null;

        var key = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c) && key.Length > 0 && key[^1] != ' ') key.Append(' ');
        }
        var words = key.ToString().TrimEnd();
        return words.Length == 0 ? null : words;
    }

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫\s]+)\s*$")]
    private static partial Regex SoundOnly();
}

/// <summary>
/// Follows a transcript's lines as they come, one at a time, and keeps the longest run of one
/// line that is at least <c>atLeast</c> long. A line that is no speech (<see cref="RepeatedLine.Key"/>)
/// neither counts nor ends a run.
/// </summary>
public sealed class RepeatWatch(int atLeast = RepeatedLine.StuckAt)
{
    private string? _key;
    private string _text = "";
    private int _count;
    private double _start, _end;

    /// <summary>The longest run so far that is at least <c>atLeast</c> long; null while there is none.</summary>
    public RepeatedLine? Longest { get; private set; }

    public bool IsStuck => Longest is not null;

    /// <summary>Takes the next line. True once, when it makes the first run long enough to look stuck.</summary>
    public bool Add(string text, double start, double end)
    {
        if (RepeatedLine.Key(text) is not { } key) return false;

        if (key == _key)
        {
            _count++;
            _end = end;
        }
        else
        {
            _key = key;
            _text = text.Trim();
            _count = 1;
            _start = start;
            _end = end;
        }

        if (_count < atLeast || (Longest is { } longest && longest.Count >= _count)) return false;

        var first = Longest is null;
        Longest = new RepeatedLine(_text, _count, _start, _end);
        return first;
    }
}
