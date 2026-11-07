using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// Drives the whole app. The pipeline is deliberately exposed as five separate commands rather
/// than one button: the user reviews the subtitles between transcription and analysis, and
/// picks a reel and a caption style before anything is rendered.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IProgressSink
{
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly ToolPaths _paths = new();
    private readonly AppSettings _settings;
    private readonly ToolchainManager _toolchain;

    private ReelProject _project = new();
    private Toolset _tools = new();
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        _paths.EnsureCreated();
        _settings = AppSettings.Load(_paths);
        _toolchain = new ToolchainManager(_paths, this);

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
        InstallLinkToolsCommand = new AsyncRelayCommand(InstallLinkToolsAsync,
            () => !IsBusy && !(_tools.HasYtDlp && _tools.HasJsRuntime));
        RefreshToolsCommand = new RelayCommand(RefreshTools);
        BrowseSourceCommand = new RelayCommand(BrowseSource);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);

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
        ImportSubtitlesCommand = new RelayCommand(ImportSubtitles, () => !IsBusy);
        ExportSubtitlesCommand = new RelayCommand(ExportSubtitles, () => Subtitles.Count > 0);

        ApplyPresetCommand = new RelayCommand(ApplyPreset, () => SelectedPreset is not null);
        RefreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewAsync, () => !IsBusy);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => _project.OutputPath is not null);
        OpenToolsFolderCommand = new RelayCommand(() => OpenInExplorer(_paths.Root));
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

    public IReadOnlyList<WhisperModelSpec> WhisperModels => ModelCatalog.WhisperModels;
    public IReadOnlyList<LlmModelSpec> LlmModels => ModelCatalog.LlmModels;

    private WhisperModelSpec _selectedWhisperModel = ModelCatalog.DefaultWhisper;
    public WhisperModelSpec SelectedWhisperModel
    {
        get => _selectedWhisperModel;
        set
        {
            if (!Set(ref _selectedWhisperModel, value)) return;
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
            _settings.LlmModelId = value.Id;
            _settings.LlamaContextSize = value.RecommendedContext;
            _settings.Save(_paths);
            RefreshTools();
        }
    }

    // ── Freeing up space ─────────────────────────────────────────────────────────────────

    public ObservableCollection<CleanupRowViewModel> CleanupItems { get; } = [];

    private string _dataFolderText = "";
    public string DataFolderText { get => _dataFolderText; private set => Set(ref _dataFolderText, value); }

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
        ? $"Delete {AssetDownloader.Humanize(SelectedCleanupBytes)}…"
        : "Nothing selected";

    /// <summary>Re-measures the data folder. Sizes are read from disk, never guessed.</summary>
    private void RefreshCleanup()
    {
        var cleaner = new DataFolderCleaner(_paths, this);

        CleanupItems.Clear();
        foreach (var item in cleaner.Survey())
            CleanupItems.Add(new CleanupRowViewModel(item, RecountCleanupSelection));

        DataFolderText = $"{_paths.Root} — {AssetDownloader.Humanize(cleaner.TotalBytes())} in total";
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
            $"Delete the following from {_paths.Root}?\n\n{summary}\n\n" +
            $"That frees {AssetDownloader.Humanize(SelectedCleanupBytes)}." +
            (redownloads
                ? "\n\nThe models and tools will have to be downloaded again before the next reel."
                : "") +
            "\n\nThis cannot be undone.";

        if (MessageBox.Show(question, "Free up space", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;

        await RunStageAsync("Freeing up space", async ct =>
        {
            var cleaner = new DataFolderCleaner(_paths, this);
            var keys = chosen.Select(i => i.Key).ToList();

            var result = await Task.Run(() => cleaner.Remove(keys, ct), ct).ConfigureAwait(true);

            RefreshCleanup();
            RefreshTools();

            Status = result.Complete
                ? $"Freed {AssetDownloader.Humanize(result.BytesFreed)}."
                : $"Freed {AssetDownloader.Humanize(result.BytesFreed)}, but some files are still in use: " +
                  string.Join("; ", result.Failures.Take(3));

            if (!result.Complete)
                this.Warn("cleanup", "Close anything using these files and try again.");
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

    private string _sourceSummary = "No source loaded.";
    public string SourceSummary { get => _sourceSummary; private set => Set(ref _sourceSummary, value); }

    private string _sourceWarning = "";
    public string SourceWarning { get => _sourceWarning; private set => Set(ref _sourceWarning, value); }

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

    private const string NoTranscriptText = "No transcript yet.";

    private string _transcriptSummary = NoTranscriptText;
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

    private const string NoPlanText = "No reel planned yet.";

    private string _planSummary = NoPlanText;
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

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }

    private const string NoPreviewText = "Render a preview once a reel has been planned.";

    private string _previewStatus = NoPreviewText;
    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }

    private double _crossfadeMs = 100;
    public double CrossfadeMs
    {
        get => _crossfadeMs;
        set
        {
            // Kept short by design: the audio crossfade matches it, and anything long enough to
            // hear would blur the words either side of the cut.
            if (Set(ref _crossfadeMs, Math.Clamp(value, 20, 350))) Raise(nameof(CrossfadeHint));
        }
    }

    public string CrossfadeHint => CrossfadeMs <= 150
        ? "Inaudible in the audio crossfade."
        : "Long enough to soften speech at the cut.";

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

    public ObservableCollection<string> Log { get; } = [];

    private string _status = "Ready.";
    public string Status { get => _status; private set => Set(ref _status, value); }

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

    // Tab order in MainWindow.xaml.
    private const int SetupTab = 0, SourceTab = 1, SubtitlesTab = 2, ReelTab = 3, CaptionsTab = 4, RenderTab = 5;

    private int _activeTab;
    public int ActiveTab { get => _activeTab; set => Set(ref _activeTab, value); }

    // ── Commands ─────────────────────────────────────────────────────────────────────────

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand InstallLinkToolsCommand { get; }
    public RelayCommand RefreshToolsCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
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
    public RelayCommand ImportSubtitlesCommand { get; }
    public RelayCommand ExportSubtitlesCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public AsyncRelayCommand RefreshPreviewCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand OpenToolsFolderCommand { get; }

    // ── Toolchain actions ────────────────────────────────────────────────────────────────

    private void RefreshTools()
    {
        _tools = _toolchain.Detect(_settings);

        Components.Clear();
        foreach (var status in _toolchain.Describe(_settings, _tools))
            Components.Add(new ComponentRowViewModel(status));

        LinkComponents.Clear();
        foreach (var status in _toolchain.DescribeLinkTools(_tools))
            LinkComponents.Add(new ComponentRowViewModel(status));

        GpuText = _tools.Gpu?.Describe() ?? "GPU unknown";
        BackendText = $"Whisper: {_tools.WhisperBackend}    ·    Qwen: {_tools.LlamaBackend}";
        ToolsReady = _tools.IsComplete;
        MissingText = _tools.IsComplete
            ? "Everything is installed."
            : "Missing: " + string.Join(", ", _tools.MissingComponents());

        RefreshCommands();
    }

    private async Task InstallAsync()
    {
        await RunStageAsync("Installing tools", async ct =>
        {
            _tools = await _toolchain.EnsureAsync(_settings, ct).ConfigureAwait(true);
            RefreshTools();
        }).ConfigureAwait(true);
    }

    private async Task InstallLinkToolsAsync()
    {
        if (!ConfirmLinkToolsDownload()) return;

        await RunStageAsync("Downloading link tools", async ct =>
        {
            _tools = await _toolchain.InstallLinkToolsAsync(_settings, ct).ConfigureAwait(true);
            RefreshTools();
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Asks before fetching yt-dlp. It is a third-party tool that does not come with the app, so
    /// nothing downloads it until the user has said yes here. No is the default, so a stray Enter
    /// does not count as agreeing.
    /// </summary>
    private bool ConfirmLinkToolsDownload()
    {
        var downloads = string.Join(Environment.NewLine,
            _toolchain.DescribeLinkTools(_tools)
                .Where(c => c.NeedsDownload)
                .Select(c => $"  • {c.Name}, about {AssetDownloader.Humanize(c.ApproxBytes)}"));

        var question =
            "Loading a video from a link needs yt-dlp, a separate third-party tool. " +
            "It is not part of ReelsMagician and does not come with it.\n\n" +
            $"If you continue, ReelsMagician downloads from the publishers' own GitHub releases:\n\n{downloads}\n\n" +
            "You are responsible for having the right to download the videos you use it on, " +
            "and for following the terms of the site they come from.\n\n" +
            "Local video files work without it. Download now?";

        return MessageBox.Show(question, "Video links need yt-dlp",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    // ── Pipeline stages ──────────────────────────────────────────────────────────────────

    private async Task LoadSourceAsync()
    {
        var input = SourceInput.Trim();
        var isLink = SourceResolver.LooksLikeUrl(input);

        if (isLink && !_tools.HasFfmpeg)
        {
            // yt-dlp cannot join the video and audio without it, and would only fail after the download.
            Status = "Links need ffmpeg. Install the required tools on the Setup tab first.";
            ActiveTab = SetupTab;
            return;
        }

        if (isLink && !_tools.HasYtDlp && !ConfirmLinkToolsDownload())
        {
            Status = "yt-dlp was not downloaded, so the link was not loaded. Local video files work without it.";
            return;
        }

        // The current project stays until the new source has loaded, so a bad link or a cancelled
        // download costs nothing, and what is on screen is always what the buttons act on.
        var project = new ReelProject
        {
            SourceInput = input,
            Style = StyleEditor.Style,
        };

        await RunStageAsync("Loading source", async ct =>
        {
            if (isLink && !_tools.HasYtDlp)
            {
                _tools = await _toolchain.InstallLinkToolsAsync(_settings, ct).ConfigureAwait(true);
                RefreshTools();
            }

            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            await pipeline.ResolveSourceAsync(project, MaxHeight, ct, UseProvidedSubtitles)
                .ConfigureAwait(true);

            StartProject(project);

            var source = project.Source!;
            Topic = source.Title;
            SourceSummary = $"{source.Title}\n{source.Info.Describe()}";
            SourceWarning = source.Info.Is4K
                ? ""
                : source.Info.IsAtLeast1080p
                    ? $"Source is {source.Info.QualityLabel}. The 1080×1920 crop will be upscaled — a 4K source looks sharper."
                    : $"Source is only {source.Info.QualityLabel}, below the 1080p minimum. Expect a soft reel.";

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
        TranscriptSummary = NoTranscriptText;
        PlanSummary = NoPlanText;
        PreviewStatus = NoPreviewText;
    }

    private async Task TranscribeAsync()
    {
        await RunStageAsync("Transcribing", async ct =>
        {
            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            await pipeline.TranscribeAsync(_project, UseProvidedSubtitles, ct).ConfigureAwait(true);
            PopulateSubtitles();
            ActiveTab = SubtitlesTab;
        }).ConfigureAwait(true);
    }

    private async Task AnalyzeAsync()
    {
        await RunStageAsync("Analysing", async ct =>
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
            PlanSummary = _project.Plan.UsedFallback
                ? $"{Candidates.Count} option(s) — assembled from paragraph scores. {_project.Plan.Notes}"
                : $"{Candidates.Count} option(s) proposed for a {TargetSeconds:0}s reel.";
            ActiveTab = ReelTab;
        }).ConfigureAwait(true);
    }

    private async Task PlanFramingAsync()
    {
        await RunStageAsync("Planning framing", async ct =>
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
        await RunStageAsync("Rendering", async ct =>
        {
            _project.RenderOptions = _project.RenderOptions with { CrossfadeSeconds = CrossfadeMs / 1000.0 };

            Directory.CreateDirectory(OutputFolder);
            var name = Sanitise(_project.SelectedCandidate?.Title ?? _project.Topic);
            var output = Path.Combine(OutputFolder, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

            var pipeline = new ReelPipeline(_tools, _settings, _paths, this);
            var result = await pipeline.RenderAsync(_project, output, ct).ConfigureAwait(true);

            Status = $"Reel written to {result.OutputPath}";

            RefreshCommands();
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

        var origin = transcript.Origin == TranscriptOrigin.ProvidedSubtitles
            ? "supplied subtitles"
            : $"Whisper ({transcript.Language})";
        TranscriptSummary =
            $"{Subtitles.Count} cues · {transcript.WordCount} words · from {origin}. " +
            "Fix anything wrong before analysing — the analysis and the burned-in captions both use this text.";
        RefreshCommands();
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

    private void ImportSubtitles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import subtitles",
            Filter = "Subtitles (*.srt;*.vtt)|*.srt;*.vtt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var transcript = SubtitleParser.Load(dialog.FileName);
            transcript.EnsureWordTimings();
            _project.Transcript = transcript;
            PopulateSubtitles();
            this.Info("transcribe", $"Imported {transcript.Segments.Count} cues from {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            this.Error("transcribe", $"Import failed: {ex.Message}");
        }
    }

    private void ExportSubtitles()
    {
        CommitSubtitleEdits();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export subtitles",
            Filter = "SubRip (*.srt)|*.srt",
            FileName = Sanitise(Topic) + ".srt",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : null,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _project.Transcript?.ToSrt() ?? "");
            this.Info("transcribe", $"Exported to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            this.Error("transcribe", $"Export failed: {ex.Message}");
        }
    }

    // ── Captions and preview ─────────────────────────────────────────────────────────────

    private void ApplyPreset()
    {
        if (SelectedPreset is null) return;
        StyleEditor.LoadFrom(SelectedPreset);
    }

    private void OnStyleChanged() => PreviewStatus = "Style changed — refresh the preview to see it.";

    private async Task RefreshPreviewAsync()
    {
        if (!_tools.HasFfmpeg)
        {
            PreviewStatus = "ffmpeg is required for previews. Install it on the Setup tab.";
            return;
        }

        try
        {
            var captions = ReelPipeline.BuildCaptions(_project);
            if (captions.Count == 0)
            {
                PreviewStatus = "No captions to preview yet — plan a reel first.";
                return;
            }

            var reelTime = PreviewRenderer.ChoosePreviewTime(captions);
            var fade = ReelRenderer.ResolveCrossfade(_project.RenderClips, _project.RenderOptions);
            var (clipIndex, sourceTime) = PreviewRenderer.ToSource(_project.RenderClips, fade, reelTime);
            var crop = clipIndex < _project.RenderClips.Count ? _project.RenderClips[clipIndex].Crop : null;

            var renderer = new PreviewRenderer(new FFmpegTool(_tools.Ffmpeg, this), this);
            var workDir = Path.Combine(_project.WorkDir.Length > 0 ? _project.WorkDir : _paths.Cache, "preview");

            var png = await renderer.RenderAsync(
                _project.Source?.VideoPath, _project.Source?.Info, crop, sourceTime,
                captions, StyleEditor.Style, reelTime, workDir, CancellationToken.None).ConfigureAwait(true);

            PreviewImage = LoadBitmap(png);
            PreviewStatus = $"Preview at {TimeSpan.FromSeconds(reelTime):mm\\:ss\\.ff} of the reel · {StyleEditor.Summary}";
        }
        catch (Exception ex)
        {
            PreviewStatus = $"Preview failed: {ex.Message}";
            this.Warn("preview", ex.Message);
        }
    }

    /// <summary>Loads the PNG fully into memory so the file is not locked against re-rendering.</summary>
    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ── Shell plumbing ───────────────────────────────────────────────────────────────────

    private void BrowseSource()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a video",
            Filter = "Video (*.mp4;*.mov;*.mkv;*.webm;*.avi)|*.mp4;*.mov;*.mkv;*.webm;*.avi|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true) SourceInput = dialog.FileName;
    }

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where reels are saved",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : null,
        };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    private void OpenOutput()
    {
        if (_project.OutputPath is null || !File.Exists(_project.OutputPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_project.OutputPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { this.Warn("render", $"Could not open the folder: {ex.Message}"); }
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { this.Warn("install", $"Could not open {path}: {ex.Message}"); }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        Status = "Cancelling…";
    }

    /// <summary>Runs one pipeline stage with shared busy state, cancellation and error reporting.</summary>
    private async Task RunStageAsync(string label, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Indeterminate = true;
        Progress = 0;
        Status = label + "…";
        this.Info(label.ToLowerInvariant(), label + " started.");

        try
        {
            await work(cts.Token).ConfigureAwait(true);
            if (!Status.StartsWith("Reel written", StringComparison.Ordinal))
                Status = label + " complete.";
        }
        catch (OperationCanceledException)
        {
            Status = label + " cancelled.";
            this.Warn(label.ToLowerInvariant(), "Cancelled by the user.");
        }
        catch (Exception ex)
        {
            Status = $"{label} failed: {ex.Message}";
            this.Error(label.ToLowerInvariant(), ex.Message);
            ErrorReporter.Report(ex, $"{label} failed");
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            Indeterminate = false;
            Progress = 0;
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        // A property setter that runs during construction reaches here before the commands
        // exist; there is nothing to refresh yet, and the constructor refreshes at its end.
        if (CleanupCommand is null) return;

        foreach (var command in new[]
        {
            LoadSourceCommand, TranscribeCommand, AnalyzeCommand, FramingCommand,
            RenderCommand, InstallCommand, InstallLinkToolsCommand, RefreshPreviewCommand, CleanupCommand,
        })
            command.RaiseCanExecuteChanged();

        foreach (var command in new[]
        {
            CancelCommand, SplitCueCommand, MergeCueCommand, DeleteCueCommand,
            ShiftEarlierCommand, ShiftLaterCommand, ImportSubtitlesCommand,
            ExportSubtitlesCommand, ApplyPresetCommand, OpenOutputCommand,
        })
            command.RaiseCanExecuteChanged();
    }

    private static string Sanitise(string name)
    {
        var cleaned = new string((name ?? "reel")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "reel";
        return cleaned.Length <= 60 ? cleaned : cleaned[..60].TrimEnd();
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
        Post(() =>
        {
            Status = message;
            Indeterminate = fraction is null;
            Progress = fraction is null ? 0 : Math.Clamp(fraction.Value, 0, 1) * 100;
        });
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
