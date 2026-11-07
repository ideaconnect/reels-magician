using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ReelsMagician.Core.Rendering;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// Editable wrapper around a <see cref="SubtitleStyle"/>. Every setter raises
/// <see cref="Changed"/> so the caption preview can re-render as the user tweaks the table.
/// </summary>
public sealed class StyleEditorViewModel : ViewModelBase
{
    private readonly SubtitleStyle _style;

    public StyleEditorViewModel(SubtitleStyle style)
    {
        _style = style;
        AvailableFonts = new ObservableCollection<string>(LoadFonts());
    }

    /// <summary>Raised whenever any styling value changes.</summary>
    public event EventHandler? Changed;

    public SubtitleStyle Style => _style;

    public ObservableCollection<string> AvailableFonts { get; }

    public static IReadOnlyList<CaptionMode> Modes { get; } = [CaptionMode.PlainText, CaptionMode.WordHighlight];

    public static IReadOnlyList<HighlightEmphasis> Emphases { get; } =
        [HighlightEmphasis.Colour, HighlightEmphasis.Grow];

    public static IReadOnlyList<CaptionAnimation> Animations { get; } =
        [CaptionAnimation.None, CaptionAnimation.FadeIn, CaptionAnimation.Pop,
         CaptionAnimation.SlideUp, CaptionAnimation.Bounce];

    public string Name
    {
        get => _style.Name;
        set { if (_style.Name != value) { _style.Name = value; Notify(); } }
    }

    public CaptionMode Mode
    {
        get => _style.Mode;
        set
        {
            if (_style.Mode == value) return;
            _style.Mode = value;
            Notify();
            Raise(nameof(IsHighlightMode));
        }
    }

    public bool IsHighlightMode => _style.Mode == CaptionMode.WordHighlight;

    public CaptionAnimation Animation
    {
        get => _style.Animation;
        set { if (_style.Animation != value) { _style.Animation = value; Notify(); } }
    }

    public string FontName
    {
        get => _style.FontName;
        set { if (_style.FontName != value) { _style.FontName = value; Notify(); } }
    }

    public int FontSize
    {
        get => _style.FontSize;
        set
        {
            var clamped = Math.Clamp(value, 20, 200);
            if (_style.FontSize == clamped) return;
            _style.FontSize = clamped;
            Notify();
        }
    }

    public bool Bold
    {
        get => _style.Bold;
        set { if (_style.Bold != value) { _style.Bold = value; Notify(); } }
    }

    public bool Italic
    {
        get => _style.Italic;
        set { if (_style.Italic != value) { _style.Italic = value; Notify(); } }
    }

    public bool Uppercase
    {
        get => _style.Uppercase;
        set { if (_style.Uppercase != value) { _style.Uppercase = value; Notify(); } }
    }

    public string PrimaryColor
    {
        get => _style.PrimaryColor;
        set => SetColor(value, v => _style.PrimaryColor = v, nameof(PrimaryColor), nameof(PrimaryBrush));
    }

    public string HighlightColor
    {
        get => _style.HighlightColor;
        set => SetColor(value, v => _style.HighlightColor = v, nameof(HighlightColor), nameof(HighlightBrush));
    }

    public string OutlineColor
    {
        get => _style.OutlineColor;
        set => SetColor(value, v => _style.OutlineColor = v, nameof(OutlineColor), nameof(OutlineBrush));
    }

    public string BackgroundColor
    {
        get => _style.BackgroundColor;
        set => SetColor(value, v => _style.BackgroundColor = v, nameof(BackgroundColor), nameof(BackgroundBrush));
    }

    public IBrush PrimaryBrush => ToBrush(_style.PrimaryColor);
    public IBrush HighlightBrush => ToBrush(_style.HighlightColor);
    public IBrush OutlineBrush => ToBrush(_style.OutlineColor);
    public IBrush BackgroundBrush => ToBrush(_style.BackgroundColor);

    public double OutlineWidth
    {
        get => _style.OutlineWidth;
        set
        {
            var clamped = Math.Clamp(value, 0, 40);
            if (Math.Abs(_style.OutlineWidth - clamped) < 0.001) return;
            _style.OutlineWidth = clamped;
            Notify();
        }
    }

    public double ShadowDepth
    {
        get => _style.ShadowDepth;
        set
        {
            var clamped = Math.Clamp(value, 0, 20);
            if (Math.Abs(_style.ShadowDepth - clamped) < 0.001) return;
            _style.ShadowDepth = clamped;
            Notify();
        }
    }

    public bool BackgroundBox
    {
        get => _style.BackgroundBox;
        set { if (_style.BackgroundBox != value) { _style.BackgroundBox = value; Notify(); } }
    }

    public double BackgroundOpacity
    {
        get => _style.BackgroundOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_style.BackgroundOpacity - clamped) < 0.001) return;
            _style.BackgroundOpacity = clamped;
            Notify();
        }
    }

    public double VerticalPositionPercent
    {
        get => _style.VerticalPositionPercent;
        set
        {
            var clamped = Math.Clamp(value, 5, 95);
            if (Math.Abs(_style.VerticalPositionPercent - clamped) < 0.001) return;
            _style.VerticalPositionPercent = clamped;
            Notify();
        }
    }

    public int MaxWordsPerCaption
    {
        get => _style.MaxWordsPerCaption;
        set
        {
            var clamped = Math.Clamp(value, 1, 14);
            if (_style.MaxWordsPerCaption == clamped) return;
            _style.MaxWordsPerCaption = clamped;
            Notify();
        }
    }

    public HighlightEmphasis Emphasis
    {
        get => _style.Emphasis;
        set
        {
            if (_style.Emphasis == value) return;
            _style.Emphasis = value;
            Notify();
            Raise(nameof(GrowsWord));
        }
    }

    /// <summary>The grow slider only means anything when the word actually grows.</summary>
    public bool GrowsWord => _style.Emphasis == HighlightEmphasis.Grow;

    public double MinCaptionSeconds
    {
        get => _style.MinCaptionSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0.3, 4.0);
            if (Math.Abs(_style.MinCaptionSeconds - clamped) < 0.001) return;
            _style.MinCaptionSeconds = clamped;
            Notify();
        }
    }

    public double MaxReadingCharsPerSecond
    {
        get => _style.MaxReadingCharsPerSecond;
        set
        {
            var clamped = Math.Clamp(value, 8, 60);
            if (Math.Abs(_style.MaxReadingCharsPerSecond - clamped) < 0.001) return;
            _style.MaxReadingCharsPerSecond = clamped;
            Notify();
        }
    }

    public double HighlightScale
    {
        get => _style.HighlightScale;
        set
        {
            var clamped = Math.Clamp(value, 1.0, AssBuilder.MaxHighlightScale);
            if (Math.Abs(_style.HighlightScale - clamped) < 0.001) return;
            _style.HighlightScale = clamped;
            Notify();
        }
    }

    public string Summary => _style.Describe();

    /// <summary>Copies a preset's values in without swapping the instance the pipeline holds.</summary>
    public void LoadFrom(SubtitleStyle preset)
    {
        _style.Name = preset.Name;
        _style.Mode = preset.Mode;
        _style.Animation = preset.Animation;
        _style.FontName = preset.FontName;
        _style.FontSize = preset.FontSize;
        _style.Bold = preset.Bold;
        _style.Italic = preset.Italic;
        _style.Uppercase = preset.Uppercase;
        _style.PrimaryColor = preset.PrimaryColor;
        _style.HighlightColor = preset.HighlightColor;
        _style.OutlineColor = preset.OutlineColor;
        _style.ShadowColor = preset.ShadowColor;
        _style.OutlineWidth = preset.OutlineWidth;
        _style.ShadowDepth = preset.ShadowDepth;
        _style.BackgroundBox = preset.BackgroundBox;
        _style.BackgroundColor = preset.BackgroundColor;
        _style.BackgroundOpacity = preset.BackgroundOpacity;
        _style.VerticalPositionPercent = preset.VerticalPositionPercent;
        _style.MaxWordsPerCaption = preset.MaxWordsPerCaption;
        _style.Emphasis = preset.Emphasis;
        _style.HighlightScale = preset.HighlightScale;
        _style.MinCaptionSeconds = preset.MinCaptionSeconds;
        _style.MaxReadingCharsPerSecond = preset.MaxReadingCharsPerSecond;
        _style.LetterSpacing = preset.LetterSpacing;

        foreach (var property in new[]
        {
            nameof(Name), nameof(Mode), nameof(IsHighlightMode), nameof(Animation), nameof(FontName),
            nameof(FontSize), nameof(Bold), nameof(Italic), nameof(Uppercase), nameof(PrimaryColor),
            nameof(HighlightColor), nameof(OutlineColor), nameof(BackgroundColor), nameof(PrimaryBrush),
            nameof(HighlightBrush), nameof(OutlineBrush), nameof(BackgroundBrush), nameof(OutlineWidth),
            nameof(ShadowDepth), nameof(BackgroundBox), nameof(BackgroundOpacity),
            nameof(VerticalPositionPercent), nameof(MaxWordsPerCaption), nameof(HighlightScale),
            nameof(Emphasis), nameof(GrowsWord), nameof(MinCaptionSeconds),
            nameof(MaxReadingCharsPerSecond), nameof(Summary),
        })
            Raise(property);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetColor(string value, Action<string> assign, params string[] properties)
    {
        var normalised = Normalise(value);
        if (normalised is null) { Raise(properties[0]); return; }

        assign(normalised);
        foreach (var p in properties) Raise(p);
        Raise(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Notify()
    {
        Raise(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Accepts "#RGB", "#RRGGBB" or the bare hex, and normalises to "#RRGGBB".</summary>
    public static string? Normalise(string? value)
    {
        var text = (value ?? "").Trim().TrimStart('#');
        if (text.Length == 3) text = string.Concat(text.Select(c => new string(c, 2)));
        if (text.Length != 6 || !text.All(Uri.IsHexDigit)) return null;
        return "#" + text.ToUpperInvariant();
    }

    private static IBrush ToBrush(string hex)
    {
        var (r, g, b) = SubtitleStyle.ParseHex(hex);
        return new ImmutableSolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
    }

    /// <summary>
    /// The fonts installed on this computer, which are the ones libass can draw the captions
    /// with. A preset naming a font that is missing here still works: a similar one is used.
    /// </summary>
    private static List<string> LoadFonts()
    {
        try
        {
            return [.. FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            return ["Arial", "Arial Black", "Segoe UI", "Impact", "Verdana"];
        }
    }
}
