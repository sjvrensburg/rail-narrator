using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace RailNarrator.App.Views;

/// <summary>
/// A native Skia object used by both the UI thread and the render thread. Each holder of a
/// reference releases it once; the object is disposed when the last one does.
/// </summary>
/// <remarks>
/// The UI thread replaces a page's raster or the colour filter while the render thread may still
/// be drawing a frame recorded with the old one, so neither may simply be disposed by its owner.
/// </remarks>
internal sealed class Shared<T> where T : class, IDisposable
{
    private int _references = 1;

    public Shared(T value) => Value = value;

    public T Value { get; }

    /// <summary>Takes another reference. Only a holder of one may call this.</summary>
    public Shared<T> AddReference()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) Value.Dispose();
    }
}

/// <summary>
/// Draws an unfiltered page raster through a colour filter, on the render thread.
/// </summary>
/// <remarks>
/// Rasters used to be filtered once each, on the rasteriser's thread, and a change of colour
/// scheme waited for every raster on screen to be filtered again: a CPU shader pass over some
/// ten million pixels at the reader's 460 %. The highlight, drawn at paint time, changed at once
/// and the page after it. Filtering as the page is drawn, as RailReader2 does, costs the GPU a
/// pass over the pixels on screen each frame and makes a change of scheme one repaint.
/// </remarks>
internal sealed class FilteredPageDraw : ICustomDrawOperation
{
    // Linear with mipmaps: the raster is close to 1:1 at its own zoom, and a stand-in from
    // another zoom (or a preview) is minified or magnified without shimmering.
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly Shared<SKImage> _image;
    private readonly Shared<SKColorFilter>? _filter;
    private readonly Rect _destination;

    /// <param name="bounds">The control's own area: what is drawn is clipped to it.</param>
    /// <param name="image">A reference this operation now owns and releases.</param>
    /// <param name="filter">Likewise, or null for no filter.</param>
    /// <param name="destination">Where the whole raster goes, in the control's coordinates.</param>
    public FilteredPageDraw(Rect bounds, Shared<SKImage> image, Shared<SKColorFilter>? filter, Rect destination)
    {
        Bounds = bounds;
        _image = image;
        _filter = filter;
        _destination = destination;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        var image = _image.Value;
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect((float)Bounds.Left, (float)Bounds.Top, (float)Bounds.Right, (float)Bounds.Bottom));
            using var paint = new SKPaint { ColorFilter = _filter?.Value };
            canvas.DrawImage(image, new SKRect(0, 0, image.Width, image.Height),
                new SKRect((float)_destination.Left, (float)_destination.Top, (float)_destination.Right, (float)_destination.Bottom),
                Sampling, paint);
        }
        finally { canvas.Restore(); }
    }

    public void Dispose()
    {
        _image.Release();
        _filter?.Release();
    }
}
