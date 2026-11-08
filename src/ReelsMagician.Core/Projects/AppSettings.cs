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
    public int LlamaContextSize { get; set; } = 16384;

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
