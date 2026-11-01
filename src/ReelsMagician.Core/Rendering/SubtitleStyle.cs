using System.Globalization;

namespace ReelsMagician.Core.Rendering;

/// <summary>Plain captions, or captions that mark each word as it is spoken.</summary>
public enum CaptionMode
{
    /// <summary>One static line per caption — clean and unobtrusive.</summary>
    PlainText,

    /// <summary>The spoken word is recoloured and scaled up, the way CapCut and Filmora do it.</summary>
    WordHighlight,
}

/// <summary>How a caption enters the frame.</summary>
public enum CaptionAnimation { None, Pop, SlideUp, Bounce, FadeIn }

/// <summary>
/// How the spoken word is emphasised.
///
/// Neither option changes the width of any glyph, and that is the whole point. Scaling a word
/// horizontally changes its advance width, which makes the line wider, which — because the line
/// is centred — shifts every other word sideways. Sweeping the highlight along the line then
/// makes the whole caption jitter left and right on every word.
/// </summary>
public enum HighlightEmphasis
{
    /// <summary>Recolour only. The line is pixel-for-pixel still.</summary>
    Colour,

    /// <summary>Recolour and grow the word vertically. No horizontal reflow.</summary>
    Grow,
}

/// <summary>
/// Everything the caption table exposes. One of these renders to an ASS style plus the
/// override tags used per line, so the same model drives both the preview and the burn-in.
/// </summary>
public sealed class SubtitleStyle
{
    public string Name { get; set; } = "Custom";
    public CaptionMode Mode { get; set; } = CaptionMode.WordHighlight;
    public CaptionAnimation Animation { get; set; } = CaptionAnimation.Pop;

    public string FontName { get; set; } = "Arial Black";
    public int FontSize { get; set; } = 76;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool Uppercase { get; set; } = true;

    public string PrimaryColor { get; set; } = "#FFFFFF";
    /// <summary>Colour of the word currently being spoken (word-highlight mode only).</summary>
    public string HighlightColor { get; set; } = "#FFD93D";
    public string OutlineColor { get; set; } = "#000000";
    public string ShadowColor { get; set; } = "#000000";

    public double OutlineWidth { get; set; } = 4.5;
    public double ShadowDepth { get; set; } = 1.5;

    /// <summary>Draws a filled box behind the text instead of an outline.</summary>
    public bool BackgroundBox { get; set; }
    public string BackgroundColor { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.6;

    /// <summary>Caption baseline as a percentage of frame height, measured from the top.</summary>
    public double VerticalPositionPercent { get; set; } = 76;
    public int MaxWordsPerCaption { get; set; } = 4;

    /// <summary>
    /// Shortest a caption line may stay on screen. A line that flashes past cannot be read even
    /// when it is perfectly in sync with the speech.
    /// </summary>
    public double MinCaptionSeconds { get; set; } = 1.0;

    /// <summary>
    /// Fastest a line may be expected to be read, in characters per second. Broadcast subtitling
    /// aims for about 17; burned-in social captions can run faster because the viewer is hearing
    /// the same words, so this defaults higher.
    /// </summary>
    public double MaxReadingCharsPerSecond { get; set; } = 25;

    /// <summary>Whether the spoken word only changes colour, or also grows.</summary>
    public HighlightEmphasis Emphasis { get; set; } = HighlightEmphasis.Grow;

    /// <summary>
    /// How much the spoken word grows, as a multiplier (1.0 = no growth). Applied to the
    /// vertical axis only, so the line's width — and therefore its position — never changes.
    /// Beyond about 1.3 the letterforms start to look stretched.
    /// </summary>
    public double HighlightScale { get; set; } = 1.16;
    public double LetterSpacing { get; set; }

    public SubtitleStyle Clone() => (SubtitleStyle)MemberwiseClone();

    /// <summary>The presets offered in the caption table.</summary>
    public static List<SubtitleStyle> Presets() =>
    [
        new()
        {
            Name = "Plain white",
            Mode = CaptionMode.PlainText,
            Animation = CaptionAnimation.FadeIn,
            FontName = "Arial",
            FontSize = 64,
            Bold = true,
            Uppercase = false,
            PrimaryColor = "#FFFFFF",
            OutlineColor = "#000000",
            OutlineWidth = 3.5,
            ShadowDepth = 1,
            MaxWordsPerCaption = 7,
            VerticalPositionPercent = 80,
        },
        new()
        {
            Name = "Pop yellow (CapCut style)",
            Mode = CaptionMode.WordHighlight,
            Animation = CaptionAnimation.Pop,
            FontName = "Arial Black",
            FontSize = 78,
            Bold = true,
            Uppercase = true,
            PrimaryColor = "#FFFFFF",
            HighlightColor = "#FFD93D",
            OutlineColor = "#000000",
            OutlineWidth = 5,
            ShadowDepth = 2,
            Emphasis = HighlightEmphasis.Grow,
            HighlightScale = 1.16,
            MaxWordsPerCaption = 4,
            VerticalPositionPercent = 74,
        },
        new()
        {
            Name = "Green karaoke",
            Mode = CaptionMode.WordHighlight,
            Animation = CaptionAnimation.SlideUp,
            FontName = "Arial Black",
            FontSize = 72,
            Bold = true,
            Uppercase = true,
            PrimaryColor = "#F2F2F2",
            HighlightColor = "#3BE86B",
            OutlineColor = "#0B1A0F",
            OutlineWidth = 4.5,
            ShadowDepth = 2,
            Emphasis = HighlightEmphasis.Grow,
            HighlightScale = 1.14,
            MaxWordsPerCaption = 3,
            VerticalPositionPercent = 72,
        },
        new()
        {
            Name = "Boxed caption",
            Mode = CaptionMode.WordHighlight,
            Animation = CaptionAnimation.None,
            FontName = "Segoe UI",
            FontSize = 62,
            Bold = true,
            Uppercase = false,
            PrimaryColor = "#FFFFFF",
            HighlightColor = "#5EC8FF",
            BackgroundBox = true,
            BackgroundColor = "#101015",
            BackgroundOpacity = 0.72,
            OutlineWidth = 14,
            ShadowDepth = 0,
            Emphasis = HighlightEmphasis.Colour,
            HighlightScale = 1.0,
            MaxWordsPerCaption = 5,
            VerticalPositionPercent = 78,
        },
        new()
        {
            Name = "Big bounce",
            Mode = CaptionMode.WordHighlight,
            Animation = CaptionAnimation.Bounce,
            FontName = "Impact",
            FontSize = 92,
            Bold = false,
            Uppercase = true,
            PrimaryColor = "#FFFFFF",
            HighlightColor = "#FF4D6D",
            OutlineColor = "#000000",
            OutlineWidth = 6,
            ShadowDepth = 3,
            Emphasis = HighlightEmphasis.Grow,
            HighlightScale = 1.24,
            MaxWordsPerCaption = 3,
            VerticalPositionPercent = 68,
        },
    ];

    /// <summary>
    /// Converts "#RRGGBB" to the 8-digit "&amp;HAABBGGRR" form used in a [V4+ Styles] line
    /// (alpha 0 = opaque).
    /// </summary>
    public static string ToAssColor(string hex, double opacity = 1.0)
    {
        var (r, g, b) = ParseHex(hex);
        var alpha = (int)Math.Round(255 * (1 - Math.Clamp(opacity, 0, 1)));
        return $"&H{alpha:X2}{b:X2}{g:X2}{r:X2}";
    }

    /// <summary>
    /// Converts "#RRGGBB" to the 6-digit "&amp;HBBGGRR&amp;" form required by inline override
    /// tags such as \c. libass rejects the 8-digit style form there, which silently discards the
    /// whole override block — taking the word highlight and its scaling with it.
    /// </summary>
    public static string ToOverrideColor(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return $"&H{b:X2}{g:X2}{r:X2}&";
    }

    public static (int R, int G, int B) ParseHex(string hex)
    {
        var text = (hex ?? "").Trim().TrimStart('#');
        if (text.Length == 3)
            text = string.Concat(text.Select(c => new string(c, 2)));
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return (255, 255, 255);

        return (
            Convert.ToInt32(text[..2], 16),
            Convert.ToInt32(text.Substring(2, 2), 16),
            Convert.ToInt32(text.Substring(4, 2), 16));
    }

    public string Describe()
    {
        var mode = Mode == CaptionMode.PlainText ? "plain" : "word highlight";
        var animation = Animation == CaptionAnimation.None ? "no animation" : Animation.ToString().ToLowerInvariant();
        var emphasis = Mode == CaptionMode.PlainText
            ? ""
            : Emphasis == HighlightEmphasis.Grow ? $" · grow {HighlightScale:0.00}×" : " · colour only";
        return $"{mode}{emphasis} · {animation} · {FontName} {FontSize}px · {MaxWordsPerCaption} words/line";
    }
}
