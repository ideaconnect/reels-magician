using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.Updates;

/// <summary>How this copy of the app reached the user, which decides how it can update itself.</summary>
public enum PackageKind
{
    /// <summary>Installed on Windows with ReelsMagician-win-x64-Setup.exe (Velopack, issue #56).</summary>
    WindowsInstaller,

    /// <summary>Unpacked from ReelsMagician-win-x64.zip: the user manages its files.</summary>
    WindowsZip,

    /// <summary>Started from ReelsMagician-x86_64.AppImage, which <c>$APPIMAGE</c> names.</summary>
    AppImage,

    /// <summary>Anything else, such as <c>dotnet run</c> or an extracted AppImage.</summary>
    Other,
}

/// <param name="Kind">What it runs from.</param>
/// <param name="AppImagePath">For an AppImage, the file to replace.</param>
/// <param name="CanReplace">For an AppImage, whether its folder takes a new file beside it.</param>
public sealed record AppPackage(PackageKind Kind, string? AppImagePath = null, bool CanReplace = false)
{
    /// <summary>
    /// Works out what the app runs from. On Windows, Velopack knows whether it installed this copy;
    /// any other Windows build is taken for the zip. On Linux, the AppImage runtime sets
    /// <c>$APPIMAGE</c> to the file it mounted; without it, or when that file isn't there, the app
    /// runs from somewhere it can't update.
    /// </summary>
    public static AppPackage Detect(TargetOs os, bool installedByVelopack, string? appImageVariable,
        Func<string, bool> fileExists, Func<string, bool> canReplace)
    {
        if (os == TargetOs.Windows)
            return new AppPackage(installedByVelopack ? PackageKind.WindowsInstaller : PackageKind.WindowsZip);

        if (os == TargetOs.Linux && !string.IsNullOrWhiteSpace(appImageVariable)
            && Path.IsPathRooted(appImageVariable) && fileExists(appImageVariable))
            return new AppPackage(PackageKind.AppImage, appImageVariable, canReplace(appImageVariable));

        return new AppPackage(PackageKind.Other);
    }
}

/// <summary>How a newer version reaches this copy.</summary>
public enum UpdateMethod
{
    /// <summary>Velopack downloads the package and applies it after the app exits.</summary>
    Installer,

    /// <summary>The new AppImage is downloaded beside the old one and renamed over it.</summary>
    AppImage,

    /// <summary>The app says what is new and links to the release page; the user updates it.</summary>
    Link,
}

/// <summary>Why a newer version can only be linked to.</summary>
public enum LinkReason
{
    /// <summary>The zip's files are the user's to manage.</summary>
    Zip,

    /// <summary>Not run from the installer or an AppImage.</summary>
    NotPackaged,

    /// <summary>The AppImage's folder can't be written to, /opt say.</summary>
    FolderNotWritable,

    /// <summary>The release lacks the file this package updates from.</summary>
    MissingAsset,

    /// <summary>That file has no sha256 digest to check the download against.</summary>
    MissingDigest,
}

/// <summary>A newer release and how this copy can get it.</summary>
/// <param name="Assets">What the update downloads, each with its digest; empty for a link.</param>
/// <param name="Reason">For a link, why; <see cref="ReasonDetail"/> names the folder or the file.</param>
public sealed record UpdateOffer(
    SemVersion Version,
    SemVersion Current,
    GhRelease Release,
    UpdateMethod Method,
    IReadOnlyList<GhAsset> Assets,
    LinkReason? Reason = null,
    string? ReasonDetail = null)
{
    public bool CanUpdateInPlace => Method != UpdateMethod.Link;

    /// <summary>The release's page, or the releases page when GitHub didn't give one.</summary>
    public string PageUrl =>
        Release.PageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var page) && UpdateClient.IsAllowed(page)
            ? url
            : About.AppInfo.ReleasesLink;

    /// <summary>The release notes as text, without the carriage returns GitHub keeps.</summary>
    public string Notes => (Release.Body ?? "").ReplaceLineEndings("\n").Trim();
}

/// <summary>
/// Picks the release to offer out of GitHub's list, and how this copy can take it (issue #57).
/// </summary>
public static class UpdatePlanner
{
    /// <summary>The file names release.yml gives what it publishes.</summary>
    public const string AppImageAsset = "ReelsMagician-x86_64.AppImage";
    public const string ZipAsset = "ReelsMagician-win-x64.zip";
    public const string InstallerAsset = "ReelsMagician-win-x64-Setup.exe";

    /// <summary>Velopack's feed: an installed copy updates only from a release that has it.</summary>
    public const string InstallerFeedAsset = "releases.win.json";

    /// <summary>The package the feed points at, IDCT.ReelsMagician-&lt;version&gt;-full.nupkg.</summary>
    public const string InstallerPackageSuffix = "-full.nupkg";

    /// <summary>
    /// A pre-release on GitHub or by its tag: v0.2.0 is marked a pre-release while its tag is not,
    /// and a tag like v0.3.0-beta.1 is one whatever its mark says.
    /// </summary>
    public static bool IsPrerelease(GhRelease release, SemVersion version) => release.Prerelease || version.IsPrerelease;

    /// <summary>
    /// The newest release above <paramref name="current"/> that the user's channel takes, or null.
    /// Drafts never count, pre-releases only when included, tags that aren't versions are passed
    /// over, and GitHub's order doesn't matter. When the newest is a version the user chose to skip,
    /// or older than it, nothing is offered: an older release is no reason to ask again.
    /// </summary>
    public static (GhRelease Release, SemVersion Version)? Newest(IEnumerable<GhRelease> releases, SemVersion current,
        bool includePrereleases, SemVersion? skipped = null)
    {
        (GhRelease Release, SemVersion Version)? newest = null;
        foreach (var release in releases)
        {
            if (release.Draft || !SemVersion.TryParse(release.TagName, out var version)) continue;
            if (!includePrereleases && IsPrerelease(release, version)) continue;
            if (version <= current) continue;
            if (newest is null || version > newest.Value.Version) newest = (release, version);
        }
        if (newest is { } found && skipped is not null && found.Version <= skipped) return null;
        return newest;
    }

    /// <summary>What to offer this copy, or null when it is up to date.</summary>
    public static UpdateOffer? Plan(IEnumerable<GhRelease> releases, SemVersion current, bool includePrereleases,
        SemVersion? skipped, AppPackage package)
    {
        if (Newest(releases, current, includePrereleases, skipped) is not { } newest) return null;
        var (release, version) = newest;

        UpdateOffer Link(LinkReason reason, string? detail = null) =>
            new(version, current, release, UpdateMethod.Link, [], reason, detail);

        switch (package.Kind)
        {
            case PackageKind.WindowsInstaller:
            {
                var feed = Find(release, a => a.Name == InstallerFeedAsset);
                var nupkg = Find(release, a => a.Name.EndsWith(InstallerPackageSuffix, StringComparison.OrdinalIgnoreCase));
                if (feed is null) return Link(LinkReason.MissingAsset, InstallerFeedAsset);
                if (nupkg is null) return Link(LinkReason.MissingAsset, "*" + InstallerPackageSuffix);
                foreach (var asset in new[] { feed, nupkg })
                    if (!AssetDigest.TryParse(asset.Digest, out _)) return Link(LinkReason.MissingDigest, asset.Name);
                return new UpdateOffer(version, current, release, UpdateMethod.Installer, [feed, nupkg]);
            }

            case PackageKind.AppImage:
            {
                var image = Find(release, a => a.Name == AppImageAsset);
                if (image is null) return Link(LinkReason.MissingAsset, AppImageAsset);
                if (!AssetDigest.TryParse(image.Digest, out _) || image.Size <= 0) return Link(LinkReason.MissingDigest, image.Name);
                if (!package.CanReplace)
                    return Link(LinkReason.FolderNotWritable, Path.GetDirectoryName(package.AppImagePath) ?? package.AppImagePath);
                return new UpdateOffer(version, current, release, UpdateMethod.AppImage, [image]);
            }

            case PackageKind.WindowsZip:
                return Link(LinkReason.Zip);

            default:
                return Link(LinkReason.NotPackaged);
        }
    }

    private static GhAsset? Find(GhRelease release, Func<GhAsset, bool> match) => release.Assets.FirstOrDefault(match);
}

/// <summary>When the app asks GitHub on its own: at most once a day, and never as a development build.</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>
    /// The version updates compare against, from the version the build was given: null for a
    /// development build (0.0.0-dev, or anything that isn't a version), which never checks.
    /// </summary>
    public static SemVersion? CurrentVersion(string? informationalVersion)
    {
        var version = (informationalVersion ?? "").Split('+')[0].Trim();
        if (version == About.AppInfo.DevelopmentVersion) return null;
        return SemVersion.TryParse(version, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Whether the check that runs once the window is up should ask GitHub now. A last check in
    /// the future, after the clock was set back, counts as long ago.
    /// </summary>
    public static bool IsDue(bool enabled, SemVersion? current, DateTimeOffset? lastCheck, DateTimeOffset now) =>
        enabled && current is not null && (lastCheck is not { } last || last > now || now - last >= Interval);
}
