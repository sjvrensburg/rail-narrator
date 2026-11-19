using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

        Surface.PageChanged += (page, count) =>
        {
            PageLabel.Text = count > 0 ? $"Page {page + 1} / {count}" : "Page –";
            _viewModel.ShowingPage(page);
        };
        Surface.Attach(_viewModel);
        ApplyHighlightStyle();
        _viewModel.TopLevelProvider = () => this;
        // The surface only opens a document the view model reports as successfully loaded; a
        // display failure must not take narration down with it, so it is reported, not thrown.
        _viewModel.DocumentLoaded += path => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ShowDocumentAsync(path));
        Surface.SetColourEffect(_viewModel.ColourEffect, _viewModel.ColourEffectIntensity);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.HighlightAlpha) or nameof(MainViewModel.HighlightHighContrast))
                ApplyHighlightStyle();
            if (e.PropertyName is nameof(MainViewModel.ColourEffect) or nameof(MainViewModel.ColourEffectIntensity))
                Surface.SetColourEffect(_viewModel.ColourEffect, _viewModel.ColourEffectIntensity);
            // The pointer says "busy" wherever it is in the window while a document is opened.
            if (e.PropertyName is nameof(MainViewModel.IsLoading))
                Cursor = _viewModel.IsLoading ? new Cursor(StandardCursorType.AppStarting) : null;
        };
        _viewModel.Coordinator.ActiveCueChanged += cue =>
        {
            void Apply()
            {
                if (cue.Generation != _viewModel.Coordinator.Generation) return;
                var map = _viewModel.Coordinator.Scheduler?.SourceMap;
                var pages = map?.HighlightRectsByPage(cue.AnchorIds);
                if (map is null || pages is null) { Surface.ClearHighlight(); return; }
                // One box height per line, the same across the paragraph (a box sized to each word's
                // ink jumps up and down as the reading moves along the line), and the boxes joined
                // into one shape, so a passage lit all at once is not a stack of overlapping boxes.
                // The coordinator's plan holds the segment as resolved: a pending maths passage is
                // known to be a paraphrase only once it has been interpreted.
                var segment = _viewModel.Coordinator.Plan?.Segment(cue.SegmentId) ?? map.Segment(cue.SegmentId);
                bool paraphrase = segment?.Provenance is ScriptProvenance.ModelGenerated or ScriptProvenance.LocalRule;
                Surface.SetHighlightMultiPage(pages.ToDictionary(
                    p => p.Key,
                    p => HighlightLines.Passage(p.Value, map.UnitRects(cue.SegmentId, p.Key))), paraphrase);
            }
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Apply();
            else Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        _viewModel.SettingsRequested += ShowSettings;
        _viewModel.RestartRequested += Restart;
        _viewModel.PageRequested += page => Surface.ShowPageFromTop(page);
        SetUpSidebar();
        SetUpFileDrop();
    }

    // ---- dropping a PDF onto the window -------------------------------------------------------

    /// <summary>
    /// A PDF dragged from a file manager onto the window is opened, as from File ▸ Open. RailReader2
    /// has no drop; the reader asked for it here. Only a local file ending in .pdf is accepted, so
    /// the pointer says "no" over the window for anything else.
    /// </summary>
    private void SetUpFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropHint.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    /// <summary>The first local PDF among the dragged items, or null.</summary>
    private static string? DraggedPdf(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return null;
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path
                && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path))
                return path;
        }
        return null;
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        string? path = DraggedPdf(e);
        e.DragEffects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        DropHintText.Text = path is null ? "" : $"Drop to open {Path.GetFileName(path)}";
        DropHint.IsVisible = path is not null;
        e.Handled = true;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        e.Handled = true;
        if (DraggedPdf(e) is not { } path)
        {
            _viewModel.Status("Only a PDF file on this computer can be opened.");
            return;
        }
        Activate();
        _viewModel.RunObserved(_viewModel.LoadDocumentAsync(path), "Could not open the dropped file");
    }

    // ---- sidebar ---------------------------------------------------------------------------

    private readonly List<(Expander Section, string Name)> _sections = [];
    private bool _syncingSections;

    /// <summary>
    /// The sidebar as RailReader2's: shown and hidden with the toggle (F6), its width dragged and
    /// remembered, and its sections an accordion in which one is open at a time and fills the panel.
    /// </summary>
    private void SetUpSidebar()
    {
        _sections.AddRange([(OutlineSection, "Outline"), (ScriptSection, "Script"), (MathsSection, "MathsReview"), (NotesSection, "Notes")]);
        string open = _services.CurrentSettings.SidebarSection;
        foreach (var (section, name) in _sections)
        {
            section.IsExpanded = name == open;
            section.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) OnSectionToggled(section, name); };
        }
        if (!_sections.Any(s => s.Section.IsExpanded)) OutlineSection.IsExpanded = true;
        UpdateSectionRows();

        _sidebarWidth = Math.Clamp(_services.CurrentSettings.SidebarWidth, SidebarMinWidth, SidebarMaxWidth);
        ApplySidebarVisibility();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SidebarVisible)) ApplySidebarVisibility();
            if (e.PropertyName == nameof(MainViewModel.DocumentName))
                Title = _viewModel.DocumentName is { } name ? $"{name} — Rail Narrator" : "Rail Narrator";
            // A passage chosen for review opens its section, so the reader sees it.
            if (e.PropertyName == nameof(MainViewModel.MathsReview) && _viewModel.MathsReview is not null
                && _viewModel.SidebarVisible && !ScriptSection.IsExpanded)
                MathsSection.IsExpanded = true;
        };
        SidebarSplitter.DragCompleted += (_, _) =>
        {
            _sidebarWidth = Math.Clamp(ContentGrid.ColumnDefinitions[0].ActualWidth, SidebarMinWidth, SidebarMaxWidth);
            _viewModel.PersistSettings(s => s with { SidebarWidth = Math.Round(_sidebarWidth) });
        };
    }

    private const double SidebarMinWidth = 200, SidebarMaxWidth = 600;
    private double _sidebarWidth = 300;

    private void ApplySidebarVisibility()
    {
        bool visible = _viewModel.SidebarVisible;
        var column = ContentGrid.ColumnDefinitions[0];
        if (!visible && column.ActualWidth >= SidebarMinWidth) _sidebarWidth = column.ActualWidth;
        Sidebar.IsVisible = visible;
        SidebarSplitter.IsVisible = visible;
        column.MinWidth = visible ? SidebarMinWidth : 0;
        column.MaxWidth = visible ? SidebarMaxWidth : double.PositiveInfinity;
        column.Width = new GridLength(visible ? _sidebarWidth : 0);
    }

    private void OnSectionToggled(Expander section, string name)
    {
        if (_syncingSections) return;
        _syncingSections = true;
        try
        {
            if (section.IsExpanded)
            {
                foreach (var (other, _) in _sections) if (other != section) other.IsExpanded = false;
                _viewModel.PersistSettings(s => s with { SidebarSection = name });
            }
            UpdateSectionRows();
        }
        finally { _syncingSections = false; }
    }

    /// <summary>The open section's row fills the sidebar; the others are a header's height.</summary>
    private void UpdateSectionRows()
    {
        for (int i = 0; i < _sections.Count; i++)
            Accordion.RowDefinitions[i].Height = _sections[i].Section.IsExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    /// <summary>
    /// A heading chosen in the outline. Selection changes only when the reader chooses (nothing
    /// syncs the tree to the reading), so this is not the trap the script list's selection is.
    /// </summary>
    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutlineTree.SelectedItem is OutlineItem item) _viewModel.OpenOutlineItem(item);
    }

    private SettingsWindow? _settingsWindow;
    private bool _restartAfterClose;

    /// <summary>
    /// Opens the Settings dialogue, or brings it forward if it is open. Not modal, as in RailReader2:
    /// a highlight change can be watched on the page while it is made.
    /// </summary>
    private void ShowSettings()
    {
        if (_settingsWindow is { } open) { open.Activate(); return; }
        var settings = new SettingsViewModel(_viewModel);
        var window = new SettingsWindow(settings) { FontSize = FontSize };
        settings.RestartRequested += () =>
        {
            window.Close();
            Restart();
        };
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show(this);
    }

    /// <summary>Closes (saving the position) and starts again with the same document.</summary>
    private void Restart()
    {
        _restartAfterClose = true;
        Close();   // OnClosing saves the position, then relaunches
    }

    /// <summary>
    /// Starts Rail Narrator again with the document that was open, after this instance has saved
    /// where the reader was. The new instance resumes there (a document opened from the command
    /// line is loaded, never played).
    /// </summary>
    private void Relaunch()
    {
        try
        {
            string? executable = Environment.ProcessPath;
            if (executable is null) return;
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
            // Run as "dotnet rail-narrator.dll": the host needs the assembly again.
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(MainWindow).Assembly.Location);
            if (_viewModel.DocumentPath is { } document) start.ArgumentList.Add(document);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception)
        {
            // Nothing to report to: this window is closing. The reader can start the app again.
        }
    }

    /// <summary>
    /// Opens a loaded document in the reading surface and, when the reading position was restored,
    /// shows the passage it will resume from rather than the first page.
    /// </summary>
    private async Task ShowDocumentAsync(string path)
    {
        try
        {
            await Surface.OpenDocumentAsync(path);
            _viewModel.ShowOutline(Surface.Outline);
            if (_viewModel.RestoredSegmentId is { } segmentId) Surface.ShowPassage(segmentId);
        }
        catch (Exception ex)
        {
            _viewModel.Status($"Narration is ready, but the page could not be displayed: {ex.Message}");
        }
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
        if (_restartAfterClose) Relaunch();
        Close();
    }

    private void ApplyHighlightStyle()
    {
        byte alpha = _viewModel.HighlightAlpha;
        if (_viewModel.HighlightHighContrast)
            Surface.SetHighlightStyle(NarrationHighlightStyle.HighContrast.WithAlpha(alpha),
                NarrationHighlightStyle.ParaphraseHighContrast.WithAlpha(alpha));
        else
            Surface.SetHighlightStyle(NarrationHighlightStyle.Default.WithAlpha(alpha),
                NarrationHighlightStyle.Paraphrase.WithAlpha(alpha));
    }

    private void OnOpenClicked(object? sender, RoutedEventArgs e)
        => _viewModel.RunObserved(_viewModel.OpenDocumentAsync(this), "Could not open a document");

    private void OnPreviousPageClicked(object? sender, RoutedEventArgs e) => Surface.TurnPage(-1);

    private void OnNextPageClicked(object? sender, RoutedEventArgs e) => Surface.TurnPage(+1);

    private void OnCycleColourFilterClicked(object? sender, RoutedEventArgs e) => _viewModel.CycleColourEffect();

    private void OnColourFilterClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } && Enum.TryParse<RailReader.Core.Models.ColourEffect>(name, out var effect))
            _viewModel.ColourEffect = effect;
    }
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

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

    /// <summary>
    /// Space, C and Escape, handled before any child control sees them.
    /// </summary>
    /// <remarks>
    /// These were window key bindings, which fire wherever focus is: typing a space into the maths
    /// correction box started playback, and pressing Escape to dismiss a menu stopped it. Handling
    /// them here lets the context decide. Space toggles play/pause unless a text box has focus, and
    /// does so even when a toolbar button is focused (a button would otherwise swallow it and click
    /// itself); while a start is waiting for audio it cancels that start. Escape pauses playback
    /// only when it is not being used to close a menu or drop-down.
    /// </remarks>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        var focused = FocusManager?.GetFocusedElement();

        if (e.Key == Key.Space)
        {
            if (focused is TextBox || MainMenu.IsOpen || IsInsideOpenDropDown(focused)) return;
            if (_viewModel.PlayPause.CanExecute(null)) _viewModel.PlayPause.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            // RailReader2's key for cycling the colour filter. Not while typing, or in a menu.
            if (focused is TextBox || MainMenu.IsOpen || IsInsideOpenDropDown(focused)) return;
            _viewModel.CycleColourEffect();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (MainMenu.IsOpen || IsInsideOpenDropDown(focused)) return;
            // The PDF surface uses Escape to resume following; a text box uses it to give up focus.
            if (focused is PdfSurface or TextBox) return;
            if (_viewModel.Playback.State == PlaybackState.Playing && _viewModel.Pause.CanExecute(null))
            {
                _viewModel.Pause.Execute(null);
                e.Handled = true;
            }
        }
    }

    private static bool IsInsideOpenDropDown(Avalonia.Input.IInputElement? focused)
        => focused is ComboBox { IsDropDownOpen: true } or ComboBoxItem or MenuItem;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // A document passed on the command line is opened; it is never auto-played.
        string[] args = Environment.GetCommandLineArgs();
        string? pdf = args.Skip(1).FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is not null) _viewModel.RunObserved(_viewModel.LoadDocumentAsync(pdf), "Could not open the document");
    }
}
