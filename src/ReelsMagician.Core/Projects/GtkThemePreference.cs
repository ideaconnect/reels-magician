namespace ReelsMagician.Core.Projects;

/// <summary>
/// GTK's own light or dark setting, for a Linux desktop whose portal says nothing: sway and other
/// wlroots desktops have no portal that answers, and a setup without any portal is common there.
/// Plain file and environment reads, the places GTK itself takes its theme from:
/// <list type="number">
/// <item><c>GTK_THEME</c>, <c>theme[:variant]</c>. GTK lets it override the settings files, so when
/// it is set it decides alone: dark for <c>Adwaita:dark</c> or a name ending in <c>-dark</c>.</item>
/// <item><c>gtk-4.0/settings.ini</c>, then <c>gtk-3.0/settings.ini</c>, in <c>$XDG_CONFIG_HOME</c>
/// (<c>~/.config</c> when unset): dark for <c>gtk-application-prefer-dark-theme=1</c> or a
/// <c>gtk-theme-name</c> ending in <c>-dark</c>. The first file that sets either key decides.</item>
/// </list>
/// </summary>
public static class GtkThemePreference
{
    private const string Group = "Settings";
    private const string PreferDarkKey = "gtk-application-prefer-dark-theme";
    private const string ThemeNameKey = "gtk-theme-name";

    /// <summary>True for dark, false for light, null when GTK is not set up either way.</summary>
    public static bool? PrefersDark() =>
        PrefersDark(Environment.GetEnvironmentVariable("GTK_THEME"), ConfigFolder());

    /// <summary>The same, from a given <c>GTK_THEME</c> value and configuration folder.</summary>
    public static bool? PrefersDark(string? gtkTheme, string configFolder)
    {
        if (FromGtkTheme(gtkTheme) is { } fromVariable) return fromVariable;

        foreach (var version in (string[])["gtk-4.0", "gtk-3.0"])
        {
            var file = Path.Combine(configFolder, version, "settings.ini");
            string text;
            try
            {
                if (!File.Exists(file)) continue;
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (FromSettingsIni(text) is { } fromFile) return fromFile;
        }
        return null;
    }

    /// <summary>What a <c>GTK_THEME</c> value says, or null when it is unset or empty.</summary>
    public static bool? FromGtkTheme(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(':', 2);
        return (parts.Length == 2 && parts[1].Trim().Equals("dark", StringComparison.OrdinalIgnoreCase))
               || IsDarkThemeName(parts[0]);
    }

    /// <summary>
    /// What a GTK settings.ini says: its [Settings] group's prefer-dark switch or theme name. Null
    /// when the file sets neither, so the next file gets its say.
    /// </summary>
    public static bool? FromSettingsIni(string text)
    {
        string? group = null, preferDark = null, themeName = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                group = line.TrimStart('[').TrimEnd(']').Trim();
                continue;
            }
            var equals = line.IndexOf('=');
            if (group != Group || equals < 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim().Trim('"', '\'').Trim();
            if (key == PreferDarkKey) preferDark = value;
            else if (key == ThemeNameKey) themeName = value;
        }

        if (preferDark is null && themeName is null) return null;
        return IsTrue(preferDark) || (themeName is not null && IsDarkThemeName(themeName));
    }

    private static bool IsTrue(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static bool IsDarkThemeName(string name) =>
        name.Trim().EndsWith("-dark", StringComparison.OrdinalIgnoreCase);

    private static string ConfigFolder()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }
}
