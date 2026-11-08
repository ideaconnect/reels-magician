using Avalonia;
using Avalonia.Controls;

namespace ReelsMagician.App;

/// <summary>
/// The step strip's row: the steps from the left, and the tabs marked
/// <c>DockPanel.Dock="Right"</c> (Cleanup, About) together on the right, each group in tab order.
/// A DockPanel would put the first right-docked tab rightmost, so the strip would read Cleanup
/// last while the arrow keys, which follow tab order, end on About.
/// </summary>
public sealed class StepStripPanel : Panel
{
    // The tabs' widths as the last arrange placed them.
    private double[] _arranged = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // A switch of language changes every header at once. The strip is then measured again,
        // but its own slot stays the same, and Avalonia doesn't arrange it again: each tab would
        // keep its old width and cut its new name. So the strip asks for an arrange itself.
        if (!Children.Select(c => c.DesiredSize.Width).SequenceEqual(_arranged))
            InvalidateArrange();

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _arranged = [.. Children.Select(c => c.DesiredSize.Width)];

        var x = 0.0;
        foreach (var child in Children.Where(c => !OnRight(c)))
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }

        var right = Children.Where(OnRight).ToList();
        // When the window is too narrow for both groups, the right one stays after the steps
        // rather than over them.
        x = Math.Max(x, finalSize.Width - right.Sum(c => c.DesiredSize.Width));
        foreach (var child in right)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }

    private static bool OnRight(Control child) => DockPanel.GetDock(child) == Dock.Right;
}
