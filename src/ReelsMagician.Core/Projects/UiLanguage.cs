using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelsMagician.Core.Projects;

/// <summary>
/// The interface's language: the system's, when it is one the app speaks, or one of them whatever
/// the system says. Log files, settings.json and the smoke tests stay in English either way.
/// </summary>
public enum UiLanguage
{
    System,
    English,
    Polish,
}

/// <summary>
/// Writes the language as <c>"system"</c>, <c>"en"</c> or <c>"pl"</c>, and reads any value it
/// doesn't know, such as a language a later version adds, as <see cref="UiLanguage.System"/>, so an
/// unknown value never throws away the rest of settings.json.
/// </summary>
public sealed class UiLanguageJsonConverter : JsonConverter<UiLanguage>
{
    public override UiLanguage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return Parse(reader.GetString());
        // A number, an object or an array is skipped whole; any other value is a single token.
        reader.Skip();
        return UiLanguage.System;
    }

    public override void Write(Utf8JsonWriter writer, UiLanguage value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Code(value));

    /// <summary>The value settings.json holds for a language.</summary>
    public static string Code(UiLanguage language) => language switch
    {
        UiLanguage.English => "en",
        UiLanguage.Polish => "pl",
        _ => "system",
    };

    /// <summary>A language from its settings.json value, its code or its name, in any case.</summary>
    public static UiLanguage Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "en" or "english" => UiLanguage.English,
        "pl" or "polish" or "polski" => UiLanguage.Polish,
        _ => UiLanguage.System,
    };
}
