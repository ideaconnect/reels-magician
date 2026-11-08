using System.Diagnostics;
using System.Net;
using ReelsMagician.App.Localization;
using ReelsMagician.App.Updates;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Updates;

namespace ReelsMagician.App.ViewModels;

/// <summary>What the update offer needs from the main window's model.</summary>
/// <param name="IsBusy">Whether a stage runs: an update never starts during one.</param>
/// <param name="HasOpenProject">Whether a video is loaded, which a restart would close.</param>
/// <param name="RunStage">Runs the update as a stage: busy state, progress, Cancel and errors.</param>
/// <param name="OpenLink">Opens a page and says so in the status bar.</param>
/// <param name="ShowAbout">Opens the About tab, where the release notes are.</param>
/// <param name="Sink">The activity log and the status bar's progress.</param>
public sealed record UpdateHost(
    Func<bool> IsBusy,
    Func<bool> HasOpenProject,
    Func<Func<CancellationToken, Task<Func<string>?>>, Task> RunStage,
    Func<string, Task> OpenLink,
    Action ShowAbout,
    IProgressSink Sink);

/// <summary>
/// Checks GitHub for a newer release and offers it, never interrupting (issue #57). Once the
/// window is up, at most once a day, it asks quietly in the background; a failure of the
/// network says nothing, which keeps the promise that local videos work offline. A newer version
/// shows in an information box in the status area and on the About tab, with its notes, and is
/// installed only when the user says so and no stage is running: through Velopack for the
/// installed app on Windows, by replacing the AppImage on Linux, and otherwise by linking to the
/// release page.
/// </summary>
public sealed class UpdatesViewModel : ViewModelBase
{
    private const string Log = "updates";

    private readonly IAppShell _shell;
    private readonly AppSettings _settings;
    private readonly ToolPaths _paths;
    private readonly UpdateHost _host;
    private readonly Lazy<AppPackage> _package;
    private readonly Func<UpdateClient> _client;
    private readonly TimeProvider _clock;

    private List<GhRelease> _releases = [];
    private bool _manual;

    /// <param name="informationalVersion">This build's version; a development build never checks.</param>
    /// <param name="detectPackage">What this copy runs from; asked once, when a check first finds releases.</param>
    /// <param name="client">The connection to GitHub, a stand-in for one in a check.</param>
    public UpdatesViewModel(IAppShell shell, AppSettings settings, ToolPaths paths, UpdateHost host,
        string? informationalVersion, Func<AppPackage> detectPackage, Func<UpdateClient>? client = null, TimeProvider? clock = null)
    {
        _shell = shell;
        _settings = settings;
        _paths = paths;
        _host = host;
        _package = new Lazy<AppPackage>(detectPackage);
        _clock = clock ?? TimeProvider.System;
        Current = UpdateSchedule.CurrentVersion(informationalVersion);
        _client = client ?? (() => new UpdateClient(Current?.ToString() ?? Core.About.AppInfo.DevelopmentVersion));

        CheckNowCommand = new AsyncRelayCommand(() => CheckAsync(manual: true), () => CanCheck && !IsChecking);
        UpdateNowCommand = new AsyncRelayCommand(UpdateNowAsync, () => Offer is { CanUpdateInPlace: true } && !_host.IsBusy());
        OpenReleaseCommand = new AsyncRelayCommand(() => Offer is { } offer ? _host.OpenLink(offer.PageUrl) : Task.CompletedTask,
            () => Offer is not null);
        LaterCommand = new RelayCommand(Later, () => Offer is not null);
        SkipCommand = new RelayCommand(Skip, () => Offer is not null);
        ShowNotesCommand = new RelayCommand(_host.ShowAbout);
        RestartNowCommand = new AsyncRelayCommand(RestartNowAsync, () => _restartInto is not null && !_host.IsBusy());

        _status = InitialStatus;
    }

    /// <summary>This build's version, or null for a development build.</summary>
    public SemVersion? Current { get; }

    /// <summary>What it runs from, which decides how it updates.</summary>
    public AppPackage Package => _package.Value;

    /// <summary>A development build has nothing to compare and never asks.</summary>
    public bool CanCheck => Current is not null;

    // ── Settings ─────────────────────────────────────────────────────────────────────────

    /// <summary>Check for updates, once a day once the window is up. On by default.</summary>
    public bool CheckAutomatically
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates == value) return;
            _settings.CheckForUpdates = value;
            _settings.Save(_paths);
            Raise();
            if (Offer is null && !IsChecking) ShowStatus(InitialStatus);
        }
    }

    /// <summary>Whether pre-releases are offered; on by default while every release is one.</summary>
    public bool IncludePrereleases
    {
        get => _settings.TakesPrereleases;
        set
        {
            if (_settings.TakesPrereleases == value) return;
            _settings.IncludePrereleases = value;
            _settings.Save(_paths);
            Raise();
            // The list already fetched says what the other channel offers; no need to ask again.
            if (_releases.Count > 0 && !IsChecking) Replan();
        }
    }

    // ── State ────────────────────────────────────────────────────────────────────────────

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (!Set(ref _isChecking, value)) return;
            CheckNowCommand.RaiseCanExecuteChanged();
        }
    }

    private Func<string> _status;

    /// <summary>The line under the settings: when it last checked and what it found.</summary>
    public string StatusText => _status();

    private void ShowStatus(Func<string> text)
    {
        _status = text;
        Raise(nameof(StatusText));
    }

    private Func<string> InitialStatus => () =>
        !CanCheck ? Strings.Updates_Status_Development
        : !_settings.CheckForUpdates ? Strings.Updates_Status_Off
        : _settings.LastUpdateCheck is { } last ? Localizer.Instance.Format(nameof(Strings.Updates_Status_LastChecked), When(last))
        : Strings.Updates_Status_NotChecked;

    private string When(DateTimeOffset at) => at.ToLocalTime().ToString("g", Localizer.Instance.Formats);

    private UpdateOffer? _offer;

    /// <summary>The newer release on offer, or null.</summary>
    public UpdateOffer? Offer
    {
        get => _offer;
        private set
        {
            _offer = value;
            Raise();
            foreach (var name in new[]
                     {
                         nameof(HasOffer), nameof(OfferText), nameof(Notes), nameof(HasNotes), nameof(CanUpdateInPlace),
                         nameof(IsLinkOnly), nameof(LinkText), nameof(ShowsInStatusArea),
                     })
                Raise(name);
            RefreshCommands();
        }
    }

    public bool HasOffer => Offer is not null;

    /// <summary>"ReelsMagician 0.3.0 is available (you have 0.2.0)."</summary>
    public string OfferText => Offer is { } offer
        ? Localizer.Instance.Format(nameof(Strings.Updates_Available), offer.Version, offer.Current)
        : "";

    /// <summary>The release notes, shown as text: the Markdown is not rendered.</summary>
    public string Notes => Offer?.Notes ?? "";

    public bool HasNotes => Notes.Length > 0;

    public bool CanUpdateInPlace => Offer is { CanUpdateInPlace: true };

    public bool IsLinkOnly => Offer is { CanUpdateInPlace: false };

    /// <summary>Why this copy can only be pointed at the release page, and what to do.</summary>
    public string LinkText => Offer is { Reason: { } reason } offer ? DescribeLink(reason, offer.ReasonDetail) : "";

    private static string DescribeLink(LinkReason reason, string? detail) => reason switch
    {
        LinkReason.Zip => Strings.Updates_Link_Zip,
        LinkReason.FolderNotWritable => Localizer.Instance.Format(nameof(Strings.Updates_Link_FolderNotWritable), detail),
        LinkReason.MissingAsset => Localizer.Instance.Format(nameof(Strings.Updates_Link_MissingAsset), detail),
        LinkReason.MissingDigest => Localizer.Instance.Format(nameof(Strings.Updates_Link_MissingDigest), detail),
        _ => Strings.Updates_Link_NotPackaged,
    };

    private bool _later;

    /// <summary>The box in the status area: until Later, Skip or the update itself.</summary>
    public bool ShowsInStatusArea => Offer is not null && !_later;

    // An AppImage replaced and waiting for a restart, with the version it now holds.
    private string? _restartInto;
    private SemVersion? _restartVersion;

    public bool IsReadyToRestart => _restartInto is not null;

    /// <summary>"ReelsMagician 0.3.0 is in place. Restart to use it; …"</summary>
    public string RestartText => _restartVersion is { } version && Current is { } current
        ? Localizer.Instance.Format(nameof(Strings.Updates_Ready), version, current)
        : "";

    // ── Commands ─────────────────────────────────────────────────────────────────────────

    public AsyncRelayCommand CheckNowCommand { get; }
    public AsyncRelayCommand UpdateNowCommand { get; }
    public AsyncRelayCommand OpenReleaseCommand { get; }
    public RelayCommand LaterCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand ShowNotesCommand { get; }
    public AsyncRelayCommand RestartNowCommand { get; }

    /// <summary>Follows a stage starting or ending: an update waits for it.</summary>
    public void RefreshCommands()
    {
        foreach (var command in new[] { CheckNowCommand, UpdateNowCommand, OpenReleaseCommand, RestartNowCommand })
            command.RaiseCanExecuteChanged();
        LaterCommand.RaiseCanExecuteChanged();
        SkipCommand.RaiseCanExecuteChanged();
    }

    /// <summary>After a switch of language: every text built in code.</summary>
    public void RefreshTexts()
    {
        foreach (var name in new[] { nameof(StatusText), nameof(OfferText), nameof(LinkText), nameof(RestartText) })
            Raise(name);
    }

    // ── Checking ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The check that runs once the window is up: only when it is on, this is a release build and
    /// a day has passed since GitHub last answered. It never shows an error.
    /// </summary>
    public Task CheckOnStartAsync() =>
        UpdateSchedule.IsDue(_settings.CheckForUpdates, Current, _settings.LastUpdateCheck, _clock.GetUtcNow())
            ? CheckAsync(manual: false)
            : Task.CompletedTask;

    /// <summary>
    /// Asks GitHub for the releases and offers the newest this copy should take. Check now
    /// (<paramref name="manual"/>) also shows a version the user skipped, and says why when it
    /// fails; the automatic check keeps a failure to the log.
    /// </summary>
    public async Task CheckAsync(bool manual)
    {
        if (Current is not { } current || IsChecking) return;

        IsChecking = true;
        var before = _status;
        if (manual) ShowStatus(() => Strings.Updates_Status_Checking);
        try
        {
            using var client = _client();
            var releases = await client.GetReleasesAsync(CancellationToken.None).ConfigureAwait(true);
            Answered();
            _releases = releases;
            _manual = manual || _manual;
            Replan();
            _host.Sink.Debug(Log, $"GitHub lists {releases.Count} releases; {(Offer is { } o ? $"{o.Version} is newer than {current}, by {o.Method}" : $"{current} is the newest")}.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            // GitHub answered, so the day's check is used: 403 means the hour's 60 are gone.
            Answered();
            _host.Sink.Debug(Log, $"Update check: GitHub answered {(int)status}.");
            ShowStatus(manual ? Failed(status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? Strings.Updates_Failed_RateLimit
                : Localizer.Instance.Format(nameof(Strings.Updates_Failed_Answered), (int)status)) : before);
        }
        catch (Exception ex)
        {
            // Offline, a proxy, a timeout: silent, and asked again at the next start.
            _host.Sink.Debug(Log, $"Update check failed: {ex.Message}");
            ShowStatus(manual ? Failed(ex is UpdateRefusedException ? ex.Message : Strings.Updates_Failed_Offline) : before);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static Func<string> Failed(string reason) => () => Localizer.Instance.Format(nameof(Strings.Updates_Status_Failed), reason);

    private void Answered()
    {
        _settings.LastUpdateCheck = _clock.GetUtcNow();
        _settings.Save(_paths);
    }

    /// <summary>Works out the offer from the releases fetched last, for the channel now chosen.</summary>
    private void Replan()
    {
        if (Current is not { } current) return;
        var skipped = !_manual && SemVersion.TryParse(_settings.SkippedVersion, out var s) ? s : null;
        var offer = UpdatePlanner.Plan(_releases, current, _settings.TakesPrereleases, skipped, Package);
        // An AppImage already replaced holds that version: it only needs the restart.
        if (offer is not null && _restartVersion is { } replaced && offer.Version <= replaced) offer = null;
        if (offer?.Version != Offer?.Version) _later = false;
        Offer = offer;

        // The box above says what is on offer or in place; the line says when GitHub was asked.
        var checkedAt = _settings.LastUpdateCheck ?? _clock.GetUtcNow();
        ShowStatus(offer is not null || IsReadyToRestart
            ? () => Localizer.Instance.Format(nameof(Strings.Updates_Status_LastChecked), When(checkedAt))
            : _manual || !SemVersion.TryParse(_settings.SkippedVersion, out var skip) || skip <= current
                ? () => Localizer.Instance.Format(nameof(Strings.Updates_Status_UpToDate), When(checkedAt))
                : () => Localizer.Instance.Format(nameof(Strings.Updates_Status_Skipped), skip));
    }

    // ── The offer ────────────────────────────────────────────────────────────────────────

    private void Later()
    {
        _later = true;
        Raise(nameof(ShowsInStatusArea));
        _host.Sink.Info(Log, $"Update to {Offer?.Version} put off until the next check.");
    }

    private void Skip()
    {
        if (Offer is not { } offer) return;
        _settings.SkippedVersion = offer.Version.ToString();
        _settings.Save(_paths);
        _manual = false;
        _host.Sink.Info(Log, $"Version {offer.Version} skipped.");
        Replan();
    }

    /// <summary>
    /// Installs the offer, between stages only. The installed app on Windows asks first when a
    /// video is open, since Velopack restarts it straight away; the AppImage is replaced first and
    /// restarted when the user says so.
    /// </summary>
    private async Task UpdateNowAsync()
    {
        if (Offer is not { CanUpdateInPlace: true } offer || _host.IsBusy()) return;
        if (offer.Method == UpdateMethod.Installer && !await ConfirmRestartAsync(Strings.Updates_Ask_Confirm).ConfigureAwait(true))
            return;

        var releases = _releases;
        await _host.RunStage(async ct =>
        {
            using var client = _client();
            var label = $"ReelsMagician {offer.Version}";
            _host.Sink.Info(Log, $"Updating from {offer.Current} to {offer.Version} ({offer.Method}).");

            if (offer.Method == UpdateMethod.Installer && OperatingSystem.IsWindows())
            {
                await InstallerUpdate.PrepareAsync(offer, releases, _settings.TakesPrereleases, client,
                    percent => Progress(label, percent / 100.0), ct).ConfigureAwait(true);
                // Velopack applies it once the app has exited, then starts the new version.
                _settings.Save(_paths);
                _host.Sink.Info(Log, $"Velopack applies {offer.Version} once ReelsMagician exits.");
                _shell.Quit();
                return () => Localizer.Instance.Format(nameof(Strings.Updates_Restarting), offer.Version);
            }

            var image = Package.AppImagePath!;
            await AppImageSwap.ReplaceAsync(client, offer.Assets[0], offer.Version, image,
                (done, total) => Progress(label, total > 0 ? (double)done / total : null, done, total), ct).ConfigureAwait(true);
            _host.Sink.Info(Log, $"{image} now holds {offer.Version}; the running copy stays {offer.Current} until a restart.");

            _restartInto = image;
            _restartVersion = offer.Version;
            Offer = null;
            Raise(nameof(IsReadyToRestart));
            Raise(nameof(RestartText));
            // The box says it is in place: the status line ends as any stage does.
            Replan();
            return null;
        }).ConfigureAwait(true);
    }

    private void Progress(string label, double? fraction, long done = 0, long total = 0)
    {
        var size = total > 0 ? $"{AssetDownloader.Humanize(done)} / {AssetDownloader.Humanize(total)}"
            : fraction is { } f ? $"{f * 100:0}%" : "";
        _host.Sink.Report("updates", Core.Localization.Lang.Ui.Format(nameof(Core.Localization.Strings.Install_Downloading), label, size), fraction);
    }

    /// <summary>Starts the new AppImage and exits, once the user agrees to close an open video.</summary>
    private async Task RestartNowAsync()
    {
        if (_restartInto is not { } image || _host.IsBusy()) return;
        if (!await ConfirmRestartAsync(Strings.Updates_RestartNow).ConfigureAwait(true)) return;

        _settings.Save(_paths);
        try
        {
            var start = new ProcessStartInfo(image) { UseShellExecute = false };
            // The AppImage runtime keeps the folder it was started from in $OWD.
            if (Environment.GetEnvironmentVariable("OWD") is { Length: > 0 } owd && Directory.Exists(owd))
                start.WorkingDirectory = owd;
            Process.Start(start);
        }
        catch (Exception ex)
        {
            _host.Sink.Warn(Log, $"Could not start {image}: {ex.Message}");
            await _shell.TellAsync(Strings.Updates_Ask_Title,
                Localizer.Instance.Format(nameof(Strings.Updates_RestartFailed), ex.Message)).ConfigureAwait(true);
            return;
        }
        _host.Sink.Info(Log, $"Started {image}; this copy exits.");
        _shell.Quit();
    }

    /// <summary>A restart closes the video that is open, which isn't saved anywhere: ask, with Cancel as the default.</summary>
    private Task<bool> ConfirmRestartAsync(string confirm) =>
        _host.HasOpenProject()
            ? _shell.AskAsync(Strings.Updates_Ask_Title, Strings.Updates_Ask_Project, confirm, Strings.Common_Cancel)
            : Task.FromResult(true);
}
