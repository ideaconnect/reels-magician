using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReelsMagician.App.Dialogs;
using ReelsMagician.App.ViewModels;

namespace ReelsMagician.App;

public partial class MainWindow : Window, IAppShell
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(this);

        ErrorReporter.Presenter = (title, message) =>
            Dispatcher.UIThread.InvokeAsync(() => MessageDialog.ShowAsync(this, title, message));

        // Clicking into a cue's text box should make that cue the selected one, so Split, Merge
        // and the time nudges act on the row being edited.
        SubtitleList.AddHandler(GotFocusEvent, OnCueFocused, RoutingStrategies.Bubble);
    }

    private void OnCueFocused(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control && control.FindAncestorOfType<ListBoxItem>() is { } item)
            item.IsSelected = true;
    }

    // ── IAppShell ────────────────────────────────────────────────────────────────────────

    public Task<bool> AskAsync(string title, string message, string confirmText, string cancelText) =>
        MessageDialog.AskAsync(this, title, message, confirmText, cancelText);

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [.. filters.Select(ToFileType)],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string? startFolder, FileFilter filter)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = await FolderAsync(startFolder),
            FileTypeChoices = [ToFileType(filter)],
            DefaultExtension = filter.Patterns.FirstOrDefault()?.TrimStart('*', '.'),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await FolderAsync(startFolder),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private static FilePickerFileType ToFileType(FileFilter filter) => new(filter.Name) { Patterns = filter.Patterns };

    private async Task<IStorageFolder?> FolderAsync(string? path) =>
        path is not null && Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null;
}
