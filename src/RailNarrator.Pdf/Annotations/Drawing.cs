using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailNarrator.Pdf.Annotations;

/// <summary>A pen stroke being drawn, in page points.</summary>
/// <remarks>
/// A point closer than <see cref="MinStep"/> to the last one kept adds nothing to the line and only
/// makes the stroke (and the PDF it is exported to) larger, so it is dropped. A stroke of a single
/// point is a dot the reader did not mean, and is not kept.
/// </remarks>
public sealed class StrokeDrag
{
    /// <summary>The smallest step between two kept points, in points.</summary>
    public const float MinStep = 0.75f;

    private readonly List<PointF> _points = [];

    public StrokeDrag(int pageIndex, float x, float y, string colour, float opacity, float width)
    {
        PageIndex = pageIndex;
        Colour = colour;
        Opacity = opacity;
        Width = width;
        _points.Add(new PointF(x, y));
    }

    public int PageIndex { get; }

    public string Colour { get; }

    public float Opacity { get; }

    public float Width { get; }

    public IReadOnlyList<PointF> Points => _points;

    /// <summary>True once there is a line to keep.</summary>
    public bool IsStroke => _points.Count >= 2;

    /// <summary>The line so far, to draw while drawing; a new object each time, never changed after.</summary>
    public FreehandAnnotation? Preview { get; private set; }

    public void To(float x, float y)
    {
        var last = _points[^1];
        if (Math.Abs(x - last.X) < MinStep && Math.Abs(y - last.Y) < MinStep) return;
        _points.Add(new PointF(x, y));
        Preview = new FreehandAnnotation { Points = [.. _points], Color = Colour, Opacity = Opacity, StrokeWidth = Width };
    }
}

/// <summary>
/// A pen stroke stretched by one of its eight handles with the select tool, as Core's own tool does
/// it: every point scaled from the old box to the new, and a stroke never made smaller than
/// <see cref="MinSize"/> points either way.
/// </summary>
public sealed class StrokeResize
{
    /// <summary>The smallest box a stroke is resized to, in points (Core's limit).</summary>
    public const float MinSize = 10f;

    private readonly RectF _bounds;
    private readonly float _startX, _startY;

    private StrokeResize(FreehandAnnotation stroke, ResizeHandle handle, RectF bounds, float x, float y)
    {
        Stroke = stroke;
        Handle = handle;
        _bounds = bounds;
        Before = [.. stroke.Points];
        _startX = x;
        _startY = y;
    }

    public FreehandAnnotation Stroke { get; }

    public ResizeHandle Handle { get; }

    /// <summary>The points before the resize began.</summary>
    public List<PointF> Before { get; }

    public bool Resized { get; private set; }

    /// <summary>Starts a resize when (x, y) is on one of the stroke's handles; null otherwise.</summary>
    public static StrokeResize? Begin(FreehandAnnotation stroke, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        var handle = AnnotationGeometry.HitTestResizeHandle(stroke, x, y);
        if (handle == ResizeHandle.None || AnnotationGeometry.GetAnnotationBounds(stroke) is not { } bounds) return null;
        if (bounds.Width < 1 || bounds.Height < 1) return null;
        return new StrokeResize(stroke, handle, bounds, x, y);
    }

    public void To(float x, float y)
    {
        var box = AnnotationGeometry.ComputeNewBounds(_bounds, Handle, x, y, _startX, _startY);
        if (box.Width < MinSize || box.Height < MinSize) return;
        Resized = true;
        var points = new List<PointF>(Before.Count);
        foreach (var p in Before)
        {
            float nx = (p.X - _bounds.Left) / _bounds.Width, ny = (p.Y - _bounds.Top) / _bounds.Height;
            points.Add(new PointF(box.Left + nx * box.Width, box.Top + ny * box.Height));
        }
        Stroke.Points = points;
    }

    /// <summary>Puts it back as it was (a resize taken away mid-way).</summary>
    public void Cancel() => Stroke.Points = [.. Before];
}
