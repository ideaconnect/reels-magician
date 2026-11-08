using System.Globalization;
using System.Text.Json;
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

    public string Title => Stage == ModelStage.Transcription
        ? "ReelsMagician closed while transcribing"
        : "ReelsMagician closed while analysing";

    /// <summary>What was running when the app went down, and what to do about it.</summary>
    public string Explanation
    {
        get
        {
            var local = StartedUtc.ToLocalTime();
            var when = local.ToString("d MMMM 'at' HH:mm", CultureInfo.InvariantCulture);
            var doing = Stage == ModelStage.Transcription ? "transcribing the speech" : "analysing the transcript";
            var kind = Kind == MemoryKind.Graphics ? "graphics memory" : "memory";
            var context = Context is null ? "" : " at that context";
            var has = AvailableBytes is { } total
                ? $", and this computer has {MemoryFit.Format(total)}{(Device is { Length: > 0 } d && Kind == MemoryKind.Graphics ? $" on {d}" : "")}"
                : "";

            return $"The last time, ReelsMagician closed while {Model} was {doing}, on {when}. " +
                   $"It needs about {MemoryFit.Format(NeedBytes)} of {kind}{context}{has}.\n\n" +
                   (WasTooBig
                       ? "The model was probably too big for this computer, which can take the app or the whole system " +
                         "down. Choose a smaller model on the Setup tab."
                       : "If the app or the computer went down then, the model may have been too much for it: " +
                         "a smaller model on the Setup tab needs less memory.");
        }
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
