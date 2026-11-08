using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelsMagician.Core.Projects;

/// <summary>The app's theme: the system's light or dark setting, or one of the two whatever it says.</summary>
public enum Appearance
{
    System,
    Light,
    Dark,
}

/// <summary>What <see cref="Appearance.System"/> comes to on each system.</summary>
public static class SystemAppearance
{
    /// <summary>
    /// The theme to ask the toolkit for: <see cref="Appearance.System"/> to follow the system's own
    /// setting, live, or a fixed <see cref="Appearance.Light"/> or <see cref="Appearance.Dark"/>.
    /// Windows always answers. On Linux the answer comes from the desktop portal, and the toolkit
    /// reports Light both when the portal prefers light and when there is no portal or it has no
    /// preference, so those cases can't be told apart. Whenever the system doesn't say Dark there,
    /// GTK's own setting decides (<see cref="GtkThemePreference"/>), and with no answer from GTK
    /// either the theme is Light, as GNOME's default style and the portal's "no preference" mean.
    /// </summary>
    public static Appearance Resolve(Appearance setting, bool systemIsDark, bool onLinux, Func<bool?> gtkPrefersDark) =>
        setting switch
        {
            Appearance.Light or Appearance.Dark => setting,
            _ when systemIsDark || !onLinux => Appearance.System,
            _ => gtkPrefersDark() == true ? Appearance.Dark : Appearance.System,
        };
}

/// <summary>
/// Writes the appearance as <c>"system"</c>, <c>"light"</c> or <c>"dark"</c>, and reads any value it
/// doesn't know, such as a theme a later version adds, as <see cref="Appearance.System"/>, so an
/// unknown value never throws away the rest of settings.json.
/// </summary>
public sealed class AppearanceJsonConverter : JsonConverter<Appearance>
{
    public override Appearance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Appearance value;
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse(reader.GetString(), ignoreCase: true, out value) && Enum.IsDefined(value))
            return value;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number)
            && Enum.IsDefined((Appearance)number))
            return (Appearance)number;

        // An object or an array is skipped whole; any other value is a single token.
        reader.Skip();
        return Appearance.System;
    }

    public override void Write(Utf8JsonWriter writer, Appearance value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
