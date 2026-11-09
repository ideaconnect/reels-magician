using System.Text.RegularExpressions;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// A transcript stuck on one line (issue #75). whisper.cpp hands each window the text it has just
/// written, so a sentence made up over music can repeat for minutes: on Tears of Steel, 419 of
/// 442 lines were "I'm not sure how to do this.". The app sees such a run, transcribes the audio
/// again with no text carried between windows (-mc 0), and warns about a run that is left.
/// </summary>
public static class StuckTranscriptChecks
{
    private const string Stuck = "I'm not sure how to do this.";

    public static void Run()
    {
        Check.Section("A transcript stuck on one line is seen, transcribed again and warned about (issue #75)");

        Lines();
        Runs();
        Watching();
        PrintedCues();
        Arguments();
        Window();
    }

    /// <summary>What two lines are compared by.</summary>
    private static void Lines()
    {
        Check.True(RepeatedLine.Key(Stuck) is { } key && key == RepeatedLine.Key("  i'm NOT sure how to do this!  "),
            "lines are compared without case, punctuation or the space round them");
        Check.True(RepeatedLine.Key(Stuck) != RepeatedLine.Key("I'm not sure how to do that."),
            "but by every word");
        foreach (var sound in (string[])["[Music]", "(applause)", "*music*", "♪ ♪", "...", " "])
            Check.True(RepeatedLine.Key(sound) is null, $"\"{sound}\" is no speech, so it is never a stuck line");
        foreach (var sounds in (string[])["[Music] (applause)", "♪ (music) ♪", "[MUSIC PLAYING]♪♪"])
            Check.True(RepeatedLine.Key(sounds) is null, $"nor is \"{sounds}\", made only of sounds");
        Check.True(RepeatedLine.Key("♪ la la la ♪") is not null, "but sung words are");
        Check.True(RepeatedLine.Key("(laughs) Not again.") == "laughs not again", "and so is a line with words beside a sound");
    }

    /// <summary>Finding the longest run in a transcript.</summary>
    private static void Runs()
    {
        // The shape the issue reports: the first minute of dialogue, then runs of the same line,
        // the last of them 273 long, from 5:29 to 12:02.
        var cues = new List<TranscriptSegment>();
        var t = 0.0;
        void Add(string text, int times = 1, double each = 1.4)
        {
            for (var i = 0; i < times; i++, t += each)
                cues.Add(new TranscriptSegment { Start = t, End = t + each, Text = text });
        }
        Add("We have main engine start, four, three, two, one.");
        Add("You're a jerk, Tom.");
        Add(Stuck, 40);
        Add("Shouldn't you be down there?");
        Add(Stuck, 60);
        Add("Listen, Celia, I was young and a dick.");
        t = 329;
        Add(Stuck, 273);

        var longest = RepeatedLine.Longest(cues);
        Check.True(longest is { Count: 273 }, $"the longest run is found ({longest?.Count ?? 0} times, expected 273)");
        Check.True(longest is { Text: Stuck } && Math.Abs(longest.Start - 329) < 1e-9 && Math.Abs(longest.End - (329 + 273 * 1.4)) < 1e-6,
            "with its line as Whisper wrote it, where it starts and where it ends");

        Check.True(RepeatedLine.Longest(Repeat(Stuck, RepeatedLine.StuckAt - 1)) is null,
            $"{RepeatedLine.StuckAt - 1} times in a row is not stuck yet");
        Check.True(RepeatedLine.Longest(Repeat(Stuck, RepeatedLine.StuckAt)) is { Count: RepeatedLine.StuckAt },
            $"{RepeatedLine.StuckAt} times in a row is");
        Check.Equal(5, RepeatedLine.StuckAt, "five in a row, as the issue suggests and the README says");

        var broken = Repeat(Stuck, 3).Concat(Repeat("Abort!", 1)).Concat(Repeat(Stuck, 3)).ToList();
        Check.True(RepeatedLine.Longest(broken) is null, "another line in between ends the run");

        var throughMusic = Repeat(Stuck, 2).Concat(Repeat("[Music]", 3)).Concat(Repeat(Stuck, 3)).ToList();
        Check.True(RepeatedLine.Longest(throughMusic) is { Count: 5 },
            "a sound description in between neither counts nor ends it");
        Check.True(RepeatedLine.Longest(Repeat("*music*", 12)) is null,
            "and a long piece of music, one \"*music*\" per window, is not stuck");

        // A clean speech transcript, such as the end-to-end check's narration, gives no warning.
        Check.True(RepeatedLine.Longest(
            [.. "Most people set enormous goals. The problem is not willpower. Pick one habit. Pick one habit."
                .Split(". ").Select((s, i) => new TranscriptSegment { Start = i * 3, End = i * 3 + 2, Text = s })]) is null,
            "speech that says a line twice is not stuck");

        // What whisper-cli writes: the run is found in its JSON as well.
        var json = "{\"result\":{\"language\":\"en\"},\"transcription\":[" + string.Join(",",
            Enumerable.Range(0, 6).Select(i =>
                $"{{\"offsets\":{{\"from\":{62000 + i * 1000},\"to\":{63000 + i * 1000}}},\"text\":\" {Stuck}\",\"tokens\":[]}}")) + "]}";
        Check.True(RepeatedLine.Longest(WhisperTranscriber.ParseWhisperJson(json).Segments) is { Count: 6 } fromJson
                   && Math.Abs(fromJson.Start - 62) < 1e-9,
            "a run in whisper.cpp's JSON is found, with its time");
    }

    /// <summary>Following the lines as whisper-cli prints them, to stop a stuck pass early.</summary>
    private static void Watching()
    {
        var watch = new RepeatWatch();
        var signals = new List<int>();
        for (var i = 1; i <= 8; i++)
            if (watch.Add(Stuck, i, i + 1)) signals.Add(i);

        Check.True(signals.SequenceEqual([RepeatedLine.StuckAt]),
            $"the watch says so once, at the {RepeatedLine.StuckAt}th line in a row (said at: {string.Join(", ", signals)})");
        Check.True(watch is { IsStuck: true, Longest.Count: 8 }, "and keeps counting the run after that");

        var clean = new RepeatWatch();
        foreach (var line in (string[])["One.", "Two.", "One.", "Two.", "One.", "Two."]) clean.Add(line, 0, 1);
        Check.True(!clean.IsStuck, "lines that take turns are not one line in a row");
    }

    private static void PrintedCues()
    {
        var cue = WhisperTranscriber.ParsePrintedCue("[00:05:29.280 --> 00:05:30.280]   I'm not sure how to do this.");
        Check.True(cue is { } c && Math.Abs(c.Start - 329.28) < 1e-9 && Math.Abs(c.End - 330.28) < 1e-9 && c.Text == Stuck,
            "a cue whisper-cli prints is read: its start, end and text");
        Check.True(WhisperTranscriber.ParsePrintedCue("[01:02:03.004 --> 01:02:04.000]  Hi.") is { Start: > 3723.003 and < 3723.005 },
            "with the hours");
        foreach (var other in (string[])["whisper_print_progress_callback: progress =  60%", "", "main: processing 'speech16k.wav'"])
            Check.True(WhisperTranscriber.ParsePrintedCue(other) is null, $"\"{other}\" is no cue");
    }

    /// <summary>The two passes differ only in the text carried from one window to the next.</summary>
    private static void Arguments()
    {
        var first = WhisperTranscriber.Arguments("model.bin", "speech16k.wav", "transcript", 10, "auto", keepContext: true);
        var again = WhisperTranscriber.Arguments("model.bin", "speech16k.wav", "transcript", 10, "auto", keepContext: false);

        Check.True(!first.Contains("-mc"), "the first pass keeps whisper.cpp's context, which keeps names and punctuation steady");
        Check.True(again.IndexOf("-mc") is var mc and >= 0 && mc + 1 < again.Count && again[mc + 1] == "0",
            "the second pass carries no text over: -mc 0");
        Check.True(again.Take(again.Count - 2).SequenceEqual(first) && again.Count == first.Count + 2,
            "and is otherwise the same run");
        Check.True(first.Contains("-ojf") && Pair(first, "-ml", "0") && Pair(first, "-t", "10") && Pair(first, "-l", "auto"),
            "both ask for word timings, natural cue lengths, the threads and the language as before");
        Check.True(Pair(WhisperTranscriber.Arguments("m", "w", "p", 4, "AUTO", true), "-l", "auto")
                   && Pair(WhisperTranscriber.Arguments("m", "w", "p", 4, "pl", true), "-l", "pl"),
            "a language is passed on, and Auto in any case is auto");
    }

    /// <summary>The Subtitles tab warns, and Analyse asks before it takes a stuck transcript.</summary>
    private static void Window()
    {
        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var window = AppXaml.Read(root, "MainWindow.axaml");
        var subtitles = Regex.Match(window, @"<!-- ══ Subtitles ══ -->.*?</TabItem>", RegexOptions.Singleline).Value;
        Check.True(Regex.IsMatch(subtitles, @"<local:InfoBox Kind=""Warning"" Text=""\{Binding StuckWarning\}"""),
            "the Subtitles tab shows the warning in an amber box");

        var model = File.ReadAllText(Path.Combine(app, "ViewModels", "MainViewModel.cs"));
        var analyse = Regex.Match(model, @"private async Task AnalyzeAsync\(\)\s*\{\s*(?<first>[^\n]*)").Groups["first"].Value;
        Check.True(analyse.Contains("ConfirmStuckAsync()", StringComparison.Ordinal),
            "Analyse asks first, before the memory check or anything else");
        Check.Contains(model, "_shell.AskAsync(Strings.Stuck_Title, question, Strings.Stuck_Analyse, Strings.Common_Cancel)",
            "with Cancel as the answer that changes nothing");
        Check.Contains(model, "_project.Transcript is { Origin: TranscriptOrigin.Whisper }",
            "only a Whisper transcript is warned about: supplied and imported subtitles are their author's");

        // Working the warning out after every row added reads the list once per row: filling 4,000
        // cues took 0.9 s on the window's thread, against 26 ms when it is worked out once.
        Check.Contains(model, "if (!_populatingSubtitles) ShowStuckWarning();",
            "an added or removed row updates the warning, but not while the list is being filled");
        var populate = Regex.Match(model, @"private void PopulateSubtitles\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline)
            .Groups["body"].Value;
        var filled = populate.IndexOf("_populatingSubtitles = false;", StringComparison.Ordinal);
        Check.True(populate.Contains("_populatingSubtitles = true;", StringComparison.Ordinal) && filled >= 0
                   && populate.IndexOf("ShowStuckWarning();", filled, StringComparison.Ordinal) > filled,
            "filling the list works the warning out once, after the last row");
    }

    private static List<TranscriptSegment> Repeat(string text, int times) =>
        [.. Enumerable.Range(0, times).Select(i => new TranscriptSegment { Start = i * 2, End = i * 2 + 1.5, Text = text })];

    private static bool Pair(List<string> args, string name, string value) =>
        args.IndexOf(name) is var i and >= 0 && i + 1 < args.Count && args[i + 1] == value;
}
