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

        var prefix = Path.Combine(workDir, "transcript");
        var jsonPath = prefix + ".json";
        if (File.Exists(jsonPath)) File.Delete(jsonPath);

        var args = new List<string>
        {
            "-m", tools.WhisperModelPath!,
            "-f", wav,
            "-t", settings.WhisperThreads.ToString(),
            "-oj",            // JSON output
            "-ojf",           // include per-token timings
            "-of", prefix,
            "-pp",            // print progress so the UI can follow along
            "-ml", "0",       // let whisper choose natural segment lengths
        };
        if (!string.Equals(settings.WhisperLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-l");
            args.Add(settings.WhisperLanguage);
        }
        else
        {
            args.Add("-l");
            args.Add("auto");
        }

        sink.Report("transcribe", Lang.Ui[nameof(Strings.Whisper_Running)], 0);
        sink.Info("transcribe", $"Backend: {tools.WhisperBackendInfo.Describe(Lang.Log)}");

        var result = await ProcessRunner.RunAsync(tools.WhisperCli, args,
            workingDirectory: Path.GetDirectoryName(tools.WhisperCli),
            onStdErrLine: line =>
            {
                var m = ProgressPattern().Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
                    sink.Report("transcribe", Lang.Ui.Format(nameof(Strings.Whisper_Progress), pct), pct / 100.0);
                else if (!string.IsNullOrWhiteSpace(line))
                    sink.Debug("whisper", line);
            },
            captureOutput: true,
            ct: ct).ConfigureAwait(false);

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
        transcript.Normalize();
        transcript.EnsureWordTimings();

        sink.Report("transcribe", Lang.Ui.Format(nameof(Strings.Whisper_Done),
            Lang.Ui.Plural("Count_Cues", transcript.Segments.Count), Lang.Ui.Plural("Count_Words", transcript.WordCount)), 1);
        sink.Info("transcribe", $"Language: {transcript.Language}; {transcript.Segments.Count} cues");
        return transcript;
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
