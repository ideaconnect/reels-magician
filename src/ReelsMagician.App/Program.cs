using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Dialogs;
using Velopack;

namespace ReelsMagician.App;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // First, before anything else starts: the Windows installer (Velopack) runs the app with
        // --veloapp-install, --veloapp-uninstall and the like, and this answers and exits at once.
        // A normal start returns: from the zip straight away, installed after removing old update
        // packages. Linux is left out: the AppImage is not a Velopack package, so this would only
        // open a log file in /tmp on every start.
        if (OperatingSystem.IsWindows()) VelopackApp.Build().Run();

        // Avalonia's Wayland backend has no fallback: without a compositor the window would
        // simply never appear. Say why instead.
        if (OperatingSystem.IsLinux() && !HasWaylandSession())
        {
            Console.Error.WriteLine(
                "ReelsMagician needs a Wayland session and could not find one (WAYLAND_DISPLAY is not set " +
                "or its socket is missing). Log in to a Wayland desktop, such as Ubuntu's default GNOME " +
                "session, and start it again. It does not run under X11.");
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
        builder = OperatingSystem.IsWindows() ? builder.UseWin32()
            : OperatingSystem.IsLinux() ? builder.UseWayland()
            : throw new PlatformNotSupportedException("ReelsMagician runs on Windows and Linux.");

        // Avalonia 12 leaves text shaping to a separate package; without it no window can open.
        // The app draws its own file pickers instead of the system's, on both systems.
        return builder.UseSkia().UseHarfBuzz().UseManagedSystemDialogs().LogToTrace();
    }

    private static bool HasWaylandSession()
    {
        var display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (string.IsNullOrEmpty(display)) return false;
        if (Path.IsPathRooted(display)) return File.Exists(display);

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtime) && File.Exists(Path.Combine(runtime, display));
    }
}
