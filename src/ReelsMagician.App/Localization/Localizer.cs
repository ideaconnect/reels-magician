using System.ComponentModel;
using System.Globalization;
using ReelsMagician.Core.Localization;
using ReelsMagician.Core.Projects;
using CoreStrings = ReelsMagician.Core.Localization.Strings;

namespace ReelsMagician.App.Localization;

/// <summary>
/// The interface's language, switched while the app runs. XAML reads its texts through
/// <c>{l:Tr Key}</c>, a binding to this object's indexer, which follows a switch at once because
/// the indexer raises <c>PropertyChanged("Item[]")</c>. Texts a view model builds in code are
/// rebuilt on <see cref="LanguageChanged"/>.
/// </summary>
/// <remarks>
/// Core's texts follow <see cref="UiCultures.Current"/>. Both generated resource classes, the
/// interface's <see cref="Strings"/> and Core's, are pointed at the chosen culture, as are the
/// process's current and default cultures, so Avalonia's own formatting agrees. Numbers on screen
/// follow <see cref="Formats"/>.
/// </remarks>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>The name a binding to an indexer listens for.</summary>
    public const string IndexerName = "Item[]";

    public static Localizer Instance { get; } = new();

    private Localizer()
    {
        // The system's own cultures, as they are before the first Apply changes them.
        SystemTexts = CultureInfo.CurrentUICulture;
        SystemFormats = CultureInfo.CurrentCulture;
        (Texts, Formats) = UiCultures.Resolve(UiLanguage.System, SystemTexts, SystemFormats);
        UiCultures.Current = new UiCulture(Texts, Formats);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a switch, once every culture has been set.</summary>
    public event EventHandler? LanguageChanged;

    public CultureInfo SystemTexts { get; }
    public CultureInfo SystemFormats { get; }

    public UiLanguage Language { get; private set; } = UiLanguage.System;

    /// <summary>The culture the texts come from.</summary>
    public CultureInfo Texts { get; private set; }

    /// <summary>The culture numbers, sizes and durations on screen are written in.</summary>
    public CultureInfo Formats { get; private set; }

    /// <summary>The interface's text for a key; the key itself, in brackets, when there is none.</summary>
    public string this[string key] => Strings.ResourceManager.GetString(key, Texts) ?? $"[{key}]";

    /// <summary>A text with its placeholders filled in, numbers written the interface's way.</summary>
    public string Format(string key, params object?[] args) => string.Format(Formats, this[key], args);

    /// <summary>A counted phrase in the form the count takes: "1 napis", "3 napisy", "5 napisów".</summary>
    public string Plural(string key, double count, params object?[] args) =>
        Core.Localization.Plural.Format(Strings.ResourceManager, key, count, Texts, Formats, args);

    /// <summary>Switches the interface to a language setting, at once, without a restart.</summary>
    public void Apply(UiLanguage language)
    {
        var (texts, formats) = UiCultures.Resolve(language, SystemTexts, SystemFormats);
        var changed = !Equals(texts, Texts) || !Equals(formats, Formats);

        Language = language;
        Texts = texts;
        Formats = formats;
        UiCultures.Current = new UiCulture(texts, formats);
        Strings.Culture = texts;
        CoreStrings.Culture = texts;
        CultureInfo.CurrentUICulture = texts;
        CultureInfo.CurrentCulture = formats;
        CultureInfo.DefaultThreadCurrentUICulture = texts;
        CultureInfo.DefaultThreadCurrentCulture = formats;

        if (!changed) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Texts)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Formats)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
