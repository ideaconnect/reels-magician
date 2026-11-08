using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Data;
using ReelsMagician.App.Localization;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.App;

/// <summary>
/// A note on a page, in a bordered box with a Font Awesome icon before it, so it stands apart
/// from the field labels and data around it: <c>&lt;local:InfoBox Text="…" /&gt;</c>. Its
/// <see cref="Kind"/> picks the icon and colour (Theme.axaml): a blue circle-info for an
/// explanation, an amber triangle-exclamation for a warning, a red circle-xmark for an error.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Text"/> covers most notes. A note that needs a link or bold text can hold controls
/// as its <c>Content</c> instead, drawn in the same place.
/// </para>
/// <para>
/// The icon is decoration. The box is named for screen readers by its kind and then its text,
/// as in "Warning: …" (<see cref="Notes.AccessibleName"/>), so the kind is announced too. A name
/// set in XAML wins over that one.
/// </para>
/// <para>
/// The <c>compact</c> class draws the icon and the text alone, with no box and no padding, for a
/// short note beside a field where a full box would make the card heavy.
/// </para>
/// </remarks>
public sealed class InfoBox : ContentControl
{
    public static readonly StyledProperty<NoteKind> KindProperty =
        AvaloniaProperty.Register<InfoBox, NoteKind>(nameof(Kind));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InfoBox, string?>(nameof(Text));

    public InfoBox() => ShowKind();

    public NoteKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == TextProperty)
            ShowKind();
    }

    /// <summary>
    /// Sets the pseudo-class the theme styles each kind by, and the name a screen reader
    /// announces. The name is set below a local value, so one given in XAML is kept.
    /// </summary>
    private void ShowKind()
    {
        PseudoClasses.Set(":info", Kind == NoteKind.Info);
        PseudoClasses.Set(":warning", Kind == NoteKind.Warning);
        PseudoClasses.Set(":error", Kind == NoteKind.Error);
        SetValue(AutomationProperties.NameProperty, Notes.AccessibleName(Kind, Text), BindingPriority.Style);
    }

    // The kind is named in the interface's language, so a switch renames a note whose text stays.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
        ShowKind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Localizer.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ShowKind();

    protected override AutomationPeer OnCreateAutomationPeer() => new InfoBoxAutomationPeer(this);

    /// <summary>
    /// A note with text is one element, read as its name; the text inside the template is hidden
    /// from screen readers, so it is not read twice. A note holding controls is a group named by
    /// its kind, and the controls inside it are read in turn.
    /// </summary>
    private sealed class InfoBoxAutomationPeer(InfoBox owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            owner.Content is null ? AutomationControlType.Text : AutomationControlType.Group;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
