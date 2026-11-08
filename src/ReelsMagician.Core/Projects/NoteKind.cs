using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Projects;

/// <summary>
/// What a note on a page is: an explanation, a warning about something the user may want to act
/// on, or an error. The window draws each kind as a bordered box with its own icon and colour
/// (InfoBox), and names it so a screen reader announces the kind before the text.
/// </summary>
public enum NoteKind
{
    Info,
    Warning,
    Error,
}

public static class Notes
{
    /// <summary>The word a screen reader hears before a note's text, since its icon is decoration.</summary>
    public static string Label(NoteKind kind) => Lang.Ui[kind switch
    {
        NoteKind.Warning => nameof(Strings.Note_Warning),
        NoteKind.Error => nameof(Strings.Note_Error),
        _ => nameof(Strings.Note_Info),
    }];

    /// <summary>
    /// A note's accessible name: its kind, then its text, as in "Warning: Source is 720p…". A
    /// note with no text of its own, one whose content is other controls, is named by its kind
    /// alone, and the controls inside it are read after that.
    /// </summary>
    public static string AccessibleName(NoteKind kind, string? text) =>
        string.IsNullOrWhiteSpace(text) ? Label(kind) : Lang.Ui.Format(nameof(Strings.Note_Named), Label(kind), text.Trim());
}
