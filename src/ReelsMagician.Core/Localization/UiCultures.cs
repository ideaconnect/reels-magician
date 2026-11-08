using System.Globalization;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.Core.Localization;

/// <summary>The cultures of an interface language: the one its texts come from, and its numbers'.</summary>
public sealed record UiCulture(CultureInfo Texts, CultureInfo Formats);

/// <summary>
/// The cultures a language setting comes to. The texts come from the resources of the text
/// culture. Sizes, durations and numbers on screen are written the way that language writes them,
/// "22,5 MB" in Polish, by the format culture, which keeps the system's own regional settings
/// when they are for the same language.
/// </summary>
/// <remarks>
/// Everything machine-facing (ffmpeg arguments, ASS and SRT files, settings.json, logs) is written
/// with <see cref="CultureInfo.InvariantCulture"/> and never depends on either culture.
/// </remarks>
public static class UiCultures
{
    /// <summary>The English the interface is written in: its texts are the neutral resources.</summary>
    public static CultureInfo English { get; } = CultureInfo.GetCultureInfo("en-US");

    public static CultureInfo Polish { get; } = CultureInfo.GetCultureInfo("pl-PL");

    private static UiCulture _current = new(English, English);

    /// <summary>
    /// The interface's cultures, which <see cref="Lang.Ui"/> writes in. The app sets them when it
    /// applies the language setting; until then, as in the smoke tests or a tool built on Core,
    /// they are English, whatever the process's own culture is. Held apart from the thread's
    /// cultures, which flow with async code and would keep a stage started before a switch in the
    /// old language.
    /// </summary>
    public static UiCulture Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>
    /// The text and format cultures for a setting, given the system's own cultures as they were when
    /// the app started. <see cref="UiLanguage.System"/> is Polish when the system's interface is,
    /// and English for every other language, since the app speaks only these two.
    /// </summary>
    public static UiCulture Resolve(UiLanguage language, CultureInfo systemTexts, CultureInfo systemFormats)
    {
        var polish = language switch
        {
            UiLanguage.Polish => true,
            UiLanguage.English => false,
            _ => IsPolish(systemTexts),
        };

        var texts = polish ? (IsPolish(systemTexts) ? systemTexts : Polish) : English;
        var formats = IsPolish(systemFormats) == polish && SpeaksAppLanguage(systemFormats)
            ? systemFormats
            : texts;
        return new UiCulture(texts, formats);
    }

    /// <summary>The language a resolved text culture is, for showing which one System picked.</summary>
    public static UiLanguage LanguageOf(CultureInfo texts) => IsPolish(texts) ? UiLanguage.Polish : UiLanguage.English;

    public static bool IsPolish(CultureInfo culture) => culture.TwoLetterISOLanguageName == "pl";

    // English or Polish formats; any other language's are replaced by the text language's.
    private static bool SpeaksAppLanguage(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName is "en" or "pl";
}
