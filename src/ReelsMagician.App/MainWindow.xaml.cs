using System.Windows;
using System.Windows.Controls;

namespace ReelsMagician.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Commits the cell being edited before a command runs. Without this, a subtitle the user
    /// is still typing into would not reach the transcript when they click Analyse.
    /// </summary>
    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindGrid(source) is null)
            CommitOpenEdits(this);
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private static void CommitOpenEdits(DependencyObject root)
    {
        foreach (var grid in FindGrids(root))
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
            grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        }
    }

    private static DataGrid? FindGrid(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is DataGrid grid) return grid;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static IEnumerable<DataGrid> FindGrids(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is DataGrid grid) yield return grid;
            foreach (var nested in FindGrids(child)) yield return nested;
        }
    }
}
