using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
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
/// The page raster is cached and copied into a <see cref="WriteableBitmap"/>; only the zoom ladder and
/// page changes force a re-render, so panning and highlighting cost a blit rather than a PDF render.
/// </para>
/// </remarks>
public sealed class PdfSurface : Control
{
    /// <summary>Where the active cue sits vertically when the camera must move.</summary>
    private const double FollowVerticalBias = 0.42;

    private IPdfService? _pdf;
    private SkiaRenderedPage? _renderedPage;
    private int _renderedPageIndex = -1;
    private int _renderedDpi;
    private WriteableBitmap? _bitmap;
    private double _zoom = 1.0;
    private double _offsetX, _offsetY;
    private Point _dragStart;
    private (double X, double Y) _dragOffsetStart;
    private bool _dragging;
    private MainViewModel? _viewModel;

    private IReadOnlyDictionary<int, IReadOnlyList<PageRect>> _highlightsByPage =
        new Dictionary<int, IReadOnlyList<PageRect>>();
    private NarrationHighlightStyle _highlightStyle = NarrationHighlightStyle.Default;

    private bool _frameRequested;

    public PdfSurface()
    {
        Focusable = true;
        ClipToBounds = true;

        AttachedToVisualTree += (_, _) => RequestFrame();
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
            // (wheel, fit) have already set _zoom, so they do not come through twice.
            if (e.PropertyName == nameof(MainViewModel.Zoom) && Math.Abs(viewModel.Zoom - _zoom) > 1e-9)
                ZoomAbout(viewModel.Zoom, Bounds.Width / 2, Bounds.Height / 2);
            InvalidateVisual();
        };
        _zoom = viewModel.Zoom;
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
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            if (_viewModel is null) return;
            _viewModel.Coordinator.Tick();
            RequestFrame();
        });
    }

    private void OnInvalidate()
    {
        if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
        else Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /// <summary>Opens a document for display, releasing any previous one.</summary>
    public void OpenDocument(string path, IPdfServiceFactory? factory = null)
    {
        ReleasePage();
        (_pdf as IDisposable)?.Dispose();
        _pdf = (factory ?? new SkiaPdfServiceFactory()).CreatePdfService(path);
        CurrentPage = 0;
        FitToWidth();
        PageChanged?.Invoke(CurrentPage, PageCount);
        InvalidateVisual();
    }

    public int CurrentPage { get; private set; }

    public int PageCount => _pdf?.PageCount ?? 0;

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.25, 8.0);
            if (_viewModel is not null) _viewModel.Zoom = _zoom;
            ClampOffsets();
            InvalidateVisual();
        }
    }

    /// <summary>Raised with the zero-based page shown and the page count whenever either changes.</summary>
    public event Action<int, int>? PageChanged;

    public void GoToPage(int page, PageRect? focus = null)
    {
        if (_pdf is null || page < 0 || page >= _pdf.PageCount) return;
        if (page != CurrentPage)
        {
            ReleasePage();
            CurrentPage = page;
            ClampOffsets();   // the new page may be a different size
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
    public void TurnPage(int direction)
    {
        if (_pdf is null) return;
        int target = CurrentPage + Math.Sign(direction);
        if (target < 0 || target >= _pdf.PageCount) return;
        _viewModel?.NotifyManualNavigation();
        GoToPage(target);
        var (_, h) = _pdf.GetPageSize(CurrentPage);
        _offsetY = direction > 0 ? 0 : Bounds.Height - h * _zoom;
        ClampOffsets();
        InvalidateVisual();
    }

    /// <summary>Fits the page width to the control and records that as the reader's zoom.</summary>
    public void FitToWidth()
    {
        if (_pdf is null || Bounds.Width <= 0) return;
        var (w, _) = _pdf.GetPageSize(CurrentPage);
        if (w <= 0) return;
        _zoom = Math.Clamp(Bounds.Width / w, 0.25, 8.0);
        _offsetX = 0;
        _offsetY = 0;
        if (_viewModel is not null) _viewModel.Zoom = _zoom;
        InvalidateVisual();
    }

    /// <summary>
    /// Changes magnification keeping the page point under (<paramref name="x"/>, <paramref name="y"/>)
    /// where it is, then re-checks follow. Zooming is not browsing: it does not suspend following, so
    /// at high magnification the view keeps up with the word being read.
    /// </summary>
    private void ZoomAbout(double zoom, double x, double y)
    {
        var (pageX, pageY) = new PageTransform(_zoom, _offsetX, _offsetY).ToPage(x, y);
        double next = Math.Clamp(zoom, 0.25, 8.0);
        _offsetX = x - pageX * next;
        _offsetY = y - pageY * next;
        Zoom = next;
        _viewModel?.Coordinator.RefollowCurrentCue();
    }

    private void CentreOn(PageRect rect)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _offsetX = Bounds.Width / 2 - rect.MidX * _zoom;
        _offsetY = Bounds.Height * FollowVerticalBias - rect.MidY * _zoom;
        ClampOffsets();
    }

    private void ClampOffsets()
    {
        if (_pdf is null) return;
        var (w, h) = _pdf.GetPageSize(CurrentPage);
        double scaledW = w * _zoom, scaledH = h * _zoom;
        // The page may be scrolled off-centre at high magnification, but never entirely out of view.
        _offsetX = Math.Clamp(_offsetX, Math.Min(0, Bounds.Width - scaledW), Math.Max(0, Bounds.Width - scaledW));
        _offsetY = Math.Clamp(_offsetY, Math.Min(0, Bounds.Height - scaledH), Math.Max(0, Bounds.Height - scaledH));
    }

    /// <summary>Sets highlights grouped by page, so a unit spanning a page break lights up on either page.</summary>
    public void SetHighlightMultiPage(IReadOnlyDictionary<int, IReadOnlyList<PageRect>> byPage)
    {
        _highlightsByPage = byPage ?? new Dictionary<int, IReadOnlyList<PageRect>>();
        InvalidateVisual();
    }

    public void ClearHighlight() => SetHighlightMultiPage(new Dictionary<int, IReadOnlyList<PageRect>>());

    /// <summary>Sets highlight colours and opacity. Configurable because one fixed value is unreadable for some readers.</summary>
    public void SetHighlightStyle(NarrationHighlightStyle style)
    {
        _highlightStyle = style ?? NarrationHighlightStyle.Default;
        InvalidateVisual();
    }

    private void OnCameraRequested(int page, IReadOnlyList<PageRect> rects)
    {
        if (rects.Count == 0) return;
        // Reading moved to another page: show it, then place the cue on it now. Returning here kept
        // the old scroll position, so at high magnification the new page opened at its bottom.
        if (page != CurrentPage)
        {
            GoToPage(page);
            if (page != CurrentPage) return;
        }

        // Follow the active cue rather than the midpoint of a whole paragraph, and move only when the
        // cue has left the safe zone. The decision is made in page points, so it behaves identically at
        // every zoom and DPI, and it preserves the reader's magnification instead of zooming out to fit.
        var visible = new PageTransform(_zoom, _offsetX, _offsetY)
            .ToPageRect(0, 0, Bounds.Width, Bounds.Height);
        var decision = CameraFollowPlanner.Plan(
            new PageViewport(page, visible), rects, page,
            _viewModel?.FollowMode ?? FollowMode.KeepVisible,
            new FollowOptions(ReduceMotion: _viewModel?.Services.CurrentSettings.ReduceMotion ?? false));

        double beforeX = _offsetX, beforeY = _offsetY;
        if (decision.ShouldMove)
        {
            _offsetX += (visible.Left - decision.TargetVisible.Visible.Left) * _zoom;
            _offsetY += (visible.Top - decision.TargetVisible.Visible.Top) * _zoom;
            ClampOffsets();
            InvalidateVisual();
        }
        FollowLog.Write(page, rects, visible, decision, Bounds, _zoom, (beforeX, beforeY), (_offsetX, _offsetY));
    }

    /// <summary>The DPI to rasterise at, quantised so a small zoom change does not force a re-render.</summary>
    private int TargetDpi()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int dpi = (int)Math.Round(72 * _zoom * scale);
        int[] ladder = [72, 96, 120, 150, 200, 260, 340, 440, 580, 760];
        foreach (int step in ladder) if (dpi <= step) return step;
        return ladder[^1];
    }

    private void EnsureRenderedPage()
    {
        if (_pdf is null) return;
        int dpi = TargetDpi();
        if (_renderedPage is not null && _renderedPageIndex == CurrentPage && _renderedDpi == dpi) return;

        ReleasePage();
        // Rendering runs on the UI thread because the PDFium backend is not thread-safe. Extraction,
        // analysis, OCR, inference and synthesis all run off it.
        _renderedPage = _pdf.RenderPage(CurrentPage, dpi) as SkiaRenderedPage;
        _renderedPageIndex = CurrentPage;
        _renderedDpi = dpi;
        _bitmap = null;   // force a re-upload of the new raster
    }

    private void ReleasePage()
    {
        _renderedPage?.Dispose();
        _renderedPage = null;
        _renderedPageIndex = -1;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    /// <summary>Copies the Skia raster into a bitmap the compositor can blit.</summary>
    private unsafe WriteableBitmap? EnsureBitmap()
    {
        var skia = _renderedPage?.Bitmap;
        if (skia is null || skia.Width <= 0 || skia.Height <= 0) return null;
        if (_bitmap is not null && _bitmap.PixelSize.Width == skia.Width && _bitmap.PixelSize.Height == skia.Height)
            return _bitmap;

        // A PDF render is BGRA8888; anything else would need a channel swap, which shows up as red and
        // blue reversed rather than as an error.
        if (skia.BytesPerPixel != 4) return null;

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(skia.Width, skia.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var buffer = _bitmap.Lock())
        {
            int rowBytes = skia.Width * 4;
            byte* src = (byte*)skia.GetPixels();
            byte* dst = (byte*)buffer.Address;
            for (int y = 0; y < skia.Height; y++)
                Buffer.MemoryCopy(src + (long)y * skia.RowBytes, dst + (long)y * buffer.RowBytes, rowBytes, rowBytes);
        }
        return _bitmap;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.Black, bounds);
        if (_pdf is null || Bounds.Width <= 0) return;

        try
        {
            EnsureRenderedPage();
            var bitmap = EnsureBitmap();
            var (pageW, pageH) = _pdf.GetPageSize(CurrentPage);

            if (bitmap is not null && pageW > 0 && pageH > 0)
            {
                var destination = new Rect(_offsetX, _offsetY, pageW * _zoom, pageH * _zoom);
                context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), destination);
            }
            DrawHighlights(context);
        }
        catch (Exception ex)
        {
            // A rendering failure must not take the window down with it.
            context.DrawText(new FormattedText($"Render error: {ex.Message}",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("DejaVu Sans"), 13, Brushes.OrangeRed), new Point(8, 8));
        }
    }

    private void DrawHighlights(DrawingContext context)
    {
        if (!_highlightsByPage.TryGetValue(CurrentPage, out var rects) || rects.Count == 0) return;

        var transform = new PageTransform(_zoom, _offsetX, _offsetY);
        var fillColour = _highlightStyle.FillColour;
        var fill = new SolidColorBrush(Color.FromArgb(_highlightStyle.FillAlpha,
            fillColour.Red, fillColour.Green, fillColour.Blue));
        IPen? outline = _highlightStyle.OutlineColour is { } c
            ? new Pen(new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue)),
                      _highlightStyle.OutlineWidthPoints * _zoom)
            : null;
        float radius = (float)(_highlightStyle.CornerRadiusPoints * _zoom);
        // Padding is in page points and is applied before the page-to-screen transform, which
        // already scales by the zoom. Multiplying by the zoom here as well scaled it twice, so at
        // 800% each box grew by 64 px a side and covered the neighbouring lines.
        float pad = _highlightStyle.PaddingPoints;

        foreach (var rect in rects)
        {
            if (rect.IsEmpty) continue;
            var (x0, y0) = transform.ToScreen(rect.Left - pad, rect.Top - pad);
            var (x1, y1) = transform.ToScreen(rect.Right + pad, rect.Bottom + pad);
            var target = new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
            context.DrawRectangle(fill, outline, target, radius, radius);
        }
    }

    // ---- input ------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsLeftButtonPressed && _viewModel?.Plan is { } plan && _pdf is not null)
        {
            // Click to read: hit-test the original PDF and start at the containing unit. A click on
            // blank space does nothing rather than choosing an arbitrary passage.
            var (pageX, pageY) = new PageTransform(_zoom, _offsetX, _offsetY).ToPage(point.X, point.Y);
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var point = e.GetPosition(this);
        // Manual panning suspends the camera, not the audio.
        if (Math.Abs(point.X - _dragStart.X) + Math.Abs(point.Y - _dragStart.Y) > 4) _viewModel?.NotifyManualNavigation();
        _offsetX = _dragOffsetStart.X + (point.X - _dragStart.X);
        _offsetY = _dragOffsetStart.Y + (point.Y - _dragStart.Y);
        ClampOffsets();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
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
            ZoomAbout(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), point.X, point.Y);
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
        if (_pdf is null) return;
        const double Step = 80;
        _viewModel?.NotifyManualNavigation();
        var (w, h) = _pdf.GetPageSize(CurrentPage);
        double minY = Math.Min(0, Bounds.Height - h * _zoom);
        bool atBottom = _offsetY <= minY + 0.5, atTop = _offsetY >= -0.5;
        if (notchesY < 0 && atBottom && CurrentPage + 1 < _pdf.PageCount) { TurnPage(+1); return; }
        if (notchesY > 0 && atTop && CurrentPage > 0) { TurnPage(-1); return; }
        _offsetX += notchesX * Step;
        _offsetY += notchesY * Step;
        ClampOffsets();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.PageDown:
                TurnPage(+1); e.Handled = true; break;
            case Key.PageUp:
                TurnPage(-1); e.Handled = true; break;
            case Key.Escape:
                _viewModel?.Coordinator.ResumeFollow(); e.Handled = true; break;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) FitToWidth();
    }
}

/// <summary>
/// Opt-in camera diagnostics: with <c>RN_FOLLOW_LOG=1</c> every follow request is appended to
/// <c>$XDG_STATE_HOME/rail-narrator/logs/follow.log</c> with the cue, the visible region, the
/// control size, zoom and offsets, and the decision. Off by default; nothing is logged otherwise.
/// </summary>
internal static class FollowLog
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("RN_FOLLOW_LOG") == "1"
        ? System.IO.Path.Combine(RailNarrator.Infrastructure.XdgPaths.State, "logs", "follow.log")
        : null;

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
