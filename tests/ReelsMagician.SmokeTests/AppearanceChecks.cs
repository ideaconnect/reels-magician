using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The Appearance setting (issue #49): how it is saved, what System comes to on each system, and
/// how GTK's own setting is read on a Linux desktop whose portal says nothing.
/// </summary>
public static class AppearanceChecks
{
    public static void Run()
    {
        Check.Section("Appearance: the setting, the system's theme and GTK's (issue #49)");
        Setting();
        Resolve();
        GtkTheme();
        SettingsFiles();
    }

    private static void Setting()
    {
        Check.Equal(Appearance.System, new AppSettings().Appearance, "a new install follows the system");

        var root = Directory.CreateTempSubdirectory("rm-appearance-").FullName;
        try
        {
            var paths = new ToolPaths(root);
            var settings = new AppSettings { Appearance = Appearance.Dark, OutputDirectory = "/tmp/reels" };
            settings.Save(paths);
            var json = File.ReadAllText(paths.SettingsFile);
            Check.Contains(json, "\"appearance\": \"dark\"", "settings.json names the theme in words");
            Check.Equal(Appearance.Dark, AppSettings.Load(paths).Appearance, "the theme survives a restart");

            foreach (var (written, expected) in new (string, Appearance)[]
                     {
                         ("\"Light\"", Appearance.Light), ("\"DARK\"", Appearance.Dark), ("\"system\"", Appearance.System),
                         ("2", Appearance.Dark), ("\"dim\"", Appearance.System), ("7", Appearance.System),
                         ("null", Appearance.System), ("{\"variant\": \"dark\"}", Appearance.System),
                     })
            {
                File.WriteAllText(paths.SettingsFile, $$"""{ "appearance": {{written}}, "outputDirectory": "/tmp/kept" }""");
                var loaded = AppSettings.Load(paths);
                Check.True(loaded.Appearance == expected && loaded.OutputDirectory == "/tmp/kept",
                    $"appearance {written} reads as {expected}, and the other settings are kept");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Resolve()
    {
        // A GTK that would say dark, so a rule that asks it when it shouldn't comes out wrong.
        bool? Unasked() => true;
        Check.Equal(Appearance.Light, SystemAppearance.Resolve(Appearance.Light, true, true, Unasked),
            "Light stays light, even when the system is dark");
        Check.Equal(Appearance.Dark, SystemAppearance.Resolve(Appearance.Dark, false, false, Unasked),
            "Dark stays dark, even when the system is light");
        Check.Equal(Appearance.System, SystemAppearance.Resolve(Appearance.System, true, true, Unasked),
            "System follows a system that says dark, live, without reading GTK");
        Check.Equal(Appearance.System, SystemAppearance.Resolve(Appearance.System, false, false, Unasked),
            "on Windows System follows the system, which always answers");
        Check.Equal(Appearance.Dark, SystemAppearance.Resolve(Appearance.System, false, true, () => true),
            "on Linux, with no dark answer from the portal, GTK's dark setting makes it dark");
        Check.Equal(Appearance.System, SystemAppearance.Resolve(Appearance.System, false, true, () => false),
            "on Linux, GTK's light setting leaves it light");
        Check.Equal(Appearance.System, SystemAppearance.Resolve(Appearance.System, false, true, () => null),
            "on Linux, with no answer from the portal or GTK, it falls back to light");
    }

    private static void GtkTheme()
    {
        Check.Equal<bool?>(true, GtkThemePreference.FromGtkTheme("Adwaita:dark"), "GTK_THEME=Adwaita:dark is dark");
        Check.Equal<bool?>(true, GtkThemePreference.FromGtkTheme("Yaru-dark"), "GTK_THEME=Yaru-dark is dark");
        Check.Equal<bool?>(false, GtkThemePreference.FromGtkTheme("Adwaita"), "GTK_THEME=Adwaita is light");
        Check.Equal<bool?>(false, GtkThemePreference.FromGtkTheme("Adwaita:hc"), "another variant is not dark");
        Check.Equal<bool?>(null, GtkThemePreference.FromGtkTheme(""), "an empty GTK_THEME says nothing");
        Check.Equal<bool?>(null, GtkThemePreference.FromGtkTheme(null), "no GTK_THEME says nothing");

        Check.Equal<bool?>(true, GtkThemePreference.FromSettingsIni("[Settings]\ngtk-application-prefer-dark-theme=1\n"),
            "prefer-dark=1 is dark");
        Check.Equal<bool?>(true, GtkThemePreference.FromSettingsIni("[Settings]\r\ngtk-application-prefer-dark-theme = true\r\n"),
            "prefer-dark = true, with spaces and Windows line ends, is dark");
        Check.Equal<bool?>(true, GtkThemePreference.FromSettingsIni("[Settings]\ngtk-theme-name=\"Arc-Dark\"\n"),
            "a quoted theme name ending in -Dark is dark");
        Check.Equal<bool?>(true, GtkThemePreference.FromSettingsIni(
                "[Settings]\ngtk-application-prefer-dark-theme=0\ngtk-theme-name=Adwaita-dark\n"),
            "a dark theme is dark even without the prefer-dark switch");
        Check.Equal<bool?>(false, GtkThemePreference.FromSettingsIni(
                "[Settings]\ngtk-theme-name=Breeze\ngtk-application-prefer-dark-theme=false\n"),
            "KDE's light Breeze is light");
        Check.Equal<bool?>(null, GtkThemePreference.FromSettingsIni("[Settings]\ngtk-font-name=Cantarell 11\n"),
            "a file that only sets a font says nothing");
        Check.Equal<bool?>(null, GtkThemePreference.FromSettingsIni(
                "# gtk-application-prefer-dark-theme=1\n[Other]\ngtk-application-prefer-dark-theme=1\n"),
            "a commented line and a key outside [Settings] say nothing");
    }

    private static void SettingsFiles()
    {
        var config = Directory.CreateTempSubdirectory("rm-gtk-").FullName;
        try
        {
            void Write(string version, string text)
            {
                Directory.CreateDirectory(Path.Combine(config, version));
                File.WriteAllText(Path.Combine(config, version, "settings.ini"), text);
            }

            Check.Equal<bool?>(null, GtkThemePreference.PrefersDark(null, config), "no GTK files and no GTK_THEME say nothing");

            Write("gtk-3.0", "[Settings]\ngtk-application-prefer-dark-theme=1\n");
            Check.Equal<bool?>(true, GtkThemePreference.PrefersDark(null, config), "gtk-3.0/settings.ini is read");

            Write("gtk-4.0", "[Settings]\ngtk-font-name=Cantarell 11\n");
            Check.Equal<bool?>(true, GtkThemePreference.PrefersDark(null, config),
                "a gtk-4.0 file that says nothing leaves it to gtk-3.0");

            Write("gtk-4.0", "[Settings]\ngtk-application-prefer-dark-theme=0\n");
            Check.Equal<bool?>(false, GtkThemePreference.PrefersDark(null, config), "gtk-4.0 comes before gtk-3.0");

            Check.Equal<bool?>(true, GtkThemePreference.PrefersDark("Adwaita:dark", config),
                "GTK_THEME overrides the files, as it does in GTK");
            Check.Equal<bool?>(false, GtkThemePreference.PrefersDark("Adwaita", Path.Combine(config, "missing")),
                "GTK_THEME alone is enough");
        }
        finally
        {
            Directory.Delete(config, true);
        }
    }
}
