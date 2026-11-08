using System.Globalization;
using System.Resources;

namespace ReelsMagician.Core.Localization;

/// <summary>The plural categories of CLDR that the interface's languages use.</summary>
public enum PluralCategory
{
    One,
    Few,
    Many,
    Other,
}

/// <summary>
/// Picks the form of a counted phrase, such as "1 plik", "2 pliki", "5 plików", by the language's
/// CLDR plural rule. .NET has no public plural rules, and the two languages here need little:
/// English tells one from other, Polish one, few, many and other.
/// </summary>
/// <remarks>
/// A plural lives in the resources as one key per form: <c>Key_one</c>, <c>Key_few</c>,
/// <c>Key_many</c> and <c>Key_other</c>. The English file holds <c>_one</c> and <c>_other</c>, the
/// Polish file all four. A form a language doesn't hold falls back to <c>_other</c>.
/// </remarks>
public static class Plural
{
    /// <summary>The category of a count in a language, by CLDR's rule for it.</summary>
    public static PluralCategory Category(double count, CultureInfo culture)
    {
        // A count with a fraction is "other" in both languages: "1,5 pliku", "1.5 files".
        var n = Math.Abs(count);
        if (n != Math.Floor(n) || n > long.MaxValue) return PluralCategory.Other;
        var i = (long)n;

        if (IsPolish(culture))
        {
            if (i == 1) return PluralCategory.One;
            var lastDigit = i % 10;
            var lastTwo = i % 100;
            if (lastDigit is >= 2 and <= 4 && lastTwo is < 12 or > 14) return PluralCategory.Few;
            return PluralCategory.Many;
        }

        return i == 1 ? PluralCategory.One : PluralCategory.Other;
    }

    /// <summary>The resource key of a plural's form for this count, such as <c>Key_few</c>.</summary>
    public static string Key(string key, PluralCategory category) => key + category switch
    {
        PluralCategory.One => "_one",
        PluralCategory.Few => "_few",
        PluralCategory.Many => "_many",
        _ => "_other",
    };

    /// <summary>
    /// The plural's form for <paramref name="count"/>, with <c>{0}</c> the count and <c>{1}</c>…
    /// the further arguments. The texts come from <paramref name="texts"/> and the numbers are
    /// written in <paramref name="formats"/>; null means the interface's own cultures.
    /// </summary>
    public static string Format(ResourceManager resources, string key, double count, CultureInfo? texts = null,
        CultureInfo? formats = null, params object?[] args)
    {
        var ui = texts ?? CultureInfo.CurrentUICulture;
        var category = Category(count, ui);
        var pattern = resources.GetString(Key(key, category), ui)
                      ?? resources.GetString(Key(key, PluralCategory.Other), ui)
                      ?? throw new MissingManifestResourceException($"No plural forms for '{key}'.");
        object?[] all = [count, .. args];
        return string.Format(formats ?? CultureInfo.CurrentCulture, pattern, all);
    }

    private static bool IsPolish(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "pl";
}
