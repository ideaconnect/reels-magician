using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace ReelsMagician.App.Localization;

/// <summary>
/// <c>{l:Tr Key}</c>: the interface's text for a key, kept up to date when the language changes.
/// It binds to <see cref="Localizer"/>'s indexer, whatever the element's data context is.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding($"[{Key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay };
}

/// <summary>
/// <c>{l:Format Path, Key=Format_Seconds}</c>: a value of the data context written through a
/// composite format from the resources, such as <c>{0:0} s</c>, in the interface's number format,
/// so "0.5" reads "0,5" in Polish. Both follow a switch of language at once.
/// </summary>
public sealed class FormatExtension : MarkupExtension
{
    public FormatExtension() { }

    public FormatExtension(string path) => Path = path;

    /// <summary>The path of the value, from the data context.</summary>
    public string Path { get; set; } = "";

    /// <summary>The resource key whose text is the composite format, with <c>{0}</c> the value.</summary>
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Bindings =
        {
            new ReflectionBinding(Path) { Mode = BindingMode.OneWay },
            new ReflectionBinding($"[{Key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay },
            new ReflectionBinding(nameof(Localizer.Formats)) { Source = Localizer.Instance, Mode = BindingMode.OneWay },
        },
        Converter = FormattedValue.Instance,
    };

    private sealed class FormattedValue : IMultiValueConverter
    {
        public static readonly FormattedValue Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [var value and not UnsetValueType, string format, CultureInfo formats]
                ? string.Format(formats, format, value)
                : "";
    }
}

/// <summary>
/// <c>{l:TrValue Name, Prefix=Preset_}</c>: the interface's text for a key made of a prefix and a
/// bound English name, such as <c>Preset_PopYellow</c> for a preset, or the name itself
/// when there is no such key, as for a style of the user's own. Follows a switch of language.
/// </summary>
public sealed class TrValueExtension : MarkupExtension
{
    public TrValueExtension() { }

    public TrValueExtension(string path) => Path = path;

    /// <summary>The binding path of the English name.</summary>
    public string Path { get; set; } = "";

    /// <summary>The start of the keys, such as <c>Preset_</c>.</summary>
    public string Prefix { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Bindings =
        {
            new ReflectionBinding(Path) { Mode = BindingMode.OneWay },
            new ReflectionBinding(nameof(Localizer.Texts)) { Source = Localizer.Instance, Mode = BindingMode.OneWay },
        },
        Converter = new NamedText(Prefix),
    };

    private sealed class NamedText(string prefix) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [string name, ..]
                ? Strings.ResourceManager.GetString(prefix + Core.Localization.Lang.Slug(name), Localizer.Instance.Texts) ?? name
                : "";
    }
}

/// <summary>
/// <c>{l:EnumText}</c>, in an item template: the interface's name for the enum value that is the
/// item, from the key <c>Enum_TypeName_Value</c>, such as <c>Enum_CaptionMode_PlainText</c>.
/// </summary>
public sealed class EnumTextExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Bindings =
        {
            new ReflectionBinding(".") { Mode = BindingMode.OneWay },
            new ReflectionBinding(nameof(Localizer.Texts)) { Source = Localizer.Instance, Mode = BindingMode.OneWay },
        },
        Converter = EnumName.Instance,
    };

    /// <summary>The resource key of an enum value's name.</summary>
    public static string KeyOf(Enum value) => $"Enum_{value.GetType().Name}_{value}";

    private sealed class EnumName : IMultiValueConverter
    {
        public static readonly EnumName Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [Enum value, ..] ? Localizer.Instance[KeyOf(value)] : "";
    }
}
