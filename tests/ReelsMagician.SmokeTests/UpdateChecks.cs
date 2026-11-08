using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ReelsMagician.Core.About;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Updates;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// Checking GitHub for a newer release and updating to it (issue #57): versions compared as
/// SemVer, the release a channel offers, how each package takes it, what is downloaded and how it
/// is held to its digest, the AppImage swap, the schedule and the settings, and the app's wiring.
/// No check reaches the network: a stand-in answers for GitHub.
/// </summary>
public static class UpdateChecks
{
    public static void Run()
    {
        Check.Section("Updates: versions, the release on offer, packages, downloads and the AppImage swap (issue #57)");

        Versions();
        Selection();
        Packages();
        Schedule();
        Settings();
        Digests();
        Hosts();
        Requests();
        Downloads();
        AppImage();

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;
        Wiring(root);
        Docs(root);
    }

    // ── Versions ─────────────────────────────────────────────────────────────────────────

    private static SemVersion V(string text) => SemVersion.Parse(text);

    private static void Versions()
    {
        // semver.org's own example of precedence, rule 11.
        string[] ordered =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0", "2.0.0", "2.1.0", "2.1.1",
        ];
        var pairs = ordered.Zip(ordered.Skip(1)).Where(p => !(V(p.First) < V(p.Second)) || !(V(p.Second) > V(p.First))).ToList();
        Check.True(pairs.Count == 0, "SemVer precedence follows semver.org's example: 1.0.0-alpha < 1.0.0-alpha.1 < … < 1.0.0-rc.1 < 1.0.0 < 2.1.1"
            + (pairs.Count == 0 ? "" : $" (wrong: {string.Join(", ", pairs.Select(p => $"{p.First} vs {p.Second}"))})"));
        var shuffled = ordered.Reverse().OrderBy(s => s.Length).Select(V).Order().Select(v => v.ToString()).ToArray();
        Check.True(shuffled.SequenceEqual(ordered), "and sorting a shuffled list gives that order");

        Check.True(V("0.10.0") > V("0.9.0") && V("1.0.0-beta.11") > V("1.0.0-beta.2") && V("1.0.0-alpha.10") > V("1.0.0-alpha.9"),
            "numbers compare as numbers, not as text: 0.10.0 > 0.9.0, beta.11 > beta.2");
        Check.True(V("1.0.0-100000000000000000000") > V("1.0.0-99999999999999999999"),
            "a numeric identifier too long for a long still compares by value");
        Check.True(V("1.0.0-1") < V("1.0.0-a") && V("1.0.0-B") < V("1.0.0-a"),
            "a numeric identifier comes before a word, and words compare in ASCII order");
        Check.True(V("0.3.0-beta.1") > V("0.2.0") && V("0.3.0-beta.1") < V("0.3.0"),
            "0.3.0-beta.1 is newer than 0.2.0 and older than 0.3.0");
        Check.True(SemVersion.TryParse("v0.2.0", out var tagged) && tagged == V("0.2.0")
                   && SemVersion.TryParse("V0.2.0", out var upper) && upper.Equals(V("0.2.0")),
            "a tag's leading v is read past: v0.2.0 is 0.2.0");
        Check.True(V("0.3.0+build.5").Equals(V("0.3.0")) && V("0.3.0+a").GetHashCode() == V("0.3.0+b").GetHashCode(),
            "build metadata doesn't count");
        Check.Equal("0.3.0-beta.1", V("v0.3.0-beta.1+abc123").ToString(), "a version prints without its v and build metadata");

        string[] refused =
        [
            "", " ", "v", "0.3", "0.3.0.1", "01.2.3", "1.02.3", "1.2.03", "1.0.0-01", "1.0.0-", "1.0.0-beta..1",
            "1.0.0+", "1.0.0+a..b", "1.0.0-beta_1", "latest", "-1.0.0", "1.0.0 beta", "１.0.0",
        ];
        var accepted = refused.Where(t => SemVersion.TryParse(t, out _)).ToList();
        Check.True(accepted.Count == 0, $"what isn't a version is refused, such as 0.3, 01.2.3, 1.0.0-01 and latest"
            + (accepted.Count == 0 ? "" : $" (accepted: {string.Join(", ", accepted.Select(a => $"'{a}'"))})"));
        Check.True(!System.Version.TryParse("0.3.0-beta.1", out _),
            "System.Version can't read a pre-release, which is why updates use SemVersion");
    }

    // ── The release on offer ─────────────────────────────────────────────────────────────

    private const string Digest = "sha256:1cb901fa84f9e036c714e33e0caf71a2742647311e1aefcee9da6ee0b973556b";

    private static GhAsset Asset(string name, string? digest = Digest, long size = 1000) => new()
    {
        Name = name,
        Url = $"https://github.com/ideaconnect/reels-magician/releases/download/v/{name}",
        Size = size,
        Digest = digest,
    };

    private static GhAsset[] FullAssets(string version) =>
    [
        Asset(UpdatePlanner.InstallerAsset), Asset(UpdatePlanner.ZipAsset), Asset(UpdatePlanner.AppImageAsset),
        Asset($"IDCT.ReelsMagician-{version}-full.nupkg"), Asset(UpdatePlanner.InstallerFeedAsset),
    ];

    private static GhRelease Release(string tag, bool prerelease = false, bool draft = false, params GhAsset[] assets) => new()
    {
        TagName = tag,
        Name = "ReelsMagician " + tag.TrimStart('v'),
        Prerelease = prerelease,
        Draft = draft,
        Body = "Notes for " + tag + ".\r\n\r\n- One\r\n- Two",
        PageUrl = $"https://github.com/ideaconnect/reels-magician/releases/tag/{tag}",
        Assets = [.. assets],
    };

    /// <summary>Releases as GitHub would list them, newest first, with the cases a list can hold.</summary>
    private static List<GhRelease> Feed() =>
    [
        Release("v0.4.0", draft: true, assets: FullAssets("0.4.0")),
        Release("v0.3.1-beta.1", prerelease: true, assets: FullAssets("0.3.1-beta.1")),
        Release("nightly", prerelease: true, assets: FullAssets("0.9.9")),
        Release("v0.3.0", assets: FullAssets("0.3.0")),
        Release("v0.2.0", prerelease: true, assets: [Asset(UpdatePlanner.ZipAsset), Asset(UpdatePlanner.AppImageAsset)]),
        Release("v0.1.0", prerelease: true),
    ];

    private static void Selection()
    {
        var current = V("0.2.0");
        string? Newest(IEnumerable<GhRelease> releases, bool pre, string? skipped = null) =>
            UpdatePlanner.Newest(releases, current, pre, skipped is null ? null : V(skipped))?.Version.ToString();

        Check.Equal("0.3.1-beta.1", Newest(Feed(), pre: true), "with pre-releases, the newest is 0.3.1-beta.1; the draft 0.4.0 never counts");
        Check.Equal("0.3.0", Newest(Feed(), pre: false), "without them, 0.3.0");
        Check.Equal("0.3.1-beta.1", Newest(Enumerable.Reverse(Feed()), pre: true), "GitHub's order doesn't matter");
        Check.True(Newest(Feed().Where(r => r.TagName is "v0.2.0" or "v0.1.0" or "nightly"), pre: true) is null,
            "nothing above this version means no offer, and a tag that isn't a version is passed over");
        Check.True(UpdatePlanner.Newest(Feed(), V("0.3.1-beta.1"), true) is null, "the newest version itself is up to date");
        Check.True(Newest(Feed().Where(r => r.TagName != "v0.3.0"), pre: false) is null,
            "while every release is a pre-release, a channel without them offers nothing: hence the default");

        var rc = Release("v0.5.0-rc.1", prerelease: false, assets: FullAssets("0.5.0-rc.1"));
        Check.Equal("0.3.0", Newest([.. Feed(), rc], pre: false),
            "a tag with a pre-release part is a pre-release even when GitHub doesn't mark it so");
        // Today's case: v0.2.0's tag has no pre-release part, but GitHub marks the release as one.
        var today = new[] { Release("v0.2.0", prerelease: true, assets: FullAssets("0.2.0")), Release("v0.1.0", prerelease: true) };
        Check.True(UpdatePlanner.Newest(today, V("0.1.0"), includePrereleases: false) is null
                   && UpdatePlanner.Newest(today, V("0.1.0"), includePrereleases: true)?.Version == V("0.2.0"),
            "and a release GitHub marks as a pre-release is one, whatever its tag: 0.1.0 is offered 0.2.0 only with pre-releases");

        Check.True(Newest(Feed(), pre: true, skipped: "0.3.1-beta.1") is null,
            "a skipped version isn't offered again unasked");
        Check.Equal("0.3.1-beta.1", Newest(Feed(), pre: true, skipped: "0.3.0"), "but one newer than it is");
        Check.True(Newest(Feed(), pre: false, skipped: "0.3.1-beta.1") is null,
            "and an older one isn't offered after a newer one was skipped");
    }

    private static readonly AppPackage Installed = new(PackageKind.WindowsInstaller);
    private static readonly AppPackage Image = new(PackageKind.AppImage, "/home/kasia/Aplikacje/ReelsMagician-x86_64.AppImage", CanReplace: true);

    private static void Packages()
    {
        var current = V("0.2.0");
        UpdateOffer? Plan(AppPackage package, IEnumerable<GhRelease>? releases = null, bool pre = false) =>
            UpdatePlanner.Plan(releases ?? Feed(), current, pre, null, package);

        var installer = Plan(Installed);
        Check.True(installer is { Method: UpdateMethod.Installer, CanUpdateInPlace: true } && installer.Version == V("0.3.0")
                   && installer.Assets.Select(a => a.Name).SequenceEqual([UpdatePlanner.InstallerFeedAsset, "IDCT.ReelsMagician-0.3.0-full.nupkg"]),
            "the installed app on Windows updates through Velopack, from the release's releases.win.json and its full .nupkg");
        var image = Plan(Image);
        Check.True(image is { Method: UpdateMethod.AppImage } && image.Assets.Single().Name == UpdatePlanner.AppImageAsset,
            "an AppImage in a writable folder is replaced with the release's AppImage");
        Check.True(Plan(new AppPackage(PackageKind.WindowsZip)) is { Method: UpdateMethod.Link, Reason: LinkReason.Zip, Assets.Count: 0 },
            "the zip is told about it and linked to the release; its files are left alone");
        Check.True(Plan(new AppPackage(PackageKind.Other)) is { Method: UpdateMethod.Link, Reason: LinkReason.NotPackaged },
            "and so is a copy run with dotnet run or from an extracted AppImage");

        var readOnlyImage = "/opt/ReelsMagician/ReelsMagician.AppImage";
        var readOnly = Plan(Image with { AppImagePath = readOnlyImage, CanReplace = false });
        Check.True(readOnly is { Method: UpdateMethod.Link, Reason: LinkReason.FolderNotWritable } && readOnly.ReasonDetail == Path.GetDirectoryName(readOnlyImage),
            "an AppImage in a folder it can't write to, /opt say, falls back to the link, naming the folder");

        GhRelease With(string tag, params GhAsset[] assets) => Release(tag, assets: assets);
        var noFeed = Plan(Installed, [With("v0.3.0", Asset(UpdatePlanner.InstallerAsset), Asset("IDCT.ReelsMagician-0.3.0-full.nupkg"))]);
        Check.True(noFeed is { Method: UpdateMethod.Link, Reason: LinkReason.MissingAsset, ReasonDetail: UpdatePlanner.InstallerFeedAsset },
            "a release without releases.win.json can't update the installed app: it is linked to, as Velopack would not see it");
        var noPackage = Plan(Installed, [With("v0.3.0", Asset(UpdatePlanner.InstallerFeedAsset))]);
        Check.True(noPackage is { Reason: LinkReason.MissingAsset, ReasonDetail: "*-full.nupkg" }, "nor one without its .nupkg");
        var undigested = Plan(Installed, [With("v0.3.0", Asset(UpdatePlanner.InstallerFeedAsset), Asset("IDCT.ReelsMagician-0.3.0-full.nupkg", digest: null))]);
        Check.True(undigested is { Reason: LinkReason.MissingDigest, ReasonDetail: "IDCT.ReelsMagician-0.3.0-full.nupkg" },
            "nor one whose package has no digest");

        var noImage = Plan(Image, [With("v0.3.0", Asset(UpdatePlanner.ZipAsset))]);
        Check.True(noImage is { Reason: LinkReason.MissingAsset, ReasonDetail: UpdatePlanner.AppImageAsset },
            "a release without an AppImage is linked to on Linux");
        Check.True(Plan(Image, [With("v0.3.0", Asset(UpdatePlanner.AppImageAsset, digest: null))]) is { Reason: LinkReason.MissingDigest },
            "and one whose AppImage has no digest, which is refused");
        Check.True(Plan(Image, [With("v0.3.0", Asset(UpdatePlanner.AppImageAsset, digest: "sha512:" + new string('a', 128)))]) is { Reason: LinkReason.MissingDigest },
            "as is a digest in an algorithm the app doesn't check");
        Check.True(Plan(Image, [With("v0.3.0", Asset(UpdatePlanner.AppImageAsset, size: 0))]) is { Reason: LinkReason.MissingDigest },
            "and an AppImage without a size to hold the download to");
        Check.True(Plan(Image, [With("v0.3.0", Asset("ReelsMagician-x86_64.AppImage.zsync"), Asset("reelsmagician-x86_64.appimage"))]) is { Reason: LinkReason.MissingAsset },
            "the AppImage is found by its exact name, not by a file that merely resembles it");

        var offer = Plan(Image, pre: true)!;
        Check.Equal("Notes for v0.3.1-beta.1.\n\n- One\n- Two", offer.Notes, "the release notes are shown as text, with GitHub's line endings made plain");
        Check.Equal("https://github.com/ideaconnect/reels-magician/releases/tag/v0.3.1-beta.1", offer.PageUrl, "Release page opens that release");
        var elsewhere = Release("v0.3.0", assets: FullAssets("0.3.0"));
        elsewhere.PageUrl = "https://example.com/releases/v0.3.0";
        Check.Equal(AppInfo.ReleasesLink, Plan(Image, [elsewhere])!.PageUrl, "and a page outside GitHub is never opened: the releases page instead");

        // Which package this is.
        var os = TargetOs.Linux;
        AppPackage Detect(TargetOs system, bool velopack, string? variable, bool exists = true, bool writable = true) =>
            AppPackage.Detect(system, velopack, variable, _ => exists, _ => writable);
        Check.Equal(PackageKind.WindowsInstaller, Detect(TargetOs.Windows, true, null).Kind, "Velopack's install on Windows is the installer's");
        Check.Equal(PackageKind.WindowsZip, Detect(TargetOs.Windows, false, "/x.AppImage").Kind, "any other Windows copy is the zip");
        Check.True(Detect(os, false, "/home/kasia/ReelsMagician-x86_64.AppImage") is { Kind: PackageKind.AppImage, CanReplace: true, AppImagePath: "/home/kasia/ReelsMagician-x86_64.AppImage" },
            "on Linux, $APPIMAGE names the AppImage to replace");
        Check.True(Detect(os, false, "/home/kasia/ReelsMagician-x86_64.AppImage", writable: false) is { Kind: PackageKind.AppImage, CanReplace: false },
            "with whether its folder takes a new file");
        Check.Equal(PackageKind.Other, Detect(os, false, "/tmp/gone.AppImage", exists: false).Kind, "a $APPIMAGE whose file is gone is no AppImage");
        Check.Equal(PackageKind.Other, Detect(os, false, null).Kind, "nor is a copy without $APPIMAGE, such as dotnet run");
        Check.Equal(PackageKind.Other, Detect(os, false, "ReelsMagician.AppImage").Kind, "nor a relative $APPIMAGE");
    }

    // ── Schedule and settings ────────────────────────────────────────────────────────────

    private static void Schedule()
    {
        var now = new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);
        var release = V("0.2.0");
        Check.True(UpdateSchedule.IsDue(true, release, null, now), "a check that never ran is due once the window is up");
        Check.True(!UpdateSchedule.IsDue(true, release, now.AddHours(-23), now), "one 23 hours ago is not: at most once a day");
        Check.True(UpdateSchedule.IsDue(true, release, now.AddDays(-1), now), "one a day ago is");
        Check.True(UpdateSchedule.IsDue(true, release, now.AddDays(3), now), "a last check in the future, after the clock was set back, counts as long ago");
        Check.True(!UpdateSchedule.IsDue(false, release, null, now), "nothing is checked with Check for updates off");
        Check.True(!UpdateSchedule.IsDue(true, null, null, now), "and a development build never checks");

        Check.True(UpdateSchedule.CurrentVersion("0.2.0+7b16f3e") == V("0.2.0"), "a release build compares as its version, without the commit");
        Check.True(UpdateSchedule.CurrentVersion("0.3.0-beta.1") == V("0.3.0-beta.1"), "a pre-release build as its pre-release");
        Check.True(UpdateSchedule.CurrentVersion($"{AppInfo.DevelopmentVersion}+7b16f3e") is null && UpdateSchedule.CurrentVersion(AppInfo.DevelopmentVersion) is null
                   && UpdateSchedule.CurrentVersion(null) is null && UpdateSchedule.CurrentVersion("dev") is null,
            $"a development build ({AppInfo.DevelopmentVersion}), or one without a version, has none and never checks");
    }

    private static void Settings()
    {
        var fresh = new AppSettings();
        Check.True(fresh is { CheckForUpdates: true, IncludePrereleases: null, TakesPrereleases: true, LastUpdateCheck: null, SkippedVersion: null },
            "a new install checks for updates and takes pre-releases, as every release so far is one");

        var dir = Path.Combine(Path.GetTempPath(), "rm-updates-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ToolPaths(dir);
            fresh.Save(paths);
            var json = File.ReadAllText(paths.SettingsFile);
            Check.True(json.Contains("\"checkForUpdates\": true", StringComparison.Ordinal) && !json.Contains("includePrereleases", StringComparison.Ordinal),
                "settings.json says checkForUpdates and leaves includePrereleases out until it is chosen, so the default can change later");

            var at = new DateTimeOffset(2026, 11, 8, 19, 42, 7, 123, TimeSpan.FromHours(1));
            fresh.CheckForUpdates = false;
            fresh.IncludePrereleases = false;
            fresh.LastUpdateCheck = at;
            fresh.SkippedVersion = "0.3.0";
            fresh.Save(paths);
            json = File.ReadAllText(paths.SettingsFile);
            var loaded = AppSettings.Load(paths);
            Check.True(loaded is { CheckForUpdates: false, IncludePrereleases: false, TakesPrereleases: false, SkippedVersion: "0.3.0" }
                       && loaded.LastUpdateCheck == at,
                "the choices, the last check and a skipped version survive a restart");
            Check.Contains(json, "\"lastUpdateCheck\": \"2026-11-08T19:42:07.123+01:00\"",
                "and the last check is written the same way whatever the regional settings");

            File.WriteAllText(paths.SettingsFile, "{ \"whisperModelId\": \"small\", \"language\": \"pl\" }");
            Check.True(AppSettings.Load(paths) is { CheckForUpdates: true, TakesPrereleases: true, LastUpdateCheck: null },
                "a settings.json from before updates gets the defaults: checks on, pre-releases in");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* a temporary folder */ }
        }
    }

    // ── Digests and hosts ────────────────────────────────────────────────────────────────

    private static void Digests()
    {
        Check.True(AssetDigest.TryParse(Digest, out var bytes) && bytes.Length == 32, "GitHub's sha256:<hex> digest is read as 32 bytes");
        Check.True(AssetDigest.TryParse(Digest.ToUpperInvariant(), out var upper) && upper.SequenceEqual(bytes), "in either case");
        string?[] bad = [null, "", "sha256:", "sha256:abc", Digest + "00", "sha256:" + new string('g', 64), "sha512:" + new string('a', 128), Digest[7..], "md5:" + new string('a', 32)];
        Check.True(bad.All(d => !AssetDigest.TryParse(d, out _)), "and none, a short or long one, a non-hex one or another algorithm is no digest");

        var file = Path.Combine(Path.GetTempPath(), "rm-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(file, "hello\n");
            Check.Equal("sha256:5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03", AssetDigest.Of(file),
                "a file's digest is written as GitHub writes it");
        }
        finally { File.Delete(file); }
    }

    private static void Hosts()
    {
        string[] allowed =
        [
            "https://github.com/ideaconnect/reels-magician/releases/download/v0.3.0/ReelsMagician-x86_64.AppImage",
            "https://api.github.com/repos/ideaconnect/reels-magician/releases?per_page=10",
            "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r",
            "https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/2",
            "https://GitHub.com/ideaconnect/reels-magician",
        ];
        string[] refused =
        [
            "http://github.com/ideaconnect/reels-magician/releases/download/v0.3.0/x", "https://github.com.example.net/x",
            "https://example.net/github.com/x", "https://github.com:8443/x", "https://user:secret@github.com/x",
            "ftp://github.com/x", "https://raw.githubusercontent.com/ideaconnect/reels-magician/main/x",
        ];
        Check.True(allowed.All(u => UpdateClient.IsAllowed(new Uri(u))), "HTTPS to github.com, its API and its release download hosts is allowed");
        var through = refused.Where(u => UpdateClient.IsAllowed(new Uri(u))).ToList();
        Check.True(through.Count == 0, "plain HTTP, another host or port, a look-alike name and credentials in the address are not"
            + (through.Count == 0 ? "" : $" (allowed: {string.Join(", ", through)})"));
    }

    // ── Requests, through a stand-in for GitHub ──────────────────────────────────────────

    /// <summary>Answers the requests the client makes, and keeps each one's address and headers.</summary>
    private sealed class FakeGitHub(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, Dictionary<string, string> Headers)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value))));
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private const string ReleasesJson = """
        [
          { "tag_name": "v0.3.0", "name": "ReelsMagician 0.3.0", "draft": false, "prerelease": true,
            "body": "**Updates.**", "html_url": "https://github.com/ideaconnect/reels-magician/releases/tag/v0.3.0",
            "assets": [ { "name": "ReelsMagician-x86_64.AppImage", "size": 97122808,
                          "digest": "sha256:4e83341d14b4938361c5088158ec97af45af69902c15d9641274b6a85a90cdec",
                          "browser_download_url": "https://github.com/ideaconnect/reels-magician/releases/download/v0.3.0/ReelsMagician-x86_64.AppImage" } ] }
        ]
        """;

    private static void Requests()
    {
        var github = new FakeGitHub(uri => uri.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesJson, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using (var client = new UpdateClient("0.2.0", github))
        {
            var releases = client.GetReleasesAsync(CancellationToken.None).GetAwaiter().GetResult();
            var release = releases.Single();
            Check.True(release is { TagName: "v0.3.0", Prerelease: true, Draft: false, Body: "**Updates.**" }
                       && release.Assets.Single().Digest == "sha256:4e83341d14b4938361c5088158ec97af45af69902c15d9641274b6a85a90cdec",
                "the releases are read with their notes, flags, assets and digests");
        }
        var (uri, headers) = github.Requests.Single();
        Check.Equal(UpdateClient.ReleasesUrl, uri.ToString(), "one request, for the ten newest releases");
        Check.Equal("https://api.github.com/repos/ideaconnect/reels-magician/releases?per_page=10", UpdateClient.ReleasesUrl, "of this repository");
        Check.True(headers.TryGetValue("User-Agent", out var agent) && agent == "ReelsMagician/0.2.0",
            $"it sends a plain User-Agent: ReelsMagician/0.2.0 (got {agent})");
        Check.True(headers.Keys.Order().SequenceEqual(["Accept", "User-Agent"]) && headers["Accept"] == "application/vnd.github+json",
            $"and nothing else but the type it accepts: no token, no system, no machine (got {string.Join(", ", headers.Keys)})");

        var limited = new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using (var client = new UpdateClient("0.2.0", limited))
        {
            var status = Thrown<HttpRequestException>(() => client.GetReleasesAsync(CancellationToken.None))?.StatusCode;
            Check.Equal(HttpStatusCode.Forbidden, status, "GitHub's refusal, 403 once the 60 requests an hour are used up, is told apart by its status");
        }
        var offline = new FakeGitHub(_ => throw new HttpRequestException("No such host is known."));
        using (var client = new UpdateClient("0.2.0", offline))
            Check.True(Thrown<HttpRequestException>(() => client.GetReleasesAsync(CancellationToken.None)) is { StatusCode: null },
                "and from a network that isn't there, which the automatic check keeps to itself");
    }

    private static T? Thrown<T>(Func<Task> action) where T : Exception
    {
        try { action().GetAwaiter().GetResult(); }
        catch (T ex) { return ex; }
        catch (Exception) { return null; }
        return null;
    }

    private static void Downloads()
    {
        var body = Encoding.UTF8.GetBytes("a new AppImage, 0.3.0");
        var dir = Path.Combine(Path.GetTempPath(), "rm-updates-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "source");
            File.WriteAllBytes(source, body);
            var asset = new GhAsset
            {
                Name = UpdatePlanner.AppImageAsset,
                Url = "https://github.com/ideaconnect/reels-magician/releases/download/v0.3.0/ReelsMagician-x86_64.AppImage",
                Size = body.Length,
                Digest = AssetDigest.Of(source),
            };
            const string Storage = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r&sig=abc";
            var target = Path.Combine(dir, "download");

            (UpdateRefusedException? Error, FakeGitHub GitHub) Run(GhAsset a, Func<Uri, HttpResponseMessage> respond)
            {
                var github = new FakeGitHub(respond);
                using var client = new UpdateClient("0.2.0", github);
                var error = Thrown<UpdateRefusedException>(() => client.DownloadAsync(a, target, null, CancellationToken.None));
                return (error, github);
            }

            var (error, github) = Run(asset, u => u.Host == "github.com" ? Redirect(Storage) : Bytes(body));
            Check.True(error is null && File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(body),
                "a download follows GitHub's redirect to its storage and arrives whole");
            Check.True(github.Requests.Count == 2 && github.Requests[1].Uri.Host == "release-assets.githubusercontent.com"
                       && github.Requests.All(r => r.Headers["User-Agent"] == "ReelsMagician/0.2.0" && r.Headers.Count == 2),
                "with the same two headers on both requests");
            File.Delete(target);

            (error, github) = Run(asset, u => u.Host == "github.com" ? Redirect("https://downloads.example.net/ReelsMagician-x86_64.AppImage") : Bytes(body));
            Check.True(error is not null && github.Requests.Count == 1 && !File.Exists(target),
                "a redirect to any other host is refused before anything is asked of it");
            (error, github) = Run(asset, u => u.Host == "github.com" ? Redirect(Storage.Replace("https:", "http:")) : Bytes(body));
            Check.True(error is not null && github.Requests.Count == 1, "and so is one to plain HTTP");
            (error, github) = Run(asset, u => Redirect(asset.Url));
            Check.True(error is not null && github.Requests.Count == 6 && !File.Exists(target), "and a redirect loop ends after five");

            (error, _) = Run(asset, u => u.Host == "github.com" ? Redirect(Storage) : Bytes(Encoding.UTF8.GetBytes("a new AppImage, 0.3.1")));
            Check.True(error is not null && !File.Exists(target),
                "a download that doesn't match its digest is refused and deleted");
            Check.True(error?.Message.Contains(UpdatePlanner.AppImageAsset, StringComparison.Ordinal) == true
                       && error.LogMessage.Contains("checksum", StringComparison.Ordinal),
                "saying which file, in the interface's language and in the log's English");
            (error, _) = Run(asset, u => Bytes(body[..^3]));
            Check.True(error is not null && !File.Exists(target), "as is one shorter than GitHub says");
            (error, _) = Run(asset, u => Bytes([.. body, .. body]));
            Check.True(error is not null && !File.Exists(target), "and one longer");
            (error, _) = Run(new GhAsset { Name = asset.Name, Url = asset.Url, Size = asset.Size + 1, Digest = asset.Digest }, u => Bytes(body));
            Check.True(error is not null && !File.Exists(target),
                "and one whose digest matches but whose size isn't the one GitHub gives: both are held to");

            (error, github) = Run(new GhAsset { Name = asset.Name, Url = asset.Url, Size = asset.Size, Digest = null }, u => Bytes(body));
            Check.True(error is not null && github.Requests.Count == 0, "an asset without a digest is refused before anything is downloaded");
            (error, github) = Run(new GhAsset { Name = asset.Name, Url = asset.Url.Replace("https:", "http:"), Size = asset.Size, Digest = asset.Digest }, u => Bytes(body));
            Check.True(error is not null && github.Requests.Count == 0, "and an asset address that isn't HTTPS to GitHub is never asked");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* a temporary folder */ }
        }
    }

    // ── The AppImage swap ────────────────────────────────────────────────────────────────

    private static void AppImage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm-updates-appimage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var image = Path.Combine(dir, "ReelsMagician-x86_64.AppImage");
            var old = Encoding.UTF8.GetBytes("the running AppImage, 0.2.0");
            var fresh = Encoding.UTF8.GetBytes("the new AppImage, 0.3.0, a little longer");
            File.WriteAllBytes(image, old);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(image, (UnixFileMode)Convert.ToInt32("644", 8));
            var source = Path.Combine(dir, ".source");
            File.WriteAllBytes(source, fresh);
            var asset = new GhAsset
            {
                Name = UpdatePlanner.AppImageAsset, Size = fresh.Length, Digest = AssetDigest.Of(source),
                Url = "https://github.com/ideaconnect/reels-magician/releases/download/v0.3.0/ReelsMagician-x86_64.AppImage",
            };
            File.Delete(source);
            var version = V("0.3.0");

            var temporary = AppImageSwap.TemporaryPath(image, version);
            Check.True(Path.GetDirectoryName(temporary) == dir && Path.GetFileName(temporary).StartsWith('.')
                       && !temporary.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase),
                "the new image downloads into the same folder, so the rename stays on one file system, hidden and not named .AppImage");
            Check.True(AppImageSwap.CanReplace(image) && Directory.GetFiles(dir).Length == 1,
                "a writable folder takes a new file, and the probe leaves nothing behind");
            Check.True(!AppImageSwap.CanReplace(Path.Combine(dir, "missing.AppImage")), "a missing image can't be replaced");

            Exception? Swap(byte[] served)
            {
                using var client = new UpdateClient("0.2.0", new FakeGitHub(_ => Bytes(served)));
                return Thrown<Exception>(() => AppImageSwap.ReplaceAsync(client, asset, version, image, null, CancellationToken.None));
            }

            Check.True(Swap(Encoding.UTF8.GetBytes("the new AppImage, 0.3.0, a little LONGER")) is UpdateRefusedException
                       && File.ReadAllBytes(image).SequenceEqual(old) && Directory.GetFiles(dir).Length == 1,
                "a download that doesn't match its digest leaves the old image as it was, and no temporary file");

            // The running copy holds the old file open, as the AppImage runtime does.
            using var running = OperatingSystem.IsWindows() ? null : File.OpenRead(image);
            var error = Swap(fresh);
            Check.True(error is null && File.ReadAllBytes(image).SequenceEqual(fresh) && Directory.GetFiles(dir).Length == 1,
                "a matching download takes the old image's place, under its name, with nothing else left in the folder" + (error is null ? "" : $" ({error.Message})"));
            if (!OperatingSystem.IsWindows())
            {
                Check.Equal("755", Convert.ToString((int)File.GetUnixFileMode(image), 8), "and is executable, 0755");
                var kept = new byte[old.Length];
                running!.ReadExactly(kept);
                Check.True(kept.SequenceEqual(old), "while the running copy still reads the old image it opened: the rename leaves it alone");

                File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("555", 8));
                try
                {
                    var asRoot = File.Exists(image) && CanWriteAnyway(dir);
                    if (asRoot) Console.WriteLine("  SKIP  a read-only folder can't be tested as root, who writes anyway");
                    else Check.True(!AppImageSwap.CanReplace(image), "a folder that can't be written to is reported as such, without asking for admin rights");
                }
                finally { File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("755", 8)); }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* a temporary folder */ }
        }
    }

    private static bool CanWriteAnyway(string dir)
    {
        var probe = Path.Combine(dir, ".root-probe");
        try { File.WriteAllText(probe, ""); File.Delete(probe); return true; }
        catch (Exception) { return false; }
    }

    // ── The app's wiring and the docs ────────────────────────────────────────────────────

    private static void Wiring(string root)
    {
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts])).ReplaceLineEndings("\n");
        var app = Path.Combine("src", "ReelsMagician.App");
        var installer = Read(app, "Updates", "InstallerUpdate.cs");
        var model = Read(app, "ViewModels", "UpdatesViewModel.cs");
        var window = Read(app, "MainWindow.axaml");
        var code = Read(app, "MainWindow.axaml.cs");
        var release = Read(".github", "workflows", "release.yml");
        var build = Read("packaging", "windows", "build-installer.ps1");

        Check.Contains(installer, "new GithubSource(AppInfo.RepositoryLink, accessToken: null, prerelease: includePrereleases,",
            "Velopack reads this repository's releases with no token, in the channel the user chose");
        Check.Contains(installer, "new GuardedDownloader(client, releases, ct)", "through the guarded client, so its requests too reach only GitHub");
        Check.True(Regex.IsMatch(installer, @"version != offer\.Version\)[\s\S]*DownloadUpdatesAsync[\s\S]*WaitExitThenApplyUpdates\(target, silent: false, restart: true\)"),
            "and applies only the version offered, after downloading it, once the app exits, then starts it again");
        Check.Contains(installer, "releases.SelectMany(r => r.Assets).FirstOrDefault(a => a.Url == url)",
            "a file Velopack asks for must be an asset of a release the check listed, held to its digest");

        Check.True(Regex.IsMatch(code, @"await vm\.ExplainLastCrashAsync\(\);\s*await vm\.Updates\.CheckOnStartAsync\(\);"),
            "the window checks for updates once it is up, after any notice of the last crash");
        Check.Contains(model, "UpdateSchedule.IsDue(_settings.CheckForUpdates, Current, _settings.LastUpdateCheck, _clock.GetUtcNow())",
            "only when checks are on, it is a release build, and a day has passed");
        Check.Contains(model, "() => Offer is { CanUpdateInPlace: true } && !_host.IsBusy()", "Update now waits for a running stage to end");
        var quits = Regex.Matches(model, @"_shell\.Quit\(\);").Select(m => m.Index).ToList();
        var savedFirst = quits.Where(at =>
        {
            var method = model.LastIndexOf("private async Task", at, StringComparison.Ordinal);
            var saved = model.LastIndexOf("_settings.Save(_paths);", at, StringComparison.Ordinal);
            return method >= 0 && saved > method;
        }).Count();
        Check.True(quits.Count == 2 && savedFirst == 2,
            $"the app exits for an update in two places, Velopack's and the AppImage's restart, each after saving the settings ({savedFirst} of {quits.Count})");
        Check.Contains(model, "_host.HasOpenProject()", "a restart asks first while a video is open, as nothing keeps it");

        foreach (var binding in new[] { "CheckAutomatically", "IncludePrereleases", "CheckNowCommand", "UpdateNowCommand", "LaterCommand", "SkipCommand", "Notes", "StatusText" })
            Check.Contains(window, $"{{Binding {binding}}}", $"the About tab's Updates card binds {binding}");
        Check.Contains(window, "DataContext=\"{Binding Updates}\" x:DataType=\"vm:UpdatesViewModel\"", "and the card is the updates' own");
        Check.True(window.Contains("IsVisible=\"{Binding Updates.ShowsInStatusArea}\"", StringComparison.Ordinal)
                   && window.IndexOf("Updates.ShowsInStatusArea", StringComparison.Ordinal) > window.IndexOf("<!-- Status bar -->", StringComparison.Ordinal),
            "the offer also shows as a box in the status area");

        // The names the planner looks for are the ones release.yml publishes.
        Check.Contains(release, $"artifacts/{UpdatePlanner.AppImageAsset}", $"release.yml publishes the AppImage as {UpdatePlanner.AppImageAsset}");
        Check.Contains(release, $"artifacts/{UpdatePlanner.InstallerFeedAsset}", "and releases.win.json beside the installer");
        Check.Contains(release, $"artifacts/*{UpdatePlanner.InstallerPackageSuffix}", "and the full .nupkg");
        Check.Contains(release, $"artifacts/{UpdatePlanner.ZipAsset}", "and the zip");
        Check.Contains(build, $"$SetupName = '{UpdatePlanner.InstallerAsset}'", "and the installer under the name the planner knows");
        Check.Contains(release, "gh release create", "with gh, which GitHub gives each asset's sha256 digest on upload");
    }

    private static void Docs(string root)
    {
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var requirements = File.ReadAllText(Path.Combine(root, "docs", "system-requirements.md"));
        foreach (var (name, text) in new[] { ("README.md", readme), ("docs/system-requirements.md", requirements) })
            Check.True(text.Contains("once a day", StringComparison.Ordinal) && text.Contains("**Check for updates**", StringComparison.Ordinal),
                $"{name} says the app asks GitHub once a day, and how to turn it off");
        Check.True(!requirements.Contains("The AppImage doesn't update itself", StringComparison.Ordinal),
            "and the requirements no longer say the AppImage doesn't update itself");
    }
}
