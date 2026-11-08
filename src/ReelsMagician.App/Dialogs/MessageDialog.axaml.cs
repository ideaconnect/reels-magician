using Avalonia.Controls;
using ReelsMagician.App.Localization;

namespace ReelsMagician.App.Dialogs;

/// <summary>
/// The app's one dialog for questions and messages, drawn by the app itself rather than the
/// system. For a question, the answer that does nothing is the default and has focus, so a
/// stray Enter never counts as agreeing.
/// </summary>
public partial class MessageDialog : Window
{
    private bool _confirmed;

    public MessageDialog() => InitializeComponent();

    private MessageDialog(string title, string message, string confirmText, string? cancelText) : this()
    {
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Click += (_, _) => { _confirmed = true; Close(); };

        if (cancelText is null)
        {
            // A plain message: the single button is the default and closes it.
            CancelButton.IsVisible = false;
            ConfirmButton.IsDefault = true;
            Opened += (_, _) => ConfirmButton.Focus();
        }
        else
        {
            CancelButton.Content = cancelText;
            CancelButton.IsDefault = true;
            CancelButton.Click += (_, _) => Close();
            Opened += (_, _) => CancelButton.Focus();
        }
    }

    /// <summary>Asks a question. True only when the user chose <paramref name="confirmText"/>.</summary>
    public static async Task<bool> AskAsync(Window owner, string title, string message, string confirmText, string cancelText)
    {
        var dialog = new MessageDialog(title, message, confirmText, cancelText);
        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    /// <summary>Shows a message with a single OK button.</summary>
    public static Task ShowAsync(Window owner, string title, string message) =>
        new MessageDialog(title, message, Strings.Common_Ok, null).ShowDialog(owner);
}
