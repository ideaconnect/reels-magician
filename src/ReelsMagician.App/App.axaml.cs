using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ReelsMagician.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A background failure should surface as a message, not a silent process exit — but it
        // must go through ErrorReporter, which refuses to open a second dialog while one is up.
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            ErrorReporter.Report(args.Exception, "Something went wrong");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is not Exception ex) return;
            // The process is going down regardless; record it rather than opening a dialog
            // nobody will get to read.
            ErrorReporter.Write(ex, "Fatal — the process is terminating");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            ErrorReporter.Write(args.Exception, "Unobserved background task failure");
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UseAppIconOnEveryWindow();
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Every window shows the app's icon: the main window, the message dialog and Avalonia's file
    // picker. On Windows that is the .ico, from which Avalonia's Win32 backend takes the frame
    // closest to each size it needs: 16 px for the title bar and 24 px for the taskbar at 100 %,
    // 32 and 48 px at 200 %. From a PNG, Windows would scale the one 256 px image down instead.
    // A Wayland window can't set its icon at all: Avalonia 12.1.5 doesn't support
    // xdg-toplevel-icon-v1 (AvaloniaUI/Avalonia#21910). There the compositor finds the desktop entry
    // named after the app_id, which is the assembly name, ReelsMagician, and shows its Icon=.
    private void UseAppIconOnEveryWindow()
    {
        var file = OperatingSystem.IsWindows() ? "reelsmagician.ico" : "reelsmagician-256.png";
        using var stream = AssetLoader.Open(new Uri($"avares://ReelsMagician/Assets/{file}"));
        var icon = new WindowIcon(stream);
        Styles.Add(new Style(x => x.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } });
    }
}
