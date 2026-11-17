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
public sealed record TranscriptEntry(string SegmentId, int Index, string Text, bool IsMaths, bool HasWarning);

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
        _services.Coordinator.ActiveCueChanged += OnActiveCueChanged;
        _services.Coordinator.FollowRequested += OnFollowRequested;

        OpenDocument = new RelayCommand(() => OpenDocumentAsync(), () => !Busy);
        Play = new RelayCommand(() => _services.Coordinator.PlayAsync(), () => Plan is { SegmentCount: > 0 });
        Pause = new RelayCommand(() => _services.Coordinator.PauseAsync());
        Stop = new RelayCommand(() => _services.Coordinator.StopAsync());
        Next = new RelayCommand(() => _services.Coordinator.NextSegmentAsync());
        Previous = new RelayCommand(() => _services.Coordinator.PreviousSegmentAsync());
        Replay = new RelayCommand(() => _services.Coordinator.ReplaySegmentAsync());
        ToggleFollow = new RelayCommand(CycleFollowMode);
        ResumeFollow = new RelayCommand(() => _services.Coordinator.ResumeFollow(), () => FollowSuspended);
        ZoomIn = new RelayCommand(() => ApplyZoom(Zoom * 1.25));
        ZoomOut = new RelayCommand(() => ApplyZoom(Zoom / 1.25));
        Zoom200 = new RelayCommand(() => ApplyZoom(2.0));
        Zoom400 = new RelayCommand(() => ApplyZoom(4.0));
        Zoom800 = new RelayCommand(() => ApplyZoom(8.0));
        TestMathEndpoint = new RelayCommand(() => TestMathEndpointAsync(), () => !Busy);
        TestVisionEndpoint = new RelayCommand(() => TestVisionEndpointAsync(), () => !Busy);
        DownloadKokoroModel = new RelayCommand(() => DownloadModelAsync(ModelManifest.KokoroFp16), () => !Busy);
        RegenerateMaths = new RelayCommand(() => RegenerateMathsAsync(), () => MathsReview is { CanRegenerate: true });
        ApplyMathsEdit = new RelayCommand(() => ApplyMathsEditAsync(), () => !Busy);
    }

    /// <summary>Raised when the PDF surface must repaint (a new highlight, a page change).</summary>
    public event Action? InvalidateSurface;

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
                _ = Coordinator.GoToSegmentAsync(Transcript[value].SegmentId);
        }
    }

    public double Zoom
    {
        get => _zoom;
        set { if (Set(ref _zoom, Math.Clamp(value, 0.25, 8.0))) OnPropertyChanged(nameof(ZoomPercent)); }
    }

    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    public double PlaybackRate { get => _playbackRate; set { if (Set(ref _playbackRate, value)) Coordinator.SetPlaybackRate(value); } }

    public FollowMode FollowMode
    {
        get => _followMode;
        set { if (Set(ref _followMode, value)) Coordinator.SetFollowMode(value); }
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
            _ = Coordinator.SetVoiceAsync(id);
        }
    }

    private TtsVoice? _selectedVoice;

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

    public long CacheBytes => _services.AssetStore.TotalBytes();

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

        Busy = true;
        try
        {
            await Coordinator.StopAsync();
            DocumentPath = path;
            StatusMessage = "Reading the document…";

            string hash = CacheKey.Sha256HexOfFile(path);
            var cachedPlan = await _services.PlanStore.TryGetPlanAsync(hash);

            _services.Extraction.AttachDocument(path);
            var request = _services.CreateExtractionRequest(path);
            _snapshot = await _services.Extraction.ExtractAsync(request,
                new Progress<ExtractionProgress>(p => Dispatcher.UIThread.Post(
                    () => StatusMessage = $"{p.Stage}: page {p.PagesDone} of {p.PagesTotal}")));

            foreach (string warning in _snapshot.AllWarnings().Take(8))
                if (!Notes.Contains(warning)) Notes.Add(warning);

            if (_snapshot.Pages.All(p => p.Coverage.CharCount == 0))
            {
                StatusMessage = "This document has no readable text. Enable OCR in settings and try again.";
                Plan = null;
                Transcript.Clear();
                return;
            }

            StatusMessage = "Finding the reading order…";
            var units = _services.CreateUnitBuilder(_overrides).Build(_snapshot, out var diagnostics);
            foreach (string d in diagnostics) if (!Notes.Contains(d)) Notes.Add(d);

            if (units.Count == 0)
            {
                StatusMessage = "Nothing narratable was found. Check the content filters in settings.";
                Plan = null;
                Transcript.Clear();
                return;
            }

            StatusMessage = units.Any(u => u.IsMaths)
                ? "Preparing narration, including mathematical expressions…"
                : "Preparing narration…";

            var planner = _services.CreatePlanner();
            Plan = cachedPlan is { } cached && cached.Matches(_snapshot.Identity)
                ? cached
                : await planner.BuildPlanAsync(_snapshot, units, _services.CurrentSettings.Filters);

            if (cachedPlan is null || !cachedPlan.Matches(_snapshot.Identity))
                await _services.PlanStore.StorePlanAsync(Plan);

            RebuildTranscript();
            await LoadVoicesAsync();

            var position = await _services.PlanStore.TryGetPositionAsync(hash);
            await Coordinator.LoadPlanAsync(Plan, position);

            StatusMessage = position is { SegmentId: not null }
                ? $"Ready. Restored your position ({Plan.SegmentCount} passages)."
                : $"Ready. {Plan.SegmentCount} passages to read.";
            OnPropertyChanged(nameof(MathEndpointSummary));
            OnPropertyChanged(nameof(CacheBytes));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the document: {ex.Message}";
            Notes.Add($"Open failed for '{path}': {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
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

        foreach (var voice in await provider.ListVoicesAsync()) Voices.Add(voice);
        foreach (var device in _services.AudioDevice.Enumerate())
            AudioDevices.Add($"{device.Id}: {device.Name}{(device.IsDefault ? " (default)" : "")}");

        SelectedVoice = Voices.FirstOrDefault(v => v.IsDefault) ?? Voices.FirstOrDefault();
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

    /// <summary>Called by the surface when the reader pans or navigates by hand.</summary>
    public void NotifyManualNavigation()
    {
        if (FollowSuspended) return;
        Coordinator.SuspendFollow();
        FollowSuspended = true;
        StatusMessage = "Following paused while you browse. Audio continues.";
    }

    private void OnSnapshotChanged(PlaybackSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
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
            RefreshCommands();
        });
    }

    private int SelectedTranscriptIndexCore
    {
        get => _selectedTranscriptIndex;
        set => _selectedTranscriptIndex = value;
    }

    private void OnActiveCueChanged(ActiveCue cue)
    {
        Dispatcher.UIThread.Post(() =>
        {
            InvalidateSurface?.Invoke();
            UpdateMathsReview(cue.SegmentId);
        });
    }

    private void OnFollowRequested(ActiveCue cue, IReadOnlyList<PageRect> rects, int page)
        => Dispatcher.UIThread.Post(() => CameraRequested?.Invoke(page, rects));

    private void UpdateMathsReview(string segmentId)
    {
        var segment = Plan?.Segment(segmentId);
        if (segment is not { IsMaths: true }) { MathsReview = null; return; }
        var unit = segment.ReadingUnitIds.Select(id => Plan?.Unit(id)).FirstOrDefault(u => u is not null);
        MathsReview = new MathsReview(
            segment.Id,
            CropImagePath: null,
            RecognisedExpression: unit?.Latex ?? "(not recognised)",
            SpokenVersion: segment.SpokenText,
            Warnings: segment.Warnings,
            Provenance: segment.Provenance,
            CanRegenerate: _services.MathVerbaliser is not null && unit is not null);
        OnPropertyChanged(nameof(MathsReview));
    }

    private async Task RegenerateMathsAsync()
    {
        if (MathsReview is null || Plan is null || _snapshot is null) return;
        Busy = true;
        try
        {
            StatusMessage = "Re-reading the expression…";
            var units = _services.CreateUnitBuilder(_overrides).Build(_snapshot);
            var planner = _services.CreatePlanner();
            Plan = await planner.BuildPlanAsync(_snapshot, units, _services.CurrentSettings.Filters);
            RebuildTranscript();
            await Coordinator.LoadPlanAsync(Plan);
            StatusMessage = "Regenerated.";
        }
        finally { Busy = false; }
    }

    private async Task ApplyMathsEditAsync()
    {
        if (MathsReview is null || string.IsNullOrEmpty(EditedSpokenText)) return;
        // An edit invalidates this segment's speech and timings; the coordinator handles that.
        await Coordinator.ApplyScriptEditAsync(MathsReview.SegmentId, EditedSpokenText);
        RebuildTranscript();
        StatusMessage = "Your correction will be used, and the previous audio for it discarded.";
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
        if (DocumentPath is null || Plan is null) return;
        string hash = CacheKey.Sha256HexOfFile(DocumentPath);
        var snapshot = Coordinator.Snapshot;
        await _services.PlanStore.SavePositionAsync(new ReaderPosition(
            hash, snapshot.SegmentId, PageIndex: 0, snapshot.PositionSeconds,
            SelectedVoiceId, _services.CurrentSettings.MathMode, Zoom, FollowMode, PlaybackRate));
    }

    private void RefreshCommands()
    {
        Play.RaiseCanExecuteChanged();
        OpenDocument.RaiseCanExecuteChanged();
        ResumeFollow.RaiseCanExecuteChanged();
        RegenerateMaths.RaiseCanExecuteChanged();
        TestMathEndpoint.RaiseCanExecuteChanged();
        TestVisionEndpoint.RaiseCanExecuteChanged();
        DownloadKokoroModel.RaiseCanExecuteChanged();
        ApplyMathsEdit.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _services.Coordinator.SnapshotChanged -= OnSnapshotChanged;
        _services.Coordinator.ActiveCueChanged -= OnActiveCueChanged;
        _services.Coordinator.FollowRequested -= OnFollowRequested;
        await SavePositionAsync();
    }
}
