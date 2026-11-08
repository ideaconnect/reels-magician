using System.Globalization;
using System.Xml.Linq;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The Setup tab's component lists (issue #69). Required tools and Video links share one row
/// template, whose name column used to be a fixed 320 px and cut a model's name just where it
/// says which one is recommended, more so in Polish. A column set back to a fixed width, a size
/// group with no scope above it or a scope round one list only all build and run, and only show
/// as a cut name again or two lists out of line.
/// </summary>
public static class SetupListChecks
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static void Run()
    {
        Check.Section("Setup lists fit the name column to the longest name (issue #69)");

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        var window = XDocument.Load(Path.Combine(root, "src", "ReelsMagician.App", "MainWindow.axaml"));
        var row = window.Descendants().FirstOrDefault(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(X + "Key") == "ComponentRow");
        Check.True(row is not null, "MainWindow.axaml has the ComponentRow template");
        if (row is null) return;

        var grid = row.Descendants().First(e => e.Name.LocalName == "Grid");
        Check.True(grid.Attribute("ColumnDefinitions") is null, "the row's columns are no fixed list of widths any more");
        var columns = grid.Descendants().Where(e => e.Name.LocalName == "ColumnDefinition").ToList();
        Check.Equal(5, columns.Count, "the row has its five columns: status, name, detail, state and size");
        if (columns.Count != 5) return;

        var name = columns[1];
        Check.True((string?)name.Attribute("Width") == "Auto" && (string?)name.Attribute("SharedSizeGroup") == "ComponentName",
            "the name column is as wide as the longest name in its size group");
        var least = Pixels(name, "MinWidth");
        var most = Pixels(name, "MaxWidth");
        Check.Equal(320d, least, "it keeps the old 320 px as its least, so short names stay where they were");
        Check.True(most > least && most <= 480, $"and stops at {most} px, no more than 480, so the Detail column keeps room at the window's smallest size");
        Check.Equal("*", (string?)columns[2].Attribute("Width"), "the Detail column takes the rest of the row");

        // The texts that can still be trimmed show whole in their tooltip.
        var texts = grid.Elements().Where(e => e.Name.LocalName == "TextBlock").ToList();
        foreach (var (column, binding) in new[] { ("1", "{Binding Name}"), ("2", "{Binding Detail}") })
        {
            var text = texts.FirstOrDefault(t => (string?)t.Attribute("Grid.Column") == column);
            Check.True(text is not null && (string?)text.Attribute("Text") == binding
                       && (string?)text.Attribute("TextTrimming") == "CharacterEllipsis"
                       && (string?)text.Attribute("ToolTip.Tip") == binding,
                $"the text {binding} is trimmed with an ellipsis and its tooltip shows it whole");
        }

        // Both lists use the row, under one size scope, so their name columns line up.
        var lists = window.Descendants()
            .Where(e => e.Name.LocalName == "ItemsControl" && (string?)e.Attribute("ItemTemplate") == "{StaticResource ComponentRow}")
            .ToList();
        Check.True(lists.Select(l => (string?)l.Attribute("ItemsSource")).Order().SequenceEqual(["{Binding Components}", "{Binding LinkComponents}"]),
            "Required tools and Video links both use the row");
        var scopes = lists.Select(l => l.Ancestors().FirstOrDefault(a => (string?)a.Attribute("Grid.IsSharedSizeScope") == "True")).ToList();
        Check.True(scopes.Count == 2 && scopes[0] is not null && ReferenceEquals(scopes[0], scopes[1]),
            "both lists sit in one size scope, so the group spans them and stops there");
    }

    private static double Pixels(XElement element, string attribute) =>
        double.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
}
