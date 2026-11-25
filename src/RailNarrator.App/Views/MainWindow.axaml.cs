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
            // At the end of the document nothing is being read: the last word is not left lit.
            if (e.PropertyName is nameof(MainViewModel.Playback) && _viewModel.Playback.State == PlaybackState.Completed)
                Surface.ClearHighlight();
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
        AddHandler(TextInputEvent, (_, e) => { if (_viewModel.IsPickerOpen) e.Handled = true; }, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        _viewModel.SettingsRequested += ShowSettings;
        // In code, not XAML: closing the window is the window's to do, and has no command to bind.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Q, KeyModifiers.Control), Command = new RelayCommand(Close) });
        _viewModel.RestartRequested += Restart;
        _viewModel.PageRequested += page => Surface.ShowPageFromTop(page);
        SetUpSidebar();
        SetUpNoteCard();
        SetUpFileDrop();
        SetUpFlows();
    }

    // ---- welcome, recent documents, dialogs ---------------------------------------------------

    private void SetUpFlows()
    {
        try
        {
            using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://rail-narrator/Assets/rail-narrator.png"));
            Icon = new WindowIcon(icon);
        }
        catch (Exception) { /* no icon is a cosmetic loss only */ }

        _viewModel.RecentDocuments.CollectionChanged += (_, _) => RebuildRecentMenu();
        RebuildRecentMenu();
        // A model may have been installed by hand or by the setup scripts since the last look.
        ModelsMenu.SubmenuOpened += (_, _) => _viewModel.RefreshModelMenu();
        _viewModel.RegionOverrideRequested += ApplyRegionOverride;
        // While a correction is being typed, the review stays on its passage.
        MathsEditBox.GotFocus += (_, _) => _viewModel.HoldMathsReview = true;
        // Leaving the box with an unsaved change keeps the hold, or the change would be dropped at
        // the next cue (and the Apply button, which takes the focus, would apply it to another passage).
        MathsEditBox.LostFocus += (_, _) => _viewModel.HoldMathsReview =
            _viewModel.MathsReview is { } review && MathsEditBox.Text != review.SpokenVersion;
        _viewModel.DocumentClosed += () =>
        {
            Surface.CloseDocument();
            Surface.ClearHighlight();
            PageLabel.Text = "Page –";
            Surface.InvalidateVisual();
        };
        _viewModel.ShortcutsRequested += () => ShowModal(new ShortcutsDialog());
        _viewModel.AboutRequested += () => ShowModal(new AboutDialog(_viewModel));
        _viewModel.GoToPageRequested += async () =>
        {
            if (Surface.PageCount == 0) return;
            var dialog = new GoToPageDialog(Surface.PageCount, Surface.CurrentPage) { FontSize = FontSize };
            if (await dialog.ShowDialog<int?>(this) is { } page) Surface.ShowPageFromTop(page);
        };
    }

    /// <summary>A modal dialog at the window's text size, as RailReader2 opens its dialogs.</summary>
    private void ShowModal(Window dialog)
    {
        dialog.FontSize = FontSize;
        _ = dialog.ShowDialog(this);
    }

    /// <summary>
    /// File ▸ Open recent: one item per recent document (a missing one marked, and choosing it offers
    /// to remove it), then the list's own housekeeping.
    /// </summary>
    private void RebuildRecentMenu()
    {
        var items = new List<object>();
        foreach (var recent in _viewModel.RecentDocuments)
        {
            items.Add(new MenuItem
            {
                // Escaped: a header string is access text, so "my_paper.pdf" would lose its underscore.
                Header = (recent.Exists ? recent.Name : $"{recent.Name} (not found)").Replace("_", "__"),
                Command = _viewModel.OpenRecent,
                CommandParameter = recent,
                [ToolTip.TipProperty] = recent.Path,
            });
        }
        if (items.Count > 0)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = "Remove _missing documents", Command = _viewModel.RemoveMissingRecent });
            items.Add(new MenuItem { Header = "_Clear recent documents", Command = _viewModel.ClearRecent });
        }
        RecentMenu.ItemsSource = items;
    }

    // The welcome screen's context menu on a recent document. Its items take the document from their
    // own data context, which the menu inherits from the card it was opened on.
    private void OnRecentOpenClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecentDocumentView recent) _viewModel.OpenRecent.Execute(recent);
    }

    private void OnRecentRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecentDocumentView recent) _viewModel.RemoveRecent.Execute(recent);
    }

    private void OnRecentRemoveMissingClicked(object? sender, RoutedEventArgs e) => _viewModel.RemoveMissingRecent.Execute(null);

    private void OnRecentClearClicked(object? sender, RoutedEventArgs e) => _viewModel.ClearRecent.Execute(null);

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
        _sections.AddRange([(OutlineSection, "Outline"), (ScriptSection, "Script"), (MathsSection, "MathsReview"), (ReviewSection, "Review"), (NotesSection, "Notes")]);
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
            // Only when the passage under review changes, not when the same one is refreshed: the
            // reader who has opened another section meanwhile keeps it open.
            if (e.PropertyName == nameof(MainViewModel.MathsReview))
            {
                string? segment = _viewModel.MathsReview?.SegmentId;
                if (segment is not null && segment != _reviewedSegmentId
                    && _viewModel.SidebarVisible && !ScriptSection.IsExpanded)
                    MathsSection.IsExpanded = true;
                _reviewedSegmentId = segment;
            }
        };
        SidebarSplitter.DragCompleted += (_, _) =>
        {
            _sidebarWidth = Math.Clamp(ContentGrid.ColumnDefinitions[0].ActualWidth, SidebarMinWidth, SidebarMaxWidth);
            _viewModel.PersistSettings(s => s with { SidebarWidth = Math.Round(_sidebarWidth) });
        };
    }

    private const double SidebarMinWidth = 200, SidebarMaxWidth = 600;
    private string? _reviewedSegmentId;
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

    /// <summary>
    /// A note chosen in the Review notes list. As the outline: the selection changes only when the
    /// reader chooses (the list is rebuilt, never synced to the reading), so this does not seek on
    /// its own.
    /// </summary>
    private void OnReviewNoteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReviewNotesList.SelectedItem is ReviewNoteItem item) _viewModel.OpenReviewNote.Execute(item);
    }

    // ---- the note card -------------------------------------------------------------------------

    private void SetUpNoteCard()
    {
        _viewModel.NoteEditorOpened += () =>
        {
            if (_viewModel.NoteAnchor is { } anchor && Surface.ScreenRectOf(anchor.Page, anchor.Rect) is null)
                Surface.GoToPage(anchor.Page, anchor.Rect);
            // After layout, so the page shown and the card's own size are known.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                PlaceNoteCard();
                NoteEditorBox.Focus();
                NoteEditorBox.CaretIndex = NoteEditorBox.Text?.Length ?? 0;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        };
        NoteEditorBox.AddHandler(KeyDownEvent, OnNoteEditorKeyDown, RoutingStrategies.Tunnel);
        _viewModel.AnnotationFocusRequested += (page, rect) => Surface.GoToPage(page, rect);
        _viewModel.PropertyChanged += (_, e) =>
        {
            // The card gone: the keyboard goes back to the page, so Space plays again.
            if (e.PropertyName == nameof(MainViewModel.IsNoteEditorOpen) && !_viewModel.IsNoteEditorOpen) Surface.Focus();
        };
    }

    /// <summary>Below the text the note is on, else above it, else at the foot of the page; inside the reading area.</summary>
    private void PlaceNoteCard()
    {
        if (Surface.Parent is not Control area) return;
        // Measured without the margin it was last placed with: a control's desired size includes its
        // margin, and the card measured as its own size plus where it last stood.
        NoteCard.Margin = default;
        NoteCard.Measure(Avalonia.Size.Infinity);
        double width = NoteCard.DesiredSize.Width, height = NoteCard.DesiredSize.Height;
        double areaW = area.Bounds.Width, areaH = area.Bounds.Height;
        const double gap = 10, edge = 8;
        double x, y;
        if (_viewModel.NoteAnchor is { } anchor && Surface.ScreenRectOf(anchor.Page, anchor.Rect) is { } text)
        {
            x = text.Left;
            y = text.Bottom + gap + height <= areaH - edge ? text.Bottom + gap
                : text.Top - gap - height >= edge ? text.Top - gap - height
                : areaH - height - edge;
        }
        else
        {
            x = (areaW - width) / 2;
            y = areaH - height - 2 * edge;
        }
        x = Math.Clamp(x, edge, Math.Max(edge, areaW - width - edge));
        y = Math.Clamp(y, edge, Math.Max(edge, areaH - height - edge));
        NoteCard.Margin = new Avalonia.Thickness(x, y, 0, 0);
    }

    private void OnNoteEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.CancelNote.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            _viewModel.SaveNote.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            _viewModel.SaveNoteAndResume.Execute(null);
            e.Handled = true;
        }
        // Shift+Enter: the text box's own new line.
    }

    private SettingsWindow? _settingsWindow;
    private bool _restartAfterClose;

    /// <summary>
    /// Opens the Settings dialogue, or brings it forward if it is open. Not modal, as in RailReader2:
    /// a highlight change can be watched on the page while it is made.
    /// </summary>
    /// <param name="page">The page to show by its name ("Maths", "Speech"), or null for the one it is on.</param>
    private void ShowSettings(string? page)
    {
        if (_settingsWindow is { } open) { open.ShowPage(page); open.Activate(); return; }
        var settings = new SettingsViewModel(_viewModel);
        var window = new SettingsWindow(settings) { FontSize = FontSize };
        window.ShowPage(page);
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
            // The welcome screen's picture of it, once: the first page, small, off this thread.
            if (_viewModel.PendingThumbnailPath is { } thumbnail)
            {
                try { await Surface.SaveFirstPageThumbnailAsync(thumbnail); _viewModel.RefreshRecent(); }
                catch (Exception) { /* a thumbnail is only a picture: the document is open */ }
            }
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
        _viewModel.HoldMathsReview = false;
    }

    private void OnMathsRegenerateClicked(object? sender, RoutedEventArgs e) => _viewModel.RegenerateMaths.Execute(null);

    /// <summary>
    /// Marks the currently highlighted region as maths, or restores it to prose. Inline detection is
    /// heuristic and a model-free extraction has no semantic roles at all, so the reader must be able
    /// to correct it.
    /// </summary>
    private void ApplyRegionOverride(bool treatAsMaths)
    {
        var cue = _viewModel.Playback.ActiveCue;
        var map = _viewModel.Coordinator.Scheduler?.SourceMap;
        if (cue is null || map is null) { _viewModel.Status("Play or click a passage first: the passage being read is the one corrected."); return; }
        var pages = map.HighlightRectsByPage(cue.AnchorIds);
        foreach (var (page, rects) in pages)
            foreach (var rect in rects)
                _viewModel.SetRegionMathsOverride(page, rect, treatAsMaths);
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
        // A file dialogue is not modal to this window: keys reaching it meanwhile are not for it.
        if (_viewModel.IsPickerOpen) { e.Handled = true; return; }
        var focused = FocusManager?.GetFocusedElement();
        if (HandleReviewKey(e, focused)) { e.Handled = true; return; }
        if (e.KeyModifiers != KeyModifiers.None) return;

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
            // A text box uses Escape to give up focus; a menu or drop-down to close.
            if (focused is TextBox || MainMenu.IsOpen || IsInsideOpenDropDown(focused)) return;
            // The same wherever focus is: back to the reading after browsing, otherwise pause.
            if (_viewModel.FollowSuspended && _viewModel.ResumeFollow.CanExecute(null))
            {
                _viewModel.ResumeFollow.Execute(null);
                e.Handled = true;
            }
            else if (_viewModel.Playback.IsAudible && _viewModel.Pause.CanExecute(null))
            {
                _viewModel.Pause.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            // Page keys turn pages wherever focus is, except where a control uses them itself.
            if (focused is TextBox or ListBox or ListBoxItem or TreeView or TreeViewItem or Slider or ComboBox
                || MainMenu.IsOpen || IsInsideOpenDropDown(focused)) return;
            if (Surface.HandleNavigationKey(e.Key)) e.Handled = true;
        }
    }

    /// <summary>
    /// The annotation keys (ADR-0014): N and H on what was just read (Shift for the whole
    /// passage), Ctrl+Z and Ctrl+Shift+Z (or Ctrl+Y) to undo and redo, Delete for the selected
    /// annotation. None of them while typing or in a menu: here and not as window key bindings,
    /// which fire inside a text box and would take its own undo.
    /// </summary>
    private bool HandleReviewKey(KeyEventArgs e, Avalonia.Input.IInputElement? focused)
    {
        if (focused is TextBox || MainMenu.IsOpen || IsInsideOpenDropDown(focused)) return false;
        var mods = e.KeyModifiers;
        bool shift = mods == KeyModifiers.Shift, plain = mods == KeyModifiers.None;
        bool ctrl = mods == KeyModifiers.Control, ctrlShift = mods == (KeyModifiers.Control | KeyModifiers.Shift);

        // Annotate mode: 1 to 5 choose a colour (and recolour the selection); Escape leaves the
        // mode (before it pauses, as it would otherwise).
        if (_viewModel.AnnotateMode && plain)
        {
            int colour = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2,
                Key.D4 or Key.NumPad4 => 3, Key.D5 or Key.NumPad5 => 4, _ => -1,
            };
            if (colour >= 0) { _viewModel.UseColour(colour); return true; }
            if (e.Key == Key.Escape) { _viewModel.AnnotateMode = false; return true; }
        }
        RelayCommand? command = e.Key switch
        {
            Key.N when plain => _viewModel.NoteOnSentence,
            Key.N when shift => _viewModel.NoteOnPassage,
            Key.H when plain => _viewModel.HighlightSentence,
            Key.H when shift => _viewModel.HighlightPassage,
            Key.Z when ctrl => _viewModel.UndoAnnotation,
            Key.Z when ctrlShift => _viewModel.RedoAnnotation,
            Key.Y when ctrl => _viewModel.RedoAnnotation,
            Key.Delete when plain => _viewModel.DeleteSelectedAnnotation,
            Key.E when ctrl => _viewModel.ToggleAnnotateMode,
            _ => null,
        };
        if (command is null || !command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
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
