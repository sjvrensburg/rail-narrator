using RailNarrator.Pdf.Annotations;
using RailReader.Core.Models;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>Pen strokes, rectangles and resizing a stroke (ADR-0014).</summary>
public sealed class DrawingTests
{
    private static (ReviewSession Session, ReviewDocument Document) New()
    {
        var document = new ReviewDocument("0123", null);
        return (new ReviewSession(document), document);
    }

    [Fact]
    public void AStrokeKeepsThePointsItWasGiven_DroppingOnesThatAddNothing()
    {
        var drag = new StrokeDrag(0, 10, 10, "#FF0000", 0.8f, 2);
        Assert.False(drag.IsStroke);
        drag.To(10.2f, 10.3f);   // under the step: nothing
        Assert.False(drag.IsStroke);
        Assert.Null(drag.Preview);
        drag.To(20, 15);
        drag.To(30, 12);
        Assert.Equal([new PointF(10, 10), new PointF(20, 15), new PointF(30, 12)], drag.Points);
        Assert.Equal(3, drag.Preview!.Points.Count);

        var (session, document) = New();
        var stroke = session.AddStroke(0, drag.Points, drag.Colour, drag.Width);
        Assert.Equal(drag.Points, stroke.Points);
        Assert.Equal((0.8f, 2f, "#FF0000"), (stroke.Opacity, stroke.StrokeWidth, stroke.Color));
        Assert.Same(stroke, Assert.Single(document.File.Pages[0]));
        Assert.Throws<ArgumentException>(() => session.AddStroke(0, [new PointF(1, 1)], "#FF0000", 2));
    }

    [Fact]
    public void ARectangleIsUnfilledAndUndoable()
    {
        var (session, document) = New();
        var box = session.AddRectangle(1, 50, 60, 200, 80, "#0066FF", 2);
        Assert.Equal((50f, 60f, 200f, 80f, false, 0.5f), (box.X, box.Y, box.W, box.H, box.Filled, box.Opacity));
        session.Undo();
        Assert.Empty(document.File.Pages[1]);
    }

    [Fact]
    public void AStrokeResizedByACornerScalesEveryPoint_AndUndoesInOneStep()
    {
        var (session, _) = New();
        var stroke = session.AddStroke(0, [new PointF(100, 100), new PointF(150, 125), new PointF(200, 200)], "#FF0000", 2);

        // The bottom-right handle sits 3 points outside the stroke's box (Core's selection margin).
        var resize = StrokeResize.Begin(stroke, 203, 203)!;
        Assert.Equal(ResizeHandle.BottomRight, resize.Handle);
        resize.To(303, 303);   // the box from 100×100 to 200×200
        Assert.Equal([new PointF(100, 100), new PointF(200, 150), new PointF(300, 300)], stroke.Points);
        session.Resized(resize);

        session.Undo();
        Assert.Equal([new PointF(100, 100), new PointF(150, 125), new PointF(200, 200)], stroke.Points);
        session.Redo();
        Assert.Equal(new PointF(300, 300), stroke.Points[^1]);
    }

    [Fact]
    public void AStrokeIsNotResizedBelowCoresMinimum_NorFromOffItsHandles()
    {
        var stroke = new FreehandAnnotation { Points = [new PointF(100, 100), new PointF(200, 200)] };
        Assert.Null(StrokeResize.Begin(stroke, 150, 150));   // the middle is no handle

        var resize = StrokeResize.Begin(stroke, 203, 203)!;
        resize.To(108, 108);   // a 5×5 box
        Assert.False(resize.Resized);
        Assert.Equal(new PointF(200, 200), stroke.Points[1]);
        resize.To(150, 150);
        resize.Cancel();
        Assert.Equal(new PointF(200, 200), stroke.Points[1]);
    }
}
