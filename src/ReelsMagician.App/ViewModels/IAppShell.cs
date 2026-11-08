using ReelsMagician.Core.About;

namespace ReelsMagician.App.ViewModels;

/// <summary>A file type a picker offers, such as ("Video", ["*.mp4", "*.mkv"]).</summary>
public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>
/// What the view models need from the window around them: the app's own dialogs and file
/// pickers, the clipboard, and the system's browser for the support and About links. Nothing the app
/// needs to work depends on the desktop: it hands no folder to a file manager, so it works the
/// same with or without one, and Copy path stands in for it.
/// </summary>
public interface IAppShell
{
    /// <summary>Asks a question. No (<paramref name="cancelText"/>) is the default answer.</summary>
    Task<bool> AskAsync(string title, string message, string confirmText, string cancelText);

    /// <summary>Shows a message with a single OK button.</summary>
    Task TellAsync(string title, string message);

    Task<string?> PickFileAsync(string title, IReadOnlyList<FileFilter> filters);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, string? startFolder, FileFilter filter);

    Task<string?> PickFolderAsync(string title, string? startFolder);

    Task CopyTextAsync(string text);

    /// <summary>
    /// Shows licence texts in the app's own dialog, selectable and scrollable, with a text the
    /// build lacks marked as not found. Nothing is handed to the desktop.
    /// </summary>
    Task ShowLicenceAsync(string title, IReadOnlyList<LicenceFile> files);

    /// <summary>
    /// Opens a web page in the system's browser. False when it could not be started. On Linux it
    /// can also be true when no browser opened, as xdg-open reports nothing back.
    /// </summary>
    Task<bool> OpenLinkAsync(Uri link);
}
