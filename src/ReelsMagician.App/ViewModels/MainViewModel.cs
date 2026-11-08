using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReelsMagician.App.Localization;
using ReelsMagician.App.Updates;
using ReelsMagician.Core.About;
using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Transcription;
using ReelsMagician.Core.Updates;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// Drives the whole app. The pipeline is deliberately exposed as five separate commands rather
/// than one button: the user reviews the subtitles between transcription and analysis, and
/// picks a reel and a caption style before anything is rendered.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IProgressSink
{
    private readonly IAppShell _shell;
    private readonly ToolPaths _paths = new();
    private readonly AppSettings _settings;
    private readonly ToolchainManager _toolchain;
    private readonly LinkToolsConsent _linkConsent;

    private ReelProject _project = new();
    private Toolset _tools = new();
    private CancellationTokenSource? _cts;

    // The number of the stage that is running, or 0 while none is. A progress report keeps the
    // number of the stage it came from and is dropped once that stage has finished, so a report
    // still queued from the stage's threads cannot replace the status it finished with.
    private int _runningStage;
    private int _stagesStarted;

    public MainViewModel(IAppShell shell)
    {
        _shell = shell;
        _paths.EnsureCreated();
        _settings = AppSettings.Load(_paths);
        // Before any text is built, so the window opens in the language it will keep.
        Localizer.Instance.Apply(_settings.Language);
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
        // Before anything can start a model: a record left by one means the app went down with it.
        _leftoverRun = ModelRunMarker.TakeLeftover(_paths.ModelRunMarkerFile);
        // Before the window first shows, so it opens in the theme it will keep.
        AppTheme.Apply(_settings.Appearance);
        _toolchain = new ToolchainManager(_paths, this);
        _linkConsent = new LinkToolsConsent(shell, _toolchain);
        // Checking for a newer version (issue #57), shown on the About tab and in the status area.
        Updates = new UpdatesViewModel(shell, _settings, _paths, new UpdateHost(
                IsBusy: () => IsBusy,
                HasOpenProject: () => _project.HasSource,
                RunStage: work => RunStageAsync(Stage.Update, work),
                OpenLink: link => OpenLinkAsync(link, () => L.Format(nameof(Strings.Link_Opening), link)),
                ShowAbout: () => ActiveTab = AboutTab,
                Sink: this),
            AppInfo.InformationalVersion(typeof(MainViewModel).Assembly), DetectPackage);
        // Before RefreshTools below, which shows the downloaded tools' state on it too.
        About = new AboutViewModel(shell, link => OpenLinkAsync(link, () => L.Format(nameof(Strings.Link_Opening), link)), _paths.Root,
            updates: Updates);

        StyleEditor = new StyleEditorViewModel(_project.Style);
        StyleEditor.Changed += (_, _) => OnStyleChanged();

        // Straight into the fields: the property setters save the settings and refresh the
        // toolchain view, which needs the commands below to exist. RefreshTools() runs once at
        // the end of the constructor, after everything it touches has been built.
        _selectedWhisperModel = ModelCatalog.ResolveWhisper(_settings.WhisperModelId);
        _selectedLlmModel = ModelCatalog.ResolveLlm(_settings.LlmModelId);
        OutputFolder = _settings.OutputDirectory;
        // Show which preset the starting style came from, rather than an empty box.
        _selectedPreset = Presets.FirstOrDefault(p => p.Name == StyleEditor.Name);

        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy);
        InstallLinkToolsCommand = new AsyncRelayCommand(InstallLinkToolsAsync, () => !IsBusy && !_tools.HasLinkTools);
        RefreshToolsCommand = new RelayCommand(RefreshTools);
        BrowseSourceCommand = new AsyncRelayCommand(BrowseSourceAsync);
        BrowseOutputCommand = new AsyncRelayCommand(BrowseOutputAsync);

        LoadSourceCommand = new AsyncRelayCommand(LoadSourceAsync, () => !IsBusy && SourceInput.Length > 0);
        TranscribeCommand = new AsyncRelayCommand(TranscribeAsync, () => !IsBusy && _project.HasSource);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy && _project.HasTranscript);
        FramingCommand = new AsyncRelayCommand(PlanFramingAsync, () => !IsBusy && SelectedCandidate is not null);
        RenderCommand = new AsyncRelayCommand(RenderAsync, () => !IsBusy && _project.HasFraming);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);

        SplitCueCommand = new RelayCommand(SplitCue, () => SelectedSubtitle is not null);
        MergeCueCommand = new RelayCommand(MergeCue, () => SelectedSubtitle is not null);
        DeleteCueCommand = new RelayCommand(DeleteCue, () => SelectedSubtitle is not null);
        ShiftEarlierCommand = new RelayCommand(() => ShiftSelected(-0.1), () => SelectedSubtitle is not null);
        ShiftLaterCommand = new RelayCommand(() => ShiftSelected(+0.1), () => SelectedSubtitle is not null);
        ImportSubtitlesCommand = new AsyncRelayCommand(ImportSubtitlesAsync, () => !IsBusy);
        ExportSubtitlesCommand = new AsyncRelayCommand(ExportSubtitlesAsync, () => Subtitles.Count > 0);

        ApplyPresetCommand = new RelayCommand(ApplyPreset, () => SelectedPreset is not null);
        RefreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewAsync, () => !IsBusy);
        CopyOutputPathCommand = new AsyncRelayCommand(
            () => _shell.CopyTextAsync(_project.OutputPath ?? ""), () => _project.OutputPath is not null);
        CopyDataFolderCommand = new AsyncRelayCommand(() => _shell.CopyTextAsync(_paths.Root));
        OpenCoffeeCommand = new AsyncRelayCommand(() => OpenSupportLinkAsync(CoffeeLink));
        OpenSponsorsCommand = new AsyncRelayCommand(() => OpenSupportLinkAsync(SponsorsLink));
        RefreshCleanupCommand = new RelayCommand(RefreshCleanup);
        CleanupCommand = new AsyncRelayCommand(CleanupAsync, () => !IsBusy && SelectedCleanupBytes > 0);

        RefreshTools();
        RefreshCleanup();
    }

    // ── Toolchain ────────────────────────────────────────────────────────────────────────

    public ObservableCollection<ComponentRowViewModel> Components { get; } = [];

    public ObservableCollection<ComponentRowViewModel> LinkComponents { get; } = [];

    private string _gpuText = "";
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }

    private string _backendText = "";
    public string BackendText { get => _backendText; private set => Set(ref _backendText, value); }

    private bool _toolsReady;
    public bool ToolsReady { get => _toolsReady; private set => Set(ref _toolsReady, value); }

    private string _missingText = "";
    public string MissingText { get => _missingText; private set => Set(ref _missingText, value); }

    /// <summary>The drop-downs' items: each model, marked when it probably won't fit (issue #54).</summary>
    public IReadOnlyList<ModelOptionViewModel> WhisperOptions { get; } =
        [.. ModelCatalog.WhisperModels.Select(m => new ModelOptionViewModel(m.Id, () => m.UiName))];

    public IReadOnlyList<ModelOptionViewModel> LlmOptions { get; } =
        [.. ModelCatalog.LlmModels.Select(m => new ModelOptionViewModel(m.Id, () => m.UiName))];

    public ModelOptionViewModel SelectedWhisperOption
    {
        get => WhisperOptions.First(o => o.Id == SelectedWhisperModel.Id);
        set { if (value is not null) SelectedWhisperModel = ModelCatalog.ResolveWhisper(value.Id); }
    }

    public ModelOptionViewModel SelectedLlmOption
    {
        get => LlmOptions.First(o => o.Id == SelectedLlmModel.Id);
        set { if (value is not null) SelectedLlmModel = ModelCatalog.ResolveLlm(value.Id); }
    }

    private string _whisperModelNotes = "";
    /// <summary>The chosen Whisper model's note and what it needs.</summary>
    public string WhisperModelNotes { get => _whisperModelNotes; private set => Set(ref _whisperModelNotes, value); }

    private string _llmModelNotes = "";
    /// <summary>The chosen Qwen model's note and what it needs at the context it will get.</summary>
    public string LlmModelNotes { get => _llmModelNotes; private set => Set(ref _llmModelNotes, value); }

    private string? _whisperFitWarning;
    /// <summary>Why the chosen Whisper model may not fit this computer, or null.</summary>
    public string? WhisperFitWarning { get => _whisperFitWarning; private set => Set(ref _whisperFitWarning, value); }

    private string? _llmFitWarning;
    /// <summary>Why the chosen Qwen model may not fit this computer, or null.</summary>
    public string? LlmFitWarning { get => _llmFitWarning; private set => Set(ref _llmFitWarning, value); }

    /// <summary>Which builds each model runs on, which differs between the two systems.</summary>
    public string ModelsHint => OperatingSystem.IsWindows() ? Strings.Setup_ModelsHint_Windows : Strings.Setup_ModelsHint_Linux;

    private WhisperModelSpec _selectedWhisperModel = ModelCatalog.DefaultWhisper;
    public WhisperModelSpec SelectedWhisperModel
    {
        get => _selectedWhisperModel;
        set
        {
            if (!Set(ref _selectedWhisperModel, value)) return;
            Raise(nameof(SelectedWhisperOption));
            _settings.WhisperModelId = value.Id;
            _settings.Save(_paths);
            RefreshTools();
        }
    }

    private LlmModelSpec _selectedLlmModel = ModelCatalog.DefaultLlm;
    public LlmModelSpec SelectedLlmModel
    {
        get => _selectedLlmModel;
        set
        {
            if (!Set(ref _selectedLlmModel, value)) return;
            Raise(nameof(SelectedLlmOption));
            _settings.LlmModelId = value.Id;
            _settings.LlamaContextSize = value.RecommendedContext;
            _settings.Save(_paths);
            RefreshTools();
        }
    }

    /// <summary>
    /// Holds every model against the memory it would run in, for the triangles in the drop-downs
    /// and the warnings under them. The chosen Qwen model is estimated at the context it will get;
    /// the others at the context choosing them sets.
    /// </summary>
    private void RefreshFit()
    {
        var system = MemoryFit.SystemMemoryBytes();
        var os = ToolBuilds.Current;

        var whisperBudget = MemoryFit.WhisperBudget(_tools, system, os);
        foreach (var option in WhisperOptions)
            option.Warning = MemoryFit.AssessWhisper(ModelCatalog.ResolveWhisper(option.Id), whisperBudget).Warning;

        var llmBudget = MemoryFit.LlmBudget(_tools, system, os);
        foreach (var option in LlmOptions)
        {
            var model = ModelCatalog.ResolveLlm(option.Id);
            var context = model.Id == SelectedLlmModel.Id ? _settings.LlamaContextSize : model.RecommendedContext;
            option.Warning = MemoryFit.AssessLlm(model, context, llmBudget).Warning;
        }

        var whisper = MemoryFit.AssessWhisper(SelectedWhisperModel, whisperBudget);
        WhisperModelNotes = $"{SelectedWhisperModel.UiNotes} {whisper.NeedText}";
        WhisperFitWarning = whisper.Warning;

        var llm = MemoryFit.AssessLlm(SelectedLlmModel, _settings.LlamaContextSize, llmBudget);
        LlmModelNotes = $"{SelectedLlmModel.UiNotes} {llm.NeedText}";
        LlmFitWarning = llm.Warning;
    }

    // The llama-server being asked for its devices, so a refresh meanwhile doesn't ask again, and
    // the builds that could not be asked this session.
    private string? _probingLlama;
    private readonly HashSet<string> _unprobedLlamas = [];

    /// <summary>
    /// Asks a newly found llama-server for its devices and options, off the UI thread, then
    /// shows the memory warnings with its own figures. Later detections find them kept.
    /// </summary>
    private async Task ProbeLlamaAsync(Toolset tools)
    {
        if (tools.LlamaServerPath is not { } exe || tools.LlamaFeatures is not null) return;
        if (_probingLlama == exe || _unprobedLlamas.Contains(exe)) return;

        _probingLlama = exe;
        try
        {
            if (await _toolchain.ProbeLlamaAsync(tools, CancellationToken.None).ConfigureAwait(true) is null)
                _unprobedLlamas.Add(exe);
        }
        catch (Exception ex)
        {
            // Never worth a dialog: the warnings fall back to the system's figures.
            _unprobedLlamas.Add(exe);
            this.Debug("setup", $"Could not ask llama-server for its devices: {ex.Message}");
        }
        finally { _probingLlama = null; }

        if (ReferenceEquals(tools, _tools)) RefreshFit();
    }

    /// <summary>
    /// Before a model that probably won't fit runs for the first time, asks whether to try it,
    /// with No as the default. A yes is remembered for that model, so it is asked once; its
    /// warning stays on the Setup tab.
    /// </summary>
    private async Task<bool> ConfirmFitAsync(FitAssessment fit, string modelId, ModelStage stage)
    {
        if (fit.Risk is not { } risk || _settings.AcceptedMemoryRisks.Contains(modelId)) return true;

        var transcribe = stage == ModelStage.Transcription;
        var question = L.Format(transcribe ? nameof(Strings.Fit_Ask_Transcribe) : nameof(Strings.Fit_Ask_Analyse), risk);
        var confirm = transcribe ? Strings.Fit_Transcribe : Strings.Fit_Analyse;
        if (!await _shell.AskAsync(Strings.Fit_Title, question, confirm, Strings.Common_Cancel).ConfigureAwait(true))
        {
            var model = fit.UiModel ?? fit.Model;
            ShowStatus(() => L.Format(nameof(Strings.Fit_NotStarted), model));
            return false;
        }

        _settings.AcceptedMemoryRisks.Add(modelId);
        _settings.Save(_paths);
        return true;
    }

    /// <summary>
    /// A model that was running when the app or the system last went down: what it was, and that
    /// a smaller one needs less. Found by the constructor, shown once the window is up.
    /// </summary>
    private readonly ModelRun? _leftoverRun;

    public async Task ExplainLastCrashAsync()
    {
        if (_leftoverRun is not { } run) return;

        var message = run.Explanation;
        // A yes given before the crash no longer holds: the next run asks again.
        if (_settings.AcceptedMemoryRisks.Remove(run.ModelId))
        {
            _settings.Save(_paths);
            message += "\n\n" + Strings.Fit_AskAgain;
        }
        this.Warn("setup", $"{run.Model} was running when ReelsMagician last closed unexpectedly.");
        await _shell.TellAsync(run.Title, message).ConfigureAwait(true);
    }

    /// <summary>The window closed normally, so a model it ran was not what took the app down.</summary>
    public void OnClosed()
    {
        ModelRunMarker.End(_paths.ModelRunMarkerFile);
        Localizer.Instance.LanguageChanged -= OnLanguageChanged;
    }

    // ── Appearance ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The theme: the system's light or dark setting, or one of the two. Switching applies at once,
    /// with no restart, and is saved.
    /// </summary>
    public Appearance Appearance
    {
        get => _settings.Appearance;
        set
        {
            if (_settings.Appearance == value) return;
            _settings.Appearance = value;
            _settings.Save(_paths);
            AppTheme.Apply(value);
            Raise();
            Raise(nameof(FollowsSystemTheme));
            Raise(nameof(UsesLightTheme));
            Raise(nameof(UsesDarkTheme));
        }
    }

    // One per segment of the header's switch. A segment that is unchecked because another was
    // chosen changes nothing; the chosen one sets the appearance.
    public bool FollowsSystemTheme
    {
        get => Appearance == Appearance.System;
        set { if (value) Appearance = Appearance.System; }
    }

    public bool UsesLightTheme
    {
        get => Appearance == Appearance.Light;
        set { if (value) Appearance = Appearance.Light; }
    }

    public bool UsesDarkTheme
    {
        get => Appearance == Appearance.Dark;
        set { if (value) Appearance = Appearance.Dark; }
    }

    // ── Language ─────────────────────────────────────────────────────────────────────────

    private static Localizer L => Localizer.Instance;

    public IReadOnlyList<UiLanguage> Languages { get; } = [UiLanguage.System, UiLanguage.English, UiLanguage.Polish];

    /// <summary>
    /// The interface's language: the system's, English or Polish. Switching applies at once, with
    /// no restart, and is saved. Log files, settings.json and the Activity list stay in English.
    /// </summary>
    public UiLanguage Language
    {
        get => _settings.Language;
        set
        {
            if (_settings.Language == value) return;
            _settings.Language = value;
            _settings.Save(_paths);
            Localizer.Instance.Apply(value);
            Raise();
        }
    }

    /// <summary>
    /// Rebuilds every text a view model made in code, after a switch of language. XAML's own texts
    /// follow through {l:Tr} by themselves.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var step in Steps) step.RefreshTexts();
        foreach (var option in WhisperOptions.Concat(LlmOptions)) option.RefreshTexts();
        // The component rows, the GPU line and the model notes are built from what was detected.
        ShowTools(_tools);
        RefreshCleanup(keepSelection: true);
        ShowSource();
        ShowTranscriptSummary();
        ShowPlanSummary();
        foreach (var row in Subtitles) row.RefreshTexts();
        foreach (var candidate in Candidates) candidate.RefreshTexts();
        foreach (var clip in ClipFramings) clip.RefreshTexts();
        About.RefreshTexts();
        Updates.RefreshTexts();
        Raise(nameof(Status));
        Raise(nameof(PreviewStatus));
        Raise(nameof(ModelsHint));
        Raise(nameof(DataFolderText));
        Raise(nameof(CleanupButtonText));
        Raise(nameof(CrossfadeHint));
        Raise(nameof(CrossfadeNote));
    }

    // ── Freeing up space ─────────────────────────────────────────────────────────────────

    public ObservableCollection<CleanupRowViewModel> CleanupItems { get; } = [];

    private long _dataFolderBytes;
    public string DataFolderText => L.Format(nameof(Strings.Cleanup_Total), _paths.Root, AssetDownloader.Humanize(_dataFolderBytes));

    private long _selectedCleanupBytes;
    public long SelectedCleanupBytes
    {
        get => _selectedCleanupBytes;
        private set
        {
            if (!Set(ref _selectedCleanupBytes, value)) return;
            Raise(nameof(CleanupButtonText));
            CleanupCommand.RaiseCanExecuteChanged();
        }
    }

    public string CleanupButtonText => SelectedCleanupBytes > 0
        ? L.Format(nameof(Strings.Cleanup_DeleteSize), AssetDownloader.Humanize(SelectedCleanupBytes))
        : Strings.Cleanup_NothingSelected;

    private void RefreshCleanup() => RefreshCleanup(keepSelection: false);

    /// <summary>
    /// Re-measures the data folder. Sizes are read from disk, never guessed. A switch of language
    /// rebuilds the rows in it and keeps what was ticked.
    /// </summary>
    private void RefreshCleanup(bool keepSelection)
    {
        var cleaner = new DataFolderCleaner(_paths, this);
        var ticked = keepSelection ? CleanupItems.Where(i => i.IsSelected).Select(i => i.Key).ToHashSet() : null;

        CleanupItems.Clear();
        foreach (var item in cleaner.Survey())
        {
            var row = new CleanupRowViewModel(item, RecountCleanupSelection);
            if (ticked is not null) row.IsSelected = ticked.Contains(item.Key);
            CleanupItems.Add(row);
        }

        _dataFolderBytes = cleaner.TotalBytes();
        Raise(nameof(DataFolderText));
        RecountCleanupSelection();
    }

    private void RecountCleanupSelection() =>
        SelectedCleanupBytes = CleanupItems.Where(i => i.IsSelected).Sum(i => i.Bytes);

    /// <summary>
    /// Deletes the ticked categories, after saying exactly what goes and what it will cost to get
    /// back. Deleting several gigabytes of model weights is not something to do on a stray click.
    /// </summary>
    private async Task CleanupAsync()
    {
        var chosen = CleanupItems.Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0) return;

        var summary = string.Join(Environment.NewLine,
            chosen.Select(i => $"  • {i.Name} — {i.SizeText}"));
        var redownloads = chosen.Any(i => i.Key is DataFolderCleaner.ModelsKey or DataFolderCleaner.ToolsKey);

        var question =
            L.Format(nameof(Strings.Cleanup_Ask), _paths.Root, summary, AssetDownloader.Humanize(SelectedCleanupBytes)) +
            (redownloads ? "\n\n" + Strings.Cleanup_Ask_Redownload : "") +
            "\n\n" + Strings.Cleanup_Ask_Final;

        if (!await _shell.AskAsync(Strings.Cleanup_Title, question, Strings.Common_Delete, Strings.Common_Cancel).ConfigureAwait(true))
            return;

        await RunStageAsync(Stage.Cleanup, async ct =>
        {
            var cleaner = new DataFolderCleaner(_paths, this);
            var keys = chosen.Select(i => i.Key).ToList();

            var result = await Task.Run(() => cleaner.Remove(keys, ct), ct).ConfigureAwait(true);

            RefreshCleanup();
            RefreshTools();

            if (!result.Complete)
                this.Warn("cleanup", "Close anything using these files and try again.");

            var failures = string.Join("; ", result.Failures.Take(3));
            return result.Complete
                ? () => L.Format(nameof(Strings.Cleanup_Freed), AssetDownloader.Humanize(result.BytesFreed))
                : () => L.Format(nameof(Strings.Cleanup_FreedInUse), AssetDownloader.Humanize(result.BytesFreed), failures);
        }).ConfigureAwait(true);
    }

    public RelayCommand RefreshCleanupCommand { get; }
    public AsyncRelayCommand CleanupCommand { get; }

    // ── Source ───────────────────────────────────────────────────────────────────────────

    private string _sourceInput = "";
    public string SourceInput
    {
        get => _sourceInput;
        set { if (Set(ref _sourceInput, value ?? "")) RefreshCommands(); }
    }

    public IReadOnlyList<int> QualityOptions { get; } = [2160, 1440, 1080];

    private int _maxHeight = 2160;
    public int MaxHeight { get => _maxHeight; set => Set(ref _maxHeight, value); }

    private double _targetSeconds = 45;
    public double TargetSeconds
    {
        get => _targetSeconds;
        set => Set(ref _targetSeconds, Math.Clamp(value, 8, 180));
    }

    private bool _useProvidedSubtitles = true;
    public bool UseProvidedSubtitles { get => _useProvidedSubtitles; set => Set(ref _useProvidedSubtitles, value); }

    private string _sourceSummary = Strings.Source_None;
    public string SourceSummary { get => _sourceSummary; private set => Set(ref _sourceSummary, value); }

    private string _sourceWarning = "";
    public string SourceWarning { get => _sourceWarning; private set => Set(ref _sourceWarning, value); }

    /// <summary>What the Source tab says about the loaded video, and its warning below 4K.</summary>
    private void ShowSource()
    {
        if (_project.Source is not { } source)
        {
            SourceSummary = Strings.Source_None;
            SourceWarning = "";
            return;
        }

        SourceSummary = $"{source.Title}\n{source.Info.Describe()}";
        SourceWarning = source.Info.Is4K
            ? ""
            : L.Format(source.Info.IsAtLeast1080p ? nameof(Strings.Source_Upscaled) : nameof(Strings.Source_BelowMinimum),
                source.Info.QualityLabel);
    }

    private string _topic = "";
    public string Topic
    {
        get => _topic;
        set { if (Set(ref _topic, value ?? "")) _project.Topic = _topic; }
    }

    // ── Transcript ───────────────────────────────────────────────────────────────────────

    public ObservableCollection<SubtitleRowViewModel> Subtitles { get; } = [];

    private SubtitleRowViewModel? _selectedSubtitle;
    public SubtitleRowViewModel? SelectedSubtitle
    {
        get => _selectedSubtitle;
        set { if (Set(ref _selectedSubtitle, value)) RefreshCommands(); }
    }

    private string _transcriptSummary = Strings.Transcript_None;
    public string TranscriptSummary { get => _transcriptSummary; private set => Set(ref _transcriptSummary, value); }

    // ── Reel plan ────────────────────────────────────────────────────────────────────────

    public ObservableCollection<CandidateViewModel> Candidates { get; } = [];

    private CandidateViewModel? _selectedCandidate;
    public CandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value)) return;
            _project.SelectedCandidate = value?.Candidate;
            // Framing belongs to a specific candidate, so switching invalidates it.
            _project.RenderClips.Clear();
            ClipFramings.Clear();
            RefreshCommands();
        }
    }

    public ObservableCollection<ClipFramingViewModel> ClipFramings { get; } = [];

    private string _planSummary = Strings.Plan_None;
    public string PlanSummary { get => _planSummary; private set => Set(ref _planSummary, value); }

    // ── Captions ─────────────────────────────────────────────────────────────────────────

    public StyleEditorViewModel StyleEditor { get; }

    public IReadOnlyList<SubtitleStyle> Presets { get; } = SubtitleStyle.Presets();

    private SubtitleStyle? _selectedPreset;
    public SubtitleStyle? SelectedPreset
    {
        get => _selectedPreset;
        set { if (Set(ref _selectedPreset, value)) RefreshCommands(); }
    }

    private Bitmap? _previewImage;

    /// <summary>
    /// The caption preview. Each one holds a decoded frame in native memory, about 8 MB, so the
    /// one it replaces is freed at once rather than whenever the finalizer runs. That is safe
    /// mid-frame: the renderer holds its own reference to a bitmap it is still drawing.
    /// </summary>
    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            var replaced = _previewImage;
            if (Set(ref _previewImage, value)) replaced?.Dispose();
        }
    }

    // The preview's status holds how to build its text, so a switch of language rebuilds it.
    private Func<string> _previewStatus = () => Strings.Preview_None;
    public string PreviewStatus => _previewStatus();

    private void ShowPreviewStatus(Func<string> text)
    {
        _previewStatus = text;
        Raise(nameof(PreviewStatus));
    }

    private double _crossfadeMs = 100;
    public double CrossfadeMs
    {
        get => _crossfadeMs;
        set
        {
            // Kept short by design: the audio crossfade matches it, and anything long enough to
            // hear would blur the words either side of the cut.
            if (!Set(ref _crossfadeMs, Math.Clamp(value, 20, 350))) return;
            Raise(nameof(CrossfadeHint));
            Raise(nameof(CrossfadeNote));
        }
    }

    public string CrossfadeHint => CrossfadeMs <= 150 ? Strings.Crossfade_Inaudible : Strings.Crossfade_Softens;

    /// <summary>The note under the slider: what this length does, then what it applies to.</summary>
    public string CrossfadeNote => CrossfadeHint + "\n" + Strings.Crossfade_Audio;

    // ── Output and progress ──────────────────────────────────────────────────────────────

    private string _outputFolder = "";
    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (!Set(ref _outputFolder, value ?? "")) return;
            _settings.OutputDirectory = _outputFolder;
            _settings.Save(_paths);
        }
    }

    /// <summary>
    /// The full path of the output folder as typed: <c>~</c> is the home folder, a relative folder
    /// is taken from it, and an empty box means the default folder. The box itself keeps the
    /// text as typed, so it does not change under the user's cursor.
    /// </summary>
    private string OutputFolderPath => UserPaths.ResolveOutputFolder(OutputFolder, AppSettings.DefaultOutputDirectory);

    public ObservableCollection<string> Log { get; } = [];

    // The status holds how to build its text, so a switch of language rebuilds it. A text that
    // came from Core's progress, in the language it was reported in, is kept as it is.
    private Func<string> _status = () => Strings.Status_Ready;
    public string Status
    {
        get => _status();
        private set => ShowStatus(() => value);
    }

    private void ShowStatus(Func<string> text)
    {
        _status = text;
        Raise(nameof(Status));
    }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private bool _indeterminate;
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) RefreshCommands(); }
    }

    // Tab order in MainWindow.axaml. Cleanup and About come last, on the right, apart from the steps.
    private const int SetupTab = 0, SourceTab = 1, SubtitlesTab = 2, ReelTab = 3, CaptionsTab = 4, RenderTab = 5,
        CleanupTab = 6, AboutTab = 7;

    private int _activeTab;
    public int ActiveTab
    {
        get => _activeTab;
        set
        {
            // Renders and downloads change the folder sizes, so they are measured afresh on opening.
            if (Set(ref _activeTab, value) && value == CleanupTab) RefreshCleanup();
        }
    }

    // ── Step strip ───────────────────────────────────────────────────────────────────────

    public StepViewModel SetupStep { get; } = new(1, nameof(Strings.Step_Setup));
    public StepViewModel SourceStep { get; } = new(2, nameof(Strings.Step_Source));
    public StepViewModel SubtitlesStep { get; } = new(3, nameof(Strings.Step_Subtitles));
    public StepViewModel ReelStep { get; } = new(4, nameof(Strings.Step_Reel));
    public StepViewModel CaptionsStep { get; } = new(5, nameof(Strings.Step_Captions));
    public StepViewModel RenderStep { get; } = new(6, nameof(Strings.Step_Render));

    private IEnumerable<StepViewModel> Steps => [SetupStep, SourceStep, SubtitlesStep, ReelStep, CaptionsStep, RenderStep];

    /// <summary>Shows on each step's chip whether its output exists yet.</summary>
    private void RefreshSteps()
    {
        var states = ReelSteps.Progress(_tools.IsComplete, _project);
        StepViewModel[] steps = [SetupStep, SourceStep, SubtitlesStep, ReelStep, CaptionsStep, RenderStep];
        for (var i = 0; i < steps.Length; i++) steps[i].State = states[i];
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand InstallLinkToolsCommand { get; }
    public RelayCommand RefreshToolsCommand { get; }
    public AsyncRelayCommand BrowseSourceCommand { get; }
    public AsyncRelayCommand BrowseOutputCommand { get; }
    public AsyncRelayCommand LoadSourceCommand { get; }
    public AsyncRelayCommand TranscribeCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand FramingCommand { get; }
    public AsyncRelayCommand RenderCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SplitCueCommand { get; }
    public RelayCommand MergeCueCommand { get; }
    public RelayCommand DeleteCueCommand { get; }
    public RelayCommand ShiftEarlierCommand { get; }
    public RelayCommand ShiftLaterCommand { get; }
    public AsyncRelayCommand ImportSubtitlesCommand { get; }
    public AsyncRelayCommand ExportSubtitlesCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public AsyncRelayCommand RefreshPreviewCommand { get; }
    public AsyncRelayCommand CopyOutputPathCommand { get; }
    public AsyncRelayCommand CopyDataFolderCommand { get; }
    public AsyncRelayCommand OpenCoffeeCommand { get; }
    public AsyncRelayCommand OpenSponsorsCommand { get; }

    // ── Support ──────────────────────────────────────────────────────────────────────────

    public const string CoffeeLink = "https://buymeacoffee.com/idct";
    public const string SponsorsLink = "https://github.com/sponsors/ideaconnect?frequency=one-time";

    private Task OpenSupportLinkAsync(string link) =>
        OpenLinkAsync(link, () => L.Format(nameof(Strings.Link_OpeningSupport), link));

    /// <summary>
    /// Opens a page in the system's browser. The status bar spells the address out too, because
    /// on Linux nothing reports whether a browser actually opened, and a minimal desktop may have
    /// none.
    /// </summary>
    private async Task OpenLinkAsync(string link, Func<string> opening)
    {
        ShowStatus(opening);
        try
        {
            if (!await _shell.OpenLinkAsync(new Uri(link)).ConfigureAwait(true))
                ShowStatus(() => L.Format(nameof(Strings.Link_NoBrowser), link));
        }
        catch (Exception ex)
        {
            ShowStatus(() => L.Format(nameof(Strings.Link_NoBrowser), link));
            this.Warn("links", $"Could not open {link}: {ex.Message}");
        }
    }

    // ── About ────────────────────────────────────────────────────────────────────────────

    public AboutViewModel About { get; }

    // ── Updates ──────────────────────────────────────────────────────────────────────────

    public UpdatesViewModel Updates { get; }

    /// <summary>
    /// What this copy runs from, asked only when an update is planned: Velopack's installed app,
    /// the zip, or the AppImage <c>$APPIMAGE</c> names, with whether its folder takes a new file.
    /// </summary>
    private static AppPackage DetectPackage() =>
        AppPackage.Detect(ToolBuilds.Current, OperatingSystem.IsWindows() && InstallerUpdate.IsInstalled(),
            Environment.GetEnvironmentVariable("APPIMAGE"), File.Exists, AppImageSwap.CanReplace);

    // ── Toolchain actions ────────────────────────────────────────────────────────────────

    /// <summary>Looks for the tools on disk again, probing the GPU, and shows what it finds.</summary>
    private void RefreshTools() => ShowTools(_toolchain.Detect(_settings));

    /// <summary>Shows a toolset that has just been detected, such as the one an install ends with.</summary>
    private void ShowTools(Toolset tools)
    {
        _tools = tools;

        ShowRows(Components, _toolchain.Describe(_settings, _tools));
        ShowRows(LinkComponents, _toolchain.DescribeLinkTools(_tools));
        About.ShowTools(_tools);

        GpuText = _tools.Gpu?.Describe() ?? Strings.Setup_GpuUnknown;
        BackendText = L.Format(nameof(Strings.Setup_Backends), _tools.WhisperBackend, _tools.LlamaBackend);
        ToolsReady = _tools.IsComplete;
        MissingText = _tools.IsComplete
            ? Strings.Setup_AllInstalled
            : L.Format(nameof(Strings.Setup_Missing), string.Join(", ", _tools.MissingComponents()));

        RefreshFit();
        // A llama-server not asked before: its own figures replace the system's once it answers.
        _ = ProbeLlamaAsync(_tools);
        RefreshCommands();
    }

    private static void ShowRows(ObservableCollection<ComponentRowViewModel> rows, IEnumerable<ComponentStatus> statuses)
    {
        rows.Clear();
        foreach (var status in statuses) rows.Add(new ComponentRowViewModel(status));
    }

    private Task InstallAsync() =>
        RunStageAsync(Stage.Install, ct => InstallThenRefreshAsync(c => _toolchain.EnsureAsync(_settings, c), ct));

    private async Task InstallLinkToolsAsync()
    {
        if (!await _linkConsent.AskAsync(_tools).ConfigureAwait(true)) return;

        await RunStageAsync(Stage.LinkTools,
            ct => InstallThenRefreshAsync(c => _toolchain.InstallLinkToolsAsync(_settings, c), ct)).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs an install, then shows what is on disk afterwards. Every install already ends by
    /// detecting the tools afresh, so its result is shown as it is rather than detected again.
    /// </summary>
    private async Task InstallThenRefreshAsync(Func<CancellationToken, Task<Toolset>> install, CancellationToken ct)
    {
        Toolset installed;
        try
        {
            installed = await install(ct).ConfigureAwait(true);
        }
        catch
        {
            // A failed or cancelled install may have put some of the tools in place already.
            RefreshTools();
            throw;
        }
        ShowTools(installed);
    }

    // ── Pipeline stages ──────────────────────────────────────────────────────────────────

    private async Task LoadSourceAsync()
    {
        // Normalised once, here, exactly as the resolver will read it: a quoted link is a link.
        var input = SourceResolver.NormaliseInput(SourceInput);
        var isLink = SourceResolver.LooksLikeUrl(input);
        var needsLinkTools = LinkToolsConsent.NeedsDownload(isLink, _tools);

        if (isLink && !_tools.HasFfmpeg)
        {
            // yt-dlp cannot join the video and audio without it, and would only fail after the download.
            ShowStatus(() => Strings.Source_LinksNeedFfmpeg);
            ActiveTab = SetupTab;
            return;
        }

        if (needsLinkTools && !await _linkConsent.AskAsync(_tools).ConfigureAwait(true))
        {
            ShowStatus(() => Strings.Source_LinkToolsDeclined);
            return;
        }

        // The current project stays until the new source has loaded, so a bad link or a cancelled
        // download costs nothing, and what is on screen is always what the buttons act on.
        var project = new ReelProject
        {
            SourceInput = input,
            Style = StyleEditor.Style,
        };

        await RunStageAsync(Stage.Source, async ct =>
        {
            if (needsLinkTools)
                await InstallThenRefreshAsync(c => _toolchain.InstallLinkToolsAsync(_settings, c), ct).ConfigureAwait(true);

            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            await pipeline.ResolveSourceAsync(project, MaxHeight, ct, UseProvidedSubtitles)
                .ConfigureAwait(true);

            StartProject(project);

            var source = project.Source!;
            Topic = source.Title;
            ShowSource();

            _settings.RememberSource(input);
            _settings.Save(_paths);
            // Stay on Source: Transcribe is the next button, and it lives on this tab.
            ActiveTab = SourceTab;
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Makes a newly loaded source the current project and clears everything shown for the old
    /// one. The selections are reset by hand: a list on a tab that is not open is not live, so
    /// emptying its items would not clear the selection it is bound to.
    /// </summary>
    private void StartProject(ReelProject project)
    {
        _project = project;
        Subtitles.Clear();
        SelectedSubtitle = null;
        Candidates.Clear();
        SelectedCandidate = null;
        ClipFramings.Clear();
        PreviewImage = null;
        TranscriptSummary = Strings.Transcript_None;
        PlanSummary = Strings.Plan_None;
        ShowPreviewStatus(() => Strings.Preview_None);
    }

    private async Task TranscribeAsync()
    {
        if (ReelPipeline.WillRunWhisper(_project, UseProvidedSubtitles) &&
            !await ConfirmFitAsync(MemoryFit.ForWhisper(_tools, _settings), SelectedWhisperModel.Id, ModelStage.Transcription).ConfigureAwait(true))
            return;

        await RunStageAsync(Stage.Transcribe, async ct =>
        {
            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            await pipeline.TranscribeAsync(_project, UseProvidedSubtitles, ct).ConfigureAwait(true);
            PopulateSubtitles();
            ActiveTab = SubtitlesTab;
        }).ConfigureAwait(true);
    }

    private async Task AnalyzeAsync()
    {
        // The estimate uses llama.cpp's own devices, so a build not asked yet is asked first.
        await _toolchain.ProbeLlamaAsync(_tools, CancellationToken.None).ConfigureAwait(true);
        if (!await ConfirmFitAsync(MemoryFit.ForLlm(_tools, _settings), SelectedLlmModel.Id, ModelStage.Analysis).ConfigureAwait(true))
            return;

        await RunStageAsync(Stage.Analyse, async ct =>
        {
            CommitSubtitleEdits();
            _project.Topic = Topic;
            _project.PlanOptions = _project.PlanOptions with { TargetSeconds = TargetSeconds };

            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            await pipeline.AnalyzeAsync(_project, ct).ConfigureAwait(true);

            Candidates.Clear();
            foreach (var candidate in _project.Plan!.Candidates)
                Candidates.Add(new CandidateViewModel(candidate));

            SelectedCandidate = Candidates.FirstOrDefault();
            _plannedSeconds = TargetSeconds;
            ShowPlanSummary();
            ActiveTab = ReelTab;
        }).ConfigureAwait(true);
    }

    private async Task PlanFramingAsync()
    {
        await RunStageAsync(Stage.Framing, async ct =>
        {
            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            await pipeline.PlanFramingAsync(_project, ct).ConfigureAwait(true);

            ClipFramings.Clear();
            for (var i = 0; i < _project.RenderClips.Count; i++)
                ClipFramings.Add(new ClipFramingViewModel(i,
                    _project.RenderClips[i].Range, _project.RenderClips[i].Crop));

            ActiveTab = CaptionsTab;
            await RefreshPreviewAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RenderAsync()
    {
        await RunStageAsync(Stage.Render, async ct =>
        {
            _project.RenderOptions = _project.RenderOptions with { CrossfadeSeconds = CrossfadeMs / 1000.0 };

            var folder = OutputFolderPath;
            Directory.CreateDirectory(folder);
            var name = UserPaths.FileNameFromTitle(_project.SelectedCandidate?.Title ?? _project.Topic);
            var output = Path.Combine(folder, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            var result = await pipeline.RenderAsync(_project, output, ct).ConfigureAwait(true);

            RefreshCommands();
            return () => L.Format(nameof(Strings.Render_Written), result.OutputPath);
        }).ConfigureAwait(true);
    }

    // ── Subtitle editing ─────────────────────────────────────────────────────────────────

    private void PopulateSubtitles()
    {
        Subtitles.Clear();
        var transcript = _project.Transcript;
        if (transcript is null) return;

        foreach (var segment in transcript.Segments)
            Subtitles.Add(new SubtitleRowViewModel(segment));

        ShowTranscriptSummary();
        RefreshCommands();
    }

    /// <summary>How many cues and words the transcript has, and where they came from.</summary>
    private void ShowTranscriptSummary()
    {
        if (_project.Transcript is not { } transcript || Subtitles.Count == 0)
        {
            TranscriptSummary = Strings.Transcript_None;
            return;
        }

        var origin = transcript.Origin == TranscriptOrigin.ProvidedSubtitles
            ? Strings.Transcript_FromSupplied
            : L.Format(nameof(Strings.Transcript_FromWhisper), transcript.Language);
        TranscriptSummary = L.Format(nameof(Strings.Transcript_Summary),
            L.Plural("Transcript_Cues", Subtitles.Count), L.Plural("Transcript_Words", transcript.WordCount), origin);
    }

    // The reel length the plan was made for, which its summary names.
    private double _plannedSeconds;

    /// <summary>How many reels the analysis proposed, and how it got them.</summary>
    private void ShowPlanSummary()
    {
        if (_project.Plan is not { } plan || Candidates.Count == 0)
        {
            PlanSummary = Strings.Plan_None;
            return;
        }

        var options = L.Plural("Plan_Options", Candidates.Count);
        PlanSummary = plan.UsedFallback
            ? L.Format(nameof(Strings.Plan_Fallback), options, plan.Notes)
            : L.Format(nameof(Strings.Plan_Proposed), options, _plannedSeconds);
    }

    /// <summary>Writes the edited rows back into the transcript in timeline order.</summary>
    private void CommitSubtitleEdits()
    {
        var transcript = _project.Transcript;
        if (transcript is null) return;

        transcript.Segments = [.. Subtitles.Select(r => r.Segment)];
        transcript.Normalize();
        transcript.EnsureWordTimings();

        var edited = Subtitles.Count(r => r.Edited);
        if (edited > 0) this.Info("transcribe", $"Applied {edited} edited cue(s).");
    }

    private void SplitCue()
    {
        if (SelectedSubtitle is null) return;
        var index = Subtitles.IndexOf(SelectedSubtitle);
        var segment = SelectedSubtitle.Segment;

        var words = segment.Words.Count > 0 ? segment.Words : null;
        if (words is null or { Count: < 2 })
        {
            this.Warn("transcribe", "This cue has too few words to split.");
            ShowStatus(() => Strings.Cue_TooFewWords);
            return;
        }

        var half = words.Count / 2;
        var splitTime = words[half].Start;

        var tail = new TranscriptSegment
        {
            Start = splitTime,
            End = segment.End,
            Text = string.Join(' ', words.Skip(half).Select(w => w.Text)),
            Words = [.. words.Skip(half)],
        };

        segment.End = splitTime;
        segment.Text = string.Join(' ', words.Take(half).Select(w => w.Text));
        segment.Words = [.. words.Take(half)];

        SelectedSubtitle.RefreshAll();
        Subtitles.Insert(index + 1, new SubtitleRowViewModel(tail));
    }

    private void MergeCue()
    {
        if (SelectedSubtitle is null) return;
        var index = Subtitles.IndexOf(SelectedSubtitle);
        if (index < 0 || index + 1 >= Subtitles.Count)
        {
            this.Warn("transcribe", "There is no following cue to merge with.");
            ShowStatus(() => Strings.Cue_NothingToMerge);
            return;
        }

        var current = SelectedSubtitle.Segment;
        var next = Subtitles[index + 1].Segment;

        current.Text = (current.Text.Trim() + " " + next.Text.Trim()).Trim();
        current.End = next.End;
        current.Words.AddRange(next.Words);
        current.ClampWords();

        Subtitles.RemoveAt(index + 1);
        SelectedSubtitle.RefreshAll();
    }

    private void DeleteCue()
    {
        if (SelectedSubtitle is null) return;
        Subtitles.Remove(SelectedSubtitle);
        SelectedSubtitle = null;
    }

    private void ShiftSelected(double delta)
    {
        if (SelectedSubtitle is null) return;
        var segment = SelectedSubtitle.Segment;
        segment.Start = Math.Max(0, segment.Start + delta);
        segment.End = Math.Max(segment.Start + 0.1, segment.End + delta);

        for (var i = 0; i < segment.Words.Count; i++)
        {
            var w = segment.Words[i];
            segment.Words[i] = w with { Start = Math.Max(0, w.Start + delta), End = Math.Max(0, w.End + delta) };
        }
        segment.ClampWords();
        SelectedSubtitle.RefreshAll();
    }

    private async Task ImportSubtitlesAsync()
    {
        var file = await _shell.PickFileAsync(Strings.Subtitles_ImportTitle,
            [new FileFilter(Strings.Subtitles_Filter, "*.srt", "*.vtt"), new FileFilter(Strings.Common_AllFiles, "*")]).ConfigureAwait(true);
        if (file is null) return;

        try
        {
            var transcript = SubtitleParser.Load(file);
            transcript.EnsureWordTimings();
            _project.Transcript = transcript;
            PopulateSubtitles();
            this.Info("transcribe", $"Imported {transcript.Segments.Count} cues from {Path.GetFileName(file)}.");
            var count = transcript.Segments.Count;
            ShowStatus(() => L.Plural("Subtitles_Imported", count, Path.GetFileName(file)));
        }
        catch (Exception ex)
        {
            this.Error("transcribe", $"Import failed: {ex.Message}");
            ShowStatus(() => L.Format(nameof(Strings.Subtitles_ImportFailed), ex.Message));
        }
    }

    private async Task ExportSubtitlesAsync()
    {
        CommitSubtitleEdits();
        var file = await _shell.PickSaveFileAsync(Strings.Subtitles_ExportTitle, UserPaths.FileNameFromTitle(Topic) + ".srt",
            Directory.Exists(OutputFolderPath) ? OutputFolderPath : null, new FileFilter("SubRip", "*.srt")).ConfigureAwait(true);
        if (file is null) return;

        try
        {
            File.WriteAllText(file, _project.Transcript?.ToSrt() ?? "");
            this.Info("transcribe", $"Exported to {file}.");
            ShowStatus(() => L.Format(nameof(Strings.Subtitles_Exported), file));
        }
        catch (Exception ex)
        {
            this.Error("transcribe", $"Export failed: {ex.Message}");
            ShowStatus(() => L.Format(nameof(Strings.Subtitles_ExportFailed), ex.Message));
        }
    }

    // ── Captions and preview ─────────────────────────────────────────────────────────────

    private void ApplyPreset()
    {
        if (SelectedPreset is null) return;
        StyleEditor.LoadFrom(SelectedPreset);
    }

    private void OnStyleChanged() => ShowPreviewStatus(() => Strings.Preview_StyleChanged);

    private async Task RefreshPreviewAsync()
    {
        if (!_tools.HasFfmpeg)
        {
            ShowPreviewStatus(() => Strings.Preview_NeedsFfmpeg);
            return;
        }

        // A render takes a while and Load stays available meanwhile. If another source loads
        // before this one finishes, its frame belongs to a project that is gone, and must not
        // overwrite the reset StartProject made.
        var project = _project;
        try
        {
            var captions = ReelPipeline.BuildCaptions(project);
            if (captions.Count == 0)
            {
                ShowPreviewStatus(() => Strings.Preview_NoCaptions);
                return;
            }

            var reelTime = PreviewRenderer.ChoosePreviewTime(captions);
            var fade = ReelRenderer.ResolveCrossfade(project.RenderClips, project.RenderOptions);
            var (clipIndex, sourceTime) = PreviewRenderer.ToSource(project.RenderClips, fade, reelTime);
            var crop = clipIndex < project.RenderClips.Count ? project.RenderClips[clipIndex].Crop : null;

            var renderer = new PreviewRenderer(new FFmpegTool(_tools.Ffmpeg, this), this);
            var workDir = Path.Combine(project.WorkDir.Length > 0 ? project.WorkDir : _paths.Cache, "preview");

            var png = await renderer.RenderAsync(
                project.Source?.VideoPath, project.Source?.Info, crop, sourceTime,
                captions, StyleEditor.Style, reelTime, workDir, CancellationToken.None).ConfigureAwait(true);

            if (!ReferenceEquals(project, _project)) return;
            PreviewImage = LoadBitmap(png);
            var at = TimeSpan.FromSeconds(reelTime).ToString(@"mm\:ss\.ff", L.Formats);
            ShowPreviewStatus(() => L.Format(nameof(Strings.Preview_At), at, StyleEditor.Summary));
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(project, _project)) return;
            ShowPreviewStatus(() => L.Format(nameof(Strings.Preview_Failed), ex.Message));
            this.Warn("preview", ex.Message);
        }
    }

    /// <summary>Decodes the PNG at once and closes it, so the next preview can overwrite the file.</summary>
    private static Bitmap LoadBitmap(string path)
    {
        using var file = File.OpenRead(path);
        return new Bitmap(file);
    }

    // ── Shell plumbing ───────────────────────────────────────────────────────────────────

    private async Task BrowseSourceAsync()
    {
        var file = await _shell.PickFileAsync(Strings.Source_PickTitle,
        [
            new FileFilter(Strings.Source_PickFilter, "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi"),
            new FileFilter(Strings.Common_AllFiles, "*"),
        ]).ConfigureAwait(true);
        if (file is not null) SourceInput = file;
    }

    private async Task BrowseOutputAsync()
    {
        var folder = await _shell.PickFolderAsync(Strings.Render_PickFolderTitle,
            Directory.Exists(OutputFolderPath) ? OutputFolderPath : null).ConfigureAwait(true);
        if (folder is not null) OutputFolder = folder;
    }

    private void Cancel()
    {
        _cts?.Cancel();
        ShowStatus(() => Strings.Status_Cancelling);
    }

    /// <summary>
    /// A pipeline stage: its name in the log, in English, and the key of its name on screen. The
    /// log's lines are the ones this app always wrote, so a bug report reads the same in any language.
    /// </summary>
    private sealed record Stage(string LogName, string Key)
    {
        public static readonly Stage Install = new("Installing tools", nameof(Strings.Stage_Install));
        public static readonly Stage LinkTools = new("Downloading link tools", nameof(Strings.Stage_LinkTools));
        public static readonly Stage Source = new("Loading source", nameof(Strings.Stage_Source));
        public static readonly Stage Transcribe = new("Transcribing", nameof(Strings.Stage_Transcribe));
        public static readonly Stage Analyse = new("Analysing", nameof(Strings.Stage_Analyse));
        public static readonly Stage Framing = new("Planning framing", nameof(Strings.Stage_Framing));
        public static readonly Stage Render = new("Rendering", nameof(Strings.Stage_Render));
        public static readonly Stage Cleanup = new("Freeing up space", nameof(Strings.Stage_Cleanup));
        public static readonly Stage Update = new("Updating ReelsMagician", nameof(Strings.Stage_Update));

        public string Label => Localizer.Instance[Key];
    }

    /// <summary>Runs one pipeline stage that ends with "<c>stage</c> complete."</summary>
    private Task RunStageAsync(Stage stage, Func<CancellationToken, Task> work) =>
        RunStageAsync(stage, async ct =>
        {
            await work(ct).ConfigureAwait(true);
            return (Func<string>?)null;
        });

    /// <summary>
    /// Runs one pipeline stage with shared busy state, cancellation and error reporting. The work
    /// returns how to build the status to finish with, or null for "<c>stage</c> complete.".
    /// </summary>
    private async Task RunStageAsync(Stage stage, Func<CancellationToken, Task<Func<string>?>> work)
    {
        if (IsBusy) return;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Indeterminate = true;
        Progress = 0;
        Volatile.Write(ref _runningStage, ++_stagesStarted);
        var log = stage.LogName.ToLowerInvariant();
        ShowStatus(() => L.Format(nameof(Strings.Stage_Running), stage.Label));
        this.Info(log, stage.LogName + " started.");

        Func<string> finalStatus;
        try
        {
            finalStatus = await work(cts.Token).ConfigureAwait(true)
                          ?? (() => L.Format(nameof(Strings.Stage_Complete), stage.Label));
        }
        catch (OperationCanceledException)
        {
            finalStatus = () => L.Format(nameof(Strings.Stage_Cancelled), stage.Label);
            this.Warn(log, "Cancelled by the user.");
        }
        catch (Exception ex)
        {
            finalStatus = () => L.Format(nameof(Strings.Stage_Failed), stage.Label, ex.Message);
            this.Error(log, ex.Message);
            ErrorReporter.Report(ex, L.Format(nameof(Strings.Stage_Failed_Title), stage.Label), $"{stage.LogName} failed");
        }
        finally
        {
            // From here on, reports still queued from this stage are dropped.
            Volatile.Write(ref _runningStage, 0);
            _cts = null;
            IsBusy = false;
            Indeterminate = false;
            Progress = 0;
            RefreshCommands();
        }
        ShowStatus(finalStatus);
    }

    private void RefreshCommands()
    {
        // The step strip reads the same project flags as the commands below, so it follows
        // every change they do.
        RefreshSteps();

        // A property setter that runs during construction reaches here before the commands
        // exist; there is nothing to refresh yet, and the constructor refreshes at its end.
        if (CleanupCommand is null) return;

        foreach (var command in new[]
        {
            LoadSourceCommand, TranscribeCommand, AnalyzeCommand, FramingCommand,
            RenderCommand, InstallCommand, InstallLinkToolsCommand, RefreshPreviewCommand, CleanupCommand,
            ImportSubtitlesCommand, ExportSubtitlesCommand, CopyOutputPathCommand,
        })
            command.RaiseCanExecuteChanged();

        foreach (var command in new[]
        {
            CancelCommand, SplitCueCommand, MergeCueCommand, DeleteCueCommand,
            ShiftEarlierCommand, ShiftLaterCommand, ApplyPresetCommand,
        })
            command.RaiseCanExecuteChanged();

        // An update waits for a stage to end, and a stage can't start during an update.
        Updates.RefreshCommands();
    }

    // ── IProgressSink ────────────────────────────────────────────────────────────────────

    void IProgressSink.Log(LogLevel level, string stage, string message)
    {
        if (level == LogLevel.Debug) return; // too noisy for the on-screen log
        var line = $"{DateTime.Now:HH:mm:ss}  [{stage}] {message}";
        Post(() =>
        {
            Log.Add(line);
            while (Log.Count > 500) Log.RemoveAt(0);
        });
    }

    void IProgressSink.Report(string stage, string message, double? fraction)
    {
        // Progress belongs to a running stage. A report from one that has finished, a late line
        // of ffmpeg output say, would replace the status that stage finished with.
        var from = Volatile.Read(ref _runningStage);
        if (from == 0) return;
        Post(() =>
        {
            if (from != _runningStage) return;
            Status = message;
            Indeterminate = fraction is null;
            Progress = fraction is null ? 0 : Math.Clamp(fraction.Value, 0, 1) * 100;
        });
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }
}
