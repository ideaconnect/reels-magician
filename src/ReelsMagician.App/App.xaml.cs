using System.Windows;
using System.Windows.Threading;

namespace ReelsMagician.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A background failure should surface as a message, not a silent process exit — but it
        // must go through ErrorReporter, which refuses to open a second dialog while one is up.
        DispatcherUnhandledException += (_, args) =>
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
    }
}
