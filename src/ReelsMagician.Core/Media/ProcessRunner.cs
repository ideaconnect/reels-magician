using System.Diagnostics;
using System.Text;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Media;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string CombinedTail(int maxChars = 4000)
    {
        var combined = (StdErr + "\n" + StdOut).Trim();
        return combined.Length <= maxChars ? combined : combined[^maxChars..];
    }
}

/// <summary>
/// Runs external tools (ffmpeg, yt-dlp, whisper-cli, llama-server) with cancellation and
/// line-level streaming so long-running jobs can report progress.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string exePath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null,
        bool captureOutput = true,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (environment is not null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            if (captureOutput) stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            if (captureOutput) stderr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException(Lang.Ui.Format(nameof(Strings.Process_NotStarted), exePath));

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close();

        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });

        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(outDone.Task, errDone.Task).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs a tool and throws a descriptive exception when it fails.</summary>
    public static async Task<ProcessResult> RunCheckedAsync(
        string exePath,
        IEnumerable<string> arguments,
        string toolLabel,
        IProgressSink? sink = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        Action<string>? onStdErrLine = null,
        Action<string>? onStdOutLine = null,
        CancellationToken ct = default)
    {
        var argList = arguments.ToList();
        sink?.Debug(toolLabel, $"{Path.GetFileName(exePath)} {string.Join(' ', argList.Select(Quote))}");

        var result = await RunAsync(exePath, argList, workingDirectory, environment,
            onStdOutLine: onStdOutLine, onStdErrLine: onStdErrLine, ct: ct).ConfigureAwait(false);

        if (!result.Success)
            throw new ToolFailedException(toolLabel, result.ExitCode, result.CombinedTail());
        return result;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

/// <summary>A tool that exited with an error: what the user reads first, then the tool's own last lines.</summary>
public sealed class ToolFailedException(string tool, int exitCode, string output)
    : Exception(Lang.Ui.Format(nameof(Strings.Tool_Failed), tool, exitCode) + "\n" + output)
{
    public string Tool { get; } = tool;
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
}
