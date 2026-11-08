using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.App;

/// <summary>
/// Fades the top and bottom edges of a scrolling area wherever there is more to scroll to, instead
/// of cutting the content off with a hard edge. Set <c>ScrollShadow.IsEnabled="True"</c> on a
/// ScrollViewer, or on an ItemsControl such as a ListBox, whose own ScrollViewer is found once its
/// template is applied.
/// </summary>
/// <remarks>
/// <para>
/// The strips (<see cref="ScrollShadowOverlay"/>) go into the ScrollViewer's own template, right
/// after its content presenter and over the same cell, so Fluent's template is not copied. They
/// are drawn over the viewport only, across the content's width: the scroll bars come later in
/// the template, so they stay on top, and focus rings, which Fluent draws in the window's adorner
/// layer, stay on top too. The strips take no clicks. A strip shows while there is more on its
/// side (<see cref="ScrollEdges"/>), so an area that fits shows none.
/// </para>
/// <para>
/// Each strip fades from the colour of the surface the content sits on to transparent: the
/// window's background unless <c>ScrollShadow.Surface</c> names another, such as a list's or a
/// card's. A black shadow would not show on the true-black dark theme; a fade to the background
/// shows in both, and the light theme adds a slight dark tint (ScrollShadowTint in Theme.axaml).
/// </para>
/// <para>
/// A control brought into view, such as one tabbed to, is scrolled clear of the strips rather than
/// to the very edge, where a strip would fade it out.
/// </para>
/// </remarks>
public sealed class ScrollShadow
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollShadow, Control, bool>("IsEnabled");

    /// <summary>
    /// The surface the area's content sits on, which the strips fade from: unset means the
    /// window's background. Use a theme brush through DynamicResource, so it follows the theme.
    /// </summary>
    public static readonly AttachedProperty<IBrush?> SurfaceProperty =
        AvaloniaProperty.RegisterAttached<ScrollShadow, Control, IBrush?>("Surface");

    private static readonly AttachedProperty<Hookup?> HookupProperty =
        AvaloniaProperty.RegisterAttached<ScrollShadow, Control, Hookup?>("Hookup");

    static ScrollShadow()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((host, e) =>
        {
            host.GetValue(HookupProperty)?.Dispose();
            host.SetValue(HookupProperty, e.NewValue is true ? new Hookup(host) : null);
        });
    }

    private ScrollShadow()
    {
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    public static IBrush? GetSurface(Control control) => control.GetValue(SurfaceProperty);

    public static void SetSurface(Control control, IBrush? value) => control.SetValue(SurfaceProperty, value);

    /// <summary>What one host's shadow is attached to: its ScrollViewer, the strips and the content.</summary>
    private sealed class Hookup : IDisposable
    {
        private readonly Control _host;
        private ScrollViewer? _viewer;
        private ScrollContentPresenter? _presenter;
        private ScrollShadowOverlay? _overlay;
        private Control? _content;

        public Hookup(Control host)
        {
            _host = host;
            if (host is ScrollViewer viewer)
            {
                Watch(viewer);
            }
            else if (host is TemplatedControl templated)
            {
                templated.TemplateApplied += OnHostTemplateApplied;
                if (OwnPart<ScrollViewer>(templated) is { } inner) Watch(inner);
            }
        }

        public void Dispose()
        {
            if (_host is TemplatedControl templated && _host is not ScrollViewer)
                templated.TemplateApplied -= OnHostTemplateApplied;
            Watch(null);
        }

        // An ItemsControl's template holds the ScrollViewer: Fluent's ListBox names it
        // PART_ScrollViewer. One without a ScrollViewer doesn't scroll, so it gets no shadow.
        private void OnHostTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
            Watch(e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") ?? OwnPart<ScrollViewer>((TemplatedControl)_host));

        private void Watch(ScrollViewer? viewer)
        {
            if (ReferenceEquals(viewer, _viewer)) return;
            if (_viewer is not null)
            {
                _viewer.TemplateApplied -= OnViewerTemplateApplied;
                _viewer.ScrollChanged -= OnScrollChanged;
            }
            Place(null);
            _viewer = viewer;
            if (viewer is null) return;
            viewer.TemplateApplied += OnViewerTemplateApplied;
            viewer.ScrollChanged += OnScrollChanged;
            Place(OwnPart<ScrollContentPresenter>(viewer));
        }

        private void OnViewerTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
            Place(e.NameScope.Find<ScrollContentPresenter>("PART_ContentPresenter"));

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => Update();

        // The strips go into the panel that holds the content presenter, just after it, so the
        // scroll bars after them stay on top. A template without such a panel gets no shadow.
        private void Place(ScrollContentPresenter? presenter)
        {
            if (_presenter is not null) _presenter.PropertyChanged -= OnPresenterPropertyChanged;
            if (_overlay?.GetVisualParent() is Panel old) old.Children.Remove(_overlay);
            WatchContent(null);
            _presenter = null;
            _overlay = null;

            if (presenter?.GetVisualParent() is not Panel panel) return;
            _presenter = presenter;
            _overlay = new ScrollShadowOverlay { CornerRadius = InnerCornerRadius() };
            if (_host.IsSet(SurfaceProperty))
                _overlay.Bind(ScrollShadowOverlay.SurfaceProperty, _host.GetObservable(SurfaceProperty));
            FollowCell();
            panel.Children.Insert(panel.Children.IndexOf(presenter) + 1, _overlay);
            presenter.PropertyChanged += OnPresenterPropertyChanged;
            WatchContent(presenter.Child);
            Update();
        }

        private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ContentPresenter.ChildProperty)
                WatchContent(_presenter?.Child);
            else if (e.Property == Grid.RowProperty || e.Property == Grid.ColumnProperty
                     || e.Property == Grid.RowSpanProperty || e.Property == Grid.ColumnSpanProperty
                     || e.Property == ContentPresenter.PaddingProperty)
                FollowCell();
        }

        // Over the same grid cell as the presenter: Fluent spans it over the scroll bars' row and
        // column while they auto-hide. The strips span the content, not the padding beside it,
        // which on the pages is the scroll bar's gutter; the padding above and below scrolls
        // with the content, so the strips stay at the edges.
        private void FollowCell()
        {
            if (_presenter is null || _overlay is null) return;
            Grid.SetRow(_overlay, Grid.GetRow(_presenter));
            Grid.SetColumn(_overlay, Grid.GetColumn(_presenter));
            Grid.SetRowSpan(_overlay, Grid.GetRowSpan(_presenter));
            Grid.SetColumnSpan(_overlay, Grid.GetColumnSpan(_presenter));
            _overlay.Margin = new Thickness(_presenter.Padding.Left, 0, _presenter.Padding.Right, 0);
        }

        // Inside a list's rounded border or background, the strips round off the same way. A
        // list that draws neither, such as the proposal cards, has no corners to keep inside.
        private CornerRadius InnerCornerRadius()
        {
            if (_host is not TemplatedControl templated) return default;
            var (r, b) = (templated.CornerRadius, templated.BorderThickness);
            var clear = templated.Background is null or ISolidColorBrush { Color.A: 0 } or { Opacity: 0 };
            if (b == default && clear) return default;
            return new CornerRadius(
                Math.Max(0, r.TopLeft - Math.Max(b.Left, b.Top)), Math.Max(0, r.TopRight - Math.Max(b.Right, b.Top)),
                Math.Max(0, r.BottomRight - Math.Max(b.Right, b.Bottom)), Math.Max(0, r.BottomLeft - Math.Max(b.Left, b.Bottom)));
        }

        // A request to bring something into view bubbles up from it to the content presenter,
        // which scrolls just far enough. Caught on the content, one step before, it asks for a
        // strip's height more on each side, so the control ends up clear of the strips.
        private void WatchContent(Control? content)
        {
            if (ReferenceEquals(content, _content)) return;
            _content?.RemoveHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView);
            _content = content;
            _content?.AddHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView);
        }

        private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
        {
            var clear = _overlay?.StripHeight ?? 0;
            if (clear > 0) e.TargetRect = e.TargetRect.Inflate(new Thickness(0, clear));
        }

        private void Update()
        {
            if (_viewer is null || _overlay is null) return;
            _overlay.Show(ScrollEdges.Of(_viewer.Offset.Y, _viewer.Viewport.Height, _viewer.Extent.Height));
        }

        // A part of the control's own template, not of a control nested inside it.
        private static T? OwnPart<T>(TemplatedControl owner) where T : Control =>
            owner.GetVisualDescendants().OfType<T>().FirstOrDefault(c => ReferenceEquals(c.TemplatedParent, owner));
    }
}

/// <summary>
/// The two strips of a <see cref="ScrollShadow"/>, laid over a viewport's top and bottom edges.
/// The pseudo-class <c>:scroll-up</c> is set while there is more above and <c>:scroll-down</c>
/// while there is more below; Theme.axaml shows each strip on its pseudo-class, with a short fade.
/// </summary>
public sealed class ScrollShadowOverlay : Panel
{
    /// <summary>The surface the strips fade from; Theme.axaml sets the window's background.</summary>
    public static readonly StyledProperty<IBrush?> SurfaceProperty =
        AvaloniaProperty.Register<ScrollShadowOverlay, IBrush?>(nameof(Surface));

    /// <summary>A colour laid over the surface at the strip's edge, at the brush's opacity.</summary>
    public static readonly StyledProperty<IBrush?> TintProperty =
        AvaloniaProperty.Register<ScrollShadowOverlay, IBrush?>(nameof(Tint));

    public static readonly StyledProperty<double> StripHeightProperty =
        AvaloniaProperty.Register<ScrollShadowOverlay, double>(nameof(StripHeight), 20);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<ScrollShadowOverlay, CornerRadius>(nameof(CornerRadius));

    private readonly Border _top = new() { VerticalAlignment = VerticalAlignment.Top, Classes = { "top" } };
    private readonly Border _bottom = new() { VerticalAlignment = VerticalAlignment.Bottom, Classes = { "bottom" } };

    public ScrollShadowOverlay()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(_top);
        Children.Add(_bottom);
        Repaint();
    }

    public IBrush? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    public IBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public double StripHeight
    {
        get => GetValue(StripHeightProperty);
        set => SetValue(StripHeightProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    internal void Show(ScrollEdges edges)
    {
        PseudoClasses.Set(":scroll-up", edges.MoreAbove);
        PseudoClasses.Set(":scroll-down", edges.MoreBelow);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SurfaceProperty || change.Property == TintProperty
            || change.Property == StripHeightProperty || change.Property == CornerRadiusProperty)
            Repaint();
    }

    private void Repaint()
    {
        var edge = EdgeColour(Surface, Tint);
        var r = CornerRadius;
        _top.Height = _bottom.Height = StripHeight;
        _top.CornerRadius = new CornerRadius(r.TopLeft, r.TopRight, 0, 0);
        _bottom.CornerRadius = new CornerRadius(0, 0, r.BottomRight, r.BottomLeft);
        _top.Background = Fade(edge, fromTop: true);
        _bottom.Background = Fade(edge, fromTop: false);
    }

    // The surface's colour, with the tint laid over it, fully opaque at the edge.
    private static Color EdgeColour(IBrush? surface, IBrush? tint)
    {
        if (surface is not ISolidColorBrush { Color: var s }) return Colors.Transparent;
        if (tint is not ISolidColorBrush { Color: var t } brush) return Color.FromRgb(s.R, s.G, s.B);
        var a = Math.Clamp(brush.Opacity * t.A / 255d, 0, 1);
        byte Mix(byte under, byte over) => (byte)Math.Round(under + (over - under) * a);
        return Color.FromRgb(Mix(s.R, t.R), Mix(s.G, t.G), Mix(s.B, t.B));
    }

    // From the colour at the viewport's edge to the same colour, transparent, a strip's height in.
    private static LinearGradientBrush Fade(Color edge, bool fromTop) => new()
    {
        StartPoint = new RelativePoint(0, fromTop ? 0 : 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, fromTop ? 1 : 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(edge, 0),
            new GradientStop(Color.FromArgb(0, edge.R, edge.G, edge.B), 1),
        },
    };
}
