using System.Globalization;
using System.Text.Json;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Media;

public sealed record MediaInfo(
    string Path,
    double DurationSeconds,
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio,
    string VideoCodec,
    string AudioCodec,
    int Rotation,
    int SarNum = 1,
    int SarDen = 1)
{
    /// <summary>
    /// True when a stored pixel is square. Anamorphic sources (DV, DVD, 1440×1080 HDV) store a
    /// squeezed picture that the player widens on display; every crop decision has to be made in
    /// the widened geometry or the reel inherits the squeeze.
    /// </summary>
    public bool HasSquarePixels => SarNum == SarDen || SarNum <= 0 || SarDen <= 0;

    /// <summary>
    /// The frame as the viewer sees it, in square pixels: the stored width widened by the sample
    /// aspect ratio, then the container rotation applied. The crop planner and the render filter
    /// chain both work in this space.
    /// </summary>
    public (int W, int H) DisplaySize
    {
        get
        {
            var width = HasSquarePixels ? Width : (int)Math.Round(Width * (double)SarNum / SarDen);
            return Rotation is 90 or 270 or -90 or -270 ? (Height, width) : (width, Height);
        }
    }

    public int DisplayWidth => DisplaySize.W;
    public int DisplayHeight => DisplaySize.H;

    public bool IsAtLeast1080p => Math.Min(DisplayWidth, DisplayHeight) >= 1080;
    public bool Is4K => Math.Min(DisplayWidth, DisplayHeight) >= 2160 || Math.Max(DisplayWidth, DisplayHeight) >= 3840;

    public string QualityLabel => Math.Min(DisplayWidth, DisplayHeight) switch
    {
        >= 2160 => "4K+",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        var s => $"{s}p",
    };

    /// <summary>The source's facts as the Source tab shows them, in the interface's language.</summary>
    public string Describe() => Describe(Lang.Ui);

    /// <summary>The same in a language of its own, such as the log's.</summary>
    public string Describe(Lang lang) =>
        string.Create(lang.Formats, $"{DisplayWidth}×{DisplayHeight} ({QualityLabel}) · {FrameRate:0.##} fps · ") +
        $"{TimeSpan.FromSeconds(DurationSeconds):hh\\:mm\\:ss} · {VideoCodec}" +
        (HasAudio ? $" + {AudioCodec}" : lang[nameof(Strings.Media_NoAudioTrack)]) +
        (HasSquarePixels ? "" : lang.Format(nameof(Strings.Media_Anamorphic), SarNum, SarDen));
}

/// <summary>Thin ffprobe wrapper returning the stream facts the pipeline depends on.</summary>
public sealed class FFprobe(string ffprobePath, IProgressSink sink)
{
    public async Task<MediaInfo> ProbeAsync(string mediaPath, CancellationToken ct)
    {
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException(Lang.Ui[nameof(Strings.Media_FileNotFound)], mediaPath);

        var result = await ProcessRunner.RunCheckedAsync(ffprobePath,
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            mediaPath,
        ], "ffprobe", sink, ct: ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;

        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && video is null && !IsAttachedPicture(s)) video = s;
                else if (type == "audio" && audio is null) audio = s;
            }
        }
        if (video is null)
            throw new InvalidOperationException(Lang.Ui.Format(nameof(Strings.Media_NoVideoStream), Path.GetFileName(mediaPath)));

        var v = video.Value;
        var width = GetInt(v, "width");
        var height = GetInt(v, "height");
        var fps = ParseRational(GetString(v, "avg_frame_rate") ?? GetString(v, "r_frame_rate"));
        if (fps is <= 0 or > 1000) fps = ParseRational(GetString(v, "r_frame_rate"));
        if (fps is <= 0 or > 1000) fps = 30;

        double duration = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d) &&
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            duration = parsed;
        if (duration <= 0 && v.TryGetProperty("duration", out var vd) &&
            double.TryParse(vd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var vdp))
            duration = vdp;

        var (sarNum, sarDen) = ParseAspect(GetString(v, "sample_aspect_ratio"));

        return new MediaInfo(
            mediaPath,
            duration,
            width,
            height,
            fps,
            audio is not null,
            GetString(v, "codec_name") ?? "unknown",
            audio is null ? "" : GetString(audio.Value, "codec_name") ?? "unknown",
            ReadRotation(v),
            sarNum,
            sarDen);
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disp) &&
        disp.TryGetProperty("attached_pic", out var pic) &&
        pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1;

    private static int ReadRotation(JsonElement videoStream)
    {
        // Rotation lives either in stream tags or in a display-matrix side-data entry.
        if (videoStream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("rotate", out var r) &&
            int.TryParse(r.GetString(), out var tagRotation))
            return Normalize(tagRotation);

        if (videoStream.TryGetProperty("side_data_list", out var sides))
        {
            foreach (var side in sides.EnumerateArray())
            {
                if (!side.TryGetProperty("rotation", out var rot)) continue;
                if (rot.ValueKind == JsonValueKind.Number) return Normalize((int)Math.Round(rot.GetDouble()));
                if (int.TryParse(rot.GetString(), out var sr)) return Normalize(sr);
            }
        }
        return 0;
    }

    private static int Normalize(int degrees)
    {
        var d = degrees % 360;
        if (d < 0) d += 360;
        return d;
    }

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() : null;

    /// <summary>
    /// Parses ffprobe's "num:den" sample aspect ratio. "N/A" and "0:1" mean unknown, which ffmpeg
    /// itself plays back as square pixels, so they map to 1:1 here too.
    /// </summary>
    private static (int Num, int Den) ParseAspect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (1, 1);
        var parts = value.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den) &&
            num > 0 && den > 0)
            return (num, den);
        return (1, 1);
    }

    /// <summary>Parses ffprobe rationals such as "30000/1001".</summary>
    private static double ParseRational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
            return num / den;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var flat) ? flat : 0;
    }
}
