using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Localization;
using SharpCompress.Compressors.Xz;

namespace ReelsMagician.Core.Toolchain;

/// <summary>
/// Resumable HTTP downloader with progress. Downloads land in a .part file and are only
/// renamed into place once complete, so an interrupted install never leaves a corrupt tool.
/// </summary>
public sealed class AssetDownloader(HttpClient http, IProgressSink sink)
{
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(AssetDownloader).Assembly.GetName().Version?.ToString(2) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"ReelsMagician/{version} (+https://github.com/ideaconnect/reels-magician)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="destination"/>, resuming a partial file when possible.</summary>
    /// <param name="label">What the log calls it, in English.</param>
    /// <param name="shownLabel">What the status bar calls it, in the interface's language; the log's label when null.</param>
    public async Task DownloadAsync(string url, string destination, string label, CancellationToken ct, string? shownLabel = null)
    {
        if (File.Exists(destination))
        {
            sink.Debug("install", $"{label}: already downloaded");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, part, shownLabel ?? label, existing, ct).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < 3)
            {
                sink.Warn("install", $"{label}: {ex.Message} — retry {attempt}/2");
                existing = File.Exists(part) ? new FileInfo(part).Length : 0;
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }

        if (File.Exists(destination)) File.Delete(destination);
        File.Move(part, destination);
        sink.Info("install", $"{label}: downloaded ({Humanize(new FileInfo(destination).Length, Lang.Log)})");
    }

    private async Task DownloadOnceAsync(string url, string part, string label, long resumeFrom, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // The server ignored our Range header — start over from zero.
        if (resumeFrom > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            resumeFrom = 0;
            if (File.Exists(part)) File.Delete(part);
        }
        resp.EnsureSuccessStatusCode();

        var total = (resp.Content.Headers.ContentLength ?? 0) + resumeFrom;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(part, resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
        long done = resumeFrom;
        var lastReport = DateTime.UtcNow;

        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
            {
                lastReport = DateTime.UtcNow;
                var frac = total > 0 ? Math.Clamp((double)done / total, 0, 1) : (double?)null;
                var size = total > 0 ? $"{Humanize(done)} / {Humanize(total)}" : Humanize(done);
                sink.Report("install", Lang.Ui.Format(nameof(Strings.Install_Downloading), label, size), frac);
            }
        }
    }

    /// <summary>Fetches a small text/JSON resource (used for GitHub release metadata).</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        return await http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
    }

    /// <summary>A size as the interface writes it: "22.5 MB", or "22,5 MB" in Polish.</summary>
    public static string Humanize(long bytes) => Humanize(bytes, Lang.Ui);

    /// <summary>The same with the number format of a language of its own, such as the log's.</summary>
    public static string Humanize(long bytes, Lang lang) => bytes switch
    {
        >= 1L << 30 => string.Create(lang.Formats, $"{bytes / (double)(1L << 30):0.##} GB"),
        >= 1L << 20 => string.Create(lang.Formats, $"{bytes / (double)(1L << 20):0.#} MB"),
        >= 1L << 10 => string.Create(lang.Formats, $"{bytes / (double)(1L << 10):0.#} KB"),
        _ => string.Create(lang.Formats, $"{bytes} B"),
    };
}

public static class ArchiveExtractor
{
    /// <summary>
    /// Extracts a .zip, .tar.gz or .tar.xz into <paramref name="targetDir"/>, replacing what was
    /// there. When the archive has a single top-level folder (as ffmpeg and llama.cpp builds do)
    /// its contents are lifted up one level so tool paths stay stable across releases. With
    /// <paramref name="merge"/>, the archive is laid over the existing folder instead, for a
    /// runtime that has to sit next to the binaries that load it.
    /// </summary>
    public static void Extract(string archivePath, string targetDir, IProgressSink sink, string label, bool merge = false)
    {
        var staging = targetDir + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        sink.Report("install", Lang.Ui.Format(nameof(Strings.Install_Extracting), label), null);
        try
        {
            Unpack(archivePath, staging);
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }

        var roots = Directory.GetFileSystemEntries(staging);
        var source = roots.Length == 1 && Directory.Exists(roots[0]) ? roots[0] : staging;

        if (merge && Directory.Exists(targetDir))
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(targetDir, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination, overwrite: true);
            }
        }
        else
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(source, targetDir);
        }

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        sink.Info("install", $"{label}: installed to {targetDir}");
    }

    /// <summary>
    /// Unpacks by file type. Tar archives keep each entry's execute bits and symbolic links,
    /// which the Linux builds depend on: llama.cpp ships its libraries as versioned links.
    /// </summary>
    private static void Unpack(string archivePath, string destination)
    {
        var name = Path.GetFileName(archivePath).ToLowerInvariant();
        if (name.EndsWith(".zip"))
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using Stream tar =
            name.EndsWith(".tar.gz") || name.EndsWith(".tgz") ? new GZipStream(file, CompressionMode.Decompress) :
            name.EndsWith(".tar.xz") ? new XZStream(file) :
            name.EndsWith(".tar") ? file :
            throw new NotSupportedException($"Unsupported archive type: {Path.GetFileName(archivePath)}");
        TarFile.ExtractToDirectory(tar, destination, overwriteFiles: true);
    }
}
