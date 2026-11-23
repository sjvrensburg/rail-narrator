using RailReader.Core.Models;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RailNarrator.App.Composition;
using RailNarrator.Domain;
using RailNarrator.Infrastructure;
using RailNarrator.Pdf;
using RailNarrator.Speech;

namespace RailNarrator.App.ViewModels;

/// <summary>One row in the narration transcript list.</summary>
/// <summary>
/// One transcript line. Its text and warning flag change in place when a maths passage is
/// interpreted during playback: replacing the item instead would clear the list's selection, and the
/// selection is what seeks.
/// </summary>
public sealed class TranscriptEntry(string segmentId, int index, string text, bool isMaths, bool hasWarning) : ViewModelBase
{
    private string _text = text;
    private bool _hasWarning = hasWarning;

    public string SegmentId { get; } = segmentId;
    public int Index { get; } = index;
    public bool IsMaths { get; } = isMaths;
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool HasWarning { get => _hasWarning; set => Set(ref _hasWarning, value); }
}

/// <summary>Content of the maths review panel.</summary>
public sealed record MathsReview(
    string SegmentId,
    string? CropImagePath,
    string RecognisedExpression,
    string SpokenVersion,
    IReadOnlyList<string> Warnings,
    ScriptProvenance Provenance,
    bool CanRegenerate);

/// <summary>
/// The window's state and commands.
/// </summary>
/// <remarks>
/// This view model does not own narration state — <see cref="NarrationCoordinator"/> does. It reads the
/// coordinator's snapshots and forwards commands to it. Keeping one authority is what prevents the UI
/// and the audio from disagreeing about where the reading is, which is the failure mode every
/// "highlight is slightly ahead of the voice" bug turns out to be.
/// </remarks>
public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ServiceRegistry _services;
    private string? _documentPath;
    private ExtractionSnapshot? _snapshot;
    private NarrationPlan? _plan;
    private PlaybackSnapshot _playback = PlaybackSnapshot.Idle;
    private IReadOnlyList<MathsRegionOverride> _overrides = [];
    private string? _overridesHash;   // the document the overrides were made in
    private string _statusMessage = "Open a PDF to begin.";
    private bool _busy;
    private int _selectedTranscriptIndex;
    private double _zoom = 1.0;
    private bool _fitWidth = true;
    private string? _selectedVoiceId;
    private double _playbackRate = 1.0;
    private FollowMode _followMode = FollowMode.Follow;
    private bool _followSuspended;
    private bool _transcriptVisible;
    private MathsReview? _mathsReview;

    public MainViewModel(ServiceRegistry services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _fitWidth = services.CurrentSettings.FitWidth;
        _zoom = ViewZoom.Clamp(services.CurrentSettings.PreferredZoom);
        _playbackRate = services.CurrentSettings.PlaybackRate;
        _followMode = services.CurrentSettings.FollowMode;
        _selectedVoiceId = ServiceRegistry.VoiceFor(services.CurrentSettings, services.SpeechProvider.ProviderId);
        _highlightAlpha = services.CurrentSettings.HighlightAlpha;
        _colourEffectIntensity = Math.Clamp(services.CurrentSettings.ColourEffectIntensity, 0, 1);
        _colourEffect = Enum.TryParse<ColourEffect>(services.CurrentSettings.ColourEffect, ignoreCase: true, out var effect)
            ? effect : ColourEffect.None;
        _highlightHighContrast = services.CurrentSettings.HighlightHighContrast;
        _transcriptVisible = services.CurrentSettings.SidebarVisible;

        foreach (string note in services.StartupNotes) Notes.Add(note);

        _services.Coordinator.SnapshotChanged += OnSnapshotChanged;
        _services.Coordinator.SegmentResolved += OnSegmentResolved;
        if (_services.MathResolver is { } maths) maths.EndpointProblem += OnMathsEndpointProblem;
        if (_services.Kokoro is { } kokoro)
        {
            kokoro.Initialised += OnKokoroDeviceSettled;
            kokoro.FellBackToCpu += OnKokoroFellBack;
        }
        _services.Coordinator.ActiveCueChanged += OnActiveCueChanged;
        _services.Coordinator.FollowRequested += OnFollowRequested;

        OpenDocument = new RelayCommand(() => OpenDocumentAsync(TopLevelProvider?.Invoke()), () => !Busy, OnCommandError);
        Play = new RelayCommand(StartReadingAsync, CanRead, OnCommandError);
        Pause = new RelayCommand(() => _services.Coordinator.PauseAsync(), () => _playback.IsAudible, OnCommandError);
        // One button: play, pause, or — while a start is still waiting for its audio — cancel that
        // start. Pausing then would not stop it: the start would play when the audio arrived.
        PlayPause = new RelayCommand(() => IsStarting
                ? _services.Coordinator.StopAsync()
                : _playback.IsAudible ? _services.Coordinator.PauseAsync() : StartReadingAsync(),
            CanRead, OnCommandError, allowWhileRunning: true);
        Next = new RelayCommand(() => { _pendingStartSegmentId = null; return _services.Coordinator.NextSegmentAsync(); }, CanRead, OnCommandError, allowWhileRunning: true);
        Previous = new RelayCommand(() => { _pendingStartSegmentId = null; return _services.Coordinator.PreviousSegmentAsync(); }, CanRead, OnCommandError, allowWhileRunning: true);
        Replay = new RelayCommand(() => { _pendingStartSegmentId = null; return _services.Coordinator.ReplaySegmentAsync(); }, CanRead, OnCommandError, allowWhileRunning: true);
        ToggleFollow = new RelayCommand(CycleFollowMode, null, OnCommandError);
        ResumeFollow = new RelayCommand(() => _services.Coordinator.ResumeFollow(), () => FollowSuspended, OnCommandError);
        ZoomIn = new RelayCommand(() => ApplyZoom(Zoom * 1.25), () => HasDocument, OnCommandError);
        ZoomOut = new RelayCommand(() => ApplyZoom(Zoom / 1.25), () => HasDocument, OnCommandError);
        Zoom200 = new RelayCommand(() => ApplyZoom(2.0), () => HasDocument, OnCommandError);
        Zoom400 = new RelayCommand(() => ApplyZoom(4.0), () => HasDocument, OnCommandError);
        Zoom800 = new RelayCommand(() => ApplyZoom(8.0), () => HasDocument, OnCommandError);
        OpenSettings = new RelayCommand(() => SettingsRequested?.Invoke(null));
        OpenMathsSettings = new RelayCommand(() => SettingsRequested?.Invoke("Maths"));
        ToggleSidebar = new RelayCommand(() => SidebarVisible = !SidebarVisible);
        _recent = services.Recent;
        CloseDocument = new RelayCommand(CloseDocumentAsync, () => HasDocument && !IsLoading, OnCommandError);
        NarrateDocument = new RelayCommand(AskToNarrateDocument, () => HasDocument && !IsLoading && _narration is null, OnCommandError);
        StopNarration = new RelayCommand(() => _narration?.Cancel(), () => _narration is not null);
        ExportAudiobook = new RelayCommand(StartAudiobookExport, () => HasDocument && !IsLoading && !_exporting, OnCommandError);
        OpenRecent = new RelayCommand<RecentDocumentView>(OpenRecentDocument);
        RemoveRecent = new RelayCommand<RecentDocumentView>(r => { _recent.Remove(r.Path); RefreshRecent(); });
        RemoveMissingRecent = new RelayCommand(() => { _recent.RemoveMissing(); RefreshRecent(); }, () => RecentDocuments.Any(r => !r.Exists));
        ClearRecent = new RelayCommand(() => { _recent.Clear(); RefreshRecent(); }, () => HasRecentDocuments);
        ShowShortcuts = new RelayCommand(() => ShortcutsRequested?.Invoke());
        ShowAbout = new RelayCommand(() => AboutRequested?.Invoke());
        GoToPage = new RelayCommand(() => GoToPageRequested?.Invoke(), () => HasDocument);
        ReloadDocument = new RelayCommand(() => _documentPath is { } open ? LoadDocumentAsync(open) : Task.CompletedTask,
            () => HasDocument && !Busy, OnCommandError);
        MarkAsMaths = new RelayCommand(() => RegionOverrideRequested?.Invoke(true), () => HasDocument && Playback.ActiveCue is not null);
        RestoreAsProse = new RelayCommand(() => RegionOverrideRequested?.Invoke(false), () => HasDocument && Playback.ActiveCue is not null);
        RefreshRecent();
        NoticeAction = new RelayCommand(() => { var action = Notice?.Action; Notice = null; action?.Invoke(); });
        DismissNotice = new RelayCommand(() => Notice = null);
        TestMathEndpoint = new RelayCommand(() => TestMathEndpointAsync(), () => !Busy, OnCommandError);
        TestVisionEndpoint = new RelayCommand(() => TestVisionEndpointAsync(), () => !Busy, OnCommandError);
        DownloadKokoroModel = new RelayCommand(() => ConfirmDownload(ModelManifest.KokoroFp16), () => !Busy && !KokoroModelInstalled, OnCommandError);
        DownloadKokoroTimedModel = new RelayCommand(() => ConfirmDownload(ModelManifest.KokoroTimed), () => !Busy && !KokoroTimedModelInstalled, OnCommandError);
        DownloadLayoutModel = new RelayCommand(() => ConfirmDownload(ModelManifest.LayoutHeronInt8), () => !Busy && !LayoutModelInstalled, OnCommandError);
        DownloadLayoutModelGpu = new RelayCommand(() => ConfirmDownload(ModelManifest.LayoutHeron), () => !Busy && !LayoutModelGpuInstalled, OnCommandError);
        // The vocabulary first: the model is of no use without it, and it is the small one.
        DownloadFormulaModel = new RelayCommand(() => ConfirmDownload(ModelManifest.FormulaModelConfig, ModelManifest.FormulaModel), () => !Busy && !FormulaModelInstalled, OnCommandError);
        RegenerateMaths = new RelayCommand(() => RegenerateMathsAsync(), () => MathsReview is { CanRegenerate: true }, OnCommandError);
        ApplyMathsEdit = new RelayCommand(() => ApplyMathsEditAsync(), () => !Busy, OnCommandError);
    }

    /// <summary>
    /// There is a document to read and another is not being opened over it. While one is loading the
    /// old document is still on screen with its narration stopped; Play, Next or a press of Space
    /// must not start it again underneath the document replacing it.
    /// </summary>
    private bool CanRead() => !IsLoading && Plan is { SegmentCount: > 0 };

    /// <summary>Raised when the PDF surface must repaint (a new highlight, a page change).</summary>
    public event Action? InvalidateSurface;

    /// <summary>
    /// Raised only after a document has loaded successfully, with its path. The window uses it to
    /// open the document in the PDF surface (the view model has no surface reference, and opening
    /// on <see cref="DocumentPath"/> change would display files that then fail extraction).
    /// </summary>
    public event Action<string>? DocumentLoaded;

    /// <summary>
    /// Supplies the window for file pickers. Set by the window; a Ctrl+O key binding has no
    /// TopLevel of its own to pass, so without this the shortcut could only tell the user to use
    /// the menu instead.
    /// </summary>
    public Func<TopLevel?>? TopLevelProvider { get; set; }

    /// <summary>Surfaces a failed command/background operation on the status line and in the notes.</summary>
    private void OnCommandError(Exception ex) => ReportFailure("Something went wrong", ex);

    /// <summary>
    /// Runs a fire-and-forget task with its failure observed and reported, instead of discarded
    /// with <c>_ =</c> where a fault would vanish (or surface as an unhandled async-void crash).
    /// </summary>
    public void RunObserved(Task task, string what)
    {
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            try { await task.ConfigureAwait(true); }
            catch (Exception ex) { ReportFailure(what, ex); }
        }
    }

    private void ReportFailure(string what, Exception ex)
        => Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = $"{what}: {ex.Message}";
            Notes.Add($"{what}: {ex.GetType().Name}: {ex.Message}");
        });

    /// <summary>Raised when the camera should follow the reading to a cue on a page.</summary>
    public event Action<FollowTarget>? CameraRequested;

    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<TranscriptEntry> Transcript { get; } = [];
    public ObservableCollection<TtsVoice> Voices { get; } = [];

    /// <summary>Pitch-preserving playback rates offered in the transport bar.</summary>
    public IReadOnlyList<double> PlaybackRates { get; } = [0.75, 1.0, 1.25, 1.5, 2.0];
    public ObservableCollection<string> AudioDevices { get; } = [];

    public ServiceRegistry Services => _services;
    public NarrationCoordinator Coordinator => _services.Coordinator;

    public string? DocumentPath
    {
        get => _documentPath;
        private set
        {
            if (!Set(ref _documentPath, value)) return;
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(DocumentName));
            RefreshCommands();   // Close, Go to page, Reload and zoom depend on a document being open
        }
    }
    public bool HasDocument => _documentPath is not null;

    /// <summary>The open document's file name, for its tab and the window title.</summary>
    public string? DocumentName => _documentPath is null ? null : Path.GetFileName(_documentPath);
    public ExtractionSnapshot? Snapshot => _snapshot;
    public NarrationPlan? Plan { get => _plan; private set { if (Set(ref _plan, value)) { OnPropertyChanged(nameof(SegmentCount)); } } }
    public int SegmentCount => Plan?.SegmentCount ?? 0;

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) RefreshCommands(); } }

    public PlaybackSnapshot Playback
    {
        get => _playback;
        private set
        {
            if (!Set(ref _playback, value)) return;
            OnPropertyChanged(nameof(IsPreparing));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(PlayButtonLabel));
            OnPropertyChanged(nameof(PlayButtonShowsPlay));
            OnPropertyChanged(nameof(PlayButtonShowsPause));
            OnPropertyChanged(nameof(PlayButtonShowsCancel));
            OnPropertyChanged(nameof(PreparationText));
        }
    }

    /// <summary>True while a start the reader asked for is waiting on a passage's audio.</summary>
    public bool IsPreparing => _playback.Preparation is not null;

    // ---- opening a document ---------------------------------------------------------------
    // What the card over the reading area shows while a document is opened. A status-bar line was
    // the only sign of a load that takes from seconds (a cached paper) to minutes (a scan to OCR),
    // under an otherwise empty or unchanged window.

    private bool _isLoading;
    private string _loadingTitle = "";
    private string _loadingStage = "";
    private string _loadingDetail = "";
    private double _loadingProgress;
    private bool _loadingIndeterminate = true;
    private long _loadingStarted;
    private DispatcherTimer? _loadingClock;

    /// <summary>True from the moment a document is chosen until it is ready to read or has failed.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set { if (Set(ref _isLoading, value)) RefreshCommands(); }
    }

    /// <summary>The file being opened, by name.</summary>
    public string LoadingTitle { get => _loadingTitle; private set => Set(ref _loadingTitle, value); }

    /// <summary>What is being done to it now, in words: "Reading the pages".</summary>
    public string LoadingStage { get => _loadingStage; private set => Set(ref _loadingStage, value); }

    /// <summary>How far that has got and how long the load has taken: "Page 17 of 27 · 12 s".</summary>
    public string LoadingDetail { get => _loadingDetail; private set => Set(ref _loadingDetail, value); }

    /// <summary>Progress through the current stage, 0 to 1, when it can be counted.</summary>
    public double LoadingProgress { get => _loadingProgress; private set => Set(ref _loadingProgress, value); }

    /// <summary>True for a stage with nothing to count.</summary>
    public bool LoadingIndeterminate { get => _loadingIndeterminate; private set => Set(ref _loadingIndeterminate, value); }

    private string _loadingCount = "";

    private void BeginLoading(string path)
    {
        LoadingTitle = Path.GetFileName(path);
        _loadingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        ReportLoading("Opening the document");
        IsLoading = true;
        // The elapsed time ticks on its own: a stage with nothing to count (a model starting up)
        // would otherwise show a card that never changes.
        _loadingClock ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshLoadingDetail());
        _loadingClock.Start();
    }

    /// <summary>Sets the stage shown on the card and the status line; <paramref name="total"/> 0 means nothing to count.</summary>
    private void ReportLoading(string stage, int done = 0, int total = 0, string unit = "page")
    {
        LoadingStage = stage;
        LoadingIndeterminate = total <= 0;
        LoadingProgress = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
        _loadingCount = total > 0 ? $"{char.ToUpperInvariant(unit[0])}{unit[1..]} {Math.Min(done, total)} of {total}" : "";
        StatusMessage = total > 0 ? $"{stage}: {unit} {Math.Min(done, total)} of {total}" : $"{stage}…";
        RefreshLoadingDetail();
    }

    private void RefreshLoadingDetail()
    {
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_loadingStarted).TotalSeconds;
        string elapsed = seconds >= 1 ? $"{seconds:F0} s" : "";
        LoadingDetail = string.Join(" · ", new[] { _loadingCount, elapsed }.Where(t => t.Length > 0));
    }

    private void EndLoading()
    {
        _loadingClock?.Stop();
        IsLoading = false;
    }

    /// <summary>
    /// The passage a reopened document resumes from, when its position was restored and it is not
    /// the first: the window shows that passage instead of the first page.
    /// </summary>
    public string? RestoredSegmentId { get; private set; }

    /// <summary>The play/pause button's caption: what pressing it will do now.</summary>
    public string PlayButtonText => IsStarting ? "⏹ Cancel" : _playback.IsAudible ? "⏸ Pause" : "▶ Play";

    /// <summary>Play: from the heading chosen in the outline if there is one, else from where reading stands.</summary>
    private Task StartReadingAsync()
    {
        if (Interlocked.Exchange(ref _pendingStartSegmentId, null) is { } segmentId)
            return _services.Coordinator.GoToSegmentAsync(segmentId);
        return _services.Coordinator.PlayAsync();
    }

    /// <summary>The play button's words, beside its icon: Play, Pause, or Cancel while a start is waiting.</summary>
    public string PlayButtonLabel => IsStarting ? "Cancel" : _playback.IsAudible ? "Pause" : "Play";

    public bool PlayButtonShowsPlay => !IsStarting && !_playback.IsAudible;

    public bool PlayButtonShowsPause => !IsStarting && _playback.IsAudible;

    public bool PlayButtonShowsCancel => IsStarting;

    /// <summary>A start (Play, a seek) is under way and nothing is audible yet: pressing the button cancels it.</summary>
    private bool IsStarting => _playback.Preparation is not null || _playback.State is PlaybackState.Seeking or PlaybackState.Preparing;

    /// <summary>Headline of the on-page preparing card: which passage, and what it is waiting on.</summary>
    public string? PreparationText => _playback.Preparation is { } p
        ? $"Preparing passage {p.SegmentIndex + 1} of {_playback.SegmentCount}\n{p.Describe()}"
        : null;

    public int SelectedTranscriptIndex
    {
        get => _selectedTranscriptIndex;
        set
        {
            if (!Set(ref _selectedTranscriptIndex, value)) return;
            if (value >= 0 && value < Transcript.Count)
            {
                _pendingStartSegmentId = null;
                RunObserved(Coordinator.GoToSegmentAsync(Transcript[value].SegmentId), "Could not move to that passage");
            }
        }
    }

    public double Zoom
    {
        get => _zoom;
        set { if (Set(ref _zoom, ViewZoom.Clamp(value))) OnPropertyChanged(nameof(ZoomPercent)); }
    }

    /// <summary>
    /// True while the page's width is fitted to the reading area, through page turns and resizes
    /// (the View menu's tick). Cleared by any zoom the reader chooses; clearing it by hand keeps
    /// the present magnification. Remembered between sessions.
    /// </summary>
    public bool FitWidth
    {
        get => _fitWidth;
        set { if (Set(ref _fitWidth, value)) ScheduleZoomPreferenceSave(); }
    }

    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            if (!Set(ref _playbackRate, value)) return;
            Coordinator.SetPlaybackRate(value);
            PersistPreference(s => s with { PlaybackRate = value });
        }
    }

    public FollowMode FollowMode
    {
        get => _followMode;
        set
        {
            if (!Set(ref _followMode, value)) return;
            OnPropertyChanged(nameof(FollowLabel));
            OnPropertyChanged(nameof(IsFollowing));
            Coordinator.SetFollowMode(value);
            PersistPreference(s => s with { FollowMode = value });
        }
    }

    /// <summary>The follow toggle's caption.</summary>
    public string FollowLabel => _followMode == FollowMode.Off ? "Follow: off" : "Follow: on";

    /// <summary>The view follows the reading: the follow button is shown on (accent).</summary>
    /// <summary>Following on or off; settable for View ▸ Follow the reading, a checkbox item.</summary>
    public bool IsFollowing
    {
        get => _followMode != FollowMode.Off;
        set { if (value != IsFollowing) CycleFollowMode(); }
    }

    public bool FollowSuspended { get => _followSuspended; private set => Set(ref _followSuspended, value); }

    /// <summary>
    /// The sidebar: outline, script, maths review and notes. Hidden unless asked for (a listener
    /// needs none of it), and remembered.
    /// </summary>
    public bool SidebarVisible
    {
        get => _transcriptVisible;
        set { if (Set(ref _transcriptVisible, value)) PersistPreference(s => s with { SidebarVisible = value }); }
    }

    public RelayCommand ToggleSidebar { get; }

    // ---- outline ------------------------------------------------------------------------------

    /// <summary>The document's own outline (its PDF bookmarks); empty when it has none.</summary>
    public ObservableCollection<OutlineItem> Outline { get; } = [];

    private List<OutlineItem> _outlineInOrder = [];

    public bool HasOutline => Outline.Count > 0;

    /// <summary>Shows a newly opened document's outline. Called by the window once the page source has it.</summary>
    public void ShowOutline(IReadOnlyList<OutlineEntry> entries)
    {
        var inOrder = new List<OutlineItem>();
        var items = OutlineItem.Build(entries, inOrder);
        Outline.Clear();
        foreach (var item in items) Outline.Add(item);
        _outlineInOrder = inOrder;
        OnPropertyChanged(nameof(HasOutline));
        ShowingPage(_shownPage);
    }

    /// <summary>The view should show this page from its top (an outline heading was chosen).</summary>
    public event Action<int>? PageRequested;

    private string? _pendingStartSegmentId;

    /// <summary>
    /// A heading was chosen in the outline. While reading, the reading moves there, as a click on a
    /// passage does. Otherwise the page is shown and the next Play starts there: choosing a heading
    /// is how a reader says where to begin, not that it should begin now.
    /// </summary>
    public void OpenOutlineItem(OutlineItem item)
    {
        if (item.Page is not { } page) { StatusMessage = $"“{item.Title}” does not say which page it is on."; return; }
        PageRequested?.Invoke(page);
        if (FirstPassageFrom(page) is not { } segmentId) return;
        if (_playback.IsAudible)
        {
            _pendingStartSegmentId = null;
            RunObserved(Coordinator.GoToSegmentAsync(segmentId), "Could not move to that heading");
            return;
        }
        _pendingStartSegmentId = segmentId;
        StatusMessage = $"Play reads from “{item.Title}”.";
    }

    /// <summary>The first passage on <paramref name="page"/>, else the first after it.</summary>
    private string? FirstPassageFrom(int page)
    {
        if (Plan is not { } plan) return null;
        foreach (var segment in plan.Segments)
        {
            int? first = segment.ReadingUnitIds.Select(id => plan.Unit(id)?.PageIndex).Where(p => p is not null).Min();
            if (first >= page) return segment.Id;
        }
        return null;
    }

    private int _shownPage;
    private string? _currentSection;

    /// <summary>
    /// The heading the page shown falls under, for the status bar: the heading and its parent, as
    /// RailReader2's breadcrumb. A single top-level heading is the paper's own title and is left out.
    /// </summary>
    public string? CurrentSection { get => _currentSection; private set { if (Set(ref _currentSection, value)) OnPropertyChanged(nameof(HasCurrentSection)); } }

    /// <summary>The full path of the current heading, for the breadcrumb's tooltip.</summary>
    public string? CurrentSectionPath { get => _currentSectionPath; private set => Set(ref _currentSectionPath, value); }

    private string? _currentSectionPath;

    public bool HasCurrentSection => _currentSection is not null;

    /// <summary>The view now shows <paramref name="page"/>: the status bar names its section.</summary>
    public void ShowingPage(int page)
    {
        _shownPage = page;
        var heading = _outlineInOrder.LastOrDefault(i => i.Page is { } p && p <= page);
        CurrentSectionPath = heading?.Path;
        var trail = new List<string>();
        for (var h = heading; h is not null; h = h.Parent)
            if (!(h.Parent is null && Outline.Count == 1 && h != heading)) trail.Insert(0, h.Title);
        CurrentSection = trail.Count switch
        {
            0 => null,
            1 or 2 => string.Join(" › ", trail),
            _ => "… › " + string.Join(" › ", trail.TakeLast(2)),
        };
    }

    // ---- notices --------------------------------------------------------------------------------

    private StatusNotice? _notice;
    private string? _lastNoticedError;

    /// <summary>A notice that takes over the status bar until dismissed or acted on, as RailReader2's toasts.</summary>
    public StatusNotice? Notice { get => _notice; private set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }

    public bool HasNotice => _notice is not null;

    public void ShowNotice(string text, string? actionLabel = null, Action? action = null)
        => Notice = new StatusNotice(text, actionLabel, action);

    public RelayCommand NoticeAction { get; }

    public RelayCommand DismissNotice { get; }

    /// <summary>The application should restart, keeping the open document (the window does it).</summary>
    public event Action? RestartRequested;

    // ---- closing, recent documents, help ----------------------------------------------------

    public RelayCommand CloseDocument { get; }

    /// <summary>The document was closed: the window releases its pages and shows the welcome screen.</summary>
    public event Action? DocumentClosed;

    /// <summary>
    /// Closes the document (File ▸ Close, Ctrl+W): where the reader stood is saved first, reading
    /// stops, and the welcome screen comes back.
    /// </summary>
    private async Task CloseDocumentAsync()
    {
        if (!HasDocument || IsLoading) return;
        _pendingStartSegmentId = null;
        await SavePositionAsync();
        await Coordinator.StopAsync();
        _snapshot = null;
        Plan = null;
        DocumentPath = null;
        _documentHash = null;
        RestoredSegmentId = null;
        RebuildTranscript();
        Outline.Clear();
        _outlineInOrder = [];
        OnPropertyChanged(nameof(HasOutline));
        CurrentSection = null;
        CurrentSectionPath = null;
        MathsReview = null;
        StatusMessage = "Open a PDF to begin.";
        RefreshCommands();
        RefreshRecent();
        DocumentClosed?.Invoke();
    }

    private readonly RecentDocumentsStore _recent;

    /// <summary>Documents opened before, newest first, for the welcome screen and File ▸ Open Recent.</summary>
    public ObservableCollection<RecentDocumentView> RecentDocuments { get; } = [];

    public bool HasRecentDocuments => RecentDocuments.Count > 0;

    public RelayCommand<RecentDocumentView> OpenRecent { get; }

    public RelayCommand<RecentDocumentView> RemoveRecent { get; }

    /// <summary>Forgets the recent documents whose files are not there now.</summary>
    public RelayCommand RemoveMissingRecent { get; }

    /// <summary>Forgets every recent document.</summary>
    public RelayCommand ClearRecent { get; }

    public void RefreshRecent()
    {
        RecentDocuments.Clear();
        foreach (var d in _recent.Load()) RecentDocuments.Add(new RecentDocumentView(d, _recent.ThumbnailPath(d.Path)));
        OnPropertyChanged(nameof(HasRecentDocuments));
        // Null until the constructor has built them (the first refresh runs before).
        RemoveMissingRecent?.RaiseCanExecuteChanged();
        ClearRecent?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Opens a recent document; one not found (moved, renamed, on a drive not mounted) is not an
    /// error to report but a question: the notice offers to take it off the list.
    /// </summary>
    private void OpenRecentDocument(RecentDocumentView recent)
    {
        if (!File.Exists(recent.Path))
        {
            RefreshRecent();   // it may have gone since the list was drawn
            ShowNotice($"{recent.Name} was not found at {recent.Path}.", "Remove from list", () => RemoveRecent.Execute(recent));
            return;
        }
        if (_documentPath is { } open && string.Equals(Path.GetFullPath(open), recent.Path, StringComparison.Ordinal))
        {
            StatusMessage = $"{recent.Name} is already open (File ▸ Reload opens it again).";
            return;
        }
        RunObserved(LoadDocumentAsync(recent.Path), "Could not open that document");
    }

    /// <summary>Where the open document's first-page thumbnail goes; null when it already exists.</summary>
    public string? PendingThumbnailPath
        => _documentPath is { } path && _recent.ThumbnailPath(path) is { } thumb && !File.Exists(thumb) ? thumb : null;

    public RelayCommand ShowShortcuts { get; }
    public RelayCommand ShowAbout { get; }
    public RelayCommand GoToPage { get; }
    public event Action? ShortcutsRequested;
    public event Action? AboutRequested;
    public event Action? GoToPageRequested;

    private ColourEffect _colourEffect;

    /// <summary>RailReader's colour filters, applied to the page raster, with their display names.</summary>
    public IReadOnlyList<ColourEffectOption> ColourEffects { get; } =
        ColourEffectExtensions.All.Select(e => new ColourEffectOption(e.Effect, e.Effect.DisplayName(), e.Description)).ToList();

    private double _colourEffectIntensity = 1.0;

    /// <summary>True when a colour filter is on, so its strength can be offered.</summary>
    public bool HasColourEffect => _colourEffect != ColourEffect.None;

    /// <summary>Strength of the colour filter, 0 to 1, as RailReader's intensity.</summary>
    public double ColourEffectIntensity
    {
        get => _colourEffectIntensity;
        set
        {
            double clamped = Math.Clamp(value, 0, 1);
            if (!Set(ref _colourEffectIntensity, clamped)) return;
            OnPropertyChanged(nameof(ColourEffectPercent));
            PersistPreference(s => s with { ColourEffectIntensity = clamped });
        }
    }

    public int ColourEffectPercent => (int)Math.Round(_colourEffectIntensity * 100);

    /// <summary>The View menu's colour-filter entry, naming the filter in use.</summary>
    public string ColourFilterMenuHeader => $"_Colour filter: {_colourEffect.DisplayName()}";

    /// <summary>
    /// Moves to the next colour filter, wrapping round to none: RailReader2's C key. Says which one
    /// is now on, since the change can be subtle (amber at low intensity).
    /// </summary>
    public void CycleColourEffect()
    {
        var all = ColourEffects.Select(o => o.Effect).ToList();
        ColourEffect = all[(all.IndexOf(_colourEffect) + 1) % all.Count];
        StatusMessage = $"Colour filter: {_colourEffect.DisplayName()} (C for the next)";
    }

    public ColourEffectOption? SelectedColourEffect
    {
        get => ColourEffects.FirstOrDefault(o => o.Effect == _colourEffect);
        set { if (value is not null) ColourEffect = value.Effect; }
    }

    public ColourEffect ColourEffect
    {
        get => _colourEffect;
        set
        {
            if (!Set(ref _colourEffect, value)) return;
            OnPropertyChanged(nameof(SelectedColourEffect));
            OnPropertyChanged(nameof(ColourFilterMenuHeader));
            OnPropertyChanged(nameof(HasColourEffect));
            PersistPreference(s => s with { ColourEffect = value.ToString() });
        }
    }

    public TtsVoice? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (!Set(ref _selectedVoice, value)) return;
            string? id = value?.Id;
            if (Set(ref _selectedVoiceId, id) is false || id is null) return;
            // A voice change invalidates every prepared clip: that audio was made by another voice.
            // The coordinator advances its generation token so a synthesis still in flight for the old
            // voice is discarded rather than played over the new one.
            RunObserved(Coordinator.SetVoiceAsync(id), "Could not change voice");
            // The Kokoro and Phonon voices are free preferences, kept apart so switching engines keeps
            // both. The ZONOS3 speaker id also decides whether that provider is registered at all, so
            // it stays a deliberate settings-file choice.
            if (!_choosingVoiceForReader && _voiceProviderId == "kokoro")
                PersistPreference(s => s with { KokoroVoiceId = id });
            if (!_choosingVoiceForReader && _voiceProviderId == PhononTtsProvider.Id)
                PersistPreference(s => s with { PhononVoiceId = id });
        }
    }

    private TtsVoice? _selectedVoice;
    private string? _voiceProviderId;

    public string? SelectedVoiceId
    {
        get => _selectedVoiceId;
        private set => Set(ref _selectedVoiceId, value);
    }

    public MathsReview? MathsReview { get => _mathsReview; private set => Set(ref _mathsReview, value); }

    public string? EditedSpokenText { get; set; }

    /// <summary>Highlight opacity, 0-255. Exposed because contrast needs vary.</summary>
    public byte HighlightAlpha { get => _highlightAlpha; set { if (Set(ref _highlightAlpha, value)) OnPropertyChanged(nameof(HighlightAlpha)); } }
    private byte _highlightAlpha = 72;

    public bool HighlightHighContrast { get => _highlightHighContrast; set => Set(ref _highlightHighContrast, value); }
    private bool _highlightHighContrast;

    /// <summary>Sets the status line. Available to the window for one-off messages.</summary>
    public void Status(string message) => StatusMessage = message;

    public string MathEndpointSummary => EndpointSummary(_services.CurrentSettings.MathLlmProvider,
        _services.CurrentSettings.MathLlmBaseUrl, _services.CurrentSettings.MathLlmModel);

    public string VisionEndpointSummary => EndpointSummary(_services.CurrentSettings.VlmProvider,
        _services.CurrentSettings.VlmBaseUrl, _services.CurrentSettings.VlmModel);

    /// <summary>Claude needs no address (ADR-0010), so choosing it is itself the configuration.</summary>
    private static string EndpointSummary(LlmProvider provider, string? url, string? model) => provider switch
    {
        LlmProvider.Anthropic => $"Claude (Anthropic) / {(string.IsNullOrWhiteSpace(model) ? RailNarrator.Inference.ClaudeEndpointSettings.DefaultModel : model)}"
                                 + (url is { Length: > 0 } ? $" at {url}" : ""),
        _ => url is { Length: > 0 } ? $"{url} / {model}" : "not configured",
    };

    /// <summary>Whether display equations are transcribed by the formula model (ADR-0011).</summary>
    public string FormulaModelSummary => _services.FormulaModel is not null
        ? "PP-FormulaNet_plus-M, on the CPU"
        : _services.CurrentSettings.UseFormulaModel ? "not installed" : "switched off";

    /// <summary>Kokoro has loaded: say where it runs, and why not on the GPU when that was wanted.</summary>
    private void OnKokoroDeviceSettled() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(SpeechProviderSummary));
        if (_services.Kokoro?.GpuFallbackReason is { } reason) Notes.Add(reason);
    });

    /// <summary>A GPU clip was not real audio: synthesis moved to the CPU for the rest of the run.</summary>
    private void OnKokoroFellBack(string reason) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(SpeechProviderSummary));
        Notes.Add($"Kokoro moved to the CPU: {reason}");
    });

    public string SpeechProviderSummary
        => string.Join(", ", _services.SpeechProviders.RegisteredIds.Select(id =>
            (id == "kokoro" && _services.Kokoro is { } k
                ? k.GpuDevice is { } gpu ? $"kokoro on {gpu} (WebGPU)" : "kokoro on the CPU"
                : id == PhononTtsProvider.Id ? "phonon on the CPU" : id)
            + (id == _services.SpeechProvider.ProviderId ? " (in use)" : "")));

    /// <summary>The layout analyzer and its device. Fixed for the run: the backend is built once at startup.</summary>
    public string LayoutSummary => _services.LayoutSummary;

    public long CacheBytes => _services.AssetStore.TotalBytes() + _services.CachedExtraction.TotalBytes();
    private string _timingSummary = "Sentence highlighting until word alignment is available.";
    public string TimingSummary { get => _timingSummary; private set => Set(ref _timingSummary, value); }

    // ---- commands --------------------------------------------------------------------

    public RelayCommand OpenDocument { get; }
    public RelayCommand Play { get; }
    public RelayCommand Pause { get; }
    public RelayCommand PlayPause { get; }
    public RelayCommand Next { get; }
    public RelayCommand Previous { get; }
    public RelayCommand Replay { get; }
    public RelayCommand ToggleFollow { get; }
    public RelayCommand ResumeFollow { get; }
    public RelayCommand ZoomIn { get; }
    public RelayCommand ZoomOut { get; }
    public RelayCommand Zoom200 { get; }
    public RelayCommand Zoom400 { get; }
    public RelayCommand Zoom800 { get; }
    /// <summary>Opens the Settings dialogue (File ▸ Settings…, Ctrl+,). The window handles it.</summary>
    public RelayCommand OpenSettings { get; }

    public event Action<string?>? SettingsRequested;

    /// <summary>Opens the Settings dialogue on its Maths page, where the endpoints and keys are.</summary>
    public RelayCommand OpenMathsSettings { get; }

    public RelayCommand TestMathEndpoint { get; }
    public RelayCommand TestVisionEndpoint { get; }
    public RelayCommand DownloadKokoroModel { get; }

    public RelayCommand DownloadKokoroTimedModel { get; }
    /// <summary>Docling Heron INT8, the layout model on the CPU.</summary>
    public RelayCommand DownloadLayoutModel { get; }

    /// <summary>Docling Heron FP32, the layout model on a GPU.</summary>
    public RelayCommand DownloadLayoutModelGpu { get; }

    /// <summary>PP-FormulaNet_plus-M and its vocabulary (ADR-0011).</summary>
    public RelayCommand DownloadFormulaModel { get; }

    // Whether each model is already where the application looks for it. Only a file's presence is
    // checked (hashing hundreds of MB on the UI thread is not acceptable); the download verifies it.
    // A model downloaded in this session counts as installed although it is used after a restart.

    private (bool, bool, bool, bool, bool)? _modelsInstalled;

    private bool KokoroModelInstalled
        => File.Exists(_services.CurrentSettings.KokoroModelPath ?? _services.Models.PathFor(ModelManifest.KokoroFp16));

    private bool KokoroTimedModelInstalled => File.Exists(_services.Models.PathFor(ModelManifest.KokoroTimed));

    private bool LayoutModelInstalled => LayoutFileInstalled(ModelManifest.LayoutHeronInt8);

    private bool LayoutModelGpuInstalled => LayoutFileInstalled(ModelManifest.LayoutHeron);

    /// <summary>The layout resolver also reads RailReader2's models directory (ADR-0008).</summary>
    private bool LayoutFileInstalled(ModelAsset asset)
        => File.Exists(_services.Models.PathFor(asset)) || File.Exists(Path.Combine(XdgPaths.RailReader2Models, asset.FileName));

    private bool FormulaModelInstalled
        => File.Exists(_services.CurrentSettings.FormulaModelPath ?? _services.Models.PathFor(ModelManifest.FormulaModel))
        && File.Exists(_services.CurrentSettings.FormulaModelConfigPath ?? _services.Models.PathFor(ModelManifest.FormulaModelConfig));

    private static string ModelMenuHeader(bool installed, string download, string name)
        => installed ? $"{name}: installed" : download;

    // Short, as the menu has a maximum width and clips a longer header; the full name is in the tooltip.
    public string DownloadKokoroModelHeader => ModelMenuHeader(KokoroModelInstalled,
        $"Download _Kokoro speech model ({ModelManifest.KokoroFp16.HumanSize})…", "Kokoro speech model");

    public string DownloadKokoroTimedModelHeader => ModelMenuHeader(KokoroTimedModelInstalled,
        $"Download Kokoro with _word timing ({ModelManifest.KokoroTimed.HumanSize})…", "Kokoro with word timing");

    public string DownloadLayoutModelHeader => ModelMenuHeader(LayoutModelInstalled,
        $"Download _layout model ({ModelManifest.LayoutHeronInt8.HumanSize})…", "Layout model");

    public string DownloadLayoutModelGpuHeader => ModelMenuHeader(LayoutModelGpuInstalled,
        $"Download layout model for the _GPU ({ModelManifest.LayoutHeron.HumanSize})…", "Layout model for the GPU");

    public string DownloadFormulaModelHeader => ModelMenuHeader(FormulaModelInstalled,
        $"Download _formula model ({ModelManifest.FormulaModel.HumanSize})…", "Formula model");

    /// <summary>Re-reads which models are installed, for the Models menu (called as the menu opens and after a download).</summary>
    public void RefreshModelMenu()
    {
        // Only on a change: a header notified while the menu opens is laid out again after the popup
        // has taken its width, and comes out clipped.
        var installed = (KokoroModelInstalled, KokoroTimedModelInstalled, LayoutModelInstalled, LayoutModelGpuInstalled, FormulaModelInstalled);
        if (installed == _modelsInstalled) return;
        _modelsInstalled = installed;
        OnPropertyChanged(nameof(DownloadKokoroModelHeader));
        OnPropertyChanged(nameof(DownloadKokoroTimedModelHeader));
        OnPropertyChanged(nameof(DownloadLayoutModelHeader));
        OnPropertyChanged(nameof(DownloadLayoutModelGpuHeader));
        OnPropertyChanged(nameof(DownloadFormulaModelHeader));
        DownloadKokoroModel.RaiseCanExecuteChanged();
        DownloadKokoroTimedModel.RaiseCanExecuteChanged();
        DownloadLayoutModel.RaiseCanExecuteChanged();
        DownloadLayoutModelGpu.RaiseCanExecuteChanged();
        DownloadFormulaModel.RaiseCanExecuteChanged();
    }
    public RelayCommand RegenerateMaths { get; }
    public RelayCommand ApplyMathsEdit { get; }

    /// <summary>File ▸ Reload: opens the open document again, which applies maths region corrections.</summary>
    public RelayCommand ReloadDocument { get; }

    /// <summary>Maths ▸ Mark this passage as maths / as prose; the window resolves the passage's regions.</summary>
    public RelayCommand MarkAsMaths { get; }
    public RelayCommand RestoreAsProse { get; }

    /// <summary>Raised with true to mark the passage being read as maths, false to restore it as prose.</summary>
    public event Action<bool>? RegionOverrideRequested;

    /// <summary>Opens a file picker and loads the chosen PDF.</summary>
    public async Task OpenDocumentAsync(TopLevel? topLevel = null)
    {
        if (topLevel is null) { StatusMessage = "Use File > Open to choose a document."; return; }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PDF to narrate",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF documents") { Patterns = ["*.pdf"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        await LoadDocumentAsync(file.Path.LocalPath);
    }

    /// <summary>
    /// Loads, extracts and plans a document. Every step reports what it is doing, because a large
    /// scanned PDF can take long enough that a silent spinner reads as a hang.
    /// </summary>
    /// <remarks>
    /// The work runs on the thread pool; this method only awaits it and reports. Hashing the file,
    /// finding the reading order, planning and starting the speech model used to run on the UI
    /// thread between awaits, which froze the window for a couple of seconds at the end of every
    /// load — exactly when a progress display has to keep moving.
    /// </remarks>
    public async Task LoadDocumentAsync(string path)
    {
        if (!File.Exists(path)) { StatusMessage = $"No such file: {path}"; return; }
        // Re-entrancy: argv/menu/keyboard/drop can all call this directly. Said, not swallowed: a
        // dropped file or a recent document that silently does nothing looks broken.
        if (Busy) { StatusMessage = $"Wait for the current task to finish, then open {Path.GetFileName(path)} again."; return; }
        _pendingStartSegmentId = null;

        // Build into locals and commit DocumentPath/_snapshot/Plan together only on success. A
        // failed open (corrupt PDF, empty extraction) then leaves the previous document fully
        // intact, instead of a half-state where the new path was paired with the old plan — which
        // also corrupted the saved reading position on exit.
        string? previousPath = DocumentPath;
        bool committed = false;
        SavedNarration? pendingNarration = null;   // disposed in finally unless the load commits it
        Busy = true;
        BeginLoading(path);
        try
        {
            // The document being replaced keeps its place, even if the new one fails to open.
            await SavePositionAsync();
            await Coordinator.StopAsync();

            // The speech model takes a second or two to start. It starts now, beside the extraction,
            // instead of on the UI thread once everything else is done.
            var voices = Task.Run(ListVoicesAsync);
            // Observed even when the load ends early, so a failure there is not left unobserved.
            _ = voices.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            string hash = await Task.Run(() => CacheKey.Sha256HexOfFile(path));
            var cachedPlan = await _services.PlanStore.TryGetPlanAsync(hash);
            // A narration saved beside the document is the script and the audio already made: it is
            // read from, and neither the maths model nor the speech engine is asked.
            var (saved, savedProblem) = await Task.Run(() =>
            {
                var opened = SavedNarration.TryOpen(SavedNarration.PathFor(path), hash, out string? problem);
                return (opened, problem);
            });
            pendingNarration = saved;
            if (savedProblem is not null)
                Notes.Add($"The saved narration beside this document was not used: {savedProblem}.");

            await Task.Run(() => _services.Extraction.AttachDocument(path));
            var request = _services.CreateExtractionRequest(path);
            ReportLoading("Reading the pages");
            // Progress is posted, so a report can land after extraction has returned — with a cache
            // hit, after "Ready" — and overwrite the status. Reports are ignored once it is done.
            bool extracting = true;
            var snapshot = await _services.CachedExtraction.ExtractAsync(request,
                new Progress<ExtractionProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    if (extracting) ReportLoading(ExtractionStage(p.Stage), p.PagesDone, p.PagesTotal);
                })));
            extracting = false;

            foreach (string warning in snapshot.AllWarnings().Take(8))
                if (!Notes.Contains(warning)) Notes.Add(warning);

            if (snapshot.Pages.All(p => p.Coverage.CharCount == 0))
            {
                StatusMessage = "This document has no readable text. Enable OCR in settings and try again.";
                return;
            }

            ReportLoading("Finding the reading order");
            // Region corrections belong to the document they were made in; reloading it applies them.
            var overrides = hash == _overridesHash ? _overrides : [];
            var builder = _services.CreateUnitBuilder(overrides);
            // A saved narration brings its plan, units and all: nothing is built to be thrown away.
            var (units, diagnostics) = saved is not null
                ? (saved.Plan.Units, (IReadOnlyList<string>)[])
                : await Task.Run(() =>
                {
                    var built = builder.Build(snapshot, out var notes);
                    return (built, notes);
                });
            foreach (string d in diagnostics) if (!Notes.Contains(d)) Notes.Add(d);

            if (units.Count == 0)
            {
                StatusMessage = "Nothing narratable was found. Check the content filters in settings.";
                return;
            }

            string planning = units.Any(u => u.IsMaths)
                ? "Preparing narration, including mathematical expressions"
                : "Preparing narration";
            ReportLoading(planning);

            var planner = _services.CreatePlanner();
            var filters = _services.CurrentSettings.Filters;
            bool planningNow = true;
            var planProgress = new Progress<PlanningProgress>(p =>
            {
                if (planningNow) ReportLoading(planning, p.UnitsDone, p.UnitsTotal, "passage");
            });
            var (plan, reuseCached) = await Task.Run(async () =>
            {
                // A cached plan is reusable only if it was built from the inputs in force now, not
                // merely from the same file: changed filters, maths mode or region overrides rebuild.
                if (saved is not null) return (saved.Plan, true);
                string fingerprint = NarrationPlanner.ComputeInputFingerprint(planner.Options, filters, units);
                if (cachedPlan is { } c && c.Matches(snapshot.Identity, fingerprint)) return (c, true);
                var built = await planner.BuildPlanAsync(snapshot, units, filters, progress: planProgress).ConfigureAwait(false);
                return (built, false);
            });
            planningNow = false;

            if (!reuseCached)
                await _services.PlanStore.StorePlanAsync(plan);

            ReportLoading("Starting the voice");
            var position = await _services.PlanStore.TryGetPositionAsync(hash);
            (string? ProviderId, IReadOnlyList<TtsVoice> Voices, IReadOnlyList<string> Devices) available = (null, [], []);
            try { available = await voices; }
            catch (Exception ex)
            {
                // The document can still be opened and browsed; pressing Play reports the provider's error.
                Notes.Add($"The speech voices could not be listed: {ex.GetType().Name}: {ex.Message}");
            }

            // ---- commit point: everything below describes the new document ----
            _snapshot = snapshot;
            Plan = plan;
            DocumentPath = path;
            _documentHash = hash;
            _overrides = overrides;
            _overridesHash = hash;
            var replaced = _savedNarration;
            _savedNarration = saved;
            pendingNarration = null;
            committed = true;
            OnPropertyChanged(nameof(PlaysSavedNarration));

            RebuildTranscript();
            ShowVoices(available);

            // A document last read with another engine saved that engine's voice: keep the voice in
            // use instead, or every passage would be spoken in the fallback with a warning.
            if (position is { VoiceId: { } savedVoice } && available.Voices.Count > 0
                && available.Voices.All(v => v.Id != savedVoice))
                position = position with { VoiceId = null };

            await Coordinator.LoadPlanAsync(plan, position, recording: saved);
            // Only now: the coordinator has let go of the narration of the document before.
            replaced?.Dispose();
            if (position is not null) ShowRestoredPreferences(position);
            // The starting passage is where the reader already is; only a move is worth saving.
            _lastSavedSegmentId = Coordinator.Snapshot.SegmentId;
            RestoredSegmentId = position is { SegmentId: not null } && Coordinator.Snapshot.SegmentIndex > 0
                ? Coordinator.Snapshot.SegmentId : null;

            StatusMessage = position is { SegmentId: not null }
                ? $"Ready. Restored your position ({plan.SegmentCount} passages)."
                : $"Ready. {plan.SegmentCount} passages to read.";
            if (saved is not null)
            {
                StatusMessage += $" Playing the narration saved with the document ({saved.VoiceDescription}).";
                Notes.Add($"Playing the narration saved with the document: {saved.Manifest.Passages} passages, " +
                          $"{HeadlessNarration.Minutes(saved.Manifest.Seconds)}, voice {saved.VoiceDescription}. " +
                          "No language model or speech engine is used; choosing another voice goes back to live speech.");
            }
            OnPropertyChanged(nameof(MathEndpointSummary));
            OnPropertyChanged(nameof(CacheBytes));
            _recent.Opened(path, snapshot.Pages.Count);
            if (position is not null && plan.SegmentCount > 0)
                _recent.Progressed(path, (double)Coordinator.Snapshot.SegmentIndex / plan.SegmentCount);
            RefreshRecent();
            DocumentLoaded?.Invoke(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the document: {ex.Message}";
            Notes.Add($"Open failed for '{path}': {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!committed && previousPath is not null)
            {
                // Undo the AttachDocument side effect so crop rendering still targets the
                // document that is actually loaded.
                try { _services.Extraction.AttachDocument(previousPath); }
                catch (Exception ex) { Notes.Add($"Could not restore the previous document for display: {ex.Message}"); }
            }
            pendingNarration?.Dispose();
            EndLoading();
            Busy = false;
            InvalidateSurface?.Invoke();
        }
    }

    // ---- a narration saved with the document, and an audiobook from it ----------------------

    private SavedNarration? _savedNarration;
    private CancellationTokenSource? _narration;
    private bool _exporting;

    /// <summary>True when the open document is read from a narration saved beside it.</summary>
    public bool PlaysSavedNarration => _savedNarration is not null;

    /// <summary>File ▸ Save narration with document…</summary>
    public RelayCommand NarrateDocument { get; }

    public RelayCommand StopNarration { get; }

    /// <summary>File ▸ Export audiobook…</summary>
    public RelayCommand ExportAudiobook { get; }

    /// <summary>
    /// Says what narrating the whole document will do and waits for the reader to start it: it
    /// takes as long as listening would on a slow engine, and every maths passage is sent to the
    /// maths model.
    /// </summary>
    private void AskToNarrateDocument()
    {
        if (Plan is not { } plan || DocumentPath is null) return;
        int maths = plan.Segments.Count(s => s.Provenance == ScriptProvenance.PendingInterpretation);
        string mathsNote = maths == 0 ? "" : _services.MathInterpreter is null
            ? $" {maths} maths passages will be read as notation: no maths model is set up."
            : $" {maths} maths passages go to the maths model.";
        ShowNotice($"Narrate all {plan.SegmentCount} passages and save them beside the document?{mathsNote}",
            "Narrate", () => _ = NarrateDocumentAsync(plan, DocumentPath));
    }

    private async Task NarrateDocumentAsync(NarrationPlan plan, string documentPath, bool allowUninterpreted = false)
    {
        if (_narration is not null) return;
        using var cts = new CancellationTokenSource();
        _narration = cts;
        NarrateDocument.RaiseCanExecuteChanged();
        StopNarration.RaiseCanExecuteChanged();
        string target = SavedNarration.PathFor(documentPath);
        long lastShown = 0;
        ShowNotice($"Narrating the document: starting ({plan.SegmentCount} passages).", "Stop", () => cts.Cancel());
        try
        {
            var progress = new Progress<NarrationProgress>(p =>
            {
                // Once a second at most: a notice for every passage would flicker.
                if (cts.IsCancellationRequested || (p.Done != p.Total && Environment.TickCount64 - lastShown < 1000)) return;
                lastShown = Environment.TickCount64;
                ShowNotice($"Narrating the document: {p.Done} of {p.Total} passages, {HeadlessNarration.Minutes(p.AudioSeconds)} of audio.",
                    "Stop", () => cts.Cancel());
            });
            var engines = _services.NarrationEngines(Coordinator.Options.VoiceId);
            var manifest = await Task.Run(() => NarrationJob.RunAsync(plan, Path.GetFileName(documentPath), target, engines, progress, cts.Token, allowUninterpreted));

            int uninterpreted = manifest.ByRule + manifest.Unread + manifest.AsPrinted;
            string maths = uninterpreted == 0 ? "" : $" {uninterpreted} maths passages were not interpreted (see Notes).";
            Notes.Add(HeadlessNarration.Summary(manifest, target));
            if (DocumentPath == documentPath)
                ShowNotice($"Narration saved beside the document ({HeadlessNarration.Minutes(manifest.Seconds)}, {HeadlessNarration.Megabytes(target)}).{maths}",
                    "Reload to play from it", () => ReloadDocument.Execute(null));
            else
                ShowNotice($"Narration saved beside {Path.GetFileName(documentPath)}.{maths}");
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Narration stopped. Nothing was saved; what was spoken so far is cached and will not be spoken again.");
        }
        catch (MathsNotInterpretedException ex)
        {
            // The reader decides: a narration with its maths as notation is still a narration.
            Notes.Add($"Narrating '{documentPath}' stopped: {ex.Message}");
            ShowNotice(ex.Message, "Narrate anyway", () => _ = NarrateDocumentAsync(plan, documentPath, allowUninterpreted: true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Notes.Add($"Narrating '{documentPath}' failed: {ex.Message}");
            ShowNotice($"The document could not be narrated: {ex.Message}");
        }
        finally
        {
            _narration = null;
            NarrateDocument.RaiseCanExecuteChanged();
            StopNarration.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Writes the saved narration as an M4B beside the document, or says what is missing.</summary>
    private void StartAudiobookExport()
    {
        if (DocumentPath is not { } documentPath || _documentHash is not { } hash) return;
        _ = ExportAudiobookAsync(documentPath, hash);
    }

    private async Task ExportAudiobookAsync(string documentPath, string hash)
    {
        if (_exporting) return;
        string book = Path.ChangeExtension(documentPath, ".m4b");
        var (saved, problem) = await Task.Run(() =>
        {
            var opened = SavedNarration.TryOpen(SavedNarration.PathFor(documentPath), hash, out string? p);
            return (opened, p);
        });
        if (saved is null)
        {
            ShowNotice(problem is null
                    ? "An audiobook is made from a narration saved with the document, and there is none yet."
                    : $"The saved narration cannot be used: {problem}.",
                "Narrate the document…", AskToNarrateDocument);
            return;
        }
        _exporting = true;
        ExportAudiobook.RaiseCanExecuteChanged();
        try
        {
            using (saved)
            {
                long lastShown = 0;
                ShowNotice("Exporting the audiobook…");
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    if (p.Done != p.Total && Environment.TickCount64 - lastShown < 1000) return;
                    lastShown = Environment.TickCount64;
                    ShowNotice($"Exporting the audiobook: {p.Done} of {p.Total} passages.");
                });
                var chapters = await Task.Run(() => AudiobookExporter.ExportAsync(saved, Path.GetFileNameWithoutExtension(documentPath), null, book, progress));
                Notes.Add($"Audiobook written to {book} ({HeadlessNarration.Megabytes(book)}, {chapters.Count} chapters).");
                ShowNotice($"Audiobook saved as {Path.GetFileName(book)} beside the document ({HeadlessNarration.Megabytes(book)}, {chapters.Count} chapters).");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Notes.Add($"Exporting an audiobook of '{documentPath}' failed: {ex.Message}");
            ShowNotice(ex.Message);
        }
        finally
        {
            _exporting = false;
            ExportAudiobook.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The extraction's own stage names, said the way the loading card says things.</summary>
    private static string ExtractionStage(string stage) => stage switch
    {
        "Extracting text and geometry" => "Reading the pages",
        "Loaded from cache" => "Reading the pages (already analysed)",
        _ => stage,
    };

    private void RebuildTranscript()
    {
        Transcript.Clear();
        if (Plan is null) return;
        int i = 0;
        foreach (var segment in Plan.Segments)
            Transcript.Add(new TranscriptEntry(segment.Id, i++, segment.SpokenText, segment.IsMaths, segment.HasWarnings));
    }

    /// <summary>
    /// The speech provider in use, its voices and the audio devices. Listing voices starts the
    /// speech model, so this runs on the thread pool; <see cref="ShowVoices"/> puts the result on
    /// screen.
    /// </summary>
    private async Task<(string? ProviderId, IReadOnlyList<TtsVoice> Voices, IReadOnlyList<string> Devices)> ListVoicesAsync()
    {
        var provider = _services.SpeechProvider;
        var voices = await provider.ListVoicesAsync().ConfigureAwait(false);
        var devices = _services.AudioDevice.Enumerate()
            .Select(d => $"{d.Id}: {d.Name}{(d.IsDefault ? " (default)" : "")}").ToList();
        return (provider.ProviderId, voices, devices);
    }

    private void ShowVoices((string? ProviderId, IReadOnlyList<TtsVoice> Voices, IReadOnlyList<string> Devices) available)
    {
        Voices.Clear();
        AudioDevices.Clear();
        if (available.ProviderId is null) return;
        _voiceProviderId = available.ProviderId;

        foreach (var voice in available.Voices) Voices.Add(voice);
        foreach (string device in available.Devices) AudioDevices.Add(device);

        // Keep the voice already in use (from settings, or the reader's last choice); the provider's
        // default is only for a first run. Selecting the default unconditionally switched every
        // saved voice back to it on each document load.
        // The pick made here is not the reader's choice, so it is not saved as a preference.
        _choosingVoiceForReader = true;
        try
        {
            SelectedVoice = Voices.FirstOrDefault(v => v.Id == _selectedVoiceId)
                            ?? Voices.FirstOrDefault(v => v.IsDefault) ?? Voices.FirstOrDefault();
        }
        finally { _choosingVoiceForReader = false; }
    }

    private bool _choosingVoiceForReader;

    /// <summary>
    /// Shows the voice, speed and follow mode a restored position applied to the coordinator, without
    /// writing them back as global preferences: they belong to that document.
    /// </summary>
    private void ShowRestoredPreferences(ReaderPosition position)
    {
        if (position.PlaybackRate > 0) Set(ref _playbackRate, position.PlaybackRate, nameof(PlaybackRate));
        Set(ref _followMode, position.FollowMode, nameof(FollowMode));
        OnPropertyChanged(nameof(FollowLabel));
        OnPropertyChanged(nameof(IsFollowing));
        if (position.VoiceId is { } id && Voices.FirstOrDefault(v => v.Id == id) is { } voice)
        {
            Set(ref _selectedVoiceId, id, nameof(SelectedVoiceId));
            Set(ref _selectedVoice, voice, nameof(SelectedVoice));
        }
    }

    /// <summary>Saves a preference; a settings file that cannot be written must not break playback.</summary>
    private void PersistPreference(Func<RailNarratorSettings, RailNarratorSettings> update)
    {
        try { PersistSettings(update); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notes.Add($"Could not save the preference: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a zoom the reader chose (toolbar, keyboard, Ctrl+wheel). It replaces fit-width and
    /// is remembered: a page turn, a resize or the next document must not snap back to a fit.
    /// </summary>
    public void ApplyZoom(double zoom)
    {
        Zoom = zoom;
        FitWidth = false;
        ScheduleZoomPreferenceSave();
    }

    private DispatcherTimer? _zoomSaveTimer;

    /// <summary>
    /// Saves the zoom preference once the reader has stopped zooming: a turn of the wheel is a
    /// dozen zooms, and each save reads and rewrites the settings file on this thread.
    /// </summary>
    private void ScheduleZoomPreferenceSave()
    {
        _zoomSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (_, _) => SaveZoomPreferenceNow());
        _zoomSaveTimer.Stop();
        _zoomSaveTimer.Start();
    }

    /// <summary>Writes a zoom preference that is waiting to be saved. Called on the way out as well.</summary>
    public void SaveZoomPreferenceNow()
    {
        if (_zoomSaveTimer is not { IsEnabled: true } timer) return;
        timer.Stop();
        try
        {
            // While fitting, the zoom shown is derived from the page and the window; the reader's
            // own last magnification stays on file.
            PersistSettings(s => FitWidth ? s with { FitWidth = true } : s with { FitWidth = false, PreferredZoom = Zoom });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notes.Add($"Could not save the preference: {ex.Message}");
        }
    }

    private void CycleFollowMode()
        => FollowMode = FollowMode == FollowMode.Off ? FollowMode.Follow : FollowMode.Off;

    private DispatcherTimer? _followResumeTimer;

    /// <summary>What the status line says while browsing has paused following.</summary>
    private string FollowPauseNotice => _services.CurrentSettings.FollowAutoResumeSeconds is > 0 and var seconds
        ? $"Following paused while you browse; it resumes {seconds:0.#} s after you stop (or press F9)."
        : "Following paused while you browse. Press F9 to resume.";

    /// <summary>
    /// Called by the surface when the reader pans or navigates by hand. Pauses following, and (unless
    /// <see cref="RailNarratorSettings.FollowAutoResumeSeconds"/> is 0) resumes it once the reader has
    /// stopped browsing for that long; every further scroll, drag or page turn restarts the wait.
    /// </summary>
    public void NotifyManualNavigation()
    {
        double seconds = _services.CurrentSettings.FollowAutoResumeSeconds;
        if (!FollowSuspended)
        {
            Coordinator.SuspendFollow();
            FollowSuspended = true;
        }
        StatusMessage = FollowPauseNotice + " Audio continues.";
        if (seconds <= 0) return;
        _followResumeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Background, (_, _) =>
        {
            _followResumeTimer?.Stop();
            if (FollowSuspended) _services.Coordinator.ResumeFollow();
        });
        _followResumeTimer.Interval = TimeSpan.FromSeconds(seconds);
        _followResumeTimer.Stop();
        _followResumeTimer.Start();   // restart the wait
    }

    private void OnSnapshotChanged(PlaybackSnapshot snapshot)
    {
        void Apply()
        {
            if (snapshot.Generation != Coordinator.Generation) return;
            Playback = snapshot;
            FollowSuspended = snapshot.FollowSuspended;
            if (snapshot.SegmentIndex >= 0 && snapshot.SegmentIndex < Transcript.Count)
            {
                // Suppress the selection-changed seek: following the audio must not re-seek to itself.
                int index = snapshot.SegmentIndex;
                if (_selectedTranscriptIndex != index)
                {
                    // Set the backing field directly: the public setter would seek to the segment the
                    // audio is already reading, and following the audio must not re-seek to itself.
                    SelectedTranscriptIndexCore = index;
                    OnPropertyChanged(nameof(SelectedTranscriptIndex));
                }
            }
            if (snapshot.Error is { } error)
            {
                StatusMessage = error.Message;
                // Once per error: the error stays in every snapshot until it is resolved, and a notice
                // the reader dismissed must not come back with the next one.
                if (error.Code == NarrationErrorCode.SpeechProviderUnavailable && error.Message != _lastNoticedError)
                {
                    _lastNoticedError = error.Message;
                    ShowNotice(error.Message, "Speech settings…", () => SettingsRequested?.Invoke("Speech"));
                }
            }
            else if (snapshot.Message is { } message) StatusMessage = message;
            // Playing carries no message of its own, so the line used to keep whatever was said
            // last ("Seeking", "Preparing speech") for as long as the passage lasted.
            // While the reader browses, it also says the view has stopped following: the notice
            // given when browsing began is replaced by this line within a moment, and without it
            // only the Resume button showed why the page no longer moved.
            else if (snapshot.State == PlaybackState.Playing && snapshot.SegmentCount > 0)
                StatusMessage = $"Reading passage {snapshot.SegmentIndex + 1} of {snapshot.SegmentCount}."
                    + (snapshot.FollowSuspended ? " " + FollowPauseNotice : "");
            // Save the place on every move, not only at exit: a crash, a power cut or a SIGKILL
            // otherwise lost the whole session's progress. Not while loading, when the snapshot
            // describes a plan whose restored preferences are not applied yet.
            if (!Busy && DocumentPath is not null && snapshot.SegmentId is { } segmentId && segmentId != _lastSavedSegmentId)
            {
                _lastSavedSegmentId = segmentId;
                RunObserved(SavePositionAsync(), "Could not save the reading position");
            }
            RefreshCommands();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private readonly HashSet<string> _noticedMathsProblems = new(StringComparer.Ordinal);

    /// <summary>
    /// The maths endpoint could not be asked, so a passage is read by rule or announced. Said once
    /// for each reason while the application runs: the resolver reports it for every passage, and a
    /// notice the reader dismissed must not come back with the next equation.
    /// </summary>
    private void OnMathsEndpointProblem(string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_noticedMathsProblems.Add(reason)) return;
            Notes.Add($"Maths is not being interpreted, and equations are read as notation meanwhile: {reason}");
            // The cause first and nothing after it: the status bar cuts a long line short.
            ShowNotice($"Maths not interpreted: {reason}",
                "Maths settings…", () => SettingsRequested?.Invoke("Maths"));
        });
    }

    /// <summary>A maths passage got its interpreted words (or its fallback): show what will be said.</summary>
    private void OnSegmentResolved(NarrationSegment segment)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Keep this copy of the plan current too: the maths review panel and the saved plan read it.
            if (Plan is { } plan && plan.Segments.Any(s => s.Id == segment.Id))
                Plan = plan with { Segments = plan.Segments.Select(s => s.Id == segment.Id ? segment : s).ToList() };
            if (MathsReview?.SegmentId == segment.Id) UpdateMathsReview(segment.Id);
            var entry = Transcript.FirstOrDefault(e => e.SegmentId == segment.Id);
            if (entry is null) return;
            entry.Text = segment.SpokenText;
            entry.HasWarning = segment.HasWarnings;
        });
    }

    private int SelectedTranscriptIndexCore
    {
        get => _selectedTranscriptIndex;
        set => _selectedTranscriptIndex = value;
    }

    private void OnActiveCueChanged(ActiveCue cue)
    {
        void Apply()
        {
            if (cue.Generation != Coordinator.Generation) return;
            TimingSummary = cue.Granularity == CueGranularity.Word ? "Word highlighting · aligned to speech"
                : $"{cue.Granularity} highlighting" + (cue.Diagnostics.Count > 0 ? ": " + cue.Diagnostics[0] : "");
            InvalidateSurface?.Invoke();
            UpdateMathsReview(cue.SegmentId);
            MarkAsMaths.RaiseCanExecuteChanged();
            RestoreAsProse.RaiseCanExecuteChanged();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void OnFollowRequested(ActiveCue cue, FollowTarget target)
    {
        void Apply()
        {
            if (cue.Generation == Coordinator.Generation) CameraRequested?.Invoke(target);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void UpdateMathsReview(string segmentId)
    {
        if (HoldMathsReview && MathsReview is { } held && held.SegmentId != segmentId) return;
        var segment = Plan?.Segment(segmentId);
        // Interpreted sentences are reviewable too: their words came from a model.
        bool reviewable = segment is { IsMaths: true }
            || segment?.Provenance is ScriptProvenance.ModelGenerated or ScriptProvenance.PendingInterpretation or ScriptProvenance.LocalRule;
        if (segment is null || !reviewable) { MathsReview = null; return; }
        var unit = segment.ReadingUnitIds.Select(id => Plan?.Unit(id)).FirstOrDefault(u => u is not null);
        MathsReview = new MathsReview(
            segment.Id,
            CropImagePath: null,
            // No LaTeX when the model read the page image: show the text layer it was given.
            RecognisedExpression: unit?.Latex ?? unit?.OriginalText ?? "(not recognised)",
            SpokenVersion: segment.SpokenText,
            Warnings: segment.Warnings,
            Provenance: segment.Provenance,
            CanRegenerate: (_services.MathVerbaliser is not null || _services.ResolvesMathsLate) && unit is not null);
        // No forced notification: this runs on every cue, word cues included, and the review is a
        // record, so an unchanged one raises nothing (the window reopens the section on a change).
    }

    /// <summary>
    /// Set by the window while the reader is typing a correction: the review then stays on the
    /// passage being corrected instead of moving on with the reading and dropping what was typed.
    /// </summary>
    public bool HoldMathsReview { get; set; }

    private async Task RegenerateMathsAsync()
    {
        if (MathsReview is null || Plan is null || _snapshot is null) return;
        var unit = Plan.Segment(MathsReview.SegmentId)?.ReadingUnitIds.Select(id => Plan.Unit(id)).FirstOrDefault(u => u is not null);
        if (_services.ResolvesMathsLate && unit is { Kind: ReadingUnitKind.DisplayMath } or { ContainsMaths: true })
        {
            // An interpreted passage: ask the model again for this one passage, bypassing its cache.
            Busy = true;
            try
            {
                StatusMessage = "Asking the maths interpreter again…";
                var fresh = await Coordinator.RegenerateSegmentAsync(MathsReview.SegmentId);
                StatusMessage = fresh is null ? "Nothing to regenerate."
                    : fresh.Warnings.Any(w => w.StartsWith("Regenerating did not", StringComparison.Ordinal))
                        ? "The interpreter gave no usable answer; the previous wording is kept."
                        : "Regenerated.";
            }
            finally { Busy = false; }
            return;
        }
        Busy = true;
        try
        {
            StatusMessage = "Re-reading the expression…";
            var units = _services.CreateUnitBuilder(_overrides).Build(_snapshot);
            var planner = _services.CreatePlanner();
            Plan = await planner.BuildPlanAsync(_snapshot, units, _services.CurrentSettings.Filters);
            // Persist before reporting success: without this the next open resurrects the cached
            // pre-regeneration plan and the new reading silently vanishes.
            await _services.PlanStore.StorePlanAsync(Plan);
            RebuildTranscript();
            await Coordinator.LoadPlanAsync(Plan);
            StatusMessage = "Regenerated.";
        }
        finally { Busy = false; }
    }

    private async Task ApplyMathsEditAsync()
    {
        if (MathsReview is null || string.IsNullOrEmpty(EditedSpokenText)) return;
        string segmentId = MathsReview.SegmentId;
        // An edit invalidates this segment's speech and timings; the coordinator handles that.
        // It edits its own plan copy, so adopt that plan here and persist it — otherwise the
        // transcript keeps showing the old text and the next open resurrects the cached plan.
        await Coordinator.ApplyScriptEditAsync(segmentId, EditedSpokenText);
        if (Coordinator.Plan is { } edited)
        {
            Plan = edited;
            try
            {
                await _services.PlanStore.StorePlanAsync(edited);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Your correction is in use for this session but could not be saved: {ex.Message}";
                Notes.Add($"Saving the corrected plan failed: {ex.GetType().Name}: {ex.Message}");
                RebuildTranscript();
                return;
            }
        }
        RebuildTranscript();
        UpdateMathsReview(segmentId);
        StatusMessage = "Your correction is saved and will be used; this passage will be re-read in the new wording.";
        EditedSpokenText = null;
        OnPropertyChanged(nameof(EditedSpokenText));
    }

    /// <summary>Marks a region as maths, or restores it to prose.</summary>
    public void SetRegionMathsOverride(int pageIndex, PageRect region, bool treatAsMaths)
    {
        _overrides = [.. _overrides.Where(o => !(o.PageIndex == pageIndex && o.Region == region)),
                      new MathsRegionOverride(pageIndex, region, treatAsMaths)];
        ShowNotice(treatAsMaths ? "Passage marked as maths. It applies when the document is reloaded (File ▸ Reload)."
                                : "Passage restored to prose. It applies when the document is reloaded (File ▸ Reload).",
            "Reload now", () => ReloadDocument.Execute(null));
    }

    private async Task TestMathEndpointAsync()
    {
        Busy = true;
        try
        {
            // The client in use was built at start-up; Settings ▸ Test checks what is entered there now.
            if (_services.MathVerbaliser is null) { StatusMessage = "No text LLM was configured at start-up (Settings ▸ Test checks a new one)."; return; }
            bool ok = await _services.MathVerbaliser.IsAvailableAsync();
            StatusMessage = ok ? "The text LLM endpoint in use responded." : "The text LLM endpoint in use (as set at start-up) is not reachable.";
        }
        finally { Busy = false; }
    }

    private async Task TestVisionEndpointAsync()
    {
        Busy = true;
        try
        {
            if (_services.MathTranscriber is null) { StatusMessage = "No vision model was configured at start-up (Settings ▸ Test checks a new one)."; return; }
            bool ok = await _services.MathTranscriber.IsAvailableAsync();
            // A text-only endpoint must not be reported as vision-capable; the probe is the only
            // honest answer.
            StatusMessage = ok ? "The vision endpoint in use responded." : "The vision endpoint in use (as set at start-up) is not reachable or is text-only.";
        }
        finally { Busy = false; }
    }

    /// <summary>
    /// Asks before a download: the size, licence and source are stated and nothing is fetched until
    /// the reader agrees (models are never fetched implicitly).
    /// </summary>
    private Task ConfirmDownload(params ModelAsset[] assets)
    {
        var asset = assets[^1];
        long bytes = assets.Sum(a => a.Bytes);
        string size = bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:F0} MB" : asset.HumanSize;
        string host = Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) ? uri.Host : asset.Url;
        ShowNotice($"Download {asset.Id}: {size}, {asset.Licence}, from {host}?", "Download",
            () => RunObserved(DownloadModelAsync(assets), "The download failed"));
        return Task.CompletedTask;
    }

    /// <summary>Downloads a model's files in the order given; the last one names the model in the messages.</summary>
    private async Task DownloadModelAsync(params ModelAsset[] assets)
    {
        var asset = assets[^1];
        Busy = true;
        try
        {
            foreach (var file in assets)
            {
                // The size and licence are shown before anything is fetched; a surprise multi-hundred-MB
                // download is not acceptable behaviour for a local-first app.
                StatusMessage = $"Downloading {file.Id} ({file.HumanSize}, {file.Licence})…";
                await _services.Models.DownloadAsync(file, new Progress<DownloadProgress>(p =>
                    Dispatcher.UIThread.Post(() => StatusMessage =
                        p.BytesTotal > 0
                            ? $"{p.Phase}: {p.BytesReceived / 1_000_000.0:F1} / {p.BytesTotal / 1_000_000.0:F1} MB"
                            : $"{p.Phase}…")));
            }
            StatusMessage = $"{asset.Id} downloaded and verified.";
            Notes.Add($"{asset.Id} is installed at {_services.Models.PathFor(asset)}. Restart to enable it.");
            ShowNotice($"{asset.Id} is installed. Rail Narrator uses it after a restart.", "Restart now", () => RestartRequested?.Invoke());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
            RefreshModelMenu();
        }
    }

    /// <summary>Persists a settings change: reads the file, applies <paramref name="update"/>, writes it back (owner-only).</summary>
    public void PersistSettings(Func<RailNarratorSettings, RailNarratorSettings> update)
    {
        var current = _services.Settings.Load();
        _services.Settings.Save(update(current));
    }

    /// <summary>Remembers where the reader stopped. Never triggers auto-play on the next open.</summary>
    public async Task SavePositionAsync()
    {
        if (_documentHash is null || Plan is null) return;
        // Captured on the caller's thread, then written one at a time: saves now happen on every
        // passage change, and an older position must never land after a newer one.
        var snapshot = Coordinator.Snapshot;
        var position = new ReaderPosition(
            _documentHash, snapshot.SegmentId, PageIndex: 0, snapshot.PositionSeconds,
            SelectedVoiceId, _services.CurrentSettings.MathMode, Zoom, FollowMode, PlaybackRate);
        await _saveGate.WaitAsync();
        try { await _services.PlanStore.SavePositionAsync(position); }
        finally { _saveGate.Release(); }
        if (_documentPath is { } path && Plan.SegmentCount > 0 && snapshot.SegmentIndex >= 0)
            _recent.Progressed(path, (double)snapshot.SegmentIndex / Plan.SegmentCount);
    }

    // The loaded document's content hash, computed once at load rather than on every save.
    private string? _documentHash;
    private string? _lastSavedSegmentId;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private void RefreshCommands()
    {
        Play.RaiseCanExecuteChanged();
        Pause.RaiseCanExecuteChanged();
        PlayPause.RaiseCanExecuteChanged();
        Next.RaiseCanExecuteChanged();
        Previous.RaiseCanExecuteChanged();
        Replay.RaiseCanExecuteChanged();
        OpenDocument.RaiseCanExecuteChanged();
        ResumeFollow.RaiseCanExecuteChanged();
        RegenerateMaths.RaiseCanExecuteChanged();
        TestMathEndpoint.RaiseCanExecuteChanged();
        TestVisionEndpoint.RaiseCanExecuteChanged();
        DownloadKokoroModel.RaiseCanExecuteChanged();
        DownloadLayoutModel.RaiseCanExecuteChanged();
        DownloadLayoutModelGpu.RaiseCanExecuteChanged();
        DownloadFormulaModel.RaiseCanExecuteChanged();
        ApplyMathsEdit.RaiseCanExecuteChanged();
        CloseDocument.RaiseCanExecuteChanged();
        NarrateDocument.RaiseCanExecuteChanged();
        ExportAudiobook.RaiseCanExecuteChanged();
        ReloadDocument.RaiseCanExecuteChanged();
        GoToPage.RaiseCanExecuteChanged();
        ZoomIn.RaiseCanExecuteChanged();
        ZoomOut.RaiseCanExecuteChanged();
        Zoom200.RaiseCanExecuteChanged();
        Zoom400.RaiseCanExecuteChanged();
        Zoom800.RaiseCanExecuteChanged();
        MarkAsMaths.RaiseCanExecuteChanged();
        RestoreAsProse.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        SaveZoomPreferenceNow();   // a zoom made in the last moments before closing
        _services.Coordinator.SnapshotChanged -= OnSnapshotChanged;
        _services.Coordinator.SegmentResolved -= OnSegmentResolved;
        if (_services.MathResolver is { } maths) maths.EndpointProblem -= OnMathsEndpointProblem;
        _narration?.Cancel();
        _savedNarration?.Dispose();
        _services.Coordinator.ActiveCueChanged -= OnActiveCueChanged;
        _services.Coordinator.FollowRequested -= OnFollowRequested;
        if (_services.Kokoro is { } kokoro)
        {
            kokoro.Initialised -= OnKokoroDeviceSettled;
            kokoro.FellBackToCpu -= OnKokoroFellBack;
        }
        await SavePositionAsync();
    }
}

/// <summary>A colour filter as the toolbar offers it.</summary>
public sealed record ColourEffectOption(ColourEffect Effect, string Name, string Description)
{
    public override string ToString() => Name;
}
