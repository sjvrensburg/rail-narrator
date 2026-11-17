using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RailNarrator.App.Composition;
using RailNarrator.App.ViewModels;
using RailNarrator.Domain;
using RailNarrator.Pdf;

namespace RailNarrator.App.Views;

/// <summary>
/// The main window: a PDF reading area, playback controls, transcript, maths review and settings.
/// </summary>
/// <remarks>
/// Every command has a keyboard binding, a visible label and a place in the tab order. A reader who
/// cannot use a pointer must be able to play, pause, move by unit, zoom and resume following without
/// one — and the highlight colour and motion behaviour are configurable because a fixed value is
/// unreadable for some readers.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ServiceRegistry _services;
    private readonly MainViewModel _viewModel;

    public MainWindow(ServiceRegistry services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        DataContext = _viewModel = new MainViewModel(services);
        InitializeComponent();

        Surface.Attach(_viewModel);
        Surface.SetHighlightStyle(BuildHighlightStyle());
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.HighlightAlpha) or nameof(MainViewModel.HighlightHighContrast))
                Surface.SetHighlightStyle(BuildHighlightStyle());
        };
        _viewModel.Coordinator.ActiveCueChanged += cue =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var pages = _viewModel.Coordinator.Scheduler?.SourceMap.HighlightRectsByPage(cue.AnchorIds);
                if (pages is null) { Surface.ClearHighlight(); return; }
                Surface.SetHighlightMultiPage(pages);
            });
        };
        Closed += async (_, _) => await _viewModel.DisposeAsync();
    }

    private NarrationHighlightStyle BuildHighlightStyle()
        => _viewModel.HighlightHighContrast
            ? NarrationHighlightStyle.HighContrast.WithAlpha(_viewModel.HighlightAlpha)
            : NarrationHighlightStyle.Default.WithAlpha(_viewModel.HighlightAlpha);

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
        => await _viewModel.OpenDocumentAsync(this);

    private void OnPlayClicked(object? sender, RoutedEventArgs e) => _viewModel.Play.Execute(null);
    private void OnPauseClicked(object? sender, RoutedEventArgs e) => _viewModel.Pause.Execute(null);
    private void OnStopClicked(object? sender, RoutedEventArgs e) => _viewModel.Stop.Execute(null);
    private void OnPreviousClicked(object? sender, RoutedEventArgs e) => _viewModel.Previous.Execute(null);
    private void OnNextClicked(object? sender, RoutedEventArgs e) => _viewModel.Next.Execute(null);
    private void OnReplayClicked(object? sender, RoutedEventArgs e) => _viewModel.Replay.Execute(null);
    private void OnFollowClicked(object? sender, RoutedEventArgs e) => _viewModel.ToggleFollow.Execute(null);
    private void OnResumeFollowClicked(object? sender, RoutedEventArgs e) => _viewModel.ResumeFollow.Execute(null);
    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => _viewModel.ZoomIn.Execute(null);
    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => _viewModel.ZoomOut.Execute(null);
    private void OnFitClicked(object? sender, RoutedEventArgs e) => Surface.FitToWidth();
    private void OnZoom200Clicked(object? sender, RoutedEventArgs e) => _viewModel.ApplyZoom(2.0);
    private void OnZoom400Clicked(object? sender, RoutedEventArgs e) => _viewModel.ApplyZoom(4.0);
    private void OnZoom800Clicked(object? sender, RoutedEventArgs e) => _viewModel.ApplyZoom(8.0);
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTranscriptSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TranscriptList.SelectedItem is TranscriptEntry entry)
            _ = _viewModel.Coordinator.GoToSegmentAsync(entry.SegmentId);
    }

    private void OnMathsEditClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.MathsReview is null) return;
        _viewModel.EditedSpokenText = MathsEditBox.Text;
        _viewModel.ApplyMathsEdit.Execute(null);
    }

    private void OnMathsRegenerateClicked(object? sender, RoutedEventArgs e) => _viewModel.RegenerateMaths.Execute(null);

    private void OnDownloadModelClicked(object? sender, RoutedEventArgs e) => _viewModel.DownloadKokoroModel.Execute(null);
    private void OnTestMathEndpointClicked(object? sender, RoutedEventArgs e) => _viewModel.TestMathEndpoint.Execute(null);
    private void OnTestVisionEndpointClicked(object? sender, RoutedEventArgs e) => _viewModel.TestVisionEndpoint.Execute(null);

    /// <summary>
    /// Marks the currently highlighted region as maths, or restores it to prose. Inline detection is
    /// heuristic and a model-free extraction has no semantic roles at all, so the reader must be able
    /// to correct it.
    /// </summary>
    private void OnMarkAsMathsClicked(object? sender, RoutedEventArgs e) => ApplyRegionOverride(treatAsMaths: true);

    private void OnRestoreAsProseClicked(object? sender, RoutedEventArgs e) => ApplyRegionOverride(treatAsMaths: false);

    private void ApplyRegionOverride(bool treatAsMaths)
    {
        var cue = _viewModel.Playback.ActiveCue;
        var map = _viewModel.Coordinator.Scheduler?.SourceMap;
        if (cue is null || map is null) { _viewModel.Status("Select a passage first."); return; }
        var pages = map.HighlightRectsByPage(cue.AnchorIds);
        foreach (var (page, rects) in pages)
            foreach (var rect in rects)
                _viewModel.SetRegionMathsOverride(page, rect, treatAsMaths);
        _viewModel.Status(treatAsMaths ? "Region marked as maths. Reopen the document to apply."
                                       : "Region restored to prose. Reopen the document to apply.");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Space is play/pause unless a text field has focus, where it must still type a space.
        if (e.Key == Key.Space && FocusManager?.GetFocusedElement() is not TextBox)
        {
            if (_viewModel.Playback.State == PlaybackState.Playing) _viewModel.Pause.Execute(null);
            else _viewModel.Play.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // A document passed on the command line is opened; it is never auto-played.
        string[] args = Environment.GetCommandLineArgs();
        string? pdf = args.Skip(1).FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is not null) _ = _viewModel.LoadDocumentAsync(pdf);
    }
}
