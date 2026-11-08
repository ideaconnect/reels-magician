using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ReelsMagician.App;

/// <summary>Highlights cues that Whisper was unsure about, so review effort lands where it counts.</summary>
public sealed class ConfidenceToBrushConverter : IValueConverter
{
    private static readonly IBrush Low = new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x4A));
    private static readonly IBrush Normal = new ImmutableSolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Low : Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
