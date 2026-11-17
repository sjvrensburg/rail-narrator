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
        _viewModel.TopLevelProvider = () => this;
        // The surface only opens a document the view model reports as successfully loaded; a
        // display failure must not take narration down with it, so it is reported, not thrown.
        _viewModel.DocumentLoaded += path => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { Surface.OpenDocument(path); }
            catch (Exception ex)
            {
                _viewModel.Status($"Narration is ready, but the page could not be displayed: {ex.Message}");
            }
        });
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
        Closing += OnClosing;
    }

    private bool _closeReady;

    /// <summary>
    /// Saves the reading position before the window actually closes. The first close is held,
    /// disposal is awaited (with failures contained, never an unhandled async-void crash and
    /// never a blocking .Result on the UI thread), then the close is allowed through.
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        try { await _viewModel.DisposeAsync(); }
        catch (Exception) { /* the position may be lost, but closing must not crash */ }
        _closeReady = true;
        Close();
    }

    private NarrationHighlightStyle BuildHighlightStyle()
        => _viewModel.HighlightHighContrast
            ? NarrationHighlightStyle.HighContrast.WithAlpha(_viewModel.HighlightAlpha)
            : NarrationHighlightStyle.Default.WithAlpha(_viewModel.HighlightAlpha);

    private void OnOpenClicked(object? sender, RoutedEventArgs e)
        => _viewModel.RunObserved(_viewModel.OpenDocumentAsync(this), "Could not open a document");

    private void OnFitClicked(object? sender, RoutedEventArgs e) => Surface.FitToWidth();
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTranscriptSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TranscriptList.SelectedItem is TranscriptEntry entry)
            _viewModel.RunObserved(_viewModel.Coordinator.GoToSegmentAsync(entry.SegmentId), "Could not move to that passage");
    }

    private void OnMathsEditClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.MathsReview is null) return;
        _viewModel.EditedSpokenText = MathsEditBox.Text;
        _viewModel.ApplyMathsEdit.Execute(null);
    }

    private void OnMathsRegenerateClicked(object? sender, RoutedEventArgs e) => _viewModel.RegenerateMaths.Execute(null);

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
        if (pdf is not null) _viewModel.RunObserved(_viewModel.LoadDocumentAsync(pdf), "Could not open the document");
    }
}
