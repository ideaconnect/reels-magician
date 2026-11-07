namespace ReelsMagician.App.ViewModels;

/// <summary>A file type a picker offers, such as ("Video", ["*.mp4", "*.mkv"]).</summary>
public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>
/// What the view models need from the window around them: the app's own dialogs and file
/// pickers, the clipboard, and the system's file manager where one can be opened.
/// </summary>
public interface IAppShell
{
    /// <summary>Asks a question. No (<paramref name="cancelText"/>) is the default answer.</summary>
    Task<bool> AskAsync(string title, string message, string confirmText, string cancelText);

    Task<string?> PickFileAsync(string title, IReadOnlyList<FileFilter> filters);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, string? startFolder, FileFilter filter);

    Task<string?> PickFolderAsync(string title, string? startFolder);

    Task CopyTextAsync(string text);

    /// <summary>Whether this system can show a folder in its file manager.</summary>
    bool CanOpenFolders { get; }

    /// <summary>Opens a folder, or the folder holding a file with the file selected where the system can.</summary>
    void ShowInFolder(string path);
}
