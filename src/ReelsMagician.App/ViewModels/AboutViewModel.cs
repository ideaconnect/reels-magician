using ReelsMagician.Core.About;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// The About tab: the app, its version and the system it runs on, who makes it, its links, and
/// every licence involved. The licence rows come from <see cref="Licences"/> in Core, never from
/// the XAML, so the logic checks can hold them to what a build carries.
/// </summary>
public sealed class AboutViewModel : ViewModelBase
{
    private readonly IAppShell _shell;
    private readonly string _appFolder;
    private readonly string _dataFolder;
    private readonly TargetOs _os;

    /// <param name="openLink">Opens a page and says so in the status bar, spelling it out in case no browser opens.</param>
    public AboutViewModel(IAppShell shell, Func<string, Task> openLink, string dataFolder,
        string? appFolder = null, TargetOs? os = null)
    {
        _shell = shell;
        _dataFolder = dataFolder;
        _appFolder = appFolder ?? AppContext.BaseDirectory;
        _os = os ?? ToolBuilds.Current;

        Version = AppInfo.DisplayVersion(AppInfo.InformationalVersion(typeof(AboutViewModel).Assembly));
        SystemText = AppInfo.SystemDescription();

        CopyDataFolderCommand = new AsyncRelayCommand(() => _shell.CopyTextAsync(_dataFolder));
        OpenCompanyCommand = new AsyncRelayCommand(() => openLink(AppInfo.CompanyLink));
        OpenRepositoryCommand = new AsyncRelayCommand(() => openLink(AppInfo.RepositoryLink));
        OpenIssuesCommand = new AsyncRelayCommand(() => openLink(AppInfo.IssuesLink));
        OpenReleasesCommand = new AsyncRelayCommand(() => openLink(AppInfo.ReleasesLink));
        ViewNoticesCommand = new AsyncRelayCommand(() => ShowAsync("Third-party notices", [Licences.Notices]));

        AppLicence = new LicenceRowViewModel(Licences.App, Licences.TextsFor(Licences.App, _os), openLink, ShowAsync);
        Groups =
        [
            .. Licences.For(_os).Select(g =>
            {
                var (title, description) = Licences.Describe(g.Key);
                return new LicenceGroupViewModel(title, description,
                    [.. g.Select(c => new LicenceRowViewModel(c, Licences.TextsFor(c, _os), openLink, ShowAsync))]);
            }),
        ];
    }

    public string Name => "ReelsMagician";

    /// <summary>0.2.0, or "development build" for one not made from a release tag.</summary>
    public string Version { get; }

    public string VersionText => Version == AppInfo.DevelopmentBuild ? "Development build" : $"Version {Version}";

    /// <summary>The system, its architecture and the .NET version, as a bug report would want them.</summary>
    public string SystemText { get; }

    public string DataFolder => _dataFolder;

    public string Company => AppInfo.LegalName;
    public string CompanyDescription => AppInfo.CompanyDescription;
    public string CompanyLink => AppInfo.CompanyLink;
    public string RepositoryLink => AppInfo.RepositoryLink;
    public string IssuesLink => AppInfo.IssuesLink;
    public string ReleasesLink => AppInfo.ReleasesLink;

    public string FontAwesomeAttribution => Licences.FontAwesomeAttribution;

    public LicenceRowViewModel AppLicence { get; }

    public IReadOnlyList<LicenceGroupViewModel> Groups { get; }

    public AsyncRelayCommand CopyDataFolderCommand { get; }
    public AsyncRelayCommand OpenCompanyCommand { get; }
    public AsyncRelayCommand OpenRepositoryCommand { get; }
    public AsyncRelayCommand OpenIssuesCommand { get; }
    public AsyncRelayCommand OpenReleasesCommand { get; }
    public AsyncRelayCommand ViewNoticesCommand { get; }

    /// <summary>Marks each downloaded component installed or not, so the terms that apply here show.</summary>
    public void ShowTools(Toolset tools)
    {
        foreach (var row in Groups.SelectMany(g => g.Rows))
            if (row.Component.Tool is { } tool)
                row.Install = DownloadedTerms.Describe(tool, tools, _dataFolder);
    }

    private Task ShowAsync(string title, IReadOnlyList<LicenceText> texts) =>
        _shell.ShowLicenceAsync(title, Licences.Read(_appFolder, texts));
}

public sealed class LicenceGroupViewModel(string title, string description, IReadOnlyList<LicenceRowViewModel> rows)
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public IReadOnlyList<LicenceRowViewModel> Rows { get; } = rows;
}

/// <summary>One component: what it is for, its licence and source, and for a download whether it is here.</summary>
public sealed class LicenceRowViewModel : ViewModelBase
{
    private readonly IReadOnlyList<LicenceText> _texts;

    public LicenceRowViewModel(LicensedComponent component, IReadOnlyList<LicenceText> texts,
        Func<string, Task> openLink, Func<string, IReadOnlyList<LicenceText>, Task> show)
    {
        Component = component;
        _texts = texts;
        OpenSourceCommand = new AsyncRelayCommand(() => openLink(component.Source));
        ViewLicenceCommand = new AsyncRelayCommand(() => show($"Licence: {component.Name}", _texts), () => HasTexts);
    }

    public LicensedComponent Component { get; }

    public string Name => Component.Name;
    public string Purpose => Component.Purpose;
    public string Licence => Component.Licence;
    public string Source => Component.Source;

    /// <summary>A build carries texts for it; a downloaded tool brings its own and has none here.</summary>
    public bool HasTexts => _texts.Count > 0;

    public string SourceAccessibleName => $"Source of {Name}";
    public string LicenceAccessibleName => $"Licence of {Name}";

    private InstallState? _install;

    /// <summary>For a downloaded component, whether it is on this machine; null for the rest.</summary>
    public InstallState? Install
    {
        get => _install;
        set
        {
            if (!Set(ref _install, value)) return;
            Raise(nameof(HasInstallState));
            Raise(nameof(IsPresent));
            Raise(nameof(InstallText));
        }
    }

    public bool HasInstallState => Install is not null;
    public bool IsPresent => Install?.IsPresent ?? false;
    public string InstallText => Install?.Text ?? "";

    public AsyncRelayCommand OpenSourceCommand { get; }
    public AsyncRelayCommand ViewLicenceCommand { get; }
}
