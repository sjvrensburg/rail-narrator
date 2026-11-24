using RailReader.Core.Models;

namespace RailNarrator.Pdf.Annotations;

/// <summary>
/// An annotation dragged to a new place with the select tool: a sticky note, a text box, a
/// rectangle or a pen stroke. Text markup is not moved; it belongs to the text it is on.
/// </summary>
/// <remarks>
/// The annotation itself moves as the pointer does, so the page shows it where it will land; the
/// render thread draws a copy made after each move (<see cref="AnnotationLayer"/>), never the
/// object being moved. When the drag ends, <see cref="ReviewSession.Moved"/> records one undoable
/// step from where it was to where it is, with Core's <see cref="MoveAnnotationAction"/>.
/// </remarks>
public sealed class AnnotationMove
{
    private readonly float _startX, _startY;

    private AnnotationMove(Annotation annotation, float x, float y)
    {
        Annotation = annotation;
        Before = PositionSnapshot.Capture(annotation);
        _startX = x;
        _startY = y;
    }

    public Annotation Annotation { get; }

    /// <summary>Where it was when the drag began.</summary>
    public PositionSnapshot Before { get; }

    /// <summary>True once the pointer has moved far enough for this to be a move, not a click.</summary>
    public bool Moved { get; private set; }

    public static bool CanMove(Annotation annotation)
        => annotation is TextNoteAnnotation or FreeTextAnnotation or RectAnnotation or FreehandAnnotation;

    /// <returns>Null for an annotation that does not move.</returns>
    public static AnnotationMove? Begin(Annotation annotation, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return CanMove(annotation) ? new AnnotationMove(annotation, x, y) : null;
    }

    /// <summary>The pointer is at (x, y): the annotation is moved by as much as the pointer has.</summary>
    public void To(float x, float y)
    {
        float dx = x - _startX, dy = y - _startY;
        if (!Moved && Math.Abs(dx) < MarkupDrag.DragThreshold && Math.Abs(dy) < MarkupDrag.DragThreshold) return;
        Moved = true;
        Translate(Before, dx, dy).ApplyTo(Annotation);
    }

    /// <summary>Puts it back where it was (a drag taken away mid-way).</summary>
    public void Cancel() => Before.ApplyTo(Annotation);

    private static PositionSnapshot Translate(PositionSnapshot p, float dx, float dy) => new()
    {
        X = p.X + dx,
        Y = p.Y + dy,
        Points = p.Points?.Select(q => new PointF(q.X + dx, q.Y + dy)).ToList(),
        Rects = p.Rects?.Select(r => r with { X = r.X + dx, Y = r.Y + dy }).ToList(),
    };
}

/// <summary>A box dragged out on the page, for a text box (and, later, a rectangle), in page points.</summary>
public sealed class BoxDrag(int pageIndex, float x, float y, string colour)
{
    private readonly float _x0 = x, _y0 = y;
    private float _x1 = x, _y1 = y;

    public int PageIndex { get; } = pageIndex;

    /// <summary>The box so far, corners in any order made top-left.</summary>
    public (float X, float Y, float W, float H) Box
        => (Math.Min(_x0, _x1), Math.Min(_y0, _y1), Math.Abs(_x1 - _x0), Math.Abs(_y1 - _y0));

    /// <summary>True once it is big enough to be a box rather than a click.</summary>
    public bool IsBox => Box.W >= MarkupDrag.DragThreshold * 4 && Box.H >= MarkupDrag.DragThreshold * 4;

    /// <summary>An outline to draw while dragging.</summary>
    public RectAnnotation? Preview { get; private set; }

    public void To(float px, float py)
    {
        _x1 = px;
        _y1 = py;
        var (bx, by, bw, bh) = Box;
        Preview = IsBox ? new RectAnnotation { X = bx, Y = by, W = bw, H = bh, Color = colour, StrokeWidth = 1, Opacity = 0.8f } : null;
    }
}
