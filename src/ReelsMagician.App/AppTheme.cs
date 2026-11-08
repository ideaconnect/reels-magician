using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using ReelsMagician.Core.Projects;

namespace ReelsMagician.App;

/// <summary>
/// Applies the Appearance setting to the whole app at once, without a restart. Every palette brush
/// is a theme resource (Assets/Theme.axaml) used through DynamicResource, so changing the theme
/// variant the application asks for restyles every window, the dialogs and the file picker included,
/// and Windows' title bar follows the window's variant.
/// </summary>
public static class AppTheme
{
    // What Avalonia reports as the accent colour when the system gives none (PlatformColorValues).
    private static readonly Color NoSystemAccent = Color.FromRgb(0, 120, 215);

    private const string FallbackAccentKey = "FallbackAccentColor";

    // Fluent's accent and its shades. The app sets them only while the system has no accent of its
    // own; otherwise Fluent takes the user's accent from the system.
    private static readonly (string Key, double Step)[] AccentShades =
    [
        ("SystemAccentColor", 0),
        ("SystemAccentColorDark1", -28.5), ("SystemAccentColorDark2", -49), ("SystemAccentColorDark3", -74.5),
        ("SystemAccentColorLight1", 39), ("SystemAccentColorLight2", 70), ("SystemAccentColorLight3", 103),
    ];

    private static Appearance _appearance;
    private static IPlatformSettings? _watched;

    /// <summary>Switches to the given appearance, and keeps following the system while it is System.</summary>
    public static void Apply(Appearance appearance)
    {
        if (Application.Current is not { } app) return;
        _appearance = appearance;

        // The system's setting can change while the app runs: a new dark or light preference, or
        // the portal's first answer, which arrives after the window has opened.
        if (app.PlatformSettings is { } platform && !ReferenceEquals(platform, _watched))
        {
            if (_watched is not null) _watched.ColorValuesChanged -= OnSystemColoursChanged;
            platform.ColorValuesChanged += OnSystemColoursChanged;
            _watched = platform;
        }
        Update(app);
    }

    private static void OnSystemColoursChanged(object? sender, PlatformColorValues colours)
    {
        if (Application.Current is { } app) Update(app);
    }

    private static void Update(Application app)
    {
        var colours = app.PlatformSettings?.GetColorValues();
        var theme = SystemAppearance.Resolve(_appearance, colours?.ThemeVariant == PlatformThemeVariant.Dark,
            OperatingSystem.IsLinux(), GtkThemePreference.PrefersDark);
        app.RequestedThemeVariant = theme switch
        {
            Appearance.Light => ThemeVariant.Light,
            Appearance.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        UpdateAccent(app, colours);
    }

    /// <summary>
    /// Windows always has an accent colour. A Linux desktop has one only when its portal provides
    /// it; otherwise Avalonia reports Windows' default blue, and the app uses its own blue instead,
    /// the one of its icon's film strip, with Fluent's shades of it.
    /// </summary>
    private static void UpdateAccent(Application app, PlatformColorValues? colours)
    {
        var useOwn = OperatingSystem.IsLinux() && (colours is null || colours.AccentColor1 == NoSystemAccent);
        if (!useOwn)
        {
            foreach (var (key, _) in AccentShades) app.Resources.Remove(key);
            return;
        }

        if (!app.TryGetResource(FallbackAccentKey, null, out var value) || value is not Color accent) return;
        var hsl = accent.ToHsl();
        foreach (var (key, step) in AccentShades)
        {
            var shade = step == 0
                ? accent
                : new HslColor(hsl.A, hsl.H, hsl.S, Math.Clamp(hsl.L + step / 255d, 0, 1)).ToRgb();
            if (!app.Resources.TryGetValue(key, out var current) || current is not Color c || c != shade)
                app.Resources[key] = shade;
        }
    }
}
