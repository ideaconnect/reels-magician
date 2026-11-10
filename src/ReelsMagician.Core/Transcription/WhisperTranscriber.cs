using System.Text.Json;
using System.Text.RegularExpressions;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Transcription;

/// <summary>
/// Drives whisper.cpp to produce a word-timed transcript. Token-level output is requested so
/// captions can highlight the exact moment each word is spoken.
/// </summary>
public sealed partial class WhisperTranscriber(Toolset tools, AppSettings settings, IProgressSink sink)
{
    [GeneratedRegex(@"progress\s*=\s*(\d{1,3})\s*%")]
    private static partial Regex ProgressPattern();

    // whisper.cpp emits control tokens alongside real text. They come in two shapes:
    // named markers such as [_BEG_] and [_EOT_], and timestamp markers such as [_TT_199]
    // which carry a number and no trailing underscore. Both must go, or they end up
    // glued to the previous word and burned into the captions.
    [GeneratedRegex(@"^\s*(\[_[A-Za-z0-9_]*\]|<\|[^|]*\|>)\s*$")]
    private static partial Regex SpecialToken();

    /// <summary>Removes any control marker still embedded in assembled word text.</summary>
    [GeneratedRegex(@"\[_[A-Za-z0-9_]*\]|<\|[^|]*\|>")]
    private static partial Regex EmbeddedMarker();

    /// <summary>A cue as whisper-cli prints it while it runs: <c>[00:01:02.000 --> 00:01:04.000]   Text</c>.</summary>
    [GeneratedRegex(@"^\s*\[(?<from>\d+:\d{2}:\d{2}[.,]\d{1,3})\s*-->\s*(?<to>\d+:\d{2}:\d{2}[.,]\d{1,3})\]\s*(?<text>.*)$")]
    private static partial Regex PrintedCue();

    public async Task<Transcript> TranscribeAsync(
        string videoPath, double durationSeconds, string workDir, CancellationToken ct)
    {
        if (!tools.HasWhisper)
            throw new InvalidOperationException(Lang.Ui[nameof(Strings.Whisper_NotInstalled)]);

        Directory.CreateDirectory(workDir);
        var ffmpeg = new FFmpegTool(tools.Ffmpeg, sink);
        var wav = Path.Combine(workDir, "speech16k.wav");

        if (!File.Exists(wav) || new FileInfo(wav).Length < 1024)
            await ffmpeg.ExtractSpeechWavAsync(videoPath, wav, durationSeconds, ct).ConfigureAwait(false);

        sink.Report("transcribe", Lang.Ui[nameof(Strings.Whisper_Running)], 0);
        sink.Info("transcribe", $"Backend: {tools.WhisperBackendInfo.Describe(Lang.Log)}");

        // The first pass hands each 30 s window the text before it, which keeps names, spelling
        // and punctuation the same from one window to the next. Over a long stretch without
        // speech that same context can trap Whisper in one made-up line for minutes (#75), so a
        // pass that repeats one line StuckAt times in a row is stopped there, and the audio is
        // transcribed again with no text carried over (-mc 0). Only a stuck transcript gets the
        // second pass: on speech, -mc 0 merged cues and lost the last sentence of the narrated
        // video the end-to-end check uses.
        var (transcript, stuck) = await RunAsync(wav, workDir, keepContext: true, ct).ConfigureAwait(false);
        if (stuck is not null)
        {
            sink.Warn("transcribe",
                $"Whisper got stuck on one line: \"{stuck.Text}\" {stuck.Count} times in a row from " +
                $"{Transcript.SrtTime(stuck.Start)}. Transcribing again without carrying text from one window to the next (-mc 0).");
            sink.Report("transcribe", Lang.Ui[nameof(Strings.Whisper_Retrying)], 0);

            (transcript, stuck) = await RunAsync(wav, workDir, keepContext: false, ct).ConfigureAwait(false);
            if (stuck is not null)
                sink.Warn("transcribe",
                    $"The transcript still repeats one line: \"{stuck.Text}\" {stuck.Count} times in a row from " +
                    $"{Transcript.SrtTime(stuck.Start)} to {Transcript.SrtTime(stuck.End)}. Check it on the Subtitles tab.");
        }

        transcript!.Normalize();
        transcript.EnsureWordTimings();

        sink.Report("transcribe", Lang.Ui.Format(nameof(Strings.Whisper_Done),
            Lang.Ui.Plural("Count_Cues", transcript.Segments.Count), Lang.Ui.Plural("Count_Words", transcript.WordCount)), 1);
        sink.Info("transcribe", $"Language: {transcript.Language}; {transcript.Segments.Count} cues");
        return transcript;
    }

    /// <summary>
    /// One run of whisper-cli over the speech. A run with the context kept watches the cues it
    /// prints and is stopped as soon as one line comes <see cref="RepeatedLine.StuckAt"/> times in a
    /// row; it then gives no transcript, only that line. Otherwise the transcript as whisper.cpp
    /// wrote it, before normalising, with its longest run of one line when that looks stuck.
    /// </summary>
    private async Task<(Transcript? Transcript, RepeatedLine? Stuck)> RunAsync(
        string wav, string workDir, bool keepContext, CancellationToken ct)
    {
        var prefix = Path.Combine(workDir, "transcript");
        var jsonPath = prefix + ".json";
        if (File.Exists(jsonPath)) File.Delete(jsonPath);

        var args = Arguments(tools.WhisperModelPath!, wav, prefix, settings.WhisperThreads, settings.WhisperLanguage, keepContext);
        var progressKey = keepContext ? nameof(Strings.Whisper_Progress) : nameof(Strings.Whisper_RetryProgress);

        var watch = new RepeatWatch();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(tools.WhisperCli, args,
                workingDirectory: Path.GetDirectoryName(tools.WhisperCli),
                onStdOutLine: line =>
                {
                    if (!keepContext || ParsePrintedCue(line) is not { } cue) return;
                    if (watch.Add(cue.Text, cue.Start, cue.End)) stop.Cancel();
                },
                onStdErrLine: line =>
                {
                    var m = ProgressPattern().Match(line);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
                        sink.Report("transcribe", Lang.Ui.Format(progressKey, pct), pct / 100.0);
                    else if (!string.IsNullOrWhiteSpace(line))
                        sink.Debug("whisper", line);
                },
                captureOutput: true,
                ct: stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && watch.Longest is { } early)
        {
            return (null, early);
        }

        if (!result.Success && !File.Exists(jsonPath))
        {
            if (ModelDidNotFitException.FromLog(result.CombinedTail()) is { } didNotFit)
            {
                foreach (var line in didNotFit.Evidence) sink.Warn("transcribe", $"whisper.cpp: {line}");
                throw didNotFit;
            }
            throw new ToolFailedException("whisper.cpp", result.ExitCode, result.CombinedTail());
        }

        if (!File.Exists(jsonPath))
            throw new InvalidOperationException(Lang.Ui[nameof(Strings.Whisper_NoTranscript)]);

        var transcript = ParseWhisperJson(await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false));
        return (transcript, RepeatedLine.Longest(transcript.Segments));
    }

    /// <summary>
    /// whisper-cli's arguments. Without <paramref name="keepContext"/> they add <c>-mc 0</c>
    /// (<c>--max-context 0</c>): no text from one 30 s window is handed to the next, so a line
    /// Whisper made up over music cannot carry on past its own window.
    /// </summary>
    public static List<string> Arguments(
        string modelPath, string wav, string outputPrefix, int threads, string language, bool keepContext)
    {
        var args = new List<string>
        {
            "-m", modelPath,
            "-f", wav,
            "-t", threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-oj",            // JSON output
            "-ojf",           // include per-token timings
            "-of", outputPrefix,
            "-pp",            // print progress so the UI can follow along
            "-ml", "0",       // let whisper choose natural segment lengths
            "-l", string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : language,
        };
        if (!keepContext) args.AddRange(["-mc", "0"]);
        return args;
    }

    /// <summary>A cue whisper-cli printed while it ran, or null for any other line it printed.</summary>
    public static (double Start, double End, string Text)? ParsePrintedCue(string line)
    {
        var m = PrintedCue().Match(line);
        if (!m.Success) return null;
        return (SubtitleParser.ParseTime(m.Groups["from"].Value), SubtitleParser.ParseTime(m.Groups["to"].Value),
            m.Groups["text"].Value.Trim());
    }

    /// <summary>Parses whisper.cpp's --output-json-full document into cues with word timings.</summary>
    public static Transcript ParseWhisperJson(string json)
    {
        var transcript = new Transcript { Origin = TranscriptOrigin.Whisper };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("result", out var res) &&
            res.TryGetProperty("language", out var lang) && lang.GetString() is { Length: > 0 } l)
            transcript.Language = l;

        if (!root.TryGetProperty("transcription", out var cues) || cues.ValueKind != JsonValueKind.Array)
            return transcript;

        foreach (var cue in cues.EnumerateArray())
        {
            var (start, end) = ReadOffsets(cue);
            var text = (cue.TryGetProperty("text", out var t) ? t.GetString() : null)?.Trim() ?? "";
            if (text.Length == 0) continue;

            var segment = new TranscriptSegment { Start = start, End = end, Text = text };

            if (cue.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
                segment.Words = GroupTokensIntoWords(tokens, start);

            if (segment.Words.Count == 0) segment.RetimeWords();
            transcript.Segments.Add(segment);
        }
        return transcript;
    }

    /// <summary>
    /// Whisper emits sub-word tokens; a leading space marks a word boundary, so tokens are
    /// merged until the next boundary and the word inherits the span of its pieces.
    /// </summary>
    private static List<TranscriptWord> GroupTokensIntoWords(JsonElement tokens, double cueStart)
    {
        var words = new List<TranscriptWord>();
        string current = "";
        double currentStart = cueStart, currentEnd = cueStart, confidenceSum = 0;
        var pieceCount = 0;

        void Flush()
        {
            // Belt and braces: a marker fused to a real token would otherwise survive into the
            // burned-in captions, where it is very visible and very hard to explain.
            var trimmed = EmbeddedMarker().Replace(current, "").Trim();
            if (trimmed.Length == 0) { current = ""; pieceCount = 0; confidenceSum = 0; return; }
            var end = Math.Max(currentEnd, currentStart + 0.02);
            words.Add(new TranscriptWord(currentStart, end, trimmed,
                pieceCount > 0 ? confidenceSum / pieceCount : 1.0));
            current = "";
            pieceCount = 0;
            confidenceSum = 0;
        }

        foreach (var token in tokens.EnumerateArray())
        {
            var raw = token.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            if (raw.Length == 0 || SpecialToken().IsMatch(raw)) continue;

            var (s, e) = ReadOffsets(token);
            var p = token.TryGetProperty("p", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetDouble() : 1.0;

            var startsNewWord = raw.StartsWith(' ') || current.Length == 0;
            if (startsNewWord && current.Length > 0) Flush();

            if (current.Length == 0) currentStart = s > 0 ? s : currentEnd;
            current += raw;
            currentEnd = Math.Max(e, currentStart);
            confidenceSum += p;
            pieceCount++;
        }
        Flush();

        // Word times are left exactly as the tokens report them. Dragging the first word back to
        // the cue start would fire its highlight before the word is actually spoken, and the cue
        // bounds are enforced later by TranscriptSegment.ClampWords.
        return words;
    }

    /// <summary>Reads the millisecond offsets whisper.cpp attaches to cues and tokens.</summary>
    private static (double Start, double End) ReadOffsets(JsonElement element)
    {
        if (element.TryGetProperty("offsets", out var off))
        {
            var from = off.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
            var to = off.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : from;
            return (from / 1000.0, to / 1000.0);
        }
        if (element.TryGetProperty("timestamps", out var ts))
        {
            var from = ts.TryGetProperty("from", out var f) ? SubtitleParser.ParseTime(f.GetString() ?? "") : 0;
            var to = ts.TryGetProperty("to", out var t) ? SubtitleParser.ParseTime(t.GetString() ?? "") : from;
            return (from, to);
        }
        return (0, 0);
    }
}
