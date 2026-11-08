using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace ReelsMagician.App;

/// <summary>
/// One Font Awesome icon from Assets/Icons.axaml, drawn in the foreground colour. Every icon there
/// shares Font Awesome's 640×640 viewBox, and the template (Theme.axaml) draws the path unstretched
/// on a canvas that size inside a Viewbox, so each icon keeps the viewBox's padding and all of them
/// come out at one scale. Fluent's PathIcon stretches each path to its own ink instead, which
/// would make a check mark bigger than a circle.
/// </summary>
public sealed class FaIcon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<FaIcon, Geometry?>(nameof(Data));

    /// <summary>
    /// Set on a button, the icon it shows before its text. The text stays the button's content, so
    /// it is still what a screen reader announces, and the icon takes the button's text colour.
    /// </summary>
    public static readonly AttachedProperty<Geometry?> ButtonIconProperty =
        AvaloniaProperty.RegisterAttached<FaIcon, Button, Geometry?>("ButtonIcon");

    static FaIcon()
    {
        ButtonIconProperty.Changed.AddClassHandler<Button>((button, e) =>
            button.ContentTemplate = e.NewValue is Geometry icon ? IconBeforeText(icon) : null);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static Geometry? GetButtonIcon(Button button) => button.GetValue(ButtonIconProperty);

    public static void SetButtonIcon(Button button, Geometry? value) => button.SetValue(ButtonIconProperty, value);

    private static FuncDataTemplate<object> IconBeforeText(Geometry icon) => new((content, _) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 7,
        Children =
        {
            new FaIcon { Data = icon, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = content?.ToString(), VerticalAlignment = VerticalAlignment.Center },
        },
    });

    protected override AutomationPeer OnCreateAutomationPeer() => new FaIconAutomationPeer(this);

    /// <summary>
    /// An icon with no name is decoration next to text that says the same, so screen readers skip
    /// it. One that stands for something on its own, such as a component's status, gets an
    /// AutomationProperties.Name and is announced as an image.
    /// </summary>
    private sealed class FaIconAutomationPeer(FaIcon owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override bool IsContentElementCore() => !string.IsNullOrEmpty(AutomationProperties.GetName(Owner));

        protected override bool IsControlElementCore() => IsContentElementCore();
    }
}
