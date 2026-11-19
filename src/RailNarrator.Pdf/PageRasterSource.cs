using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailNarrator.Pdf;

/// <summary>One raster of a page: which page, at what resolution, through which colour filter.</summary>
/// <param name="PageIndex">Zero-based page.</param>
/// <param name="Dpi">Resolution of the raster.</param>
/// <param name="Effect">RailReader colour filter applied to it.</param>
/// <param name="Intensity">Strength of the filter, 0 to 1. Ignored when there is no filter.</param>
public readonly record struct PageRasterKey(int PageIndex, int Dpi, ColourEffect Effect, float Intensity);

/// <summary>A rendered page, already filtered. Whoever receives it owns it and disposes it.</summary>
public sealed class PageRaster : IDisposable
{
    private readonly IDisposable? _owner;

    internal PageRaster(PageRasterKey key, SKBitmap bitmap, IDisposable? owner, TimeSpan renderTime)
    {
        Key = key;
        Bitmap = bitmap;
        _owner = owner;
        RenderTime = renderTime;
    }

    public PageRasterKey Key { get; }

    /// <summary>
    /// How long the page took to render and filter, waiting for PDFium included. Tells the host
    /// whether a quick low-resolution render of this document is in fact quick.
    /// </summary>
    public TimeSpan RenderTime { get; }

    /// <summary>BGRA, premultiplied.</summary>
    public SKBitmap Bitmap { get; }

    public void Dispose()
    {
        if (_owner is not null) _owner.Dispose();
        else Bitmap.Dispose();
    }
}

/// <summary>
/// Renders a document's pages for display on a thread of its own, so the thread that draws the
/// window never renders a page and never waits for PDFium.
/// </summary>
/// <remarks>
/// <para>
/// Core serialises every PDFium call in the process behind one lock. The page used to be rendered
/// by the UI thread inside its paint, so a page turn cost a whole render there (10 ms for a page of
/// text, 50 to 110 ms for a scanned page, seconds for a page of vector drawing) and queued behind
/// whatever else held the lock: an equation crop for the maths interpreter, an extraction. Turning
/// several pages quickly stalled the window once per page.
/// </para>
/// <para>
/// The host says which rasters it wants, most urgent first (<see cref="Request"/>). One worker
/// renders them in that order and hands each over with <see cref="Ready"/>. A new request replaces
/// the old one, and a render no longer wanted is cancelled between bands (Core's tiled renderer
/// takes the lock one band at a time), so paging through ten pages renders the page the reader
/// stopped on, not all ten.
/// </para>
/// <para>
/// Page sizes are read once when the document is opened, off the caller's thread, and answered from
/// memory afterwards: the first call to Core's <c>GetPageSize</c> loads every page under the lock.
/// </para>
/// </remarks>
public sealed class PageRasterSource : IDisposable
{
    private readonly IPdfService _pdf;
    private readonly (double Width, double Height)[] _pageSizes;
    private readonly object _gate = new();
    private readonly Thread _worker;
    private List<PageRasterKey> _wanted = [];
    private PageRasterKey? _inFlight;
    private CancellationTokenSource? _inFlightCancel;
    private ColourEffectShaders? _shaders;
    private bool _disposed;

    private PageRasterSource(string path, IPdfService pdf, (double Width, double Height)[] pageSizes, IReadOnlyList<OutlineEntry> outline)
    {
        Path = path;
        _pdf = pdf;
        _pageSizes = pageSizes;
        Outline = outline;
        _worker = new Thread(Work) { IsBackground = true, Name = "page-raster" };
        _worker.Start();
    }

    /// <summary>
    /// Opens a document for display. The file is read and its page sizes measured on the thread
    /// pool, so the caller's thread is not held while PDFium is busy.
    /// </summary>
    public static Task<PageRasterSource> OpenAsync(
        string path, string? password = null, IPdfServiceFactory? factory = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Task.Run(() =>
        {
            var pdf = (factory ?? new SkiaPdfServiceFactory()).CreatePdfService(path, password);
            var sizes = pdf.GetPageSizes(0).ToArray();
            ct.ThrowIfCancellationRequested();
            // The outline is read here too, for the same reason: it takes the PDFium lock.
            IReadOnlyList<OutlineEntry> outline;
            try { outline = pdf.Outline.ToList(); }
            catch (Exception) { outline = []; }   // a damaged outline must not stop the document opening
            return new PageRasterSource(path, pdf, sizes, outline);
        }, ct);
    }

    public string Path { get; }

    public int PageCount => _pageSizes.Length;

    /// <summary>The document's own outline (its bookmarks), read when it was opened; empty when it has none.</summary>
    public IReadOnlyList<OutlineEntry> Outline { get; }

    /// <summary>A page's size in points. Answered from memory; never calls PDFium.</summary>
    public (double Width, double Height) PageSize(int pageIndex)
        => pageIndex >= 0 && pageIndex < _pageSizes.Length ? _pageSizes[pageIndex] : (0, 0);

    /// <summary>
    /// A raster is ready. Raised on the worker thread; the handler owns the raster and disposes it.
    /// </summary>
    public event Action<PageRaster>? Ready;

    /// <summary>A raster could not be rendered. Raised on the worker thread. It is not tried again unless asked for again.</summary>
    public event Action<PageRasterKey, Exception>? Failed;

    /// <summary>
    /// Says which rasters are wanted now, most urgent first, replacing any earlier request. Each is
    /// delivered once. A render in progress carries on if it is still in the list and is cancelled
    /// if it is not.
    /// </summary>
    public void Request(IReadOnlyList<PageRasterKey> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        lock (_gate)
        {
            if (_disposed) return;
            _wanted = wanted.Where(k => k.PageIndex >= 0 && k.PageIndex < _pageSizes.Length && k.Dpi > 0).Distinct().ToList();
            if (_inFlight is { } rendering && !_wanted.Contains(rendering)) _inFlightCancel?.Cancel();
            Monitor.PulseAll(_gate);
        }
    }

    private void Work()
    {
        try { WorkLoop(); }
        finally { _shaders?.Dispose(); }
    }

    private void WorkLoop()
    {
        while (true)
        {
            PageRasterKey key;
            CancellationToken ct;
            lock (_gate)
            {
                while (!_disposed && _wanted.Count == 0) Monitor.Wait(_gate);
                if (_disposed) return;
                key = _wanted[0];
                _inFlight = key;
                _inFlightCancel = new CancellationTokenSource();
                ct = _inFlightCancel.Token;
            }

            PageRaster? raster = null;
            Exception? failure = null;
            try { raster = Render(key, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; }

            bool stillWanted, disposed;
            lock (_gate)
            {
                _inFlightCancel?.Dispose();
                _inFlightCancel = null;
                _inFlight = null;
                // Rendered or failed, it is done with: only a new request asks for it again. A
                // cancelled render stays in the list if a later request put it back.
                stillWanted = (raster is not null || failure is not null) && _wanted.Remove(key);
                disposed = _disposed;
            }

            if (disposed) { raster?.Dispose(); return; }
            // A render that could not be cancelled and finished after the host stopped wanting it
            // is dropped here, not handed over: the host has moved on to another page.
            if (!stillWanted) { raster?.Dispose(); continue; }
            try
            {
                if (raster is not null) Dispatch(raster);
                else if (failure is not null) Failed?.Invoke(key, failure);
            }
            catch (Exception)
            {
                // A handler that throws must not end the worker: no page would ever be drawn again.
            }
        }
    }

    private void Dispatch(PageRaster raster)
    {
        var handler = Ready;
        if (handler is null) raster.Dispose();
        else handler(raster);
    }

    private PageRaster Render(PageRasterKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        // The tiled renderer takes the PDFium lock a band at a time and can be cancelled between
        // bands; the plain one holds the lock for the whole page.
        IRenderedPage rendered = _pdf is IPdfTiledRenderService tiled
            ? tiled.RenderPageTiled(key.PageIndex, key.Dpi, 0, ct)
            : _pdf.RenderPage(key.PageIndex, key.Dpi);
        if (rendered is not SkiaRenderedPage page)
        {
            (rendered as IDisposable)?.Dispose();
            throw new InvalidOperationException("The PDF renderer did not produce a Skia raster.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var source = page.Bitmap;
            // A PDF render is BGRA8888; anything else would show red and blue reversed, not an error.
            if (source.Width <= 0 || source.Height <= 0 || source.BytesPerPixel != 4)
                throw new InvalidOperationException($"Page {key.PageIndex + 1} rendered as an unusable raster.");

            if (key.Effect == ColourEffect.None)
                return new PageRaster(key, source, page, System.Diagnostics.Stopwatch.GetElapsedTime(started));

            // Filtered here, on the worker, into a copy: the filter is a per-pixel shader run on the
            // CPU, and on a large raster it is as expensive as the render.
            _shaders ??= new ColourEffectShaders();
            using var paint = _shaders.CreatePaint(key.Effect, key.Intensity);
            if (paint is null) return new PageRaster(key, source, page, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            var filtered = new SKBitmap(source.Info);
            try
            {
                using (var canvas = new SKCanvas(filtered)) canvas.DrawBitmap(source, 0, 0, paint);
                page.Dispose();
                return new PageRaster(key, filtered, owner: null, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
            catch
            {
                filtered.Dispose();
                throw;
            }
        }
        catch
        {
            page.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The colour of blank paper under a colour filter: what an unprinted part of a page looks like,
    /// for drawing a page whose raster has not arrived yet. White becomes black under the contrast,
    /// visibility and invert filters and cream under amber, in proportion to the filter's strength.
    /// </summary>
    public static SKColor PaperColour(ColourEffect effect, double intensity)
    {
        float t = (float)Math.Clamp(intensity, 0, 1);
        (float r, float g, float b) = effect switch
        {
            ColourEffect.None => (1f, 1f, 1f),
            ColourEffect.Amber => (1f, 1f, 0.85f),
            _ => (0f, 0f, 0f),
        };
        static byte Mix(float to, float t) => (byte)Math.Round(255 * (1 + (to - 1) * t));
        return new SKColor(Mix(r, t), Mix(g, t), Mix(b, t));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _wanted = [];
            _inFlightCancel?.Cancel();
            Monitor.PulseAll(_gate);
        }
        // Not joined: the worker may be inside a render, and the caller is the UI thread. It ends
        // after the band it is on and disposes what it rendered.
    }
}
