using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailNarrator.App.ViewModels;
using RailNarrator.Domain;
using RailNarrator.Pdf;

namespace RailNarrator.App.Views;

/// <summary>
/// The PDF reading surface: renders the page with RailReaderCore's Skia backend and paints narration
/// highlights above it.
/// </summary>
/// <remarks>
/// <para>
/// Highlights are transient overlay draws, never PDF annotations. Writing them into the document would
/// modify the reader's own file and would collide with an annotation store another RailReader
/// application keeps.
/// </para>
/// <para>
/// Each anchor rectangle is drawn separately. Their union would cover the gap between lines and, for a
/// sentence split across two columns, the gutter and both columns — darkening text the narration is
/// not reading.
/// </para>
/// <para>
/// Painting never renders a page and never calls PDFium. Rasters come from a
/// <see cref="PageRasterSource"/> worker; the paint draws the best one it has for the page (the exact
/// one, else the page at another zoom, else a low-resolution preview fetched ahead for the
/// neighbouring pages, else blank paper) and the sharp raster replaces it when it arrives. The page
/// used to be rendered inside the paint, so every page turn stalled the window for the length of a
/// render plus however long another thread held PDFium's lock.
/// </para>
/// </remarks>
public sealed class PdfSurface : Control
{
    /// <summary>Where a passage chosen by hand is placed vertically: the follow camera's reading height.</summary>
    private static readonly double FollowVerticalBias = FollowOptions.Default.VerticalBias;

    /// <summary>Resolution of the previews kept for the pages around the one shown.</summary>
    private const int PreviewDpi = 72;

    /// <summary>Previews are fetched for this many pages either side, nearest first, next page before previous.</summary>
    private static readonly int[] PreviewOffsets = [1, -1, 2, -2];

    /// <summary>Previews further than this from the page shown are dropped.</summary>
    private const int PreviewKeepRadius = 4;

    /// <summary>
    /// A page with nothing to show gets its preview before its sharp raster, as long as previews of
    /// this document render within this time. Paging quickly then shows each page going by instead
    /// of blank paper. Where a preview costs as much as the page itself (a page of dense vector
    /// drawing), rendering it first would only delay the sharp page.
    /// </summary>
    private static readonly TimeSpan QuickPreview = TimeSpan.FromMilliseconds(100);

    private TimeSpan _previewRenderTime;

    private PageRasterSource? _source;
    private int _openGeneration;
    // Every raster held, by what it is a raster of: all unfiltered, the colour filter is applied as
    // they are drawn (FilteredPageDraw). Touched on the UI thread only.
    private readonly Dictionary<PageRasterKey, Shared<SkiaSharp.SKImage>> _rasters = [];
    private readonly Dictionary<PageRasterKey, string> _failed = [];
    private PageRasterKey[] _requested = [];
    // When the page shown was turned to, for the opt-in log of how long it took to become sharp.
    private long _pageShownAt;
    private ViewZoom _magnification = new();
    private double _offsetX, _offsetY;
    private Point _dragStart;
    private (double X, double Y) _dragOffsetStart;
    private bool _dragging;
    private MainViewModel? _viewModel;

    private IReadOnlyDictionary<int, IReadOnlyList<PageRect>> _highlightsByPage =
        new Dictionary<int, IReadOnlyList<PageRect>>();
    private NarrationHighlightStyle _highlightStyle = NarrationHighlightStyle.Default;
    private NarrationHighlightStyle _paraphraseStyle = NarrationHighlightStyle.Paraphrase;
    private bool _highlightIsParaphrase;

    /// <summary>How far past a wide page's left or right edge the view may go, as a fraction of its width.</summary>
    private const double HorizontalOverscroll = 0.5;

    private ColourEffect _colourEffect = ColourEffect.None;
    private float _colourEffectIntensity = 1f;
    // The filter the page is drawn through; null for none. Built on the UI thread.
    private Shared<SkiaSharp.SKColorFilter>? _pageFilter;
    private ColourEffectShaders? _shaders;

    private bool _frameRequested;
    private TimeSpan? _lastFrameTime;
    private RuntimeCounters? _lastFrameCounters;

    // The camera's glide: where the visible region's top-left is heading, in page points, and how
    // fast it is moving there. Null when the camera is at rest. Kept in page points rather than
    // screen offsets so a zoom during a glide does not send it to the wrong place.
    private (double X, double Y)? _glideTarget;
    private double _glideVelocityX, _glideVelocityY;
    private double _glideSeconds;

    public PdfSurface()
    {
        Focusable = true;
        ClipToBounds = true;

        AttachedToVisualTree += (_, _) => RequestFrame();
        // Light or dark chosen in Settings: the canvas around the page takes the new colour.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        DetachedFromVisualTree += (_, _) => CloseDocument();
    }

    /// <summary>Attaches the view model: subscribes to cue and camera events and starts the frame loop.</summary>
    public void Attach(MainViewModel viewModel)
    {
        Detach();
        _viewModel = viewModel;
        viewModel.InvalidateSurface += OnInvalidate;
        viewModel.CameraRequested += OnCameraRequested;
        viewModel.PropertyChanged += (_, e) =>
        {
            // A zoom from the toolbar or keyboard: keep the middle of the view where it is, then let
            // follow place the word being read at the new magnification. Zooms that start here
            // (wheel, fit) have already been applied, so they do not come through twice.
            if (e.PropertyName == nameof(MainViewModel.Zoom) && Math.Abs(viewModel.Zoom - Zoom) > 1e-9)
                ZoomAbout(viewModel.Zoom, Bounds.Width / 2, Bounds.Height / 2);
            // Annotate mode: a text cursor over the page while a markup tool marks text.
            if (e.PropertyName is nameof(MainViewModel.AnnotateMode) or nameof(MainViewModel.ActiveTool))
                Cursor = !viewModel.AnnotateMode ? null
                    : new Cursor(viewModel.IsTextMarkupTool ? StandardCursorType.Ibeam
                        : viewModel.IsSelectTool ? StandardCursorType.Arrow : StandardCursorType.Cross);
            // The View menu's "Fit width" was ticked or cleared.
            if (e.PropertyName == nameof(MainViewModel.FitWidth) && viewModel.FitWidth != _magnification.FitsWidth)
            {
                if (viewModel.FitWidth) FitToWidth();
                else _magnification.Pin();
            }
            InvalidateVisual();
        };
        _magnification = new ViewZoom(viewModel.FitWidth, viewModel.Zoom);
        RequestFrame();
    }

    public void Detach()
    {
        if (_viewModel is { } vm)
        {
            vm.InvalidateSurface -= OnInvalidate;
            vm.CameraRequested -= OnCameraRequested;
        }
        _viewModel = null;
    }

    private void RequestFrame()
    {
        if (_frameRequested || _viewModel is null || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(time =>
        {
            _frameRequested = false;
            if (_viewModel is null) return;
            _viewModel.Coordinator.Tick();
            // The compositor's frame time, not a clock read here: see CameraGlide.
            double dt = _lastFrameTime is { } last ? (time - last).TotalSeconds : 0;
            _lastFrameTime = time;
            // Opt-in diagnostics: a frame this late is a stall the reader can see.
            if (FollowLog.Enabled)
            {
                var counters = RuntimeCounters.Now();
                if (dt > FollowLog.FrameGapSeconds && _lastFrameCounters is { } previous)
                    FollowLog.Note($"frame gap {dt * 1000:F0} ms {counters.Since(previous)}");
                _lastFrameCounters = counters;
            }
            // While audio plays the camera follows the media clock every frame, travelling between
            // measured word positions at the measured pace; FollowRequested covers the rest (a
            // resume, a zoom, a seek while paused).
            if (_viewModel.Coordinator.CurrentFollowTarget() is { } follow) OnCameraRequested(follow);
            StepGlide(dt);
            ShowPreparingPassage();
            if (_viewModel.IsPreparing) InvalidateVisual();   // the preparing outline pulses
            RequestFrame();
        });
    }

    private void OnInvalidate()
    {
        if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
        else Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /// <summary>
    /// Opens a document for display, releasing any previous one. The file is read and its pages
    /// measured off the UI thread; the previous document stays on screen until this one is ready.
    /// </summary>
    public async Task OpenDocumentAsync(string path, IPdfServiceFactory? factory = null)
    {
        int generation = ++_openGeneration;
        var source = await PageRasterSource.OpenAsync(path, factory: factory).ConfigureAwait(true);
        if (generation != _openGeneration) { source.Dispose(); return; }   // a later open overtook this one

        CloseDocument();
        _source = source;
        // Raised on the rasteriser's thread: the copy for the compositor is made there too.
        source.Ready += raster => OnRasterReady(source, raster);
        source.Failed += (key, ex) => Dispatcher.UIThread.Post(() => OnRasterFailed(source, key, ex.Message));
        CurrentPage = 0;
        _offsetX = 0;
        _offsetY = 0;
        Relayout();
        PageChanged?.Invoke(CurrentPage, PageCount);
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // A frame already recorded keeps its own references, so this cannot pull a filter out
        // from under the render thread.
        _pageFilter?.Release();
        _pageFilter = null;
        _shaders?.Dispose();
        _shaders = null;
        _colourEffect = ColourEffect.None;   // re-attaching builds the filter again on the next SetColourEffect
    }

    /// <summary>Releases the document and every raster of it.</summary>
    public void CloseDocument()
    {
        _source?.Dispose();
        _source = null;
        foreach (var image in _rasters.Values) image.Release();
        _rasters.Clear();
        _failed.Clear();
        _requested = [];
        _previewRenderTime = TimeSpan.Zero;
    }

    public int CurrentPage { get; private set; }

    public int PageCount => _source?.PageCount ?? 0;

    /// <summary>Screen units per page point: fitted to the page's width, or the reader's own (<see cref="ViewZoom"/>).</summary>
    public double Zoom => _magnification.Zoom;

    /// <summary>Raised with the zero-based page shown and the page count whenever either changes.</summary>
    public event Action<int, int>? PageChanged;

    public void GoToPage(int page, PageRect? focus = null)
    {
        if (_source is null || page < 0 || page >= _source.PageCount) return;
        if (page != CurrentPage)
        {
            CurrentPage = page;
            _pageShownAt = System.Diagnostics.Stopwatch.GetTimestamp();
            Relayout();   // the new page may be a different size: fit follows it
            PageChanged?.Invoke(CurrentPage, PageCount);
        }
        if (focus is { IsEmpty: false } rect) CentreOn(rect);
        InvalidateVisual();
    }

    public void NextPage() => GoToPage(CurrentPage + 1);

    public void PreviousPage() => GoToPage(CurrentPage - 1);

    /// <summary>
    /// The reader turns to the next or previous page: shown from its top (or, going back, its bottom),
    /// with following paused so the camera does not pull the view straight back.
    /// </summary>
    /// <summary>Saves a thumbnail of the open document's first page (off the UI thread; see PageRasterSource.SaveThumbnail).</summary>
    public Task SaveFirstPageThumbnailAsync(string path, int width = 156)
        => _source is { } source ? Task.Run(() => source.SaveThumbnail(0, width, path)) : Task.CompletedTask;

    /// <summary>The open document's outline; empty when it has none or nothing is open.</summary>
    public IReadOnlyList<OutlineEntry> Outline => _source?.Outline ?? [];

    /// <summary>Shows a page from its top (an outline heading was chosen). Browsing: following pauses.</summary>
    public void ShowPageFromTop(int page)
    {
        if (_source is null || page < 0 || page >= _source.PageCount) return;
        CancelGlide();
        _viewModel?.NotifyManualNavigation();
        GoToPage(page);
        _offsetY = 0;
        ClampOffsets();
        InvalidateVisual();
    }

    public void TurnPage(int direction)
    {
        if (_source is null) return;
        CancelGlide();
        int target = CurrentPage + Math.Sign(direction);
        if (target < 0 || target >= _source.PageCount) return;
        _viewModel?.NotifyManualNavigation();
        GoToPage(target);
        var (_, h) = _source.PageSize(CurrentPage);
        _offsetY = direction > 0 ? 0 : Bounds.Height - h * Zoom;
        ClampOffsets();
        InvalidateVisual();
    }

    /// <summary>
    /// Fits the page's width to the view and keeps it fitted, through page turns and resizes, until
    /// the reader chooses a magnification.
    /// </summary>
    public void FitToWidth()
    {
        _magnification.Fit();
        if (_viewModel is { FitWidth: false } vm) vm.FitWidth = true;
        Relayout();
        _viewModel?.Coordinator.RefollowCurrentCue();
    }

    /// <summary>
    /// The page shown or the view's size changed. When fitting, the zoom is re-derived from the page
    /// now shown, keeping the same part of the page at the top of the view; a zoom the reader chose
    /// is left alone. Either way the offsets are brought back inside the page.
    /// </summary>
    private void Relayout()
    {
        if (_source is null || Bounds.Width <= 0) return;
        double before = Zoom;
        if (_magnification.Layout(Bounds.Width, _source.PageSize(CurrentPage).Width))
        {
            CancelGlide();   // its target was worked out for the old magnification's limits
            _offsetX *= Zoom / before;
            _offsetY *= Zoom / before;
            if (_viewModel is not null) _viewModel.Zoom = Zoom;
        }
        ClampOffsets();
        InvalidateVisual();
    }

    /// <summary>
    /// Changes magnification keeping the page point under (<paramref name="x"/>, <paramref name="y"/>)
    /// where it is, then re-checks follow. Zooming is not browsing: it does not suspend following, so
    /// at high magnification the view keeps up with the word being read.
    /// </summary>
    private void ZoomAbout(double zoom, double x, double y)
    {
        CancelGlide();
        var (pageX, pageY) = new PageTransform(Zoom, _offsetX, _offsetY).ToPage(x, y);
        _magnification.SetByReader(zoom);
        _offsetX = x - pageX * Zoom;
        _offsetY = y - pageY * Zoom;
        ClampOffsets();
        InvalidateVisual();
        // Remembered as the reader's preference, and "Fit width" is no longer ticked.
        _viewModel?.ApplyZoom(Zoom);
        _viewModel?.Coordinator.RefollowCurrentCue();
    }

    private void CentreOn(PageRect rect)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _offsetX = Bounds.Width / 2 - rect.MidX * Zoom;
        _offsetY = Bounds.Height * FollowVerticalBias - rect.MidY * Zoom;
        ClampOffsets();
    }

    private void ClampOffsets() => (_offsetX, _offsetY) = ClampedOffsets(_offsetX, _offsetY);

    private (double X, double Y) ClampedOffsets(double x, double y)
    {
        if (_source is null) return (x, y);
        var (w, h) = _source.PageSize(CurrentPage);
        double scaledW = w * Zoom, scaledH = h * Zoom;
        double clampedY = Math.Clamp(y, Math.Min(0, Bounds.Height - scaledH), Math.Max(0, Bounds.Height - scaledH));
        // A page no wider than the view sits in the middle of it: there is nothing to scroll to.
        if (scaledW <= Bounds.Width) return ((Bounds.Width - scaledW) / 2, clampedY);
        // A page wider than the view may be scrolled past its edges by up to half the view, so the
        // follow camera can bring a line's start in from the left edge and stop with its end near the
        // middle (FollowOptions.LineStartFraction, LineEndFraction).
        double overscroll = Bounds.Width * HorizontalOverscroll;
        return (Math.Clamp(x, Bounds.Width - scaledW - overscroll, overscroll), clampedY);
    }

    /// <summary>
    /// Sets highlights grouped by page, so a unit spanning a page break lights up on either page.
    /// <paramref name="paraphrase"/> marks a passage spoken in other words than the page's, drawn
    /// in its own colour.
    /// </summary>
    public void SetHighlightMultiPage(IReadOnlyDictionary<int, IReadOnlyList<PageRect>> byPage, bool paraphrase = false)
    {
        _highlightsByPage = byPage ?? new Dictionary<int, IReadOnlyList<PageRect>>();
        _highlightIsParaphrase = paraphrase;
        InvalidateVisual();
    }

    public void ClearHighlight() => SetHighlightMultiPage(new Dictionary<int, IReadOnlyList<PageRect>>());

    /// <summary>Sets highlight colours and opacity. Configurable because one fixed value is unreadable for some readers.</summary>
    public void SetHighlightStyle(NarrationHighlightStyle style, NarrationHighlightStyle? paraphrase = null)
    {
        _highlightStyle = style ?? NarrationHighlightStyle.Default;
        _paraphraseStyle = paraphrase ?? NarrationHighlightStyle.Paraphrase;
        InvalidateVisual();
    }

    private void OnCameraRequested(FollowTarget target)
    {
        if (target.CueRects.Count == 0 || _source is null) return;
        // Reading moved to another page: show it, then place the cue on it at once. There is nothing
        // to glide from: the old page's position means nothing on the new one.
        bool newPage = target.PageIndex != CurrentPage;
        if (newPage)
        {
            CancelGlide();
            GoToPage(target.PageIndex);
            if (target.PageIndex != CurrentPage) return;
        }

        // Decide in page points, so it behaves identically at every zoom and DPI and never changes
        // the reader's magnification; then glide there frame by frame (StepGlide).
        var visible = new PageTransform(Zoom, _offsetX, _offsetY).ToPageRect(0, 0, Bounds.Width, Bounds.Height);
        var decision = CameraFollowPlanner.Plan(
            new PageViewport(newPage ? -1 : CurrentPage, visible), target,
            _viewModel?.FollowMode ?? FollowMode.Follow,
            new FollowOptions(ReduceMotion: _viewModel?.Services.CurrentSettings.ReduceMotion ?? false));

        double beforeX = _offsetX, beforeY = _offsetY;
        bool redirect = !decision.ShouldMove && _glideTarget is not null && !decision.TargetVisible.Visible.IsEmpty;
        if (decision.ShouldMove || redirect)
        {
            // Clamp the destination the way the offsets are clamped, so the glide ends where the view
            // can actually be, rather than straining against the page edge.
            var (offX, offY) = ClampedOffsets(-decision.TargetVisible.Visible.Left * Zoom, -decision.TargetVisible.Visible.Top * Zoom);
            var destination = (X: -offX / Zoom, Y: -offY / Zoom);
            if (decision.GlideSeconds <= 0 || newPage)
            {
                CancelGlide();
                _offsetX = offX;
                _offsetY = offY;
                InvalidateVisual();
            }
            else
            {
                // A new target while gliding keeps the velocity: the next word bends the path rather
                // than restarting it, which is what turns word-by-word targets into one steady pan.
                _glideTarget = destination;
                _glideSeconds = decision.GlideSeconds;
            }
        }
        FollowLog.Write(target.PageIndex, target.CueRects, visible, decision, Bounds, Zoom, (beforeX, beforeY), (_offsetX, _offsetY));
    }

    private string? _shownPreparingSegmentId;

    /// <summary>
    /// When a start has to wait for a passage (Next or Previous across a page, a click after
    /// browsing), brings that passage into view once, so the pulsing outline and the card are seen
    /// together. The reader asked to read from there, so a pause in following from browsing ends.
    /// </summary>
    private void ShowPreparingPassage()
    {
        string? segmentId = _viewModel?.Playback.Preparation?.SegmentId;
        if (segmentId == _shownPreparingSegmentId) return;
        _shownPreparingSegmentId = segmentId;
        if (segmentId is null || _viewModel is not { } vm || vm.FollowMode == FollowMode.Off) return;
        if (vm.FollowSuspended) vm.Coordinator.ResumeFollow();
        ShowPassage(segmentId);
    }

    /// <summary>
    /// Brings a passage into view when it is not already: the page it will be read on, with its
    /// start at the reading height. Used for a passage being prepared and for the passage a reopened
    /// document will resume from. Does nothing when following is off.
    /// </summary>
    public void ShowPassage(string segmentId)
    {
        if (_viewModel is not { } vm || vm.FollowMode == FollowMode.Off || _source is null) return;
        if (vm.Coordinator.Scheduler?.SourceMap is not { } map || map.Segment(segmentId) is not { } segment) return;
        // The page the camera will hold while it is read: where it starts when its words can be
        // timed, else the page holding most of it (SourceMap.FramingPage).
        bool wholePassage = !NarrationCoordinator.SpeaksItsSourceWordForWord(segment);
        if (map.FramingPage(segment.AnchorIds, wholePassage) is not { } page) return;
        var rects = map.HighlightRects(segment.AnchorIds, page);
        if (rects.Count == 0) return;

        // Already in view (a click on the page being read): leave the camera where it is.
        var visible = new PageTransform(Zoom, _offsetX, _offsetY).ToPageRect(0, 0, Bounds.Width, Bounds.Height);
        if (page == CurrentPage && rects.All(r => r.Left >= visible.Left && r.Right <= visible.Right
                                               && r.Top >= visible.Top && r.Bottom <= visible.Bottom)) return;
        // No word timing exists yet, so the passage's start is framed and held, as for any sentence cue.
        OnCameraRequested(new FollowTarget(page, rects, rects, CueGranularity.Sentence));
    }

    /// <summary>Moves the camera one frame along its glide.</summary>
    private void StepGlide(double dtSeconds)
    {
        if (_glideTarget is not { } target || Zoom <= 0) return;
        double x = -_offsetX / Zoom, y = -_offsetY / Zoom;
        x = CameraGlide.Step(x, target.X, ref _glideVelocityX, _glideSeconds, dtSeconds);
        y = CameraGlide.Step(y, target.Y, ref _glideVelocityY, _glideSeconds, dtSeconds);
        (_offsetX, _offsetY) = ClampedOffsets(-x * Zoom, -y * Zoom);
        if (CameraGlide.HasArrived(-_offsetX / Zoom, target.X, _glideVelocityX)
            && CameraGlide.HasArrived(-_offsetY / Zoom, target.Y, _glideVelocityY))
            CancelGlide();
        InvalidateVisual();
    }

    /// <summary>Stops the glide where it is: the reader has taken the camera, or it has arrived.</summary>
    private void CancelGlide()
    {
        _glideTarget = null;
        _glideVelocityX = _glideVelocityY = 0;
    }

    /// <summary>The DPI to rasterise at, quantised so a small zoom change does not force a re-render.</summary>
    private int TargetDpi()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int dpi = (int)Math.Round(72 * Zoom * scale);
        int[] ladder = [72, 96, 120, 150, 200, 260, 340, 440, 580, 760];
        foreach (int step in ladder) if (dpi <= step) return step;
        return ladder[^1];
    }

    private float FilterStrength => _colourEffect == ColourEffect.None ? 0f : _colourEffectIntensity;

    /// <summary>The raster the page shown should be drawn from at the present zoom. Unfiltered: the filter is applied as it is drawn.</summary>
    private static PageRasterKey Unfiltered(int page, int dpi) => new(page, dpi, ColourEffect.None, 0f);

    private PageRasterKey SharpKey(int page) => Unfiltered(page, TargetDpi());

    private PageRasterKey PreviewKey(int page) => Unfiltered(page, PreviewDpi);

    /// <summary>
    /// Applies one of RailReader's colour filters (high contrast, high visibility, amber, invert) to
    /// the page. The filter is applied as the page is drawn, so a change shows at the next frame:
    /// no raster is rendered or filtered again.
    /// </summary>
    public void SetColourEffect(ColourEffect effect, double intensity)
    {
        float i = (float)Math.Clamp(intensity, 0, 1);
        if (effect == _colourEffect && Math.Abs(i - _colourEffectIntensity) < 0.001f && (_pageFilter is not null || effect == ColourEffect.None)) return;
        _colourEffect = effect;
        _colourEffectIntensity = i;
        _pageFilter?.Release();
        _pageFilter = null;
        if (effect != ColourEffect.None)
        {
            _shaders ??= new ColourEffectShaders();
            if (_shaders.CreateColorFilter(effect, i) is { } filter) _pageFilter = new Shared<SkiaSharp.SKColorFilter>(filter);
        }
        InvalidateVisual();
    }

    /// <summary>
    /// The best raster held for the page shown: the sharp one, else that page at the highest
    /// resolution held (another zoom, or its preview). Null when there is nothing of the page yet.
    /// </summary>
    private (PageRasterKey Key, Shared<SkiaSharp.SKImage> Image)? BestRaster()
    {
        var sharp = SharpKey(CurrentPage);
        if (_rasters.TryGetValue(sharp, out var exact)) return (sharp, exact);
        (PageRasterKey Key, Shared<SkiaSharp.SKImage> Image)? best = null;
        foreach (var (key, image) in _rasters)
        {
            if (key.PageIndex != CurrentPage) continue;
            if (best is null || key.Dpi > best.Value.Key.Dpi) best = (key, image);
        }
        return best;
    }

    /// <summary>
    /// Drops the rasters that are no longer worth their memory and tells the rasteriser what is
    /// missing: the page shown first, then previews of its neighbours. Called from the paint, which
    /// is where the page, zoom, filter and what has arrived are all known; it is a few dictionary
    /// lookups, and it only talks to the rasteriser when the list changes.
    /// </summary>
    private void PrepareRasters()
    {
        if (_source is not { } source) return;
        var sharp = SharpKey(CurrentPage);

        // Kept: the sharp page; a stand-in for it until it arrives; previews near the page shown.
        PageRasterKey? standIn = _rasters.ContainsKey(sharp) ? null : BestRaster()?.Key;
        List<PageRasterKey>? drop = null;
        foreach (var key in _rasters.Keys)
        {
            if (key == sharp || key == standIn) continue;
            bool nearbyPreview = key.Dpi == PreviewDpi && Math.Abs(key.PageIndex - CurrentPage) <= PreviewKeepRadius;
            if (!nearbyPreview) (drop ??= []).Add(key);
        }
        if (drop is not null)
            foreach (var key in drop)
                if (_rasters.Remove(key, out var image)) image.Release();

        var wanted = new List<PageRasterKey>(2 + PreviewOffsets.Length);
        if (!_rasters.ContainsKey(sharp) && !_failed.ContainsKey(sharp))
        {
            // Nothing of this page to draw at all (it was jumped to, or paged to faster than its
            // preview could be fetched): something to look at first, when that is cheap.
            var ownPreview = PreviewKey(CurrentPage);
            if (standIn is null && ownPreview != sharp && _previewRenderTime <= QuickPreview && !_failed.ContainsKey(ownPreview))
                wanted.Add(ownPreview);
            wanted.Add(sharp);
        }
        foreach (int offset in PreviewOffsets)
        {
            int page = CurrentPage + offset;
            if (page < 0 || page >= source.PageCount) continue;
            var preview = PreviewKey(page);
            if (!_rasters.ContainsKey(preview) && !_failed.ContainsKey(preview)) wanted.Add(preview);
        }
        if (wanted.SequenceEqual(_requested)) return;
        _requested = [.. wanted];
        source.Request(_requested);
    }

    /// <summary>
    /// A raster has been rendered. Runs on the rasteriser's thread: the pixels are copied into an
    /// image for the compositor here, and only the hand-over is posted to the UI thread.
    /// </summary>
    private void OnRasterReady(PageRasterSource source, PageRaster raster)
    {
        var key = raster.Key;
        var renderTime = raster.RenderTime;
        Shared<SkiaSharp.SKImage> bitmap;
        try
        {
            // FromBitmap copies a mutable bitmap's pixels, so the image outlives the raster.
            var image = SkiaSharp.SKImage.FromBitmap(raster.Bitmap)
                ?? throw new InvalidOperationException("The page raster could not be copied for display.");
            bitmap = new Shared<SkiaSharp.SKImage>(image);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => OnRasterFailed(source, key, ex.Message));
            return;
        }
        finally
        {
            raster.Dispose();
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Rendered for a document that has since been closed.
            if (!ReferenceEquals(source, _source)) { bitmap.Release(); return; }
            if (key.Dpi == PreviewDpi) _previewRenderTime = renderTime;
            if (_pageShownAt != 0 && key == SharpKey(CurrentPage))
            {
                // Opt-in diagnostics: what the reader saw in the meantime, and for how long.
                string standIn = BestRaster() is { } shown ? $"a {shown.Key.Dpi} dpi stand-in" : "blank paper";
                FollowLog.Note($"page {key.PageIndex + 1} sharp at {key.Dpi} dpi " +
                    $"{System.Diagnostics.Stopwatch.GetElapsedTime(_pageShownAt).TotalMilliseconds:F0} ms after it was turned to, {standIn} until then");
                _pageShownAt = 0;
            }
            if (_rasters.Remove(key, out var previous)) previous.Release();
            _rasters[key] = bitmap;
            InvalidateVisual();
        });
    }

    private void OnRasterFailed(PageRasterSource source, PageRasterKey key, string message)
    {
        if (!ReferenceEquals(source, _source)) return;
        // Remembered so the paint does not ask for it again on every frame.
        _failed[key] = message;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(CanvasBrush(), bounds);
        if (_source is not { } source || Bounds.Width <= 0) return;

        try
        {
            PrepareRasters();
            var (pageW, pageH) = source.PageSize(CurrentPage);

            if (pageW > 0 && pageH > 0)
            {
                var destination = new Rect(_offsetX, _offsetY, pageW * Zoom, pageH * Zoom);
                if (BestRaster() is { } raster)
                    context.Custom(new FilteredPageDraw(bounds, raster.Image.AddReference(), _pageFilter?.AddReference(), destination));
                else
                {
                    // Nothing of this page has been rendered yet: blank paper in the filter's
                    // colour, so the page's place and the highlight on it are there at once.
                    var paper = PageRasterSource.PaperColour(_colourEffect, FilterStrength);
                    context.FillRectangle(new SolidColorBrush(Color.FromRgb(paper.Red, paper.Green, paper.Blue)), destination);
                }
            }
            DrawAnnotations(context, bounds);
            if (_viewModel is { MarkupPreview: { } preview } vm && vm.MarkupPreviewPage == CurrentPage)
                context.Custom(new MarkupPreviewDraw(bounds, preview, new PageTransform(Zoom, _offsetX, _offsetY)));
            DrawHighlights(context);
            DrawNoteTarget(context);
            DrawPreparing(context);
            if (_failed.TryGetValue(SharpKey(CurrentPage), out string? failure)) DrawRenderError(context, failure);
        }
        catch (Exception ex)
        {
            // A rendering failure must not take the window down with it.
            DrawRenderError(context, ex.Message);
        }
    }

    /// <summary>
    /// Around the page: the theme's canvas colour (RailReader2's mid grey in the light theme), so the
    /// page reads as a sheet on a desk rather than floating in black.
    /// </summary>
    private IBrush CanvasBrush()
        => this.TryFindResource("ReadingCanvasColor", ActualThemeVariant, out var value) && value is Color colour
            ? new SolidColorBrush(colour)
            : Brushes.Gray;

    private static void DrawRenderError(DrawingContext context, string message)
        => context.DrawText(new FormattedText($"Render error: {message}",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("DejaVu Sans"), 13, Brushes.OrangeRed), new Point(8, 8));

    /// <summary>
    /// The reader's own annotations (ADR-0014), under the narration highlight so the passage being
    /// read stays plain. Drawn from a copy made here, on the UI thread, when they last changed.
    /// </summary>
    private void DrawAnnotations(DrawingContext context, Rect bounds)
    {
        if (_viewModel is not { ShowAnnotations: true, AnnotationLayer: { } layer }) return;
        var snapshot = layer.ForPage(CurrentPage, _viewModel.ReviewSession?.Selected);
        if (snapshot.Count == 0) return;
        context.Custom(new AnnotationDraw(bounds, snapshot, new PageTransform(Zoom, _offsetX, _offsetY),
            RailReader.Renderer.Skia.AnnotationRenderer.IsDarkBackdrop(_colourEffect), _viewModel.ExpandAllNotes));
    }

    /// <summary>
    /// While a note is being written on what was just read, that text is outlined, so it is plain
    /// what the note will be on. Above the narration highlight: it is what the reader is looking at.
    /// </summary>
    private void DrawNoteTarget(DrawingContext context)
    {
        if (_viewModel?.NoteTarget is not { } target) return;
        if (target.Pages.FirstOrDefault(p => p.PageIndex == CurrentPage) is not { } page) return;
        var transform = new PageTransform(Zoom, _offsetX, _offsetY);
        var accent = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var value) && value is Color c
            ? c : Color.FromRgb(0x2F, 0x80, 0xED);
        var pen = new Pen(new SolidColorBrush(accent), 2);
        var fill = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
        foreach (var rect in page.Rects)
        {
            var (x0, y0) = transform.ToScreen(rect.X, rect.Y);
            var (x1, y1) = transform.ToScreen(rect.X + rect.W, rect.Y + rect.H);
            context.DrawRectangle(fill, pen, new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)), 2, 2);
        }
    }

    /// <summary>A page-point rectangle on the page shown, in the control's coordinates; null for another page.</summary>
    public Rect? ScreenRectOf(int page, PageRect rect)
    {
        if (page != CurrentPage) return null;
        var transform = new PageTransform(Zoom, _offsetX, _offsetY);
        var (x0, y0) = transform.ToScreen(rect.Left, rect.Top);
        var (x1, y1) = transform.ToScreen(rect.Right, rect.Bottom);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// The screen rectangle around the part of <paramref name="target"/> on the page shown, for the
    /// note card to be placed beside; null when none of it is on this page.
    /// </summary>
    public Rect? ScreenBoundsOf(RailNarrator.Pdf.Annotations.ReviewTarget target)
    {
        if (target.Pages.FirstOrDefault(p => p.PageIndex == CurrentPage) is not { Rects.Count: > 0 } page) return null;
        var transform = new PageTransform(Zoom, _offsetX, _offsetY);
        var (x0, y0) = transform.ToScreen(page.Rects.Min(r => r.X), page.Rects.Min(r => r.Y));
        var (x1, y1) = transform.ToScreen(page.Rects.Max(r => r.X + r.W), page.Rects.Max(r => r.Y + r.H));
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private void DrawHighlights(DrawingContext context)
    {
        if (!_highlightsByPage.TryGetValue(CurrentPage, out var rects) || rects.Count == 0) return;

        var transform = new PageTransform(Zoom, _offsetX, _offsetY);
        var style = _highlightIsParaphrase ? _paraphraseStyle : _highlightStyle;
        var fillColour = style.FillColour;
        var outlineColour = style.OutlineColour;
        // On a filtered page the default amber can vanish (yellow on high-visibility yellow, say); use
        // the colour RailReader pairs with that filter for its line highlight. A paraphrase has its
        // own tint per filter, distinct from that one, and an outline in it.
        if (_colourEffect != ColourEffect.None)
        {
            if (_highlightIsParaphrase)
            {
                fillColour = NarrationHighlightStyle.ParaphraseTint(_colourEffect);
                if (outlineColour is { } o && o != SkiaSharp.SKColors.Black) outlineColour = fillColour.WithAlpha(220);
            }
            else
            {
                var tint = _colourEffect.GetOverlayPalette().LineHighlight;
                fillColour = new SkiaSharp.SKColor(tint.R, tint.G, tint.B);
            }
        }
        var fill = new SolidColorBrush(Color.FromArgb(style.FillAlpha,
            fillColour.Red, fillColour.Green, fillColour.Blue));
        IPen? outline = outlineColour is { } c
            ? new Pen(new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue)),
                      style.OutlineWidthPoints * Zoom)
            : null;
        float radius = (float)(style.CornerRadiusPoints * Zoom);
        // Padding is in page points and is applied before the page-to-screen transform, which
        // already scales by the zoom. Multiplying by the zoom here as well scaled it twice, so at
        // 800% each box grew by 64 px a side and covered the neighbouring lines.
        float pad = style.PaddingPoints;

        // One shape, filled and outlined once: separate boxes overlap by their padding, and every
        // overlap was tinted twice (HighlightLines.Passage joins the boxes; the union does the rest).
        Geometry? shape = null;
        foreach (var rect in rects)
        {
            if (rect.IsEmpty) continue;
            var (x0, y0) = transform.ToScreen(rect.Left - pad, rect.Top - pad);
            var (x1, y1) = transform.ToScreen(rect.Right + pad, rect.Bottom + pad);
            var box = new RectangleGeometry(new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)), radius, radius);
            shape = shape is null ? box : Geometry.Combine(shape, box, GeometryCombineMode.Union);
        }
        if (shape is not null) context.DrawGeometry(fill, outline, shape);
    }

    /// <summary>
    /// Outlines the passage a start is waiting on, pulsing, so it is plain on the page which passage
    /// will be read and that it is not playing yet.
    /// </summary>
    private void DrawPreparing(DrawingContext context)
    {
        if (_viewModel?.Playback.Preparation is not { } preparation) return;
        if (_viewModel.Coordinator.Scheduler?.SourceMap is not { } map) return;
        if (map.Segment(preparation.SegmentId) is not { } segment) return;
        var rects = map.HighlightRects(segment.AnchorIds, CurrentPage);
        if (rects.Count == 0) return;

        double phase = (Environment.TickCount64 % 1400) / 1400.0;
        byte alpha = (byte)(110 + 110 * (0.5 + 0.5 * Math.Sin(phase * 2 * Math.PI)));
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0x2F, 0x80, 0xED)), 2,
            new DashStyle([4, 3], phase * 7));
        var fill = new SolidColorBrush(Color.FromArgb((byte)(alpha / 6), 0x2F, 0x80, 0xED));
        var transform = new PageTransform(Zoom, _offsetX, _offsetY);
        float pad = _highlightStyle.PaddingPoints;
        foreach (var rect in rects)
        {
            var (x0, y0) = transform.ToScreen(rect.Left - pad, rect.Top - pad);
            var (x1, y1) = transform.ToScreen(rect.Right + pad, rect.Bottom + pad);
            context.DrawRectangle(fill, pen, new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)), 2, 2);
        }
    }

    // ---- input ------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        // A stylus's eraser end erases in annotate mode, whichever tool is chosen.
        if ((properties.IsLeftButtonPressed || properties.IsEraser) && _viewModel is { } vm && _source is not null)
        {
            // A note's icon, or the mark of a highlight's note, is the reader's own: a click there
            // opens it (a double click edits it) and does not start reading.
            var (notePageX, notePageY) = new PageTransform(Zoom, _offsetX, _offsetY).ToPage(point.X, point.Y);
            // (With the select tool in annotate mode, a press on a note picks it up to move instead.)
            if (!(vm.AnnotateMode && vm.IsSelectTool)
                && vm.ClickAnnotation(CurrentPage, notePageX, notePageY, e.ClickCount >= 2)) { e.Handled = true; return; }

            // Annotate mode: the left button marks text and selects; it never reads from there or pans.
            if (vm.AnnotatePress(CurrentPage, notePageX, notePageY, e.ClickCount >= 2, properties.IsEraser))
            {
                // Marking the page while it is read: the camera holds still under the pointer (and
                // resumes as after browsing), or a drag would slide with the reading.
                CancelGlide();
                vm.NotifyManualNavigation();
                _annotating = true;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (properties.IsLeftButtonPressed && _viewModel?.Plan is { } plan && _source is not null)
        {
            // Click to read: hit-test the original PDF and start at the containing unit. A click on
            // blank space does nothing rather than choosing an arbitrary passage.
            var (pageX, pageY) = new PageTransform(Zoom, _offsetX, _offsetY).ToPage(point.X, point.Y);
            if (SourceHitTester.HitTest(plan, CurrentPage, pageX, pageY) is { } hit
                && SourceHitTester.SegmentFor(plan, hit.Unit) is { } segment)
            {
                // Moving to a passage starts it. A second, concurrent Play raced the first and
                // restarted the passage it had just begun.
                _viewModel.RunObserved(_viewModel.Coordinator.GoToSegmentAsync(segment.Id), "Could not move to that passage");
                return;
            }
        }

        _dragging = true;
        _dragStart = point;
        _dragOffsetStart = (_offsetX, _offsetY);
        // Following is suspended once the pointer actually pans (see OnPointerMoved). A plain click
        // on blank space or between lines used to suspend it, and with it the move to the next page.
        e.Pointer.Capture(this);
    }

    private bool _annotating;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_annotating && _viewModel is { } annotating)
        {
            var at = e.GetPosition(this);
            var (x, y) = new PageTransform(Zoom, _offsetX, _offsetY).ToPage(at.X, at.Y);
            annotating.AnnotateMove(x, y);
            return;
        }
        if (!_dragging) return;
        var point = e.GetPosition(this);
        // Manual panning suspends the camera, not the audio.
        if (Math.Abs(point.X - _dragStart.X) + Math.Abs(point.Y - _dragStart.Y) > 4)
        {
            CancelGlide();
            _viewModel?.NotifyManualNavigation();
        }
        _offsetX = _dragOffsetStart.X + (point.X - _dragStart.X);
        _offsetY = _dragOffsetStart.Y + (point.Y - _dragStart.Y);
        ClampOffsets();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_annotating)
        {
            _annotating = false;
            e.Pointer.Capture(null);
            _viewModel?.AnnotateRelease();
            return;
        }
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// The pointer was taken away mid-drag (the window lost focus, a menu opened, a compositor
    /// gesture). No release follows, and a drag left "in progress" would pan the page with every
    /// later pointer move and keep following paused for as long as the pointer moved.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // A drag taken away mid-way marks nothing.
        if (_annotating)
        {
            _annotating = false;
            _viewModel?.CancelAnnotateDrag();
        }
        _dragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Math.Abs(e.Delta.Y) < 0.01 && Math.Abs(e.Delta.X) < 0.01) return;
        var point = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Zoom about the pointer so the text under the cursor stays under it. The new zoom is
            // kept (follow pans, it never zooms), and following carries on.
            ZoomAbout(Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), point.X, point.Y);
            e.Handled = true;
            return;
        }
        ScrollBy(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : e.Delta.X, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0 : e.Delta.Y);
        e.Handled = true;
    }

    /// <summary>
    /// Scrolls by wheel notches, carrying on to the next or previous page at a page's end. Scrolling
    /// is browsing, so it pauses following (Resume following, F9 or Escape restores it).
    /// </summary>
    private void ScrollBy(double notchesX, double notchesY)
    {
        if (_source is null) return;
        const double Step = 80;
        CancelGlide();
        _viewModel?.NotifyManualNavigation();
        var (_, h) = _source.PageSize(CurrentPage);
        double minY = Math.Min(0, Bounds.Height - h * Zoom);
        bool atBottom = _offsetY <= minY + 0.5, atTop = _offsetY >= -0.5;
        if (notchesY < 0 && atBottom && CurrentPage + 1 < _source.PageCount) { TurnPage(+1); return; }
        if (notchesY > 0 && atTop && CurrentPage > 0) { TurnPage(-1); return; }
        _offsetX += notchesX * Step;
        _offsetY += notchesY * Step;
        ClampOffsets();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Escape is the window's (resume following, else pause), wherever focus is.
        if (e.KeyModifiers == KeyModifiers.None && HandleNavigationKey(e.Key)) e.Handled = true;
    }

    /// <summary>
    /// The keyboard's way about the page: Page Up/Down turn pages, the arrows scroll (on to the next
    /// page at a page's end, as the wheel does), Home and End go to the page's top and bottom. All of
    /// it is browsing, so following pauses. False for any other key, or with no document.
    /// </summary>
    public bool HandleNavigationKey(Key key)
    {
        if (_source is null) return false;
        switch (key)
        {
            case Key.PageDown: TurnPage(+1); return true;
            case Key.PageUp: TurnPage(-1); return true;
            case Key.Down: ScrollBy(0, -0.5); return true;
            case Key.Up: ScrollBy(0, +0.5); return true;
            case Key.Right: ScrollBy(-0.5, 0); return true;
            case Key.Left: ScrollBy(+0.5, 0); return true;
            case Key.Home:
            case Key.End:
            {
                CancelGlide();
                _viewModel?.NotifyManualNavigation();
                var (_, h) = _source.PageSize(CurrentPage);
                _offsetY = key == Key.Home ? 0 : Bounds.Height - h * Zoom;
                ClampOffsets();
                InvalidateVisual();
                return true;
            }
            default: return false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A resize (the window, the script panel, a toolbar row): fit follows it, the reader's own
        // zoom is kept, and the passage being read is brought back into view if it was pushed out.
        if (change.Property == BoundsProperty && _source is not null)
        {
            Relayout();
            _viewModel?.Coordinator.RefollowCurrentCue();
        }
    }
}

/// <summary>
/// Opt-in camera diagnostics: with <c>RN_FOLLOW_LOG=1</c> every follow request is appended to
/// <c>$XDG_STATE_HOME/rail-narrator/logs/follow.log</c> with the cue, the visible region, the
/// control size, zoom and offsets, and the decision; so is every frame that arrives more than
/// <see cref="FrameGapSeconds"/> after the one before it ("frame gap 83 ms"), which is what a stall
/// of the window looks like from inside. Off by default; nothing is logged otherwise.
/// </summary>
internal static class FollowLog
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("RN_FOLLOW_LOG") == "1"
        ? System.IO.Path.Combine(RailNarrator.Infrastructure.XdgPaths.State, "logs", "follow.log")
        : null;

    /// <summary>A frame later than this is logged: three frames missed at 60 Hz.</summary>
    public const double FrameGapSeconds = 0.05;

    public static bool Enabled => Path is not null;

    public static void Note(string text)
    {
        if (Path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {text}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Write(int page, IReadOnlyList<PageRect> rects, PageRect visible, FollowDecision decision,
        Rect bounds, double zoom, (double X, double Y) before, (double X, double Y) after)
    {
        if (Path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var cue = PageRect.UnionAll(rects);
            var c = System.Globalization.CultureInfo.InvariantCulture;
            File.AppendAllText(Path, string.Format(c,
                "{0:HH:mm:ss.fff} page={1} cue=[{2}] visible=[{3:F1},{4:F1},{5:F1},{6:F1}] bounds={7:F0}x{8:F0} zoom={9:F2} " +
                "offset=({10:F0},{11:F0})->({12:F0},{13:F0}) move={14} reason={15}\n",
                DateTime.Now, page + 1,
                cue is { } u ? string.Format(c, "{0:F1},{1:F1},{2:F1},{3:F1} n={4}", u.Left, u.Top, u.Right, u.Bottom, rects.Count) : "none",
                visible.Left, visible.Top, visible.Right, visible.Bottom, bounds.Width, bounds.Height, zoom,
                before.X, before.Y, after.X, after.Y, decision.ShouldMove, decision.Reason));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
