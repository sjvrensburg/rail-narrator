using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RailNarrator.App.Composition;
using RailNarrator.Domain;
using RailNarrator.Infrastructure;
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
    private string _statusMessage = "Open a PDF to begin.";
    private bool _busy;
    private int _selectedTranscriptIndex;
    private double _zoom = 1.0;
    private string? _selectedVoiceId;
    private double _playbackRate = 1.0;
    private FollowMode _followMode = FollowMode.KeepVisible;
    private bool _followSuspended;
    private bool _transcriptVisible = true;
    private MathsReview? _mathsReview;

    public MainViewModel(ServiceRegistry services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _zoom = services.CurrentSettings.PreferredZoom;
        _playbackRate = services.CurrentSettings.PlaybackRate;
        _followMode = services.CurrentSettings.FollowMode;
        _selectedVoiceId = services.CurrentSettings.KokoroVoiceId ?? services.CurrentSettings.ZonosSpeakerId;
        _highlightAlpha = services.CurrentSettings.HighlightAlpha;
        _highlightHighContrast = services.CurrentSettings.HighlightHighContrast;

        foreach (string note in services.StartupNotes) Notes.Add(note);

        _services.Coordinator.SnapshotChanged += OnSnapshotChanged;
        _services.Coordinator.SegmentResolved += OnSegmentResolved;
        _services.Coordinator.ActiveCueChanged += OnActiveCueChanged;
        _services.Coordinator.FollowRequested += OnFollowRequested;

        OpenDocument = new RelayCommand(() => OpenDocumentAsync(TopLevelProvider?.Invoke()), () => !Busy, OnCommandError);
        Play = new RelayCommand(() => _services.Coordinator.PlayAsync(), () => Plan is { SegmentCount: > 0 }, OnCommandError);
        Pause = new RelayCommand(() => _services.Coordinator.PauseAsync(), () => _playback.State == PlaybackState.Playing, OnCommandError);
        Stop = new RelayCommand(() => _services.Coordinator.StopAsync(),
            () => Plan is { SegmentCount: > 0 } && _playback.State is not PlaybackState.Idle, OnCommandError);
        Next = new RelayCommand(() => _services.Coordinator.NextSegmentAsync(), () => Plan is { SegmentCount: > 0 }, OnCommandError);
        Previous = new RelayCommand(() => _services.Coordinator.PreviousSegmentAsync(), () => Plan is { SegmentCount: > 0 }, OnCommandError);
        Replay = new RelayCommand(() => _services.Coordinator.ReplaySegmentAsync(), () => Plan is { SegmentCount: > 0 }, OnCommandError);
        ToggleFollow = new RelayCommand(CycleFollowMode, null, OnCommandError);
        ResumeFollow = new RelayCommand(() => _services.Coordinator.ResumeFollow(), () => FollowSuspended, OnCommandError);
        ZoomIn = new RelayCommand(() => ApplyZoom(Zoom * 1.25), null, OnCommandError);
        ZoomOut = new RelayCommand(() => ApplyZoom(Zoom / 1.25), null, OnCommandError);
        Zoom200 = new RelayCommand(() => ApplyZoom(2.0), null, OnCommandError);
        Zoom400 = new RelayCommand(() => ApplyZoom(4.0), null, OnCommandError);
        Zoom800 = new RelayCommand(() => ApplyZoom(8.0), null, OnCommandError);
        TestMathEndpoint = new RelayCommand(() => TestMathEndpointAsync(), () => !Busy, OnCommandError);
        TestVisionEndpoint = new RelayCommand(() => TestVisionEndpointAsync(), () => !Busy, OnCommandError);
        DownloadKokoroModel = new RelayCommand(() => DownloadModelAsync(ModelManifest.KokoroFp16), () => !Busy, OnCommandError);
        DownloadLayoutModel = new RelayCommand(() => DownloadModelAsync(ModelManifest.LayoutPpDocV3), () => !Busy, OnCommandError);
        RegenerateMaths = new RelayCommand(() => RegenerateMathsAsync(), () => MathsReview is { CanRegenerate: true }, OnCommandError);
        ApplyMathsEdit = new RelayCommand(() => ApplyMathsEditAsync(), () => !Busy, OnCommandError);
    }

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

    /// <summary>Raised when the camera should move to a page and region.</summary>
    public event Action<int, IReadOnlyList<PageRect>>? CameraRequested;

    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<TranscriptEntry> Transcript { get; } = [];
    public ObservableCollection<TtsVoice> Voices { get; } = [];

    /// <summary>Pitch-preserving playback rates offered in the transport bar.</summary>
    public IReadOnlyList<double> PlaybackRates { get; } = [0.75, 1.0, 1.25, 1.5, 2.0];
    public ObservableCollection<string> AudioDevices { get; } = [];

    public ServiceRegistry Services => _services;
    public NarrationCoordinator Coordinator => _services.Coordinator;

    public string? DocumentPath { get => _documentPath; private set { if (Set(ref _documentPath, value)) OnPropertyChanged(nameof(HasDocument)); } }
    public bool HasDocument => _documentPath is not null;
    public ExtractionSnapshot? Snapshot => _snapshot;
    public NarrationPlan? Plan { get => _plan; private set { if (Set(ref _plan, value)) { OnPropertyChanged(nameof(SegmentCount)); } } }
    public int SegmentCount => Plan?.SegmentCount ?? 0;

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) RefreshCommands(); } }

    public PlaybackSnapshot Playback { get => _playback; private set => Set(ref _playback, value); }

    public int SelectedTranscriptIndex
    {
        get => _selectedTranscriptIndex;
        set
        {
            if (!Set(ref _selectedTranscriptIndex, value)) return;
            if (value >= 0 && value < Transcript.Count)
                RunObserved(Coordinator.GoToSegmentAsync(Transcript[value].SegmentId), "Could not move to that passage");
        }
    }

    public double Zoom
    {
        get => _zoom;
        set { if (Set(ref _zoom, Math.Clamp(value, 0.25, 8.0))) OnPropertyChanged(nameof(ZoomPercent)); }
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
            Coordinator.SetFollowMode(value);
            PersistPreference(s => s with { FollowMode = value });
        }
    }

    public bool FollowSuspended { get => _followSuspended; private set => Set(ref _followSuspended, value); }

    public bool TranscriptVisible { get => _transcriptVisible; set => Set(ref _transcriptVisible, value); }

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
            // Only the Kokoro voice is a free preference: the ZONOS3 speaker id also decides whether
            // that provider is registered at all, so it stays a deliberate settings-file choice.
            if (_voiceProviderId == "kokoro" && !_choosingVoiceForReader)
                PersistPreference(s => s with { KokoroVoiceId = id });
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

    public string MathEndpointSummary => _services.CurrentSettings.MathLlmBaseUrl is { } url
        ? $"{url} / {_services.CurrentSettings.MathLlmModel}"
        : "not configured";

    public string VisionEndpointSummary => _services.CurrentSettings.VlmBaseUrl is { } url
        ? $"{url} / {_services.CurrentSettings.VlmModel}"
        : "not configured";

    public string SpeechProviderSummary
        => string.Join(", ", _services.SpeechProviders.RegisteredIds);

    /// <summary>The layout analyzer and its device. Fixed for the run: the backend is built once at startup.</summary>
    public string LayoutSummary => _services.LayoutSummary;

    public long CacheBytes => _services.AssetStore.TotalBytes() + _services.CachedExtraction.TotalBytes();
    private string _timingSummary = "Sentence highlighting until word alignment is available.";
    public string TimingSummary { get => _timingSummary; private set => Set(ref _timingSummary, value); }

    // ---- commands --------------------------------------------------------------------

    public RelayCommand OpenDocument { get; }
    public RelayCommand Play { get; }
    public RelayCommand Pause { get; }
    public RelayCommand Stop { get; }
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
    public RelayCommand TestMathEndpoint { get; }
    public RelayCommand TestVisionEndpoint { get; }
    public RelayCommand DownloadKokoroModel { get; }
    public RelayCommand DownloadLayoutModel { get; }
    public RelayCommand RegenerateMaths { get; }
    public RelayCommand ApplyMathsEdit { get; }

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
    public async Task LoadDocumentAsync(string path)
    {
        if (!File.Exists(path)) { StatusMessage = $"No such file: {path}"; return; }
        if (Busy) return;   // re-entrancy: argv/menu/keyboard can all call this directly

        // Build into locals and commit DocumentPath/_snapshot/Plan together only on success. A
        // failed open (corrupt PDF, empty extraction) then leaves the previous document fully
        // intact, instead of a half-state where the new path was paired with the old plan — which
        // also corrupted the saved reading position on exit.
        string? previousPath = DocumentPath;
        bool committed = false;
        Busy = true;
        try
        {
            // The document being replaced keeps its place, even if the new one fails to open.
            await SavePositionAsync();
            await Coordinator.StopAsync();
            StatusMessage = "Reading the document…";

            string hash = CacheKey.Sha256HexOfFile(path);
            var cachedPlan = await _services.PlanStore.TryGetPlanAsync(hash);

            _services.Extraction.AttachDocument(path);
            var request = _services.CreateExtractionRequest(path);
            // Progress is posted, so a report can land after extraction has returned — with a cache
            // hit, after "Ready" — and overwrite the status. Reports are ignored once it is done.
            bool extracting = true;
            var snapshot = await _services.CachedExtraction.ExtractAsync(request,
                new Progress<ExtractionProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    if (extracting) StatusMessage = $"{p.Stage}: page {p.PagesDone} of {p.PagesTotal}";
                })));
            extracting = false;

            foreach (string warning in snapshot.AllWarnings().Take(8))
                if (!Notes.Contains(warning)) Notes.Add(warning);

            if (snapshot.Pages.All(p => p.Coverage.CharCount == 0))
            {
                StatusMessage = "This document has no readable text. Enable OCR in settings and try again.";
                return;
            }

            StatusMessage = "Finding the reading order…";
            var units = _services.CreateUnitBuilder(_overrides).Build(snapshot, out var diagnostics);
            foreach (string d in diagnostics) if (!Notes.Contains(d)) Notes.Add(d);

            if (units.Count == 0)
            {
                StatusMessage = "Nothing narratable was found. Check the content filters in settings.";
                return;
            }

            StatusMessage = units.Any(u => u.IsMaths)
                ? "Preparing narration, including mathematical expressions…"
                : "Preparing narration…";

            var planner = _services.CreatePlanner();
            // A cached plan is reusable only if it was built from the inputs in force now, not
            // merely from the same file: changed filters, maths mode or region overrides rebuild.
            string fingerprint = NarrationPlanner.ComputeInputFingerprint(
                planner.Options, _services.CurrentSettings.Filters, units);
            bool reuseCached = cachedPlan is { } c && c.Matches(snapshot.Identity, fingerprint);
            var plan = reuseCached
                ? cachedPlan!
                : await planner.BuildPlanAsync(snapshot, units, _services.CurrentSettings.Filters);

            if (!reuseCached)
                await _services.PlanStore.StorePlanAsync(plan);

            // ---- commit point: everything below describes the new document ----
            _snapshot = snapshot;
            Plan = plan;
            DocumentPath = path;
            _documentHash = hash;
            committed = true;

            RebuildTranscript();
            await LoadVoicesAsync();

            var position = await _services.PlanStore.TryGetPositionAsync(hash);
            await Coordinator.LoadPlanAsync(plan, position);
            if (position is not null) ShowRestoredPreferences(position);
            // The starting passage is where the reader already is; only a move is worth saving.
            _lastSavedSegmentId = Coordinator.Snapshot.SegmentId;

            StatusMessage = position is { SegmentId: not null }
                ? $"Ready. Restored your position ({plan.SegmentCount} passages)."
                : $"Ready. {plan.SegmentCount} passages to read.";
            OnPropertyChanged(nameof(MathEndpointSummary));
            OnPropertyChanged(nameof(CacheBytes));
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
            Busy = false;
            InvalidateSurface?.Invoke();
        }
    }

    private void RebuildTranscript()
    {
        Transcript.Clear();
        if (Plan is null) return;
        int i = 0;
        foreach (var segment in Plan.Segments)
            Transcript.Add(new TranscriptEntry(segment.Id, i++, segment.SpokenText, segment.IsMaths, segment.HasWarnings));
    }

    private async Task LoadVoicesAsync()
    {
        Voices.Clear();
        AudioDevices.Clear();
        var provider = _services.SpeechProviders.Get(_services.CurrentSettings.SpeechProviderId)
                       ?? await _services.SpeechProviders.FirstAvailableAsync();
        if (provider is null) return;
        _voiceProviderId = provider.ProviderId;

        foreach (var voice in await provider.ListVoicesAsync()) Voices.Add(voice);
        foreach (var device in _services.AudioDevice.Enumerate())
            AudioDevices.Add($"{device.Id}: {device.Name}{(device.IsDefault ? " (default)" : "")}");

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

    /// <summary>Applies a zoom, remembering it as the reader's preference.</summary>
    public void ApplyZoom(double zoom)
    {
        Zoom = zoom;
        // The user's magnification is a preference, not a transient state: follow mode must restore it
        // rather than snapping back to a fit.
        PersistSettings(s => s with { PreferredZoom = Zoom });
        OnPropertyChanged(nameof(ZoomPercent));
    }

    private void CycleFollowMode()
    {
        FollowMode = FollowMode switch
        {
            FollowMode.Off => FollowMode.KeepVisible,
            FollowMode.KeepVisible => FollowMode.ReadingRail,
            _ => FollowMode.Off,
        };
    }

    private DispatcherTimer? _followResumeTimer;

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
        StatusMessage = seconds > 0
            ? $"Following paused while you browse; it resumes {seconds:0.#} s after you stop (or press F9). Audio continues."
            : "Following paused while you browse. Press F9 to resume. Audio continues.";
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
            if (snapshot.Error is { } error) StatusMessage = error.Message;
            else if (snapshot.Message is { } message) StatusMessage = message;
            // Playing carries no message of its own, so the line used to keep whatever was said
            // last ("Seeking", "Preparing speech") for as long as the passage lasted.
            else if (snapshot.State == PlaybackState.Playing && snapshot.SegmentCount > 0)
                StatusMessage = $"Reading passage {snapshot.SegmentIndex + 1} of {snapshot.SegmentCount}.";
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
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void OnFollowRequested(ActiveCue cue, IReadOnlyList<PageRect> rects, int page)
    {
        void Apply()
        {
            if (cue.Generation == Coordinator.Generation) CameraRequested?.Invoke(page, rects);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void UpdateMathsReview(string segmentId)
    {
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
            CanRegenerate: (_services.MathVerbaliser is not null || _services.MathInterpreter is not null) && unit is not null);
        OnPropertyChanged(nameof(MathsReview));
    }

    private async Task RegenerateMathsAsync()
    {
        if (MathsReview is null || Plan is null || _snapshot is null) return;
        var unit = Plan.Segment(MathsReview.SegmentId)?.ReadingUnitIds.Select(id => Plan.Unit(id)).FirstOrDefault(u => u is not null);
        if (_services.MathInterpreter is not null && unit is { Kind: ReadingUnitKind.DisplayMath } or { ContainsMaths: true })
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
        StatusMessage = treatAsMaths ? "Region marked as maths. Reload the document to apply."
                                     : "Region restored to prose. Reload the document to apply.";
    }

    private async Task TestMathEndpointAsync()
    {
        Busy = true;
        try
        {
            if (_services.MathVerbaliser is null) { StatusMessage = "No text LLM is configured."; return; }
            bool ok = await _services.MathVerbaliser.IsAvailableAsync();
            StatusMessage = ok ? "Text LLM endpoint responded." : "Text LLM endpoint is not reachable.";
        }
        finally { Busy = false; }
    }

    private async Task TestVisionEndpointAsync()
    {
        Busy = true;
        try
        {
            if (_services.MathTranscriber is null) { StatusMessage = "No vision model is configured."; return; }
            bool ok = await _services.MathTranscriber.IsAvailableAsync();
            // A text-only endpoint must not be reported as vision-capable; the probe is the only
            // honest answer.
            StatusMessage = ok ? "Vision endpoint responded." : "Vision endpoint is not reachable or is text-only.";
        }
        finally { Busy = false; }
    }

    private async Task DownloadModelAsync(ModelAsset asset)
    {
        Busy = true;
        try
        {
            // The size and licence are shown before anything is fetched; a surprise multi-hundred-MB
            // download is not acceptable behaviour for a local-first app.
            StatusMessage = $"Downloading {asset.Id} ({asset.HumanSize}, {asset.Licence})…";
            await _services.Models.DownloadAsync(asset, new Progress<DownloadProgress>(p =>
                Dispatcher.UIThread.Post(() => StatusMessage =
                    p.BytesTotal > 0
                        ? $"{p.Phase}: {p.BytesReceived / 1_000_000.0:F1} / {p.BytesTotal / 1_000_000.0:F1} MB"
                        : $"{p.Phase}…")));
            StatusMessage = $"{asset.Id} downloaded and verified.";
            Notes.Add($"{asset.Id} is installed at {_services.Models.PathFor(asset)}. Restart to enable it.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
        finally { Busy = false; }
    }

    /// <summary>Persists a settings change. Never writes a credential.</summary>
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
    }

    // The loaded document's content hash, computed once at load rather than on every save.
    private string? _documentHash;
    private string? _lastSavedSegmentId;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private void RefreshCommands()
    {
        Play.RaiseCanExecuteChanged();
        Pause.RaiseCanExecuteChanged();
        Stop.RaiseCanExecuteChanged();
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
        ApplyMathsEdit.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _services.Coordinator.SnapshotChanged -= OnSnapshotChanged;
        _services.Coordinator.SegmentResolved -= OnSegmentResolved;
        _services.Coordinator.ActiveCueChanged -= OnActiveCueChanged;
        _services.Coordinator.FollowRequested -= OnFollowRequested;
        await SavePositionAsync();
    }
}
