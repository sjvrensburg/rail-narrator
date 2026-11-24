using RailNarrator.Domain;
using RailNarrator.Pdf.Annotations;
using RailReader.Core.Models;
using Xunit;
using static RailNarrator.TestFixtures.SyntheticNarration;

namespace RailNarrator.Pdf.Tests;

/// <summary>Text marked by hand in annotate mode (ADR-0014): a drag over the cached extraction's character boxes.</summary>
public sealed class MarkupDragTests
{
    private const string First = "The estimator is consistent.";
    private const string Second = "Its variance is bounded.";

    // Characters are 5.5 points wide from x = 72, lines 10 points high at y = 100 and y = 120.
    private static PageTextIndex Index(bool ocr = false)
        => PageTextIndex.Build(Page(0, [new LineSpec(First, 100, FromOcr: ocr), new LineSpec(Second, 120, FromOcr: ocr)]));

    private static float X(int column) => LeftMargin + column * 5.5f + 2.75f;

    [Fact]
    public void ADragAcrossTwoLinesCoversBothFromWhereItStartedToWhereItEnds()
    {
        var page = Index();
        var (drag, refusal) = MarkupDrag.Begin(page, AnnotationTool.Highlight, "#FFFF00", 0.35f, X(4), 105);
        Assert.Null(refusal);
        Assert.NotNull(drag);

        drag.Move(X(10), 112);   // between the lines: the end follows the nearest character
        drag.Move(X(2), 125);    // "Its" on the second line

        var range = drag.Range!.Value;
        string text = page.Text.Text;
        Assert.StartsWith("estimator", text.Substring(range.Start));
        Assert.EndsWith("Its", text.Substring(range.Start, range.Length));
        var preview = Assert.IsType<HighlightAnnotation>(drag.Preview);
        Assert.Equal([100f, 120f], preview.Rects.Select(r => r.Y));
        Assert.Equal(0.35f, preview.Opacity);
    }

    [Fact]
    public void ADragBackwardsCoversTheSameText()
    {
        var page = Index();
        var forward = MarkupDrag.Begin(page, AnnotationTool.Underline, "#00A000", 1f, X(4), 105).Drag!;
        forward.Move(X(12), 105);
        var backward = MarkupDrag.Begin(page, AnnotationTool.Underline, "#00A000", 1f, X(12), 105).Drag!;
        backward.Move(X(4), 105);

        Assert.Equal(forward.Range, backward.Range);
        Assert.IsType<UnderlineAnnotation>(backward.Preview);
    }

    [Fact]
    public void APressOnBlankPaperStartsNothing_AndAPageWithNoTextSaysSo()
    {
        var page = Index();
        Assert.Equal((null, null), MarkupDrag.Begin(page, AnnotationTool.Highlight, "#FFFF00", 0.35f, X(4), 300));
        Assert.Equal((null, null), MarkupDrag.Begin(page, AnnotationTool.Highlight, "#FFFF00", 0.35f, X(4), 115));   // in the leading

        var empty = PageTextIndex.Build(Page(0, []));
        var (drag, refusal) = MarkupDrag.Begin(empty, AnnotationTool.Highlight, "#FFFF00", 0.35f, 100, 100);
        Assert.Null(drag);
        Assert.Equal("No selectable text on this page.", refusal);
    }

    [Fact]
    public void APressWithoutMovingIsNotADrag()
    {
        var drag = MarkupDrag.Begin(Index(), AnnotationTool.StrikeOut, "#FF0000", 1f, X(4), 105).Drag!;
        drag.Move(X(4) + 1, 105.5f);
        Assert.Null(drag.Range);
        Assert.Null(drag.Preview);
    }

    [Fact]
    public void TextRecognisedByOcrIsMarkedFromItsOwnBoxes()
    {
        var page = Index(ocr: true);
        Assert.True(page.Text.FromOcr);
        var drag = MarkupDrag.Begin(page, AnnotationTool.Squiggly, "#00A000", 1f, X(0), 125).Drag!;
        drag.Move(X(2), 125);
        Assert.Equal(120, Assert.IsType<SquigglyAnnotation>(drag.Preview).Rects.Single().Y);
    }

    [Fact]
    public void ARangeMarkedByHandIsQuotedAndLinkedToThePassageThatReadsIt()
    {
        var page0 = Page(0, [new LineSpec(First, 100), new LineSpec(Second, 120)]);
        var snapshot = new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree, [page0]);
        int second = page0.Text.IndexOf(Second, StringComparison.Ordinal);
        var plan = Plan([
            ProseUnit("u0", First, Anchor("a0", 0, page0.Lines[0].Rect, new TextRange(0, First.Length))),
            ProseUnit("u1", Second, Anchor("a1", 0, page0.Lines[1].Rect, new TextRange(second, Second.Length))),
        ]);
        var index = PageTextIndex.Build(page0);

        // From "consistent." over the line break to "Its".
        int start = page0.Text.IndexOf("consistent", StringComparison.Ordinal);
        var target = ReviewTarget.ForRange(plan, snapshot, index, new TextRange(start, second + 3 - start))!;

        Assert.Equal("consistent. Its", target.QuotedText);
        Assert.Equal(("s00000", "u0"), (target.SegmentId, target.UnitId));
        Assert.True(target.ByHand);
        Assert.Equal(2, target.Pages.Single().Rects.Count);

        // Text no passage reads is still marked, with no passage to read from.
        var unread = ReviewTarget.ForRange(Plan([]), snapshot, index, new TextRange(0, 3))!;
        Assert.Null(unread.SegmentId);
        Assert.Null(ReviewTarget.ForRange(plan, snapshot, index, new TextRange(page0.Text.Length, 0)));
    }

    [Fact]
    public void AQuoteFromThePageReadsAsTheUnitsDo_LigaturesHyphenMarkersAndUnknownGlyphs()
    {
        // As PDFium gives a typeset article: "fi" at U+0002 inside words, U+0002 also as the marker
        // of a hyphen that ends a line, and U+0007 from a symbol font that nothing identifies.
        const string page = "a speci\u0002cation of the de\u0002ned esti-\nmate and the \u0002rst\nvari\u0002\nance of \u0007 test";
        var snapshot = new ExtractionSnapshot(new ExtractionIdentity("x", 1), CoordinateFrameId.DisplayedPagePoints(), ExtractionOptions.ModelFree,
            [Page(0, []) with { Text = page }]);
        var ligatures = PageQuote.Ligatures(snapshot);
        Assert.Equal("fi", ligatures['\u0002']);

        Assert.Equal("a specification of the defined estimate and the first variance of test",
            PageQuote.Of(page, 0, page.Length, ligatures));
        // Nothing identified: the glyphs are left out, not guessed.
        Assert.Equal("a speci cation of", PageQuote.Of(page, 0, "a speci\u0002cation of".Length, new Dictionary<char, string>()));
    }

    [Fact]
    public void EachMarkupKindIsMadeAndRecolouredAsOneStep_AndHandMarksAreNotWhatWasJustRead()
    {
        var page0 = Page(0, [new LineSpec(First, 100)]);
        var snapshot = new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree, [page0]);
        var plan = Plan([ProseUnit("u0", First, Anchor("a0", 0, page0.Lines[0].Rect, new TextRange(0, First.Length)))]);
        var target = ReviewTarget.ForRange(plan, snapshot, PageTextIndex.Build(page0), new TextRange(0, First.Length))!;
        var document = new ReviewDocument("0123", null);
        var session = new ReviewSession(document) { Author = "Reviewer" };

        var made = new[] { AnnotationTool.Highlight, AnnotationTool.Underline, AnnotationTool.StrikeOut, AnnotationTool.Squiggly }
            .Select(tool => session.Markup(target, tool, null, "#FF0000", 1f)).ToList();
        Assert.Equal(["HighlightAnnotation", "UnderlineAnnotation", "StrikeOutAnnotation", "SquigglyAnnotation"],
            made.Select(a => a.GetType().Name));
        Assert.All(made, a => Assert.Equal("Reviewer", a.Author));
        Assert.All(made, a => Assert.True(document.Links[a.NativeId!].ByHand));

        // N or H on the same sentence makes its own mark, not one of these.
        var justRead = ReviewTarget.Find(plan, snapshot, new PageTextCache(snapshot), "s00000", ["a0"], wholePassage: false)!;
        Assert.Null(session.MadeOn(justRead));

        made[0].ColorComponents = [1f, 0f, 0f];
        session.Recolour(made[0], "#0066FF");
        Assert.Equal("#0066FF", made[0].Color);
        Assert.Null(made[0].ColorComponents);
        session.Undo();
        Assert.Equal("#FF0000", made[0].Color);
        Assert.Equal([1f, 0f, 0f], made[0].ColorComponents!);

        Assert.Throws<ArgumentException>(() => session.Markup(target, AnnotationTool.Pen, null, "#FF0000", 1f));
    }
}
