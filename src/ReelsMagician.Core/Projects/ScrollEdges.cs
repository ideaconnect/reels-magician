namespace ReelsMagician.Core.Projects;

/// <summary>
/// Whether a scrolling area has more to show above or below what is in view. The window's scroll
/// shadow fades the edge on each side that has more, instead of cutting the content off there.
/// </summary>
public readonly record struct ScrollEdges(bool MoreAbove, bool MoreBelow)
{
    /// <summary>
    /// Layout rounding can leave the end of the content a fraction of a pixel out of reach, so
    /// within this much of it the area counts as scrolled to the end.
    /// </summary>
    public const double EndTolerance = 1;

    /// <summary>
    /// The edges for a vertical scroll position: <paramref name="offset"/> is how far the area is
    /// scrolled, <paramref name="viewport"/> how tall the part in view is, and
    /// <paramref name="extent"/> how tall the whole content is. An area that fits, or that has no
    /// size yet, has more on neither side.
    /// </summary>
    public static ScrollEdges Of(double offset, double viewport, double extent) =>
        viewport <= 0
            ? default
            : new ScrollEdges(offset > 0, offset + viewport < extent - EndTolerance);
}
