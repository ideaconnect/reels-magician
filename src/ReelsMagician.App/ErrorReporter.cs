using System.Text;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.App;

/// <summary>
/// Single place that surfaces a failure to the user.
///
/// A modal dialog runs a nested loop. If the failure came from layout or binding, which that
/// loop re-runs, showing a dialog re-enters the failing code, which throws again and shows
/// another dialog, until the stack overflows. So the dialog is strictly non-reentrant: the first
/// failure is shown, and anything that arrives while it is on screen is only written to the log.
/// </summary>
public static class ErrorReporter
{
    private static readonly Lock Gate = new();
    private static bool _dialogOpen;
    private static string? _lastSignature;
    private static DateTime _lastShown = DateTime.MinValue;

    /// <summary>Where the details go. Always written, whether or not a dialog appears.</summary>
    public static string LogPath { get; } = Path.Combine(new ToolPaths().Logs, $"reelsmagician-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Shows a failure (title, message) to the user. Set by the main window once it exists.</summary>
    public static Func<string, string, Task>? Presenter { get; set; }

    public static void Report(Exception ex, string title, bool showDialog = true)
    {
        Write(ex, title);
        if (!showDialog || Presenter is not { } present) return;

        lock (Gate)
        {
            if (_dialogOpen) return;

            // The same failure repeating every layout pass should not produce a wall of dialogs.
            var signature = $"{title}|{ex.GetType().FullName}|{ex.Message}";
            if (signature == _lastSignature && DateTime.UtcNow - _lastShown < TimeSpan.FromSeconds(30)) return;

            _lastSignature = signature;
            _lastShown = DateTime.UtcNow;
            _dialogOpen = true;
        }

        _ = ShowAsync(present, title, $"{ex.Message}\n\nDetails were written to:\n{LogPath}");
    }

    private static async Task ShowAsync(Func<string, string, Task> present, string title, string message)
    {
        try { await present(title, message).ConfigureAwait(true); }
        catch (Exception) { /* nothing useful left to do */ }
        finally { lock (Gate) _dialogOpen = false; }
    }

    public static void Write(Exception ex, string context)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var entry = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}")
                .AppendLine(ex.ToString())
                .AppendLine(new string('-', 78))
                .ToString();
            File.AppendAllText(LogPath, entry, Encoding.UTF8);
        }
        catch (Exception) { /* logging must never be the thing that fails */ }
    }
}
