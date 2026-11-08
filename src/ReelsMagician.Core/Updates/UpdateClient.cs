using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ReelsMagician.Core.Localization;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.Updates;

/// <summary>
/// An update step refused for the app's own safety: a download that doesn't match its digest or
/// size, an asset without a digest, or an address that isn't GitHub's. The message is the
/// interface's; <see cref="LogMessage"/> is the log's English.
/// </summary>
public sealed class UpdateRefusedException(string message, string logMessage) : InvalidOperationException(message)
{
    public string LogMessage { get; } = logMessage;

    internal static UpdateRefusedException Of(string key, params object?[] args) =>
        new(Lang.Ui.Format(key, args), Lang.Log.Format(key, args));
}

/// <summary>The <c>sha256:&lt;hex&gt;</c> digests GitHub gives release assets.</summary>
public static class AssetDigest
{
    /// <summary>The 32 bytes of a sha256 digest; false for none, another algorithm or a malformed one.</summary>
    public static bool TryParse(string? digest, out byte[] sha256)
    {
        sha256 = [];
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var hex = digest[prefix.Length..].Trim();
        if (hex.Length != 64 || !hex.All(char.IsAsciiHexDigit)) return false;
        sha256 = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>The digest GitHub would give this file.</summary>
    public static string Of(string path)
    {
        using var file = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(file));
    }
}

/// <summary>
/// The app's only connection for updates: HTTPS to GitHub's API and its release download hosts,
/// nothing else, sending <c>User-Agent: ReelsMagician/&lt;version&gt;</c> and the type it accepts
/// and no other header. Redirects are followed by hand so that each one is held to those hosts
/// too; GitHub sends a download from github.com on to release-assets.githubusercontent.com.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    public const string Owner = "ideaconnect";
    public const string Repository = "reels-magician";

    /// <summary>The ten newest releases: drafts aren't listed without a token, pre-releases are.</summary>
    public const string ReleasesUrl = $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=10";

    /// <summary>GitHub's API and the hosts its release downloads are served from.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com",
    };

    private const int MaxRedirects = 5;
    private readonly HttpClient _http;

    /// <param name="version">This build's version, 0.2.0, as the User-Agent names it.</param>
    /// <param name="handler">A stand-in for the network in the logic checks.</param>
    public UpdateClient(string version, HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = true,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        UserAgent = "ReelsMagician/" + version;
    }

    /// <summary><c>ReelsMagician/0.2.0</c>: no system, no machine, nothing else.</summary>
    public string UserAgent { get; }

    /// <summary>HTTPS on its own port to one of <see cref="AllowedHosts"/>, with no user name or password in it.</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && AllowedHosts.Contains(uri.IdnHost);

    /// <summary>
    /// GETs <paramref name="url"/>, following redirects within the allowed hosts, and returns the
    /// final response with only its headers read. Throws <see cref="UpdateRefusedException"/> for
    /// an address outside them, the first or any later one.
    /// </summary>
    public async Task<HttpResponseMessage> GetAsync(string url, string accept, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw Refused(url);
        for (var hop = 0; ; hop++)
        {
            if (!IsAllowed(uri)) throw Refused(uri.ToString());

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= MaxRedirects) throw UpdateRefusedException.Of(nameof(Strings.Update_TooManyRedirects), uri.IdnHost);
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static UpdateRefusedException Refused(string address) =>
        UpdateRefusedException.Of(nameof(Strings.Update_HostRefused), Shorten(address));

    // A signed download link runs to hundreds of characters; its host and path say enough.
    private static string Shorten(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : address;

    /// <summary>
    /// The releases, newest first as GitHub lists them. Throws <see cref="HttpRequestException"/>
    /// with the status when GitHub refuses (403 once the 60 requests an hour are used up), and
    /// on any failure of the network, which the automatic check keeps to itself.
    /// </summary>
    public async Task<List<GhRelease>> GetReleasesAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await GetAsync(ReleasesUrl, "application/vnd.github+json", timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return GitHubReleases.Parse(json);
    }

    /// <summary>
    /// Downloads a release asset to <paramref name="destination"/> and holds it to the size and
    /// sha256 digest GitHub gives for it. A file that doesn't match is deleted before the
    /// exception, so nothing half-checked is ever left where it could be used. An asset without
    /// a digest is refused before anything is downloaded.
    /// </summary>
    /// <param name="progress">Bytes so far and the total, as the download goes.</param>
    public async Task DownloadAsync(GhAsset asset, string destination, Action<long, long>? progress, CancellationToken ct)
    {
        if (!AssetDigest.TryParse(asset.Digest, out var expected))
            throw UpdateRefusedException.Of(nameof(Strings.Update_NoDigest), asset.Name);

        try
        {
            using var response = await GetAsync(asset.Url, "application/octet-stream", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long done = 0;
                var lastReport = DateTime.MinValue;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    done += read;
                    // More than GitHub said: stop now rather than fill the disk.
                    if (asset.Size > 0 && done > asset.Size) break;
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                    {
                        lastReport = DateTime.UtcNow;
                        progress(done, asset.Size);
                    }
                }

                if (asset.Size > 0 && done != asset.Size)
                    throw new UpdateRefusedException(Lang.Ui.Format(nameof(Strings.Update_SizeMismatch), asset.Name),
                        string.Create(CultureInfo.InvariantCulture, $"{asset.Name}: got {done} bytes, GitHub gives {asset.Size}; deleted"));
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                    throw UpdateRefusedException.Of(nameof(Strings.Update_DigestMismatch), asset.Name);
                progress?.Invoke(done, asset.Size);
            }
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* left for the next attempt, which overwrites it */ }
    }

    public void Dispose() => _http.Dispose();
}
