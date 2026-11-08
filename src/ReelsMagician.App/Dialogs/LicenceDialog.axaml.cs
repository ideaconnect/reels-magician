using Avalonia.Controls;
using ReelsMagician.Core.About;

namespace ReelsMagician.App.Dialogs;

/// <summary>
/// Shows licence texts from the app's own folder, in the app rather than in whatever the desktop
/// opens text files with, which a Linux system may lack, and which can't see inside the AppImage.
/// </summary>
public partial class LicenceDialog : Window
{
    public LicenceDialog() => InitializeComponent();

    private LicenceDialog(string title, IReadOnlyList<LicenceFile> files) : this()
    {
        Title = title;
        Files.ItemsSource = files;
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) => CloseButton.Focus();
    }

    public static Task ShowAsync(Window owner, string title, IReadOnlyList<LicenceFile> files) =>
        new LicenceDialog(title, files).ShowDialog(owner);
}
