using System.Text.Json;
using System.Text.Json.Serialization;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.Projects;

/// <summary>Persisted user preferences, stored next to the toolchain in %LOCALAPPDATA%\ReelsMagician.</summary>
public sealed class AppSettings
{
    public string WhisperModelId { get; set; } = ModelCatalog.DefaultWhisper.Id;
    public string LlmModelId { get; set; } = ModelCatalog.DefaultLlm.Id;

    /// <summary>Use an ffmpeg already on PATH instead of downloading a private copy.</summary>
    public bool PreferSystemFfmpeg { get; set; } = true;

    /// <summary>Optional overrides pointing at hand-installed builds (e.g. a custom Vulkan whisper.cpp).</summary>
    public string? CustomFfmpegDir { get; set; }
    public string? CustomWhisperDir { get; set; }
    public string? CustomLlamaDir { get; set; }

    /// <summary>-1 offloads every layer to the GPU; 0 forces CPU.</summary>
    public int LlamaGpuLayers { get; set; } = -1;

    /// <summary>
    /// The context llama-server gets. Choosing a model on the Setup tab sets that model's
    /// recommended context, so a new install starts at the default model's, taken from the
    /// catalogue rather than a number of its own that could drift from it (issue #70).
    /// </summary>
    public int LlamaContextSize { get; set; } = ModelCatalog.DefaultLlm.RecommendedContext;

    /// <summary>"auto" lets Whisper detect the language.</summary>
    public string WhisperLanguage { get; set; } = "auto";
    public int WhisperThreads { get; set; } = Math.Max(4, Environment.ProcessorCount - 2);

    /// <summary>Where reels go until the user picks another folder.</summary>
    public static string DefaultOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), ToolPaths.FolderName);

    public string OutputDirectory { get; set; } = DefaultOutputDirectory;

    public List<string> RecentSources { get; set; } = [];

    /// <summary>
    /// The models the user chose to run although they probably won't fit this computer's memory,
    /// so the question before a run is asked once per model (issue #54).
    /// </summary>
    public List<string> AcceptedMemoryRisks { get; set; } = [];

    /// <summary>The theme: the system's light or dark setting by default, or one of the two.</summary>
    [JsonConverter(typeof(AppearanceJsonConverter))]
    public Appearance Appearance { get; set; } = Appearance.System;

    /// <summary>The interface's language: the system's by default, or English or Polish.</summary>
    [JsonConverter(typeof(UiLanguageJsonConverter))]
    public UiLanguage Language { get; set; } = UiLanguage.System;

    /// <summary>Whether the app asks GitHub for a newer version, at most once a day (issue #57).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Whether pre-releases are offered as updates. Unset means <see cref="DefaultIncludesPrereleases"/>,
    /// which settings.json then leaves out, so a later release can change the default for everyone
    /// who never chose.
    /// </summary>
    public bool? IncludePrereleases { get; set; }

    /// <summary>On while every release is a pre-release: without them there would be nothing to offer.</summary>
    public const bool DefaultIncludesPrereleases = true;

    [JsonIgnore] public bool TakesPrereleases => IncludePrereleases ?? DefaultIncludesPrereleases;

    /// <summary>When GitHub last answered a check, so the automatic one runs once a day.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>A version the user chose to skip: it, and anything older, isn't offered again unasked.</summary>
    public string? SkippedVersion { get; set; }

    [JsonIgnore] public string? LoadedFrom { get; private set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(ToolPaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), Json);
                if (loaded is not null)
                {
                    loaded.LoadedFrom = paths.SettingsFile;
                    return loaded;
                }
            }
        }
        catch (Exception) { /* corrupt settings — fall back to defaults */ }
        return new AppSettings { LoadedFrom = paths.SettingsFile };
    }

    public void Save(ToolPaths paths)
    {
        try
        {
            paths.EnsureCreated();
            File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { /* settings are best-effort */ }
    }

    public void RememberSource(string source)
    {
        RecentSources.Remove(source);
        RecentSources.Insert(0, source);
        if (RecentSources.Count > 12) RecentSources.RemoveRange(12, RecentSources.Count - 12);
    }
}
