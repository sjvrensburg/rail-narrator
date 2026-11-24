using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailNarrator.Pdf;
using RailNarrator.Pdf.Annotations;
using SkiaSharp;

namespace RailNarrator.App.Views;

/// <summary>
/// Draws the reader's annotations on a page, on the render thread, with RailReaderCore's renderer.
/// </summary>
/// <remarks>
/// The snapshot is a private copy that nothing changes after it is made (<see cref="AnnotationPageSnapshot"/>),
/// so this operation holds no native resource and needs no reference counting, unlike
/// <see cref="FilteredPageDraw"/>.
/// </remarks>
internal sealed class AnnotationDraw : ICustomDrawOperation
{
    private readonly AnnotationPageSnapshot _snapshot;
    private readonly PageTransform _transform;
    private readonly bool _darkBackdrop;
    private readonly bool _expandAllNotes;

    /// <param name="bounds">The control's own area: what is drawn is clipped to it.</param>
    /// <param name="transform">Page points to the control's coordinates.</param>
    public AnnotationDraw(Rect bounds, AnnotationPageSnapshot snapshot, PageTransform transform, bool darkBackdrop, bool expandAllNotes)
    {
        Bounds = bounds;
        _snapshot = snapshot;
        _transform = transform;
        _darkBackdrop = darkBackdrop;
        _expandAllNotes = expandAllNotes;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect((float)Bounds.Left, (float)Bounds.Top, (float)Bounds.Right, (float)Bounds.Bottom));
            _snapshot.Draw(canvas, _transform, _darkBackdrop, _expandAllNotes);
        }
        finally { canvas.Restore(); }
    }

    public void Dispose() { }
}

/// <summary>Draws the markup being dragged in annotate mode, with Core's preview renderer, on the render thread.</summary>
/// <remarks>The annotation is made anew at each pointer move and never changed after, so it may be held here.</remarks>
internal sealed class MarkupPreviewDraw(Rect bounds, RailReader.Core.Models.Annotation preview, PageTransform transform) : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect((float)Bounds.Left, (float)Bounds.Top, (float)Bounds.Right, (float)Bounds.Bottom));
            MarkupPreview.Draw(canvas, transform, preview);
        }
        finally { canvas.Restore(); }
    }

    public void Dispose() { }
}
