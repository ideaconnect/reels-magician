using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.Media;

/// <summary>A local video ready for the pipeline, plus whatever context we know about it.</summary>
public sealed record ResolvedSource(
    string VideoPath,
    string Title,
    MediaInfo Info,
    string? ProvidedSubtitlePath,
    string? OriginalUrl)
{
    public bool CameFromLink => OriginalUrl is not null;
}

/// <summary>
/// Turns whatever the user typed — a local .mp4 or a link — into a local high-resolution file.
/// Downloads prefer 4K and refuse to settle below 1080p while a better format exists, because
/// a 1080×1920 vertical crop taken from a 1080p landscape master is already an upscale.
/// </summary>
public sealed partial class SourceResolver(Toolset tools, IProgressSink sink)
{
    [GeneratedRegex(@"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // yt-dlp --newline emits one line per update:
    //   [download]   4.2% of ~1.34GiB at    5.00MiB/s ETA 04:12
    [GeneratedRegex(@"^\[download\]\s+(?<pct>\d{1,3}(?:\.\d+)?)%(?<rest>.*)$")]
    private static partial Regex ProgressPattern();

    [GeneratedRegex(@"^\[(?<stage>Merger|ExtractAudio|VideoConvertor|FixupM3u8|Fixup\w*)\]")]
    private static partial Regex PostProcessPattern();

    public static bool LooksLikeUrl(string input) =>
        !string.IsNullOrWhiteSpace(input) && UrlPattern().IsMatch(input.Trim());

    /// <summary>
    /// The source the user typed or pasted, without surrounding whitespace or quotes, and with a
    /// leading <c>~</c> read as the home folder. Paths copied from Explorer come in double quotes,
    /// and paths copied from a Linux terminal often in single ones and starting with <c>~</c>.
    /// Everything that decides between a link and a file must look at this same string, or a
    /// quoted link would count as a file in one place and a link in another.
    /// </summary>
    public static string NormaliseInput(string? input) =>
        UserPaths.ExpandHome((input ?? "").Trim().Trim('"', '\'').Trim());

    /// <param name="fetchSubtitles">
    /// When false, the caption round trip is skipped entirely. Worth honouring: it is an extra
    /// request that can be rate limited, and it is pure waste when the user intends to transcribe.
    /// </param>
    public async Task<ResolvedSource> ResolveAsync(
        string input, string workDir, int maxHeight, CancellationToken ct, bool fetchSubtitles = true)
    {
        input = NormaliseInput(input);
        return LooksLikeUrl(input)
            ? await DownloadAsync(input, workDir, maxHeight, fetchSubtitles, ct).ConfigureAwait(false)
            : await UseLocalFileAsync(input, ct).ConfigureAwait(false);
    }

    private async Task<ResolvedSource> UseLocalFileAsync(string path, CancellationToken ct)
    {
        // Kept as a full path: the later steps run ffmpeg in the project's work folder.
        if (path.Length > 0) path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not find '{path}'.", path);

        var probe = new FFprobe(tools.Ffprobe, sink);
        var info = await probe.ProbeAsync(path, ct).ConfigureAwait(false);
        WarnIfLowRes(info);

        // The filename is the only topic hint a local file gives us, so clean it up for the model.
        var title = TitleFromFileName(Path.GetFileNameWithoutExtension(path));
        var sidecar = FindSidecarSubtitle(path);
        if (sidecar is not null)
            sink.Info("source", $"Found sidecar subtitles: {Path.GetFileName(sidecar)}");

        sink.Info("source", $"{title} — {info.Describe()}");
        return new ResolvedSource(path, title, info, sidecar, null);
    }

    private async Task<ResolvedSource> DownloadAsync(
        string url, string workDir, int maxHeight, bool fetchSubtitles, CancellationToken ct)
    {
        if (!tools.HasYtDlp)
            throw new InvalidOperationException("yt-dlp is required for links. Install it on the Setup tab.");

        Directory.CreateDirectory(workDir);
        var (title, language) = await FetchMetadataAsync(url, ct).ConfigureAwait(false);
        sink.Info("source", $"Downloading “{title}”…");

        var outputTemplate = Path.Combine(workDir, "source.%(ext)s");

        // Prefer the best video at or below maxHeight but never below 1080p if a taller
        // format exists; the trailing fallbacks keep low-res-only videos usable.
        var format =
            $"bv*[height<={maxHeight}][height>=1080]+ba/" +
            $"b[height<={maxHeight}][height>=1080]/" +
            $"bv*[height>=1080]+ba/" +
            "bv*+ba/b";

        var args = new List<string>(BaseArguments())
        {
            // One progress line per update instead of an in-place bar, so it can be parsed.
            "--newline",
            "-f", format,
            "-S", $"res:{maxHeight},fps,vcodec:h264,acodec:aac",
            "--merge-output-format", "mp4",
            "--no-overwrites",
            "-o", outputTemplate,
        };

        args.Add(url);

        await ProcessRunner.RunCheckedAsync(tools.YtDlp, args, "yt-dlp", sink,
            onStdOutLine: line => ReportDownloadProgress(line),
            onStdErrLine: line =>
            {
                if (line.Contains("No supported JavaScript runtime", StringComparison.OrdinalIgnoreCase))
                    sink.Warn("source",
                        "yt-dlp has no JavaScript runtime, so some formats may be missing. " +
                        "Install the JS runtime on the Setup tab for reliable 4K downloads.");
                else
                    sink.Debug("yt-dlp", line);
            },
            ct: ct).ConfigureAwait(false);

        var downloaded = Directory.EnumerateFiles(workDir, "source.*")
            .Where(IsMediaOutput)
            .OrderByDescending(f => new FileInfo(f).Length)
            .ToList();

        var video = downloaded.FirstOrDefault()
            ?? throw new InvalidOperationException("yt-dlp finished but produced no video file.");

        // A successful merge leaves exactly one media file. More than one means the merge was
        // skipped, and the largest is video-only — silently producing a reel with no sound.
        // yt-dlp exits 0 in that case, so this is the only place it can be caught.
        if (downloaded.Count > 1)
        {
            var names = string.Join(", ", downloaded.Select(Path.GetFileName));
            throw new InvalidOperationException(
                "yt-dlp downloaded the video and audio separately and could not merge them " +
                $"({names}). This means it could not run ffmpeg. Install ffmpeg on the Setup tab.");
        }

        // Subtitles are fetched separately and best-effort. Downloading them in the same pass
        // means a transient failure on one caption track — a 429, say — aborts the whole run
        // and throws away a gigabyte-scale video that downloaded perfectly well.
        var subtitle = fetchSubtitles
            ? await TryFetchSubtitlesAsync(url, workDir, language, ct).ConfigureAwait(false)
            : null;

        var probe = new FFprobe(tools.Ffprobe, sink);
        var info = await probe.ProbeAsync(video, ct).ConfigureAwait(false);
        WarnIfLowRes(info);
        sink.Info("source", $"{title} — {info.Describe()}");
        if (subtitle is not null)
            sink.Info("source", $"Downloaded subtitles: {Path.GetFileName(subtitle)}");

        return new ResolvedSource(video, title, info, subtitle, url);
    }

    /// <summary>
    /// Turns yt-dlp's per-line download output into progress. A 4K master can be well over a
    /// gigabyte, so without this the app looks frozen for minutes.
    /// </summary>
    private void ReportDownloadProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var match = ProgressPattern().Match(line);
        if (match.Success)
        {
            var percent = double.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture);
            // The remainder already reads "of ~1.34GiB at 5.00MiB/s ETA 04:12".
            var detail = match.Groups["rest"].Value.Trim();
            sink.Report("source",
                detail.Length > 0 ? $"Downloading — {percent:0.#}% {detail}" : $"Downloading — {percent:0.#}%",
                Math.Clamp(percent / 100.0, 0, 1));
            return;
        }

        // Merging and fixups run after the download and can take a while on a large file.
        if (PostProcessPattern().IsMatch(line))
        {
            sink.Report("source", "Merging video and audio…", null);
            sink.Info("source", line.Trim());
            return;
        }

        sink.Debug("yt-dlp", line);
    }

    /// <summary>
    /// Arguments every yt-dlp call needs.
    ///
    /// --encoding utf-8 is not optional: when its output is redirected, yt-dlp writes text in the
    /// legacy Windows ANSI codepage, so a title like "Czy to już świt" arrives as mojibake. That
    /// title is the topic handed to the analyser and the name of the rendered file, and neither
    /// PYTHONIOENCODING nor PYTHONUTF8 overrides it.
    /// </summary>
    private IEnumerable<string> BaseArguments()
    {
        yield return "--no-playlist";
        yield return "--encoding";
        yield return "utf-8";

        // yt-dlp locates ffmpeg only on PATH, and this app deliberately keeps its ffmpeg in a
        // private folder that is never added to PATH. Without this, a "bv*+ba" download cannot
        // be merged: yt-dlp warns on stderr, still exits 0, and leaves separate video-only and
        // audio-only files — which would be picked up as a silent source.
        if (tools.FfmpegPath is { Length: > 0 } ffmpeg && File.Exists(ffmpeg))
        {
            yield return "--ffmpeg-location";
            yield return ffmpeg;
        }

        // YouTube extraction without a JavaScript runtime is deprecated and silently loses
        // formats — including, on some videos, every 4K one.
        if (tools.JsRuntimePath is { Length: > 0 } deno && File.Exists(deno))
        {
            yield return "--js-runtimes";
            yield return $"deno:{deno}";
        }
    }

    /// <summary>
    /// Reads the title and spoken language in one request. The language matters: YouTube offers
    /// machine-translated caption tracks alongside the real ones, and burning an English
    /// translation over Polish speech — with the translation's word timings — would be worse
    /// than having no subtitles at all.
    /// </summary>
    private async Task<(string Title, string? Language)> FetchMetadataAsync(string url, CancellationToken ct)
    {
        try
        {
            var args = new List<string>(BaseArguments())
            {
                "--skip-download", "--print", "%(title)s\n%(language)s",
            };
            args.Add(url);

            var result = await ProcessRunner.RunAsync(tools.YtDlp, args, ct: ct).ConfigureAwait(false);
            var lines = result.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var title = lines.Count > 0 ? lines[0] : "Untitled video";
            // yt-dlp prints "NA" when a field is unknown.
            var language = lines.Count > 1 && !lines[1].Equals("NA", StringComparison.OrdinalIgnoreCase)
                ? lines[1]
                : null;

            if (language is not null) sink.Info("source", $"Spoken language: {language}");
            return (title, language);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sink.Warn("source", $"Could not read the video details: {ex.Message}");
        }
        return ("Untitled video", null);
    }

    /// <summary>
    /// Fetches caption tracks in the video's own language, never a translation. Any failure here
    /// is reported and swallowed — the video is already downloaded, and Whisper can transcribe it.
    /// </summary>
    private async Task<string?> TryFetchSubtitlesAsync(
        string url, string workDir, string? language, CancellationToken ct)
    {
        // "pl.*" matches pl, pl-orig and pl-PL, but not "en-pl", which is English *translated
        // from* Polish. Falling back to English only makes sense when the language is unknown.
        var wanted = language is { Length: > 0 } lang ? $"{lang}.*" : "en.*";

        var args = new List<string>(BaseArguments())
        {
            "--skip-download",
            "--write-subs",
            "--write-auto-subs",
            "--sub-langs", $"{wanted},-live_chat",
            "--sub-format", "vtt/srt/best",
            "--no-overwrites",
            "-o", Path.Combine(workDir, "source.%(ext)s"),
        };
        args.Add(url);

        try
        {
            sink.Report("source", "Fetching subtitles…", null);
            await ProcessRunner.RunCheckedAsync(tools.YtDlp, args, "yt-dlp (subtitles)", sink,
                onStdErrLine: line => sink.Debug("yt-dlp", line), ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sink.Warn("source",
                $"Could not download subtitles ({FirstLine(ex.Message)}). Whisper will transcribe instead.");
        }

        return SelectSubtitle(workDir, language);
    }

    /// <summary>
    /// Picks the best caption file: the original-language track first, then the plain
    /// language track, and never a track for a different language.
    /// </summary>
    private static string? SelectSubtitle(string workDir, string? language)
    {
        var candidates = Directory.EnumerateFiles(workDir, "source.*").Where(IsSubtitleFile).ToList();
        if (candidates.Count == 0) return null;

        int Rank(string path)
        {
            var tag = SubtitleLanguageTag(path);
            if (language is not { Length: > 0 }) return tag is null ? 3 : 2;

            if (tag is null) return 3;
            if (tag.Equals($"{language}-orig", StringComparison.OrdinalIgnoreCase)) return 0;
            if (tag.Equals(language, StringComparison.OrdinalIgnoreCase)) return 1;
            // "pl-PL" is still Polish; "en-pl" is a translation and must lose to both.
            if (tag.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)) return 2;
            return 4;
        }

        return candidates.OrderBy(Rank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>Test hook for <see cref="SelectSubtitle"/>, which is otherwise private.</summary>
    public static string? SelectSubtitleForTests(string workDir, string? language) =>
        SelectSubtitle(workDir, language);

    /// <summary>Extracts "pl-orig" from "source.pl-orig.vtt".</summary>
    private static string? SubtitleLanguageTag(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path); // "source.pl-orig"
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : null;
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? text;

    private void WarnIfLowRes(MediaInfo info)
    {
        if (info.Is4K) return;
        if (info.IsAtLeast1080p)
        {
            sink.Warn("source",
                $"Source is {info.QualityLabel}. A 1080×1920 crop from this will be upscaled — 4K gives a sharper reel.");
        }
        else
        {
            sink.Warn("source",
                $"Source is only {info.QualityLabel}, below the 1080p minimum. The reel will look soft.");
        }
    }

    private static bool IsSubtitleFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".vtt" or ".srt" or ".ass" or ".ssa" or ".json3";
    }

    /// <summary>
    /// A finished media file, as opposed to a caption track or one of yt-dlp's working files.
    /// Partial downloads must be excluded or an interrupted run would look like a failed merge.
    /// </summary>
    private static bool IsMediaOutput(string path)
    {
        if (IsSubtitleFile(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is not (".part" or ".ytdl" or ".temp" or ".tmp" or ".json" or ".description");
    }

    /// <summary>Looks for "video.srt" / "video.en.vtt" next to a local file.</summary>
    private static string? FindSidecarSubtitle(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(videoPath);

        return Directory.EnumerateFiles(dir, stem + ".*")
            .Where(IsSubtitleFile)
            .OrderBy(f => Path.GetExtension(f) == ".srt" ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>
    /// Recovers a readable topic from a filename: separators become spaces and the
    /// release-tag noise that would mislead the topic analysis is stripped.
    /// </summary>
    public static string TitleFromFileName(string stem)
    {
        var text = stem.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
        text = Regex.Replace(text, @"\b(1080p|2160p|720p|4k|uhd|hdr|x264|x265|h264|h265|hevc|aac|webrip|web dl|bluray|bdrip|dvdrip|mp4|mkv)\b",
            " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", " ");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();

        if (text.Length == 0) return stem;
        // Title-case all-lowercase names; leave mixed-case names as the user wrote them.
        return text == text.ToLowerInvariant()
            ? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text)
            : text;
    }
}
