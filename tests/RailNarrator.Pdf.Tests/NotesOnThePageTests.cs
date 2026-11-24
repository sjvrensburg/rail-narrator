using RailNarrator.Pdf.Annotations;
using RailReader.Core.Models;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>Sticky notes and text boxes placed on the page, and moved (ADR-0014).</summary>
public sealed class NotesOnThePageTests
{
    private static (ReviewSession Session, ReviewDocument Document) New()
    {
        var document = new ReviewDocument("0123", null);
        return (new ReviewSession(document, () => new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)) { Author = "R" }, document);
    }

    [Fact]
    public void ANoteAndATextBoxArePlacedWhereAsked_SignedAndUndoable()
    {
        var (session, document) = New();

        var note = session.AddNote(3, 540, 120, "  See the appendix. ");
        var box = session.AddText(3, 72, 700, 180, 48, "Fig. 2 is unlabelled", "#000000");

        Assert.Equal([note, box], document.File.Pages[3]);
        Assert.Equal(("See the appendix.", 540f, 120f, 0.9f), (note.EffectiveContents, note.X, note.Y, note.Opacity));
        Assert.Equal(("Fig. 2 is unlabelled", 180f, 48f), (box.Contents, box.W, box.H));
        Assert.All(document.File.Pages[3], a => Assert.Equal("R", a.Author));
        Assert.All(document.File.Pages[3], a => Assert.False(string.IsNullOrEmpty(a.NativeId)));
        Assert.Empty(document.Links);   // tied to no text
        Assert.Same(box, session.Selected);

        session.Undo();
        Assert.Equal([note], document.File.Pages[3]);
    }

    [Fact]
    public void AMoveFollowsThePointer_AndIsOneStepToUndo()
    {
        var (session, document) = New();
        var note = session.AddNote(0, 100, 100, "x");
        int changes = 0;
        document.Changed += () => changes++;

        var move = AnnotationMove.Begin(note, 101, 101)!;
        move.To(101.5f, 101.5f);   // not yet a move
        Assert.False(move.Moved);
        Assert.Equal(100, note.X);
        move.To(151, 81);
        move.To(161, 91);
        Assert.Equal((160f, 90f), (note.X, note.Y));
        session.Moved(move);
        Assert.Equal(1, changes);

        session.Undo();
        Assert.Equal((100f, 100f), (note.X, note.Y));
        session.Redo();
        Assert.Equal((160f, 90f), (note.X, note.Y));
    }

    [Fact]
    public void APenStrokeMovesEveryPoint_AMoveTakenAwayPutsItBack_AndMarkupDoesNotMove()
    {
        var stroke = new FreehandAnnotation { Points = [new PointF(10, 10), new PointF(20, 30)] };
        var move = AnnotationMove.Begin(stroke, 0, 0)!;
        move.To(5, -5);
        Assert.Equal([new PointF(15, 5), new PointF(25, 25)], stroke.Points);
        move.Cancel();
        Assert.Equal([new PointF(10, 10), new PointF(20, 30)], stroke.Points);

        Assert.Null(AnnotationMove.Begin(new HighlightAnnotation(), 0, 0));
    }

    [Fact]
    public void AClickWithoutMovingRecordsNothing()
    {
        var (session, _) = New();
        var box = session.AddText(0, 10, 10, 100, 40, "t", "#000000");
        var move = AnnotationMove.Begin(box, 20, 20)!;
        session.Moved(move);
        session.Undo();   // undoes the box, not a move
        Assert.Null(session.PageOf(box));
    }

    [Fact]
    public void ABoxIsDraggedOutInAnyDirection_AndASmallOneIsAClick()
    {
        var drag = new BoxDrag(2, 300, 400, "#000000");
        drag.To(302, 401);
        Assert.False(drag.IsBox);
        Assert.Null(drag.Preview);

        drag.To(150, 350);
        Assert.True(drag.IsBox);
        Assert.Equal((150f, 350f, 150f, 50f), drag.Box);
        Assert.Equal((150f, 350f, 150f, 50f), (drag.Preview!.X, drag.Preview.Y, drag.Preview.W, drag.Preview.H));
    }
}
