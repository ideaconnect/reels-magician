using System.Globalization;
using System.Resources;
using System.Text;

namespace ReelsMagician.Core.Localization;

/// <summary>
/// Core's texts in one of two languages: the interface's (<see cref="Ui"/>), for whatever the user
/// reads on screen, or the log's English (<see cref="Log"/>), for every line written to the log,
/// which goes into bug reports as it is. A method whose text is shown and logged both takes one.
/// </summary>
/// <remarks>
/// Keys are written <c>nameof(Strings.Key)</c>, so the compiler checks them; a plural's key is the
/// base its <c>_one</c> … <c>_other</c> forms share, held by the logic checks.
/// </remarks>
public readonly struct Lang
{
    private readonly CultureInfo? _culture;

    private Lang(CultureInfo? culture) => _culture = culture;

    /// <summary>The interface's language and number format, as the language setting chose them (<see cref="UiCultures.Current"/>).</summary>
    public static Lang Ui => default;

    /// <summary>The log's: English texts, numbers in the invariant culture.</summary>
    public static Lang Log { get; } = new(CultureInfo.InvariantCulture);

    /// <summary>A language of its own, such as Polish in a check that holds the Polish texts.</summary>
    public static Lang In(CultureInfo culture) => new(culture);

    /// <summary>The culture the texts come from.</summary>
    public CultureInfo Texts => _culture ?? UiCultures.Current.Texts;

    /// <summary>The culture numbers, sizes and times are written in.</summary>
    public CultureInfo Formats => _culture ?? UiCultures.Current.Formats;

    public string this[string key] =>
        Strings.ResourceManager.GetString(key, Texts) ?? throw new MissingManifestResourceException($"No text for '{key}'.");

    public string Format(string key, params object?[] args) => string.Format(Formats, this[key], args);

    /// <summary>A counted phrase in the form the count takes, with <c>{0}</c> the count.</summary>
    public string Plural(string key, double count, params object?[] args) =>
        Localization.Plural.Format(Strings.ResourceManager, key, count, Texts, Formats, args);

    /// <summary>
    /// A name as part of a resource key, its letters and digits with each word capitalised: the
    /// key of a text looked up by an English name, such as <c>Preset_PopYellow</c>.
    /// </summary>
    public static string Slug(string name)
    {
        var key = new StringBuilder();
        var start = true;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c)) { start = true; continue; }
            key.Append(start ? char.ToUpperInvariant(c) : c);
            start = false;
        }
        return key.ToString();
    }
}
