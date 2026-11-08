using System.Runtime.Versioning;
using ReelsMagician.App.Localization;
using ReelsMagician.Core.About;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace ReelsMagician.App.Updates;

/// <summary>
/// Updates a copy installed with ReelsMagician-win-x64-Setup.exe (issue #56) through Velopack:
/// it reads the release's releases.win.json, downloads the full package and hands it to Velopack's
/// updater, which applies it once the app has exited and starts the new version.
/// </summary>
/// <remarks>
/// Velopack reads only the 10 newest releases and passes over any without releases.win.json, so
/// v0.1.0 and v0.2.0, which have none, are invisible to it: the first release with an installer
/// is installed by hand, and updates start from there. Every request it makes goes through
/// <see cref="GuardedDownloader"/>, so it reaches only GitHub, with the app's own User-Agent, and
/// each file it downloads is held to the digest GitHub gives for it.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class InstallerUpdate
{
    /// <summary>Whether Velopack installed this copy. False for the zip, and on any doubt.</summary>
    public static bool IsInstalled()
    {
        try
        {
            return new UpdateManager(new GithubSource(AppInfo.RepositoryLink, accessToken: null, prerelease: true)).IsInstalled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads <paramref name="offer"/>'s package and starts Velopack's updater, which waits up to
    /// 60 s for the app to exit, applies the update and starts the new version. The caller saves
    /// what it keeps and exits straight after.
    /// </summary>
    /// <param name="releases">The releases the offer came from, whose files are the only ones fetched.</param>
    public static async Task PrepareAsync(UpdateOffer offer, IReadOnlyList<GhRelease> releases, bool includePrereleases,
        UpdateClient client, Action<int> progress, CancellationToken ct)
    {
        var source = new GithubSource(AppInfo.RepositoryLink, accessToken: null, prerelease: includePrereleases,
            new GuardedDownloader(client, releases, ct));
        var manager = new UpdateManager(source);

        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update?.TargetFullRelease is not { } target)
            throw new UpdateRefusedException(Strings.Updates_FeedEmpty, "Velopack's feed has nothing newer");
        var targetVersion = target.Version.ToString();
        if (!SemVersion.TryParse(targetVersion, out var version) || version != offer.Version)
            throw new UpdateRefusedException(
                Localizer.Instance.Format(nameof(Strings.Updates_FeedOtherVersion), targetVersion, offer.Version),
                $"Velopack's feed offers {targetVersion}, not {offer.Version}");

        await manager.DownloadUpdatesAsync(update, progress, ct).ConfigureAwait(false);
        manager.WaitExitThenApplyUpdates(target, silent: false, restart: true);
    }
}

/// <summary>
/// Velopack's HTTP, through <see cref="UpdateClient"/>: HTTPS to GitHub only, the app's own
/// User-Agent, and no file but a release asset this check listed, held to its digest. A file
/// without a digest, or one not in the list, is refused, and the update with it.
/// </summary>
public sealed class GuardedDownloader(UpdateClient client, IReadOnlyList<GhRelease> releases, CancellationToken ct) : IFileDownloader
{
    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelToken);
        await client.DownloadAsync(Listed(url), targetFile,
            (done, total) => progress(total > 0 ? (int)Math.Clamp(done * 100 / total, 0, 100) : 0), linked.Token).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        var asset = Listed(url);
        var file = Path.Combine(Path.GetTempPath(), $"ReelsMagician-{Environment.ProcessId}-{Guid.NewGuid():N}.download");
        try
        {
            using var limit = Limit(timeout);
            await client.DownloadAsync(asset, file, null, limit.Token).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(file, limit.Token).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { /* a temporary file */ }
        }
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        using var limit = Limit(timeout);
        var accept = headers is not null && headers.TryGetValue("Accept", out var a) ? a : "application/json";
        using var response = await client.GetAsync(url, accept, limit.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
    }

    private CancellationTokenSource Limit(double seconds)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300)));
        return limit;
    }

    private GhAsset Listed(string url) =>
        releases.SelectMany(r => r.Assets).FirstOrDefault(a => a.Url == url)
        ?? throw new UpdateRefusedException(Localizer.Instance.Format(nameof(Strings.Updates_NotListed), Path.GetFileName(url)),
            $"{url} is not an asset of a release the check listed");
}
