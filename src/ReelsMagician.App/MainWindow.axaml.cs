using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReelsMagician.App.Dialogs;
using ReelsMagician.App.ViewModels;
using ReelsMagician.Core.About;

namespace ReelsMagician.App;

public partial class MainWindow : Window, IAppShell
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel(this);
        DataContext = vm;

        ErrorReporter.Presenter = (title, message) =>
            Dispatcher.UIThread.InvokeAsync(() => MessageDialog.ShowAsync(this, title, message));

        // A model left running when the app last went down is explained once the window is up,
        // and a normal close says no model took it down. Then, in the background and at most once
        // a day, GitHub is asked for a newer version; nothing is said when it can't be reached.
        Opened += async (_, _) =>
        {
            await vm.ExplainLastCrashAsync();
            await vm.Updates.CheckOnStartAsync();
        };
        Closed += (_, _) => vm.OnClosed();

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

    public Task TellAsync(string title, string message) => MessageDialog.ShowAsync(this, title, message);

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

    public Task ShowLicenceAsync(string title, IReadOnlyList<LicenceFile> files) =>
        LicenceDialog.ShowAsync(this, title, files);

    public Task<bool> OpenLinkAsync(Uri link) => Launcher.LaunchUriAsync(link);

    public void Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Close();
    }

    private static FilePickerFileType ToFileType(FileFilter filter) => new(filter.Name) { Patterns = filter.Patterns };

    private async Task<IStorageFolder?> FolderAsync(string? path) =>
        path is not null && Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null;
}
