using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The app's XAML and resources as the checks read them (issue #55). The window's texts live in
/// src/ReelsMagician.App/Localization/Strings.resx and the XAML names them as <c>{l:Tr Key}</c>,
/// so <see cref="Read"/> puts the English text back where each key stands, escaped the way the
/// XAML held it: a check that looks for a note's words finds them where the window shows them.
/// </summary>
public static class AppXaml
{
    public static readonly Regex TrKey = new(@"\{l:Tr (?:Key=)?([A-Za-z0-9_]+)\}");

    /// <summary>A XAML file under src/ReelsMagician.App, with each {l:Tr Key} as its English text.</summary>
    public static string Read(string root, params string[] path)
    {
        var xaml = File.ReadAllText(Path.Combine([root, "src", "ReelsMagician.App", .. path]));
        var english = Texts(AppResx(root));
        return TrKey.Replace(xaml, m => english.TryGetValue(m.Groups[1].Value, out var text) ? Escape(text) : m.Value);
    }

    public static string AppResx(string root, string culture = "") => Resx(root, "ReelsMagician.App", culture);

    public static string CoreResx(string root, string culture = "") => Resx(root, "ReelsMagician.Core", culture);

    /// <summary>A resource file's texts by key, in the order the file holds them.</summary>
    public static Dictionary<string, string> Texts(string resx) =>
        XDocument.Load(resx).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static string Resx(string root, string project, string culture) =>
        Path.Combine(root, "src", project, "Localization", culture.Length == 0 ? "Strings.resx" : $"Strings.{culture}.resx");

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;").Replace("\n", "&#x0a;");
}
