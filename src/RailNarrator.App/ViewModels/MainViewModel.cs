using RailReader.Core.Models;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RailNarrator.App.Composition;
using RailNarrator.Domain;
using RailNarrator.Infrastructure;
using RailNarrator.Pdf;
using RailNarrator.Pdf.Annotations;
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
        _showAnnotations = services.CurrentSettings.ShowAnnotations;
        _expandAllNotes = services.CurrentSettings.ExpandAllNotes;
        _showPdfAnnotations = services.CurrentSettings.ShowPdfAnnotations;

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
        SetUpReview();
        SetUpAnnotateMode();
        SetUpExport();
        SetUpRevision();
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
        NoticeAction = new RelayCommand(() => { var action = Notice?.Action; CloseNotice(); action?.Invoke(); });
        DismissNotice = new RelayCommand(CloseNotice);
        TestMathEndpoint = new RelayCommand(() => TestMathEndpointAsync(), () => !Busy, OnCommandError);
        TestVisionEndpoint = new RelayCommand(() => TestVisionEndpointAsync(), () => !Busy, OnCommandError);
        DownloadKokoroModel = new RelayCommand(() => ConfirmDownload(ModelManifest.KokoroFp16), () => !Busy && !KokoroModelInstalled, OnCommandError);
        DownloadKokoroTimedModel = new RelayCommand(() => ConfirmDownload(ModelManifest.KokoroTimed), () => !Busy && !KokoroTimedModelInstalled, OnCommandError);
        DownloadLayoutModel = new RelayCommand(() => ConfirmDownload(ModelManifest.LayoutHeronInt8), () => !Busy && !LayoutModelInstalled, OnCommandError);
        DownloadLayoutModelGpu = new RelayCommand(() => ConfirmDownload(ModelManifest.LayoutHeron), () => !Busy && !LayoutModelGpuInstalled, OnCommandError);
        // The vocabulary first: the model is of no use without it, and it is the small one.
        DownloadFormulaModel = new RelayCommand(() => ConfirmDownload(ModelManifest.FormulaModelConfig, ModelManifest.FormulaModel), () => !Busy && !FormulaModelInstalled, OnCommandError);
        DownloadPhonon = new RelayCommand(ConfirmPhononDownload, () => !Busy && PhononRuntimePresent && !PhononModelInstalled, OnCommandError);
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

    // ---- the reader's annotations (ADR-0014) --------------------------------------------------

    private ReviewDocument? _review;
    private AnnotationLayer? _annotationLayer;
    private bool _showAnnotations;
    private string? _lastAnnotationSaveError;

    /// <summary>The open document's annotations, ready to draw; null with no document.</summary>
    public AnnotationLayer? AnnotationLayer => _annotationLayer;

    /// <summary>The reader's annotations are drawn on the page. Remembered.</summary>
    public bool ShowAnnotations
    {
        get => _showAnnotations;
        set
        {
            if (!Set(ref _showAnnotations, value)) return;
            PersistPreference(s => s with { ShowAnnotations = value });
            InvalidateSurface?.Invoke();
        }
    }

    private ReviewSession? _session;
    private PageTextCache? _pageText;
    private ActiveCue? _lastCue;
    private NoteDraft? _draft;
    private string _noteEditorText = "";

    /// <summary>Editing the open document's annotations; null with no document.</summary>
    public ReviewSession? ReviewSession => _session;

    /// <summary>The sidebar's Review notes: every annotation, in page and reading order.</summary>
    public ObservableCollection<ReviewNoteItem> ReviewNotes { get; } = [];

    public bool HasReviewNotes => ReviewNotes.Count > 0;

    /// <summary>A note being written: on what was just read, or the note of an annotation already made.</summary>
    /// <param name="Target">What a new note goes on; null when editing.</param>
    /// <param name="Existing">The annotation whose note is edited; null for a new one.</param>
    /// <param name="Placement">Where a new sticky note or text box goes, when it is placed by hand.</param>
    private sealed record NoteDraft(ReviewTarget? Target, Annotation? Existing, string Quote, Placement? Placement = null);

    /// <summary>A sticky note (at a point) or a text box (a box) placed by hand in annotate mode.</summary>
    private sealed record Placement(AnnotationTool Kind, int Page, float X, float Y, float W, float H, string Colour);

    public bool IsNoteEditorOpen => _draft is not null;

    /// <summary>The note card's text box.</summary>
    public string NoteEditorText { get => _noteEditorText; set => Set(ref _noteEditorText, value); }

    /// <summary>What the note is on, quoted on the card.</summary>
    public string NoteEditorQuote => _draft?.Quote ?? "";

    public string NoteEditorTitle => _draft switch
    {
        { Existing: FreeTextAnnotation } => "Edit the text",
        { Existing: not null } => "Edit note",
        { Placement.Kind: AnnotationTool.FreeText } => "Text on the page",
        { Placement: not null } => "New note",
        _ => "Note on what was just read",
    };

    /// <summary>
    /// The place on the page the note card belongs beside: what a new note is on, where a note or
    /// text box is being placed, or the annotation being edited. Null with no card open.
    /// </summary>
    public (int Page, PageRect Rect)? NoteAnchor
    {
        get
        {
            if (_draft is not { } draft) return null;
            if (draft.Placement is { } p) return (p.Page, new PageRect(p.X, p.Y, p.X + Math.Max(p.W, 16), p.Y + Math.Max(p.H, 16)));
            if (draft.Target is { } t && t.Pages[0].Rects is { Count: > 0 } r)
                return (t.FirstPage, new PageRect(r.Min(x => x.X), r.Min(x => x.Y), r.Max(x => x.X + x.W), r.Max(x => x.Y + x.H)));
            if (draft.Existing is { } e && _session?.PageOf(e) is int page
                && RailReader.Core.Services.AnnotationGeometry.GetAnnotationBounds(e) is { } b)
                return (page, new PageRect(b.Left, b.Top, b.Right, b.Bottom));
            return null;
        }
    }

    /// <summary>The PDF's own annotations shown, read-only (View menu). Remembered.</summary>
    public bool ShowPdfAnnotations
    {
        get => _showPdfAnnotations;
        set
        {
            if (!Set(ref _showPdfAnnotations, value)) return;
            PersistPreference(s => s with { ShowPdfAnnotations = value });
            _services.CurrentSettings = _services.CurrentSettings with { ShowPdfAnnotations = value };
            if (!value) ShowPdfAnnotationsOf(null);
            else if (DocumentPath is { } path) _ = LoadPdfAnnotationsAsync(path);
        }
    }

    private bool _showPdfAnnotations;

    private async Task LoadPdfAnnotationsAsync(string path)
    {
        var file = await Task.Run(() => AnnotatedPdfExport.ReadExisting(path));
        if (DocumentPath == path && _showPdfAnnotations) ShowPdfAnnotationsOf(file);
    }

    private void ShowPdfAnnotationsOf(AnnotationFile? file)
    {
        if (_review is null) return;
        if (file is null && _session?.Selected is { } selected && _review.IsFromPdf(selected)) _session.Selected = null;
        _review.SetPdfAnnotations(file);
        RebuildReviewNotes();
        int count = file?.Pages.Values.Sum(p => p.Count) ?? 0;
        if (count > 0) StatusMessage = $"The PDF carries {count} annotation{(count == 1 ? "" : "s")} of its own, shown read-only.";
    }

    // ---- export and exchange (ADR-0014) --------------------------------------------------------

    public RelayCommand CopyNotesAsMarkdown { get; private set; } = null!;
    public RelayCommand SaveNotesAsMarkdown { get; private set; } = null!;
    public RelayCommand SaveAnnotatedCopy { get; private set; } = null!;
    public RelayCommand ExportAnnotationsJson { get; private set; } = null!;
    public RelayCommand ImportAnnotationsJson { get; private set; } = null!;

    private void SetUpExport()
    {
        bool HasNotes() => _review is not null && ReviewNotes.Count > 0 && !Busy;
        bool HasOwn() => _review is { IsEmpty: false } && !Busy;
        CopyNotesAsMarkdown = new RelayCommand(CopyMarkdownAsync, HasNotes, OnCommandError);
        SaveNotesAsMarkdown = new RelayCommand(SaveMarkdownAsync, HasNotes, OnCommandError);
        SaveAnnotatedCopy = new RelayCommand(SaveAnnotatedCopyAsync, HasOwn, OnCommandError);
        ExportAnnotationsJson = new RelayCommand(ExportJsonAsync, HasOwn, OnCommandError);
        ImportAnnotationsJson = new RelayCommand(ImportJsonAsync, () => _session is not null && !Busy, OnCommandError);
    }

    private void RefreshExportCommands()
    {
        foreach (var command in new[] { CopyNotesAsMarkdown, SaveNotesAsMarkdown, SaveAnnotatedCopy, ExportAnnotationsJson, ImportAnnotationsJson })
            command?.RaiseCanExecuteChanged();
    }

    private string DocumentStem => Path.GetFileNameWithoutExtension(DocumentPath ?? "document");

    /// <summary>The notes in the list's order, as the Markdown export takes them.</summary>
    private string BuildMarkdown()
    {
        var palette = Enumerable.Range(0, ReviewPalette.Count)
            .ToDictionary(i => ReviewPalette.Hex(i), i => ReviewPalette.NameOf(_services.CurrentSettings, i), StringComparer.OrdinalIgnoreCase);
        // The colour names are the reader's: another reviewer's yellow is not the reader's "Note".
        var notes = ReviewNotes.Select(item => new ExportedNote(item.PageIndex, item.Kind,
            !item.FromPdf && palette.TryGetValue(item.Annotation.Color, out var name) ? name : null,
            item.Quote, item.Note, item.Annotation.Author, item.FromPdf) { Unplaced = item.Unplaced }).ToList();
        return ReviewMarkdown.Build(Path.GetFileName(DocumentPath ?? "document"), notes, DateOnly.FromDateTime(DateTime.Now));
    }

    private async Task CopyMarkdownAsync()
    {
        if (TopLevelProvider?.Invoke()?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(BuildMarkdown());
        ShowNotice($"Copied {ReviewNotes.Count} note{(ReviewNotes.Count == 1 ? "" : "s")} as Markdown.");
    }

    private async Task SaveMarkdownAsync()
    {
        if (await PickSaveAsync("Save the notes as Markdown", $"{DocumentStem} notes.md", "Markdown", "*.md") is not { } target) return;
        string markdown = BuildMarkdown();
        await Task.Run(() => File.WriteAllText(target, markdown));
        ShowNotice($"Notes saved as {Path.GetFileName(target)}.");
    }

    private async Task SaveAnnotatedCopyAsync()
    {
        if (_review is not { } review || DocumentPath is not { } source) return;
        if (await PickSaveAsync("Save an annotated copy of the PDF", $"{DocumentStem} (annotated).pdf", "PDF", "*.pdf") is not { } target) return;
        Busy = true;
        try
        {
            StatusMessage = "Writing the annotated copy…";
            var result = await Task.Run(() => AnnotatedPdfExport.Write(source, review, target));
            string text = $"Saved an annotated copy, {Path.GetFileName(target)}, with {result.Written} annotation{(result.Written == 1 ? "" : "s")}."
                + (result.Warning is { } warning ? " " + warning : "");
            if (Author is null)
                ShowNotice(text + " They are signed with no name.", "Set a name…", () => SettingsRequested?.Invoke("Annotations"));
            else ShowNotice(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice($"The annotated copy was not saved: {ex.Message}");
        }
        finally { Busy = false; }
    }

    private async Task ExportJsonAsync()
    {
        if (_review is not { } review) return;
        if (await PickSaveAsync("Export the annotations for RailReader2", $"{DocumentStem}.annotations.json", "Annotations (JSON)", "*.json") is not { } target) return;
        string json = ReviewAnnotationJson.Serialize(review.File);
        await Task.Run(() => File.WriteAllText(target, json));
        ShowNotice($"Annotations exported as {Path.GetFileName(target)}. In RailReader2: Import Annotations.");
    }

    private async Task ImportJsonAsync()
    {
        if (_session is not { } session || TopLevelProvider?.Invoke() is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import annotations (RailReader2 or Rail Narrator JSON)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Annotations (JSON)") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        string text = await Task.Run(() => File.ReadAllText(path));
        if (ReviewAnnotationJson.Deserialize(text) is not { } imported)
        {
            ShowNotice($"{Path.GetFileName(path)} is not an annotation file.");
            return;
        }
        var (added, skipped) = session.Import(imported);
        ShowNotice($"Imported {added} annotation{(added == 1 ? "" : "s")}" + (skipped > 0 ? $"; {skipped} were already here." : ".")
            + (added > 0 ? " Ctrl+Z takes them out again." : ""));
    }

    // ---- notes from an earlier version of the PDF (ADR-0014) ------------------------------------

    public RelayCommand BringEarlierNotes { get; private set; } = null!;
    public RelayCommand BringNotesFromFile { get; private set; } = null!;
    public RelayCommand<ReviewNoteItem> PlaceAsBefore { get; private set; } = null!;
    public RelayCommand<ReviewNoteItem> DiscardUnplaced { get; private set; } = null!;

    private bool _carrying;

    private void SetUpRevision()
    {
        BringEarlierNotes = new RelayCommand(
            async () => { if (_review?.Earlier is { CarriedUtc: null } e) await BringNotesFromAsync(e.DocumentContentHash, null); },
            () => _review is { Earlier: { CarriedUtc: null } e } r && !r.BroughtFrom.Contains(e.DocumentContentHash) && _snapshot is not null && !_carrying && !Busy, OnCommandError);
        BringNotesFromFile = new RelayCommand(BringNotesFromFileAsync, () => _session is not null && _snapshot is not null && !_carrying && !Busy, OnCommandError);
        PlaceAsBefore = new RelayCommand<ReviewNoteItem>(item =>
        {
            if (_session is not { } session || _snapshot is not { } snapshot) return;
            if (!session.PlaceAsBefore(item.Annotation, snapshot.Pages.Count))
            {
                var missing = session.PlacesAsBefore(item.Annotation).Where(p => p >= snapshot.Pages.Count).Select(p => (p + 1).ToString());
                ShowNotice($"This version has no page {string.Join(" or ", missing)}; the note can only be discarded.");
                return;
            }
            GoToReviewNote(ReviewNotes.FirstOrDefault(n => ReferenceEquals(n.Annotation, item.Annotation)) ?? item);
        });
        DiscardUnplaced = new RelayCommand<ReviewNoteItem>(item => _session?.Discard(item.Annotation));
    }

    private void RefreshRevisionCommands()
    {
        BringEarlierNotes?.RaiseCanExecuteChanged();
        BringNotesFromFile?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// A document opened at a path whose PDF had other contents when it was annotated: says how many
    /// notes the earlier version has, and offers to bring them over.
    /// </summary>
    private async Task OfferEarlierNotesAsync(ReviewDocument review)
    {
        if (review.Earlier is not { CarriedUtc: null } earlier) return;
        var loaded = await Task.Run(() => _services.ReviewAnnotations.Peek(earlier.DocumentContentHash));
        if (!ReferenceEquals(_review, review) || review.Earlier?.CarriedUtc is not null) return;
        if (loaded.Problem is { } problem)
        {
            Notes.Add($"Annotations of the earlier version: {problem}");
            ShowLastingNotice($"This PDF changed since you annotated it, and the notes on the earlier version could not be read. {problem}");
            return;
        }
        int count = ReviewCarryOver.CountNotes(loaded.Document);
        if (count == 0) return;
        // The notice may outlive the document: the action is for this one only.
        ShowLastingNotice($"This PDF changed since you annotated it: {NoteCount(count)} on the earlier version can be brought over.",
            "Bring them over", () => RunObserved(BringNotesAsync(loaded.Document, earlier.DocumentContentHash, review), "Could not bring the notes over"));
    }

    private static string NoteCount(int count) => count == 1 ? "1 note" : $"{count} notes";

    /// <summary>Review ▸ Bring notes from another version of this PDF…: a PDF chosen by the reader, by its contents.</summary>
    private async Task BringNotesFromFileAsync()
    {
        if (_review is not { } review || TopLevelProvider?.Invoke() is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Bring notes from another version of this PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        string hash = await Task.Run(() => CacheKey.Sha256HexOfFile(path));
        if (!ReferenceEquals(_review, review)) return;
        if (hash == review.DocumentContentHash)
        {
            ShowNotice($"{Path.GetFileName(path)} is this same PDF.");
            return;
        }
        await BringNotesFromAsync(hash, Path.GetFileName(path));
    }

    private async Task BringNotesFromAsync(string hash, string? name)
    {
        if (_review is not { } review) return;
        if (review.BroughtFrom.Contains(hash))
        {
            ShowNotice("The notes on that version were brought over already. Ctrl+Z undoes it, if it was the last change.");
            return;
        }
        var loaded = await Task.Run(() => _services.ReviewAnnotations.Peek(hash));
        if (!ReferenceEquals(_review, review)) return;
        if (loaded.Problem is { } problem)
        {
            ShowNotice($"The notes on {(name ?? "the earlier version")} could not be read. {problem}");
            return;
        }
        if (ReviewCarryOver.CountNotes(loaded.Document) == 0)
        {
            ShowNotice(name is null
                ? "The earlier version has no notes to bring over."
                : $"No notes are kept for {name}. Notes are kept for the versions you annotated here; an annotated copy exported "
                  + "from one is another file (open it to see its annotations, read-only).");
            return;
        }
        await BringNotesAsync(loaded.Document, hash, review);
    }

    /// <summary>
    /// Matches the earlier version's notes against this one's text off the UI thread, then adds what
    /// was placed and keeps the rest, as one undoable step.
    /// </summary>
    private async Task BringNotesAsync(ReviewDocument earlier, string hash, ReviewDocument forReview)
    {
        if (!ReferenceEquals(_review, forReview))
        {
            ShowNotice("That offer was for another document.");
            return;
        }
        if (_session is not { } session || _review is not { } review || _snapshot is not { } snapshot || _carrying) return;
        if (review.BroughtFrom.Contains(hash)) return;
        _carrying = true;
        RefreshRevisionCommands();
        try
        {
            var plan = Plan;
            var carried = await Task.Run(() => ReviewCarryOver.Match(earlier, plan, snapshot));
            if (!ReferenceEquals(_review, review) || !ReferenceEquals(_snapshot, snapshot)) return;
            session.CarryOver(carried, hash);
            const string where = "at the end of Review notes, to place as before or discard";
            string text = (carried.NotesPlaced, carried.NotesUnplaced) switch
            {
                (0, 1) => $"The note could not be placed by its text; it is {where}.",
                (0, var n) => $"None of the {NoteCount(n)} could be placed by their text; they are {where}.",
                (var p, 0) => $"Brought over {NoteCount(p)} by their text.",
                var (p, n) => $"Brought over {NoteCount(p)} by their text; {n} not placed {(n == 1 ? "is" : "are")} {where}.",
            };
            ShowLastingNotice(text + " Ctrl+Z undoes this.");
            Notes.Add($"Annotations: brought over {carried.NotesPlaced} of {carried.NotesPlaced + carried.NotesUnplaced} notes from version {hash[..12]}.");
        }
        finally
        {
            _carrying = false;
            RefreshRevisionCommands();
        }
    }

    private async Task<string?> PickSaveAsync(string title, string suggested, string kind, string pattern)
    {
        if (TopLevelProvider?.Invoke() is not { } top) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            SuggestedStartLocation = DocumentPath is { } p ? await top.StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(p)!) : null,
            FileTypeChoices = [new FilePickerFileType(kind) { Patterns = [pattern] }],
            ShowOverwritePrompt = true,
        });
        string? target = file?.TryGetLocalPath();
        if (target is not null && DocumentPath is { } source
            && string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.Ordinal))
        {
            ShowNotice("That is the PDF itself; it is never written. Choose another name.");
            return null;
        }
        return target;
    }

    /// <summary>Every note's text shown on the page, not only the open ones (View ▸ Expand all notes). Remembered.</summary>
    public bool ExpandAllNotes
    {
        get => _expandAllNotes;
        set
        {
            if (!Set(ref _expandAllNotes, value)) return;
            PersistPreference(s => s with { ExpandAllNotes = value });
            InvalidateSurface?.Invoke();
        }
    }

    private bool _expandAllNotes;

    /// <summary>A note or text box chosen in the list, to be centred on its page (it may be tied to no text).</summary>
    public event Action<int, PageRect>? AnnotationFocusRequested;

    /// <summary>
    /// Where the note being written goes, per page, for the page to outline it while the card is
    /// open (and for the card to be placed beside it). Null when no new note is being written.
    /// </summary>
    public ReviewTarget? NoteTarget => _draft?.Target;

    /// <summary>The note card opened: the window places it and gives it the keyboard.</summary>
    public event Action? NoteEditorOpened;

    public RelayCommand NoteOnSentence { get; private set; } = null!;
    public RelayCommand NoteOnPassage { get; private set; } = null!;
    public RelayCommand HighlightSentence { get; private set; } = null!;
    public RelayCommand HighlightPassage { get; private set; } = null!;
    public RelayCommand SaveNote { get; private set; } = null!;
    public RelayCommand SaveNoteAndResume { get; private set; } = null!;
    public RelayCommand CancelNote { get; private set; } = null!;
    public RelayCommand UndoAnnotation { get; private set; } = null!;
    public RelayCommand RedoAnnotation { get; private set; } = null!;
    public RelayCommand DeleteSelectedAnnotation { get; private set; } = null!;
    public RelayCommand<ReviewNoteItem> OpenReviewNote { get; private set; } = null!;
    public RelayCommand<ReviewNoteItem> EditReviewNote { get; private set; } = null!;
    public RelayCommand<ReviewNoteItem> DeleteReviewNote { get; private set; } = null!;

    private void SetUpReview()
    {
        bool CanNote() => _session is not null && Plan is not null && !IsNoteEditorOpen;
        NoteOnSentence = new RelayCommand(() => BeginNoteAsync(wholePassage: false), CanNote, OnCommandError);
        NoteOnPassage = new RelayCommand(() => BeginNoteAsync(wholePassage: true), CanNote, OnCommandError);
        HighlightSentence = new RelayCommand(() => HighlightJustRead(wholePassage: false), CanNote, OnCommandError);
        HighlightPassage = new RelayCommand(() => HighlightJustRead(wholePassage: true), CanNote, OnCommandError);
        SaveNote = new RelayCommand(() => CommitNote(resume: false), () => IsNoteEditorOpen, OnCommandError);
        SaveNoteAndResume = new RelayCommand(() => CommitNote(resume: true), () => IsNoteEditorOpen, OnCommandError);
        CancelNote = new RelayCommand(() => CloseNoteEditor(), () => IsNoteEditorOpen, OnCommandError);
        UndoAnnotation = new RelayCommand(() => _session?.Undo(), () => _session?.CanUndo == true && !IsNoteEditorOpen, OnCommandError);
        RedoAnnotation = new RelayCommand(() => _session?.Redo(), () => _session?.CanRedo == true && !IsNoteEditorOpen, OnCommandError);
        DeleteSelectedAnnotation = new RelayCommand(() => { if (_session?.Selected is { } a) _session.Delete(a); },
            () => _session?.Selected is { } a && _review?.IsFromPdf(a) != true && !IsNoteEditorOpen, OnCommandError);
        OpenReviewNote = new RelayCommand<ReviewNoteItem>(GoToReviewNote);
        EditReviewNote = new RelayCommand<ReviewNoteItem>(item => OpenNoteEditorFor(item.Annotation));
        DeleteReviewNote = new RelayCommand<ReviewNoteItem>(item => _session?.Delete(item.Annotation));
    }

    // ---- annotate mode: marking text by hand ---------------------------------------------------

    private bool _annotateMode;
    private AnnotationTool _tool = AnnotationTool.Highlight;
    private MarkupDrag? _drag;
    private AnnotationMove? _move;
    private BoxDrag? _box;
    private StrokeDrag? _stroke;
    private StrokeResize? _resize;
    private bool _erasing;
    private AnnotationTool _boxTool;
    private (int Page, float X, float Y)? _pressedAt;
    private Dictionary<AnnotationTool, int>? _colourIndices;

    /// <summary>
    /// Annotate mode (Ctrl+E): a left drag over text marks it with the chosen tool and a click
    /// selects an annotation, instead of reading from there and panning. A middle drag still pans.
    /// </summary>
    public bool AnnotateMode
    {
        get => _annotateMode;
        set
        {
            if (!Set(ref _annotateMode, value)) return;
            CancelAnnotateDrag();
            StatusMessage = value
                ? "Annotate mode: drag over text to mark it; click an annotation to select it. Escape or Ctrl+E leaves."
                : "Annotate mode off: a click reads from there again.";
            InvalidateSurface?.Invoke();
        }
    }

    /// <summary>The markup tool in annotate mode; <see cref="AnnotationTool.None"/> selects.</summary>
    public AnnotationTool ActiveTool
    {
        get => _tool;
        private set
        {
            if (!Set(ref _tool, value)) return;
            foreach (var name in new[] { nameof(IsHighlightTool), nameof(IsUnderlineTool), nameof(IsStrikeOutTool),
                         nameof(IsSquigglyTool), nameof(IsSelectTool), nameof(IsTextNoteTool), nameof(IsFreeTextTool),
                         nameof(IsTextMarkupTool), nameof(IsPenTool), nameof(IsRectangleTool), nameof(IsEraserTool),
                         nameof(HasThickness), nameof(ThicknessIndex), nameof(IsThin), nameof(IsNormalThickness), nameof(IsThick),
                         nameof(ActiveColourIndex), nameof(ActiveColour) })
                OnPropertyChanged(name);
        }
    }

    public bool IsHighlightTool => _tool == AnnotationTool.Highlight;
    public bool IsUnderlineTool => _tool == AnnotationTool.Underline;
    public bool IsStrikeOutTool => _tool == AnnotationTool.StrikeOut;
    public bool IsSquigglyTool => _tool == AnnotationTool.Squiggly;
    public bool IsSelectTool => _tool == AnnotationTool.None;
    public bool IsTextNoteTool => _tool == AnnotationTool.TextNote;
    public bool IsFreeTextTool => _tool == AnnotationTool.FreeText;
    public bool IsPenTool => _tool == AnnotationTool.Pen;
    public bool IsRectangleTool => _tool == AnnotationTool.Rectangle;
    public bool IsEraserTool => _tool == AnnotationTool.Eraser;

    /// <summary>A tool that draws a line, and so has a thickness to choose.</summary>
    public bool HasThickness => _tool is AnnotationTool.Pen or AnnotationTool.Rectangle;

    private Dictionary<AnnotationTool, int>? _thicknessIndices;

    /// <summary>The active tool's line thickness, 0 to 2 (thin, normal, thick).</summary>
    public int ThicknessIndex => ThicknessIndexOf(_tool);

    public bool IsThin => HasThickness && ThicknessIndex == 0;
    public bool IsNormalThickness => HasThickness && ThicknessIndex == 1;
    public bool IsThick => HasThickness && ThicknessIndex == 2;

    private int ThicknessIndexOf(AnnotationTool tool)
    {
        _thicknessIndices ??= (_services.CurrentSettings.AnnotationThicknessIndices ?? new Dictionary<string, int>())
            .Where(kv => Enum.TryParse<AnnotationTool>(kv.Key, out _) && kv.Value >= 0 && kv.Value < ReviewPalette.Thicknesses.Count)
            .ToDictionary(kv => Enum.Parse<AnnotationTool>(kv.Key), kv => kv.Value);
        return _thicknessIndices.TryGetValue(tool, out int index) ? index : 1;
    }

    private float WidthOf(AnnotationTool tool) => ReviewPalette.Thicknesses[ThicknessIndexOf(tool)];

    public RelayCommand<string> ChooseThickness { get; private set; } = null!;

    private void UseThickness(int index)
    {
        if (!HasThickness || index < 0 || index >= ReviewPalette.Thicknesses.Count) return;
        ThicknessIndexOf(_tool);
        _thicknessIndices![_tool] = index;
        var indices = _thicknessIndices.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        PersistPreference(s => s with { AnnotationThicknessIndices = indices });
        _services.CurrentSettings = _services.CurrentSettings with { AnnotationThicknessIndices = indices };
        foreach (var name in new[] { nameof(ThicknessIndex), nameof(IsThin), nameof(IsNormalThickness), nameof(IsThick) }) OnPropertyChanged(name);
    }

    /// <summary>A tool that marks text by dragging over it.</summary>
    public bool IsTextMarkupTool => RailReader.Core.AnnotationInteractionHandler.IsTextMarkupTool(_tool);

    /// <summary>The five colours, with what the reader calls them.</summary>
    public IReadOnlyList<PaletteColour> Palette => _palette ??= BuildPalette();

    private IReadOnlyList<PaletteColour>? _palette;

    private IReadOnlyList<PaletteColour> BuildPalette() => Enumerable.Range(0, ReviewPalette.Count)
        .Select(i => new PaletteColour(i, ReviewPalette.Hex(i), ReviewPalette.NameOf(_services.CurrentSettings, i))).ToList();

    /// <summary>The colour names changed in Settings.</summary>
    public void RefreshPalette()
    {
        _palette = null;
        OnPropertyChanged(nameof(Palette));
        OnPropertyChanged(nameof(ActiveColour));
    }

    /// <summary>The colour the active tool marks with (a highlight's for the select tool).</summary>
    public int ActiveColourIndex => ColourIndexOf(_tool == AnnotationTool.None ? AnnotationTool.Highlight : _tool);

    public PaletteColour ActiveColour => Palette[ActiveColourIndex];

    private int ColourIndexOf(AnnotationTool tool)
    {
        _colourIndices ??= (_services.CurrentSettings.AnnotationColourIndices ?? new Dictionary<string, int>())
            .Where(kv => Enum.TryParse<AnnotationTool>(kv.Key, out _) && kv.Value >= 0 && kv.Value < ReviewPalette.Count)
            .ToDictionary(kv => Enum.Parse<AnnotationTool>(kv.Key), kv => kv.Value);
        return _colourIndices.TryGetValue(tool, out int index) ? index : ReviewPalette.DefaultIndexFor(tool);
    }

    /// <summary>The markup being dragged, to draw; null when none.</summary>
    public Annotation? MarkupPreview => (Annotation?)_drag?.Preview ?? (Annotation?)_stroke?.Preview ?? _box?.Preview;

    public int MarkupPreviewPage => _drag?.PageIndex ?? _stroke?.PageIndex ?? _box?.PageIndex ?? -1;

    public RelayCommand ToggleAnnotateMode { get; private set; } = null!;
    public RelayCommand<string> ChooseTool { get; private set; } = null!;
    public RelayCommand<PaletteColour> ChooseColour { get; private set; } = null!;

    private void SetUpAnnotateMode()
    {
        ToggleAnnotateMode = new RelayCommand(() => AnnotateMode = !AnnotateMode, () => _session is not null, OnCommandError);
        ChooseTool = new RelayCommand<string>(name =>
        {
            if (!Enum.TryParse<AnnotationTool>(name, out var tool)) return;
            ActiveTool = tool;
            AnnotateMode = true;
        });
        ChooseColour = new RelayCommand<PaletteColour>(colour => UseColour(colour.Index));
        ChooseThickness = new RelayCommand<string>(index => { if (int.TryParse(index, out int i)) UseThickness(i); });
    }

    /// <summary>
    /// A colour chosen (a swatch, or keys 1 to 5): the active tool marks with it from now on, and a
    /// selected annotation takes it.
    /// </summary>
    public void UseColour(int index)
    {
        if (index < 0 || index >= ReviewPalette.Count) return;
        if (_session?.Selected is { } selected && _review?.IsFromPdf(selected) != true) _session.Recolour(selected, ReviewPalette.Hex(index));
        // With the select tool, a colour is for the selection only: the tools keep their own.
        if (_tool != AnnotationTool.None)
        {
            ColourIndexOf(_tool);
            _colourIndices![_tool] = index;
            var indices = _colourIndices.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            PersistPreference(s => s with { AnnotationColourIndices = indices });
            _services.CurrentSettings = _services.CurrentSettings with { AnnotationColourIndices = indices };
            OnPropertyChanged(nameof(ActiveColourIndex));
            OnPropertyChanged(nameof(ActiveColour));
        }
        StatusMessage = $"{ReviewPalette.NameOf(_services.CurrentSettings, index)} ({index + 1}).";
    }

    /// <summary>The name annotations are signed with now (Settings ▸ Annotations).</summary>
    private string? Author => string.IsNullOrWhiteSpace(_services.CurrentSettings.AnnotationAuthor)
        ? null : _services.CurrentSettings.AnnotationAuthor.Trim();

    /// <summary>
    /// A left press on the page in annotate mode, in page points. True when it is taken (always, in
    /// annotate mode: a click there selects; it never reads from there).
    /// </summary>
    public bool AnnotatePress(int pageIndex, double pageX, double pageY, bool doubleClick = false, bool eraserTip = false)
    {
        if (!_annotateMode || _session is null) return false;
        CancelAnnotateDrag();
        float x = (float)pageX, y = (float)pageY;
        _pressedAt = (pageIndex, x, y);
        // The eraser, or a stylus turned to its eraser end whatever the tool: what it touches goes.
        if (_tool == AnnotationTool.Eraser || eraserTip)
        {
            _erasing = true;
            EraseAt(pageIndex, x, y);
            return true;
        }
        if (_tool == AnnotationTool.Pen)
        {
            _stroke = new StrokeDrag(pageIndex, x, y, ReviewPalette.Hex(ColourIndexOf(_tool)), ReviewPalette.OpacityFor(_tool), WidthOf(_tool));
            return true;
        }
        if (_tool == AnnotationTool.None && _session.Selected is FreehandAnnotation selectedStroke
            && _session.PageOf(selectedStroke) == pageIndex && StrokeResize.Begin(selectedStroke, x, y) is { } resize)
        {
            _resize = resize;   // a handle of the selected stroke: stretch it
            return true;
        }
        if (_tool == AnnotationTool.None)
        {
            // Select: a press on an annotation selects it, and a drag moves a note, a text box or a
            // drawing; a double click edits its words.
            var hit = AnnotationAt(pageIndex, x, y);
            _session.Selected = hit;
            if (hit is null) return true;
            if (doubleClick) { _pressedAt = null; OpenNoteEditorFor(hit); return true; }
            if (_review?.IsFromPdf(hit) != true) _move = AnnotationMove.Begin(hit, x, y);
            return true;
        }
        if (_tool is AnnotationTool.FreeText or AnnotationTool.Rectangle)
        {
            _boxTool = _tool;
            _box = new BoxDrag(pageIndex, x, y, ReviewPalette.Hex(ColourIndexOf(_tool)));
            return true;
        }
        if (_tool == AnnotationTool.TextNote) return true;   // placed on release
        if (_pageText?.For(pageIndex) is { } page)
        {
            var (drag, refusal) = MarkupDrag.Begin(page, _tool, ReviewPalette.Hex(ColourIndexOf(_tool)), ReviewPalette.OpacityFor(_tool), x, y);
            _drag = drag;
            if (refusal is not null) StatusMessage = refusal;
        }
        return true;
    }

    public void AnnotateMove(double pageX, double pageY)
    {
        float x = (float)pageX, y = (float)pageY;
        if (_erasing)
        {
            if (_pressedAt is { } at) EraseAt(at.Page, x, y);
            return;
        }
        if (_resize is { } resize)
        {
            resize.To(x, y);
            if (resize.Resized) _review?.MarkViewChanged();
            return;
        }
        if (_stroke is { } stroke)
        {
            stroke.To(x, y);
            InvalidateSurface?.Invoke();
            return;
        }
        if (_move is { } move)
        {
            move.To(x, y);
            if (move.Moved) _review?.MarkViewChanged();
            return;
        }
        if (_box is { } box)
        {
            box.To(x, y);
            InvalidateSurface?.Invoke();
            return;
        }
        if (_drag is null) return;
        _drag.Move(x, y);
        InvalidateSurface?.Invoke();
    }

    /// <summary>The press released: a drag marks its text, anything else is a click that selects.</summary>
    public void AnnotateRelease()
    {
        var drag = _drag;
        var pressed = _pressedAt;
        var move = _move;
        var box = _box;
        var stroke = _stroke;
        var resize = _resize;
        bool erasing = _erasing;
        _drag = null;
        _move = null;
        _box = null;
        _stroke = null;
        _resize = null;
        _erasing = false;
        _pressedAt = null;
        if (_session is null) return;
        if (erasing) return;
        if (resize is not null)
        {
            _session.Resized(resize);
            return;
        }
        if (stroke is not null)
        {
            if (stroke.IsStroke)
            {
                _session.Author = Author;
                _session.AddStroke(stroke.PageIndex, stroke.Points, stroke.Colour, stroke.Width, stroke.Opacity);
            }
            InvalidateSurface?.Invoke();
            return;
        }
        if (box is not null && _boxTool == AnnotationTool.Rectangle)
        {
            if (box.IsBox)
            {
                var (rx, ry, rw, rh) = box.Box;
                _session.Author = Author;
                _session.AddRectangle(box.PageIndex, rx, ry, rw, rh, ReviewPalette.Hex(ColourIndexOf(AnnotationTool.Rectangle)),
                    WidthOf(AnnotationTool.Rectangle));
            }
            InvalidateSurface?.Invoke();
            return;
        }
        if (move is not null)
        {
            _session.Moved(move);
            return;
        }
        if (box is not null)
        {
            var (bx, by, bw, bh) = box.IsBox ? box.Box : (box.Box.X, box.Box.Y, ReviewSession.DefaultTextWidth, ReviewSession.DefaultTextHeight);
            OpenNoteEditor(new NoteDraft(null, null, "", new Placement(AnnotationTool.FreeText, box.PageIndex, bx, by, bw, bh,
                ReviewPalette.Hex(ColourIndexOf(AnnotationTool.FreeText)))), "");
            return;
        }
        if (_tool == AnnotationTool.TextNote && pressed is { } point && drag is null)
        {
            OpenNoteEditor(new NoteDraft(null, null, "", new Placement(AnnotationTool.TextNote, point.Page, point.X, point.Y, 0, 0,
                ReviewPalette.Hex(ColourIndexOf(AnnotationTool.TextNote)))), "");
            return;
        }
        if (drag?.Range is { } range && _snapshot is { } snapshot && _pageText?.For(drag.PageIndex) is { } page
            && ReviewTarget.ForRange(Plan, snapshot, page, range, _pageText.Ligatures) is { } target)
        {
            _session.Author = Author;
            _session.Markup(target, drag.Tool, null, drag.Colour, drag.Opacity);
            StatusMessage = $"Marked: “{Shorten(target.QuotedText)}”. N on it writes a note; Delete removes it.";
        }
        else if (pressed is { } at) SelectAt(at.Page, at.X, at.Y);
        InvalidateSurface?.Invoke();
    }

    public void CancelAnnotateDrag()
    {
        bool had = _drag is not null || _box is not null || _stroke is not null;
        if (_move is { Moved: true } move)
        {
            move.Cancel();
            _review?.MarkViewChanged();
        }
        if (_resize is { Resized: true } resize)
        {
            resize.Cancel();
            _review?.MarkViewChanged();
        }
        _drag = null;
        _move = null;
        _box = null;
        _stroke = null;
        _resize = null;
        _erasing = false;
        _pressedAt = null;
        if (had) InvalidateSurface?.Invoke();
    }

    /// <summary>Selects the annotation under a page point (the topmost), or none.</summary>
    private void SelectAt(int pageIndex, float x, float y)
    {
        if (_session is null) return;
        var hit = AnnotationAt(pageIndex, x, y);
        _session.Selected = hit;
        if (hit is not null)
            StatusMessage = "Selected. A colour (1 to 5) recolours it, N writes its note, Delete removes it.";
    }

    /// <summary>Removes the annotation under a page point (and the others made with it), each one undoable.</summary>
    private void EraseAt(int pageIndex, float x, float y)
    {
        if (_session is null || AnnotationAt(pageIndex, x, y, includePdf: false) is not { } hit) return;
        _session.Delete(hit);
        StatusMessage = "Erased. Ctrl+Z brings it back.";
    }

    /// <summary>The topmost annotation under a page point: notes over drawings over markup, as drawn.</summary>
    private Annotation? AnnotationAt(int pageIndex, float x, float y, bool includePdf = true)
    {
        if (_review is null) return null;
        var onPage = RailReader.Renderer.Skia.AnnotationRenderer.SortByZOrder(
            [.. includePdf ? _review.PdfAnnotations(pageIndex) : [], .. _review.PageAnnotations(pageIndex)]);
        for (int i = onPage.Count - 1; i >= 0; i--)
            if (RailReader.Core.Services.AnnotationGeometry.HitTest(onPage[i], x, y)) return onPage[i];
        return null;
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "…";

    private void RefreshReviewCommands()
    {
        foreach (var command in new[] { NoteOnSentence, NoteOnPassage, HighlightSentence, HighlightPassage, SaveNote,
                     SaveNoteAndResume, CancelNote, UndoAnnotation, RedoAnnotation, DeleteSelectedAnnotation, ToggleAnnotateMode })
            command.RaiseCanExecuteChanged();
        RefreshRevisionCommands();
    }

    /// <summary>
    /// What was just read: the sentence of the last cue heard (or its whole passage), else the
    /// passage the reader is at. Null when there is nothing to put a note on.
    /// </summary>
    private ReviewTarget? JustRead(bool wholePassage)
    {
        if (_session is null || _pageText is null || Plan is not { } plan || _snapshot is not { } snapshot) return null;
        if (_lastCue is { } cue && cue.Generation == Coordinator.Generation && plan.Segment(cue.SegmentId) is not null)
            return ReviewTarget.Find(plan, snapshot, _pageText, cue.SegmentId, cue.AnchorIds, wholePassage);
        var position = Coordinator.Snapshot;
        if (position.SegmentId is not { } segmentId) return null;
        return ReviewTarget.Find(plan, snapshot, _pageText, segmentId, position.ActiveAnchorIds, wholePassage);
    }

    /// <summary>
    /// N (Shift+N for the passage): pauses, outlines what was just read and opens the note card.
    /// Saving keeps it paused (the reader chose so, ADR-0014); Ctrl+Enter saves and reads on.
    /// </summary>
    private async Task BeginNoteAsync(bool wholePassage)
    {
        // In annotate mode, a selected annotation is the one the note is for.
        if (_annotateMode && _session?.Selected is { } chosen)
        {
            if (_playback.IsAudible) await Coordinator.PauseAsync();
            OpenNoteEditorFor(chosen);
            return;
        }
        // Found before pausing: the pause must not move what "just read" means.
        if (JustRead(wholePassage) is not { } target)
        {
            StatusMessage = "Nothing has been read yet to put a note on.";
            return;
        }
        if (_playback.IsAudible) await Coordinator.PauseAsync();
        // Already marked: its note is the one to write or change.
        if (_session?.MadeOn(target) is { } existing) { OpenNoteEditorFor(existing); return; }
        OpenNoteEditor(new NoteDraft(target, null, target.QuotedText), "");
    }

    /// <summary>H (Shift+H for the passage): highlights what was just read, without stopping.</summary>
    private void HighlightJustRead(bool wholePassage)
    {
        if (_session is null) return;
        if (JustRead(wholePassage) is not { } target)
        {
            StatusMessage = "Nothing has been read yet to highlight.";
            return;
        }
        if (_session.MadeOn(target) is { } existing)
        {
            _session.Selected = existing;
            StatusMessage = wholePassage ? "This passage is already highlighted." : "This sentence is already highlighted.";
            return;
        }
        _session.Author = Author;
        _session.Highlight(target);
        StatusMessage = wholePassage ? "Passage highlighted." : "Sentence highlighted.";
    }

    /// <summary>Opens the note card on an annotation already made (its group's first, which holds the note).</summary>
    public void OpenNoteEditorFor(Annotation annotation)
    {
        if (_session is null || IsNoteEditorOpen) return;
        if (_review?.IsFromPdf(annotation) == true)
        {
            StatusMessage = "This annotation is part of the PDF: it is shown here, not changed.";
            return;
        }
        var head = _session.Head(annotation);
        _session.Selected = head;
        string text = head is TextNoteAnnotation note ? note.EffectiveContents : head.Contents ?? "";
        OpenNoteEditor(new NoteDraft(null, head, _session.LinkOf(head)?.QuotedText ?? ""), text);
    }

    private void OpenNoteEditor(NoteDraft draft, string text)
    {
        _draft = draft;
        NoteEditorText = text;
        OnPropertyChanged(nameof(IsNoteEditorOpen));
        OnPropertyChanged(nameof(NoteEditorQuote));
        OnPropertyChanged(nameof(NoteEditorTitle));
        OnPropertyChanged(nameof(NoteTarget));
        OnPropertyChanged(nameof(NoteAnchor));
        RefreshReviewCommands();
        InvalidateSurface?.Invoke();
        NoteEditorOpened?.Invoke();
    }

    private void CloseNoteEditor(string? reason = null)
    {
        if (_draft is null) return;
        _draft = null;
        NoteEditorText = "";
        OnPropertyChanged(nameof(IsNoteEditorOpen));
        OnPropertyChanged(nameof(NoteTarget));
        RefreshReviewCommands();
        InvalidateSurface?.Invoke();
        if (reason is not null) StatusMessage = reason;
    }

    /// <summary>
    /// The document is being left (closed, another opened, the application quit) with the note card
    /// open: what the reader wrote is kept, as Enter would keep it. An empty card is simply closed.
    /// </summary>
    private void KeepDraftOnLeaving()
    {
        if (_draft is null) return;
        if (!string.IsNullOrWhiteSpace(NoteEditorText)) CommitNote(resume: false);
        else CloseNoteEditor();
    }

    private void CommitNote(bool resume)
    {
        if (_draft is not { } draft || _session is null) return;
        string text = NoteEditorText;
        _session.Author = Author;
        if (draft.Existing is { } existing) _session.SetNote(existing, text);
        else if (draft.Placement is { } place && !string.IsNullOrWhiteSpace(text))
        {
            if (place.Kind == AnnotationTool.FreeText) _session.AddText(place.Page, place.X, place.Y, place.W, place.H, text, place.Colour);
            else _session.AddNote(place.Page, place.X, place.Y, text, place.Colour);
        }
        else if (draft.Target is { } target && !string.IsNullOrWhiteSpace(text)) _session.Highlight(target, text);
        CloseNoteEditor(string.IsNullOrWhiteSpace(text) && draft.Existing is null ? "No note written; nothing was added." : null);
        if (resume && !_playback.IsAudible && CanRead())
        {
            // Read on with the page clear: a selected note's popup would sit over the text being read.
            _session.Selected = null;
            RunObserved(StartReadingAsync(), "Could not read on");
        }
    }

    /// <summary>
    /// A note chosen in the list: a request to read. Playing, the reading moves there; otherwise
    /// its page is shown and the next Play starts there, as the outline does.
    /// </summary>
    private void GoToReviewNote(ReviewNoteItem item)
    {
        if (_session is null) return;
        if (item.Unplaced)
        {
            if (_snapshot is { } snapshot && item.PageNow < snapshot.Pages.Count) PageRequested?.Invoke(item.PageNow);
            StatusMessage = (item.HasQuote ? "Its words are not in this version." : "It was on no text.")
                + (item.PageNow != item.PageIndex
                    ? $" Its page is page {item.PageNow + 1} now, by the notes found there; Place as before puts it in the same place on it."
                    : " Place as before puts it in the same place on the same page.");
            return;
        }
        _session.Selected = item.Annotation;
        if (RailReader.Core.Services.AnnotationGeometry.GetAnnotationBounds(item.Annotation) is { } b)
            AnnotationFocusRequested?.Invoke(item.PageIndex, new PageRect(b.Left, b.Top, b.Right, b.Bottom));
        else PageRequested?.Invoke(item.PageIndex);
        if (item.SegmentId is not { } segmentId || Plan?.Segment(segmentId) is null) return;
        if (_playback.IsAudible)
        {
            _pendingStartSegmentId = null;
            RunObserved(Coordinator.GoToSegmentAsync(segmentId), "Could not move to that note");
            return;
        }
        _pendingStartSegmentId = segmentId;
        StatusMessage = $"Play reads from the note on page {item.PageIndex + 1}.";
    }

    /// <summary>
    /// A click on the page, in page points: a note's icon opens or closes it, a highlight's note
    /// mark selects it (which shows its note), a double click edits. True when the click was
    /// taken; otherwise the selection is cleared and the click is the page's (click to read).
    /// </summary>
    public bool ClickAnnotation(int pageIndex, double pageX, double pageY, bool doubleClick)
    {
        if (_session is not { } session || _review is not { } review || !ShowAnnotations) return false;
        float x = (float)pageX, y = (float)pageY;
        var onPage = review.PdfAnnotations(pageIndex).Concat(review.PageAnnotations(pageIndex)).ToList();
        for (int i = onPage.Count - 1; i >= 0; i--)
        {
            var annotation = onPage[i];
            bool hit = annotation switch
            {
                TextNoteAnnotation => RailReader.Core.Services.AnnotationGeometry.HitTest(annotation, x, y),
                TextMarkupAnnotation { Contents.Length: > 0 } markup =>
                    RailReader.Renderer.Skia.AnnotationRenderer.GetMarkupCommentGlyphRect(markup) is { } glyph
                    && glyph.Contains(x, y),
                _ => false,
            };
            if (!hit) continue;
            if (doubleClick) { OpenNoteEditorFor(annotation); return true; }
            if (annotation is TextNoteAnnotation note)
            {
                note.IsExpanded = !note.IsExpanded;
                review.MarkViewChanged();
            }
            session.Selected = ReferenceEquals(session.Selected, annotation) && annotation is not TextNoteAnnotation ? null : annotation;
            return true;
        }
        session.Selected = null;
        return false;
    }

    private void RebuildReviewNotes()
    {
        ReviewNotes.Clear();
        if (_session is { } session && _review is { } review)
        {
            var order = Plan?.Segments.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i) ?? [];
            var items = new List<(ReviewNoteItem Item, int Segment, float Top, float Left)>();
            foreach (var (page, list) in review.File.Pages)
                foreach (var annotation in list)
                {
                    var link = session.LinkOf(annotation);
                    if (link?.GroupId is { } group && group != annotation.NativeId) continue;   // shown with its group's first
                    var bounds = RailReader.Core.Services.AnnotationGeometry.GetAnnotationBounds(annotation);
                    string? note = annotation is TextNoteAnnotation n ? n.EffectiveContents : annotation.Contents;
                    var item = new ReviewNoteItem(annotation, page, link?.SegmentId, link?.QuotedText ?? "",
                        string.IsNullOrWhiteSpace(note) ? null : note);
                    int segment = link?.SegmentId is { } id && order.TryGetValue(id, out int at) ? at : int.MaxValue;
                    items.Add((item, segment, bounds?.Top ?? 0, bounds?.Left ?? 0));
                }
            foreach (var (page, list) in review.AllPdfAnnotations)
                foreach (var annotation in list.Where(a => a.InReplyTo is null || a.ReplyType == AnnotationReplyType.Group))
                {
                    var bounds = RailReader.Core.Services.AnnotationGeometry.GetAnnotationBounds(annotation);
                    string note = annotation.EffectiveContents;
                    // The PDF does not say what text its markup is on: the words under its boxes.
                    string quote = annotation is TextMarkupAnnotation markup && _pageText?.For(page) is { } text
                        ? PageQuote.UnderRects(text, markup.Rects, _pageText.Ligatures) : "";
                    items.Add((new ReviewNoteItem(annotation, page, null, quote, string.IsNullOrWhiteSpace(note) ? null : note, FromPdf: true),
                        int.MaxValue, bounds?.Top ?? 0, bounds?.Left ?? 0));
                }
            foreach (var x in items.OrderBy(x => x.Item.PageIndex).ThenBy(x => x.Segment).ThenBy(x => x.Top).ThenBy(x => x.Left))
                ReviewNotes.Add(x.Item);
            // Brought from an earlier version but not placed: last, by their page there.
            foreach (var (page, list) in review.Unplaced.Pages.OrderBy(p => p.Key))
                foreach (var annotation in list)
                {
                    var link = session.UnplacedLinkOf(annotation);
                    if (link?.GroupId is { } group && group != annotation.NativeId) continue;
                    if (annotation.InReplyTo is not null) continue;   // goes with the note it answers
                    string? note = annotation is TextNoteAnnotation n ? n.EffectiveContents : annotation.Contents;
                    ReviewNotes.Add(new ReviewNoteItem(annotation, session.EarlierPageOf(annotation) ?? page, null, link?.QuotedText ?? "",
                        string.IsNullOrWhiteSpace(note) ? null : note, Unplaced: true) { PageNow = page });
                }
        }
        OnPropertyChanged(nameof(HasReviewNotes));
        RefreshExportCommands();
    }

    private void AttachReview(ReviewLoadResult loaded)
    {
        DetachReview();
        _review = loaded.Document;
        _annotationLayer = new AnnotationLayer(loaded.Document);
        _review.Changed += OnReviewChanged;
        _review.ViewChanged += OnReviewViewChanged;
        if (_snapshot is { } snapshot) _pageText = new PageTextCache(snapshot);
        _session = new ReviewSession(loaded.Document);
        _session.SelectionChanged += OnReviewSelectionChanged;
        _lastCue = null;
        RebuildReviewNotes();
        RefreshReviewCommands();
        _lastAnnotationSaveError = null;
        if (loaded.Problem is { } problem)
        {
            Notes.Add($"Annotations: {problem}");
            ShowLastingNotice(problem);
        }
        else RunObserved(OfferEarlierNotesAsync(loaded.Document), "Could not look for notes on the earlier version");
    }

    private void DetachReview()
    {
        CloseNoteEditor("The note was not saved: the document changed.");
        if (_review is not null)
        {
            _review.Changed -= OnReviewChanged;
            _review.ViewChanged -= OnReviewViewChanged;
        }
        if (_session is not null) _session.SelectionChanged -= OnReviewSelectionChanged;
        _review = null;
        _annotationLayer = null;
        _session = null;
        _annotateMode = false;
        _drag = null;
        _move = null;
        _box = null;
        _stroke = null;
        _resize = null;
        _erasing = false;
        _pressedAt = null;
        OnPropertyChanged(nameof(AnnotateMode));
        _pageText = null;
        _lastCue = null;
        RebuildReviewNotes();
        RefreshReviewCommands();
    }

    private void OnReviewViewChanged() => InvalidateSurface?.Invoke();

    private void OnReviewSelectionChanged()
    {
        DeleteSelectedAnnotation.RaiseCanExecuteChanged();
        InvalidateSurface?.Invoke();
    }

    /// <summary>Every change is saved as it is made, and shown.</summary>
    private void OnReviewChanged()
    {
        if (_review is { } review) _ = SaveReviewAsync(review);
        RebuildReviewNotes();
        RefreshReviewCommands();
        InvalidateSurface?.Invoke();
    }

    private async Task SaveReviewAsync(ReviewDocument review)
    {
        try
        {
            await _services.ReviewAnnotations.SaveAsync(review);
            _lastAnnotationSaveError = null;
        }
        catch (Exception ex)
        {
            // Not thrown: nobody awaits a save. Said once per reason, not once per keystroke.
            if (_lastAnnotationSaveError == ex.Message) return;
            _lastAnnotationSaveError = ex.Message;
            Notes.Add($"Annotations were not saved: {ex.Message}");
            ShowLastingNotice($"Your annotations could not be saved: {ex.Message}");
        }
    }

    /// <summary>Waits for every annotation save asked for so far.</summary>
    private Task FlushReviewAsync() => _services.ReviewAnnotations.FlushAsync();

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
    {
        var notice = new StatusNotice(text, actionLabel, action);
        // A lasting notice is not replaced: the newest of the others waits behind it.
        if (_notice is { Lasting: true }) _waitingNotice = notice;
        else Notice = notice;
    }

    /// <summary>
    /// A notice the reader must see, about their own work (annotations not read or not saved).
    /// Later notices wait until it is dismissed; start-up's own, such as an engine that is not
    /// installed, would otherwise replace it before it could be read.
    /// </summary>
    public void ShowLastingNotice(string text, string? actionLabel = null, Action? action = null)
    {
        if (_notice is { Lasting: false } current) _waitingNotice = current;
        Notice = new StatusNotice(text, actionLabel, action, Lasting: true);
    }

    private StatusNotice? _waitingNotice;

    private void CloseNotice()
    {
        Notice = _waitingNotice;
        _waitingNotice = null;
    }

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
        KeepDraftOnLeaving();
        await FlushReviewAsync();
        await Coordinator.StopAsync();
        DetachReview();
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

    /// <summary>
    /// Phonon's model and 25 voices (ADR-0009), into the folder the application reads them from.
    /// Offered only where Phonon's runtime is present: beside the application in a release, or
    /// where tools/setup-phonon.sh put it.
    /// </summary>
    public RelayCommand DownloadPhonon { get; }

    // Whether each model is already where the application looks for it. Only a file's presence is
    // checked (hashing hundreds of MB on the UI thread is not acceptable); the download verifies it.
    // A model downloaded in this session counts as installed although it is used after a restart.

    private (bool, bool, bool, bool, bool, bool)? _modelsInstalled;

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

    private bool PhononRuntimePresent => File.Exists(ServiceRegistry.PhononPaths(_services.CurrentSettings).Library);

    private string PhononModelDirectory => ServiceRegistry.PhononPaths(_services.CurrentSettings).Model;

    private bool PhononModelInstalled => PhononModel.IsInstalled(PhononModelDirectory);

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

    public string DownloadPhononHeader => ModelMenuHeader(PhononModelInstalled,
        $"Download _Phonon speech model ({PhononModel.TotalBytes / 1_000_000.0:F0} MB)…", "Phonon speech model");

    /// <summary>Re-reads which models are installed, for the Models menu (called as the menu opens and after a download).</summary>
    public void RefreshModelMenu()
    {
        // Only on a change: a header notified while the menu opens is laid out again after the popup
        // has taken its width, and comes out clipped.
        var installed = (KokoroModelInstalled, KokoroTimedModelInstalled, LayoutModelInstalled, LayoutModelGpuInstalled, FormulaModelInstalled, PhononModelInstalled);
        if (installed == _modelsInstalled) return;
        _modelsInstalled = installed;
        OnPropertyChanged(nameof(DownloadKokoroModelHeader));
        OnPropertyChanged(nameof(DownloadKokoroTimedModelHeader));
        OnPropertyChanged(nameof(DownloadLayoutModelHeader));
        OnPropertyChanged(nameof(DownloadLayoutModelGpuHeader));
        OnPropertyChanged(nameof(DownloadFormulaModelHeader));
        OnPropertyChanged(nameof(DownloadPhononHeader));
        DownloadPhonon.RaiseCanExecuteChanged();
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
            KeepDraftOnLeaving();
            await FlushReviewAsync();
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
            var review = await Task.Run(() => _services.ReviewAnnotations.Load(hash, path));
            // The PDF's own annotations (a co-reviewer's), read-only. Core's reader never throws.
            var inPdf = _services.CurrentSettings.ShowPdfAnnotations
                ? await Task.Run(() => AnnotatedPdfExport.ReadExisting(path))
                : null;
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
            AttachReview(review);
            if (inPdf is not null) ShowPdfAnnotationsOf(inPdf);

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
        // The notice may be acted on after another document is opened: it narrates this one, to its own path.
        if (Plan is not { } plan || DocumentPath is not { } documentPath) return;
        int maths = plan.Segments.Count(s => s.Provenance == ScriptProvenance.PendingInterpretation);
        string mathsNote = maths == 0 ? "" : _services.MathInterpreter is null
            ? $" {maths} maths passages will be read as notation: no maths model is set up."
            : $" {maths} maths passages go to the maths model.";
        // What this reading has already fallen back on is saved as it is: the model is not asked again.
        var mathsUnits = plan.Units.Where(u => u.IsMaths || u.ContainsMaths).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        int fellBack = plan.Segments.Count(s => s.Provenance is ScriptProvenance.SourceVerbatim or ScriptProvenance.LocalRule or ScriptProvenance.FailureAnnouncement
            && s.ReadingUnitIds.Any(mathsUnits.Contains));
        // First, where a narrow status bar still shows it: it is the part the reader can act on.
        if (fellBack > 0 && _services.MathInterpreter is not null)
            mathsNote = $" {fellBack} maths passages were not interpreted in this reading and will be saved as they are (reload first to try again)." + mathsNote;
        ShowNotice($"Save a narration of all {plan.SegmentCount} passages?{mathsNote}",
            "Narrate", () => _ = NarrateDocumentAsync(plan, documentPath));
    }

    private async Task NarrateDocumentAsync(NarrationPlan plan, string documentPath, bool allowUninterpreted = false)
    {
        if (_narration is not null) return;
        // Worked out before the job is taken: nothing may throw between taking it and the finally that frees it.
        string target = SavedNarration.PathFor(documentPath);
        using var cts = new CancellationTokenSource();
        _narration = cts;
        NarrateDocument.RaiseCanExecuteChanged();
        StopNarration.RaiseCanExecuteChanged();
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
            _lastCue = cue;
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

    /// <summary>Asks before Phonon's model and voices are fetched, with their size, licence and source.</summary>
    private void ConfirmPhononDownload()
    {
        long mb = (long)Math.Round(PhononModel.TotalBytes / 1_000_000.0);
        ShowNotice($"Download Phonon (phonon-31m-en and 25 English voices): {mb} MB, {PhononModel.Licence}, from huggingface.co?", "Download",
            () => RunObserved(DownloadPhononAsync(), "The download failed"));
    }

    private async Task DownloadPhononAsync()
    {
        Busy = true;
        try
        {
            string directory = PhononModelDirectory;
            StatusMessage = $"Downloading Phonon ({PhononModel.TotalBytes / 1_000_000.0:F0} MB, {PhononModel.Licence})…";
            await PhononModel.InstallAsync(directory, progress: new Progress<(int Done, int Total, DownloadProgress File)>(p =>
                Dispatcher.UIThread.Post(() => StatusMessage =
                    $"Phonon: file {p.Done + 1} of {p.Total}, {p.File.Phase}"
                    + (p.File.BytesTotal > 0 ? $" {p.File.BytesReceived / 1_000_000.0:F1} / {p.File.BytesTotal / 1_000_000.0:F1} MB" : ""))));
            StatusMessage = "Phonon downloaded and verified.";
            Notes.Add($"Phonon is installed at {directory}. Restart to enable it.");
            ShowNotice("Phonon is installed. Rail Narrator uses it after a restart (Settings ▸ Speech ▸ Engine chooses between voices).",
                "Restart now", () => RestartRequested?.Invoke());
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
        // Busy is among their conditions too: a document's offer is set up while it is still loading.
        RefreshRevisionCommands();
        RefreshExportCommands();
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
        DownloadPhonon.RaiseCanExecuteChanged();
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
        KeepDraftOnLeaving();
        DetachReview();
        await FlushReviewAsync();
    }
}

/// <summary>A colour filter as the toolbar offers it.</summary>
public sealed record ColourEffectOption(ColourEffect Effect, string Name, string Description)
{
    public override string ToString() => Name;
}
