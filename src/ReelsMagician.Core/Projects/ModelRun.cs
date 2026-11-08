using System.Globalization;
using System.Text.Json;
using ReelsMagician.Core.Localization;
using System.Text.Json.Serialization;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.Projects;

/// <summary>
/// A model that was running: which one, for which stage, what it was estimated to need and
/// what the computer had. Written before the model starts and removed when its stage ends, so
/// one still there at the next start means the app, or the whole system, went down meanwhile.
/// </summary>
public sealed record ModelRun(
    ModelStage Stage, string ModelId, string Model, long NeedBytes, MemoryKind Kind,
    long? AvailableBytes, string? Device, int? Context, DateTime StartedUtc)
{
    public static ModelRun Of(ModelStage stage, string modelId, FitAssessment fit) =>
        new(stage, modelId, fit.Model, fit.NeedBytes, fit.Budget.Kind, fit.Budget.Bytes, fit.Budget.Device,
            fit.Context, DateTime.UtcNow);

    /// <summary>Whether the estimate was more than the memory there was.</summary>
    public bool WasTooBig => AvailableBytes is { } total &&
                             NeedBytes > new MemoryBudget(Kind, total).Usable;

    public string Title => Lang.Ui[Stage == ModelStage.Transcription
        ? nameof(Strings.Crash_Title_Transcription)
        : nameof(Strings.Crash_Title_Analysis)];

    /// <summary>What was running when the app went down, and what to do about it.</summary>
    public string Explanation => ExplanationIn(Lang.Ui);

    /// <summary>The same in a language of its own; the date is written the way that language writes it.</summary>
    public string ExplanationIn(Lang lang)
    {
        var local = StartedUtc.ToLocalTime();
        var when = local.ToString(lang[nameof(Strings.Crash_DateFormat)], lang.Formats);
        var happened = lang.Format(Stage == ModelStage.Transcription
            ? nameof(Strings.Crash_Happened_Transcription)
            : nameof(Strings.Crash_Happened_Analysis), Model, when);

        var need = MemoryFit.Format(NeedBytes, lang);
        var graphics = Kind == MemoryKind.Graphics;
        var needs = lang.Format((graphics, Context is null) switch
        {
            (true, true) => nameof(Strings.Crash_Needs_Graphics),
            (true, false) => nameof(Strings.Crash_Needs_Graphics_Context),
            (false, true) => nameof(Strings.Crash_Needs_System),
            _ => nameof(Strings.Crash_Needs_System_Context),
        }, need);
        var has = AvailableBytes is not { } total ? ""
            : Device is { Length: > 0 } d && graphics
                ? lang.Format(nameof(Strings.Crash_Has_Device), MemoryFit.Format(total, lang), d)
                : lang.Format(nameof(Strings.Crash_Has), MemoryFit.Format(total, lang));

        return $"{happened} {needs}{has}.\n\n" +
               lang[WasTooBig ? nameof(Strings.Crash_TooBig) : nameof(Strings.Crash_MayHaveBeen)];
    }
}

public enum ModelStage { Transcription, Analysis }

/// <summary>The file that records a running model (<see cref="ModelRun"/>).</summary>
public static class ModelRunMarker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Records that a model is about to start. Never fails the stage it belongs to.</summary>
    public static void Begin(string file, ModelRun run)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(run, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing to explain later */ }
    }

    /// <summary>Records that the model's stage has ended, however it ended.</summary>
    public static void End(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* explained once at the next start */ }
    }

    /// <summary>
    /// The model that was running when the app last went down, if any, and removes the record so
    /// it is explained only once. An unreadable record is removed and ignored.
    /// </summary>
    public static ModelRun? TakeLeftover(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<ModelRun>(File.ReadAllText(file), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            End(file);
        }
    }
}
