using System.IO;
using System.Text;
using System.Windows;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.App;

/// <summary>
/// Single place that surfaces a failure to the user, and the only place allowed to open a
/// message box.
///
/// A modal dialog runs a nested message pump. If the failure came from layout or binding — which
/// is re-run by that very pump — showing a dialog re-enters the failing code, which throws again,
/// which shows another dialog. That recursion overflows the stack and kills the process, so the
/// dialog is strictly non-reentrant: the first failure is shown, anything that arrives while it
/// is on screen is only written to the log.
/// </summary>
public static class ErrorReporter
{
    private static readonly object Gate = new();
    private static bool _dialogOpen;
    private static string? _lastSignature;
    private static DateTime _lastShown = DateTime.MinValue;

    /// <summary>Where the details go. Always written, whether or not a dialog appears.</summary>
    public static string LogPath { get; } = Path.Combine(new ToolPaths().Logs, $"reelsmagician-{DateTime.Now:yyyyMMdd}.log");

    public static void Report(Exception ex, string title, bool showDialog = true)
    {
        Write(ex, title);

        if (!showDialog) return;

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

        try
        {
            MessageBox.Show(
                $"{ex.Message}\n\nDetails were written to:\n{LogPath}",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception) { /* nothing useful left to do */ }
        finally
        {
            lock (Gate) _dialogOpen = false;
        }
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
