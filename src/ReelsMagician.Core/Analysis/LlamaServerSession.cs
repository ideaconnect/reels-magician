using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Analysis;

/// <summary>
/// Owns a llama-server child process for the life of an analysis run and exposes its
/// OpenAI-compatible chat endpoint. The server is started on a free loopback port, bound to
/// 127.0.0.1 only, and killed (with its process tree) on dispose.
/// </summary>
public sealed class LlamaServerSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly IProgressSink _sink;
    private readonly List<string> _startupLog = [];

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    private LlamaServerSession(Process process, int port, HttpClient http, IProgressSink sink)
    {
        _process = process;
        Port = port;
        _http = http;
        _sink = sink;
    }

    public static async Task<LlamaServerSession> StartAsync(
        Toolset tools, AppSettings settings, int contextSize, IProgressSink sink, CancellationToken ct)
    {
        if (!tools.HasLlm)
            throw new InvalidOperationException(Lang.Ui[nameof(Strings.Llama_NotInstalled)]);

        var port = FindFreePort();
        var supportsFit = tools.LlamaFeatures?.SupportsFit == true;
        var fitTarget = FitTargetMib(tools);
        var args = ServerArgs(tools, settings, contextSize, port);

        var psi = new ProcessStartInfo
        {
            FileName = tools.LlamaServer,
            WorkingDirectory = Path.GetDirectoryName(tools.LlamaServer),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        sink.Info("analyze", $"Starting llama-server on port {port} ({tools.LlamaBackendInfo.Describe(Lang.Log)})");
        if (settings.LlamaGpuLayers < 0)
            sink.Info("analyze", supportsFit
                ? "llama.cpp fits the model to the graphics memory, keeping on the processor any layers that don't fit."
                : "This llama.cpp can't fit a model to the graphics memory (no --fit), so every layer goes to the GPU.");
        if (settings.LlamaGpuLayers < 0 && supportsFit && fitTarget is { } margin)
            sink.Info("analyze", $"This integrated GPU shares the computer's memory, so the fit leaves {margin} MiB of it free " +
                                 "(--fit-target), more than llama.cpp's default.");
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10), BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var session = new LlamaServerSession(process, port, http, sink);

        process.OutputDataReceived += (_, e) => session.OnServerLine(e.Data);
        process.ErrorDataReceived += (_, e) => session.OnServerLine(e.Data);

        if (!process.Start())
            throw new InvalidOperationException(Lang.Ui[nameof(Strings.Llama_NotStarted)]);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await session.WaitUntilReadyAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    private void OnServerLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_startupLog)
        {
            _startupLog.Add(line);
            if (_startupLog.Count > 60) _startupLog.RemoveAt(0);
        }
        // Surface the device line so the user can confirm the model really landed on the GPU.
        if ((line.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("CUDA", StringComparison.OrdinalIgnoreCase)) &&
            line.Contains("device", StringComparison.OrdinalIgnoreCase))
            _sink.Info("analyze", line.Trim());
        else
            _sink.Debug("llama", line.Trim());
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(6);
        var reported = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                var tail = LogTail();
                if (ModelDidNotFitException.FromLog(tail) is { } didNotFit) throw Reported(didNotFit);
                throw new InvalidOperationException(
                    Lang.Ui.Format(nameof(Strings.Llama_ExitedAtStart), _process.ExitCode) + "\n" + tail);
            }

            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probe.Token);
                using var resp = await _http.GetAsync("/health", linked.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _sink.Info("analyze", "llama-server is ready.");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* not up yet */ }

            if (!reported)
            {
                _sink.Report("analyze", Lang.Ui[nameof(Strings.Llama_Loading)], null);
                reported = true;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(Lang.Ui[nameof(Strings.Llama_Timeout)]);
    }

    /// <summary>Posts a chat completion and returns the assistant message text.</summary>
    public async Task<string> CompleteAsync(object requestBody, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync("/v1/chat/completions", content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The server may have died under the request, such as when a buffer it grows while
            // working could not be allocated. Its exit can trail the dropped connection a little.
            if (ExitedOutOfMemory() is { } didNotFit) throw Reported(didNotFit);
            throw;
        }

        using var _ = resp;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(Lang.Ui.Format(nameof(Strings.Llama_Http), (int)resp.StatusCode, Truncate(body, 600)));

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException(Lang.Ui.Format(nameof(Strings.Llama_NoChoices), Truncate(body, 600)));

        return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    /// <summary>
    /// The command line llama-server starts with: the model, the loopback address and port, the
    /// context, the GPU layers (<see cref="GpuLayerArgs"/>) and the threads.
    /// </summary>
    public static List<string> ServerArgs(Toolset tools, AppSettings settings, int contextSize, int port)
    {
        var args = new List<string>
        {
            "-m", tools.LlmModelPath!,
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "-c", contextSize.ToString(),
        };
        args.AddRange(GpuLayerArgs(settings.LlamaGpuLayers, tools.LlamaFeatures?.SupportsFit == true, FitTargetMib(tools)));
        args.AddRange(
        [
            "-t", Math.Max(2, Environment.ProcessorCount / 2).ToString(),
            // Not --log-disable: that silences fatal startup errors too, leaving the
            // "exited during startup" message below with nothing to report. Routine lines go to
            // the debug sink instead, which the UI already filters out.
            "--log-colors", "off",
        ]);
        return args;
    }

    /// <summary>
    /// How many layers go to the GPU. A number set in settings.json (<c>LlamaGpuLayers</c>) is
    /// passed as it is. Otherwise a build with <c>--fit</c> decides: it offloads the layers that fit
    /// in the graphics memory and keeps the rest on the processor, so a model too big for the card
    /// runs slower instead of failing. <c>-ngl 999</c> would switch that off ("n_gpu_layers already
    /// set by user"), so it goes only to an older build that can't fit, as every layer, as before.
    /// The context stays as set: fitting may shrink only what is left unset, and an unset context
    /// would be the model's whole training context, 40k for Qwen3, wherever that fits.
    /// A <paramref name="fitTargetMib"/> (<see cref="FitTargetMib"/>) goes with <c>--fit</c> only.
    /// </summary>
    public static IReadOnlyList<string> GpuLayerArgs(int configuredLayers, bool supportsFit, int? fitTargetMib = null) =>
        configuredLayers >= 0 ? ["-ngl", configuredLayers.ToString(CultureInfo.InvariantCulture)]
        : !supportsFit ? ["-ngl", "999"]
        : fitTargetMib is { } target ? ["--fit", "on", "--fit-target", target.ToString(CultureInfo.InvariantCulture)]
        : ["--fit", "on"];

    /// <summary>
    /// The memory <c>--fit</c> leaves free on an integrated GPU, in MiB, instead of llama.cpp's
    /// default, 1024 in build 12189 (issue #71). Fit's plan counts the buffers on the device, but
    /// on an APU the host-visible ones, such as the weights of the layers kept on the processor,
    /// come out of the same shared memory. On the Radeon 760M, with build 12189, Qwen3 8B (Q6_K)
    /// at a 96k context was planned with 32 of 37 layers on the GPU and 1,316 MiB left, and its
    /// warm-up then ran out of memory ("Not enough memory for command submission"); with 3072 it
    /// got 28 layers and served. Qwen3 8B (Q4_K_M) at a 128k context, there, did the same: 27
    /// layers and a failed warm-up with the default, 24 layers and served with 3072, where the
    /// weights held on the processor's side took 1,774 MiB of host-visible memory. That is one
    /// machine: another APU may want a different margin.
    /// </summary>
    public const int IntegratedFitTargetMib = 3072;

    /// <summary>
    /// The <c>--fit-target</c> for this machine: <see cref="IntegratedFitTargetMib"/> when llama.cpp
    /// runs on an integrated GPU and the build lists the option, as it does <c>--fit</c>; null, for
    /// llama.cpp's own margin, everywhere else: on a discrete card, a GPU the system can't tell
    /// apart, the processor, or a build without the option.
    /// </summary>
    public static int? FitTargetMib(Toolset tools) =>
        tools.LlamaFeatures is { SupportsFit: true, SupportsFitTarget: true } features
        && features.Devices is not { Count: 0 } // a build that lists no GPU runs on the processor
        && tools.LlamaBuild is not (BuildKind.Cpu or BuildKind.Blas or BuildKind.Cuda)
        && tools.Gpu?.VulkanRunsOnIntegrated == true
            ? IntegratedFitTargetMib
            : null;

    private string LogTail()
    {
        lock (_startupLog) return string.Join('\n', _startupLog);
    }

    private ModelDidNotFitException? ExitedOutOfMemory()
    {
        try { return _process.WaitForExit(TimeSpan.FromSeconds(2)) ? ModelDidNotFitException.FromLog(LogTail()) : null; }
        catch (SystemException) { return null; }
    }

    /// <summary>The lines that showed the shortage go to the activity log, which the message replaces.</summary>
    private ModelDidNotFitException Reported(ModelDidNotFitException didNotFit)
    {
        foreach (var line in didNotFit.Evidence) _sink.Warn("analyze", $"llama-server: {line}");
        return didNotFit;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* the process is already gone */ }
        finally { _process.Dispose(); }
    }
}
