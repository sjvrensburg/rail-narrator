using RailNarrator.Domain;
using RailNarrator.Pdf.Annotations;
using RailNarrator.TestFixtures;
using RailReader.Core.Models;
using Xunit;
using static RailNarrator.TestFixtures.SyntheticNarration;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// What was just read, found on the page, and the notes and highlights made on it (ADR-0014).
/// </summary>
public sealed class ReviewTargetTests
{
    private static readonly ExtractionIdentity Identity = new("deadbeefcafebabe", 1);

    // Page 0: two sentences on lines 0 and 1 (one passage), and a sentence starting on line 2 that
    // continues on page 1, line 0. Page 1 also holds an equation with no text behind it.
    private const string Line0 = "The estimator is consistent.";
    private const string Line1 = "Its variance is bounded.";
    private const string Line2 = "Under weak conditions the";
    private const string Over = "test has power one.";

    private static (ExtractionSnapshot Snapshot, NarrationPlan Plan) Fixture()
    {
        var page0 = Page(0, [new LineSpec(Line0, 100), new LineSpec(Line1, 120), new LineSpec(Line2, 700)]);
        var page1 = Page(1, [new LineSpec(Over, 80)]);
        var snapshot = new ExtractionSnapshot(Identity, CoordinateFrameId.DisplayedPagePoints(), ExtractionOptions.ModelFree, [page0, page1]);

        SourceAnchor AnchorOf(string id, PageExtraction page, string text)
        {
            int start = page.Text.IndexOf(text, StringComparison.Ordinal);
            var line = page.Lines.Single(l => l.Text.Start == start);
            return Anchor(id, page.PageIndex, line.Rect, new TextRange(start, text.Length));
        }

        var u0 = ProseUnit("u0", Line0, AnchorOf("a0", page0, Line0));
        var u1 = ProseUnit("u1", Line1, AnchorOf("a1", page0, Line1));
        var a2 = AnchorOf("a2", page0, Line2);
        var a3 = AnchorOf("a3", page1, Over);
        var u2 = new ReadingUnit("u2", ReadingUnitKind.Prose, [a2, a3], Line2 + " " + Over, null, "", "", [], 0);
        var equation = Anchor("eq", 1, new PageRect(200, 300, 400, 340), charRange: null, ExtractionProvenance.VisualMath);
        var u3 = MathsUnit("u3", "x^2", equation);

        NarrationSegment Segment(string id, params ReadingUnit[] units) => new(id, units.Select(u => u.Id).ToList(), units[0].Kind,
            string.Join(" ", units.Select(u => u.OriginalText)), "en-GB",
            [new SourceLink(new TextRange(0, 1), units.SelectMany(u => u.AnchorIds).ToList())],
            MathMode.Interpret, ScriptProvenance.SourceVerbatim, [], 1);

        var plan = new NarrationPlan(
            new NarrationPlanHeader("plan", Identity, "en-GB", MathMode.Interpret, NarrationVersions.MathPrompt, null, NarrationFilterSettings.Default),
            [u0, u1, u2, u3],
            [Segment("s0", u0, u1), Segment("s1", u2), Segment("s2", u3)]);
        return (snapshot, plan);
    }

    private static ReviewTarget Find(string segment, IReadOnlyList<string>? cue, bool whole = false)
    {
        var (snapshot, plan) = Fixture();
        return ReviewTarget.Find(plan, snapshot, new PageTextCache(snapshot), segment, cue, whole)
            ?? throw new Xunit.Sdk.XunitException("no target");
    }

    [Fact]
    public void TheSentenceACueWasReadingIsTheTarget()
    {
        var target = Find("s0", ["a1"]);

        Assert.Equal("u1", target.UnitId);
        Assert.Equal(Line1, target.QuotedText);
        var page = Assert.Single(target.Pages);
        var box = Assert.Single(page.Rects);
        // Line 1 is at y = 120; the box is over its glyphs, padded a point either side.
        Assert.Equal(120, box.Y);
        Assert.Equal(LeftMargin - 1, box.X);
        Assert.Equal([new LinkedRange(0, Line0.Length + 1, Line1.Length)], page.Ranges);
    }

    [Fact]
    public void TheWholePassageWhenAsked_AndWhenNoCueSaysWhichSentence()
    {
        foreach (var target in new[] { Find("s0", ["a1"], whole: true), Find("s0", cue: null) })
        {
            Assert.Null(target.UnitId);
            Assert.Equal(Line0 + " " + Line1, target.QuotedText);
            Assert.Equal([100f, 120f], Assert.Single(target.Pages).Rects.Select(r => r.Y));
        }
    }

    [Fact]
    public void ASentenceAcrossAPageBreakIsOnBothPages()
    {
        var target = Find("s1", ["a2"]);

        Assert.Equal([0, 1], target.Pages.Select(p => p.PageIndex));
        Assert.Equal(700, Assert.Single(target.Pages[0].Rects).Y);
        Assert.Equal(80, Assert.Single(target.Pages[1].Rects).Y);
        Assert.Equal(Line2 + " " + Over, target.QuotedText);
    }

    [Fact]
    public void AnEquationWithNoTextBehindItIsBoxedByItsOwnRectangle()
    {
        var target = Find("s2", ["eq"]);

        var page = Assert.Single(target.Pages);
        Assert.Equal(new HighlightRect(200, 300, 200, 40), Assert.Single(page.Rects));
        Assert.Empty(page.Ranges);
    }

    [Fact]
    public void AnAnchorFromAnotherExtractionIsBoxedByItsRectanglesNotByOffsetsIntoThisOne()
    {
        var (snapshot, plan) = Fixture();
        var older = snapshot with { Identity = new ExtractionIdentity("deadbeefcafebabe", 2) };

        var target = ReviewTarget.Find(plan, older, new PageTextCache(older), "s0", ["a0"], wholePassage: false)!;

        var page = Assert.Single(target.Pages);
        Assert.Empty(page.Ranges);
        Assert.Equal(99, Assert.Single(page.Rects).Y);   // the line's rectangle, one point above the glyphs
    }

    [Fact]
    public void AQuoteJoinsAWordHyphenatedAtALineEnd_AsThePlannerDoes()
    {
        const string text = "The vari- ance of a test.";
        var unit = new ReadingUnit("u", ReadingUnitKind.Prose, [], text, null, "", "", [], 0)
        {
            LineBreakOffsets = [text.IndexOf("ance", StringComparison.Ordinal) - 1],
        };
        Assert.Equal("The variance of a test.", ReviewTarget.QuoteOf(unit));

        // A hyphen and a space that are not at a recorded line break stay as printed.
        Assert.Equal("pre- and post-test", ReviewTarget.QuoteOf(unit with { OriginalText = "pre- and post-test", LineBreakOffsets = [] }));
    }

    [Fact]
    public void APassageNotInThePlanHasNoTarget()
    {
        var (snapshot, plan) = Fixture();
        Assert.Null(ReviewTarget.Find(plan, snapshot, new PageTextCache(snapshot), "nope", null, false));
    }

    // ---- the session ------------------------------------------------------------------------

    private static (ReviewSession Session, int Changes) Session(out ReviewDocument document)
    {
        document = new ReviewDocument("0123", "/papers/p.pdf");
        return (new ReviewSession(document, () => new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero)), 0);
    }

    [Fact]
    public void ANoteOnASentenceIsAHighlightWithTheNoteAndALinkToThePassage()
    {
        var (session, _) = Session(out var document);
        int changes = 0;
        document.Changed += () => changes++;

        var made = session.Highlight(Find("s0", ["a1"]), "  Is this bound tight?  ");

        Assert.Equal("Is this bound tight?", made.Contents);
        Assert.Equal(0.35f, made.Opacity);
        Assert.Same(made, Assert.Single(document.File.Pages[0]));
        Assert.Same(made, session.Selected);
        var link = document.Links[made.NativeId!];
        Assert.Equal(("s0", "u1", Line1), (link.SegmentId, link.UnitId, link.QuotedText));
        Assert.Null(link.GroupId);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ASentenceAcrossAPageBreakIsOneHighlightPerPage_KeptAndRemovedTogether()
    {
        var (session, _) = Session(out var document);

        var head = session.Highlight(Find("s1", ["a3"]), "Which conditions?");

        var second = Assert.Single(document.File.Pages[1]);
        Assert.Same(head, Assert.Single(document.File.Pages[0]));
        Assert.Null(second.Contents);
        Assert.Equal(head.NativeId, document.Links[second.NativeId!].GroupId);
        Assert.Same(head, session.Head(second));

        session.Delete(second);
        Assert.Empty(document.File.Pages[0]);
        Assert.Empty(document.File.Pages[1]);
        Assert.Empty(document.Links);
        Assert.Null(session.Selected);

        session.Undo();
        Assert.Single(document.File.Pages[0]);
        Assert.Single(document.File.Pages[1]);
        Assert.Equal(2, document.Links.Count);
    }

    [Fact]
    public void AnEditAndTheHighlightUndoAndRedoInOrder()
    {
        var (session, _) = Session(out var document);
        var made = session.Highlight(Find("s0", ["a0"]));
        Assert.Null(made.Contents);

        session.SetNote(made, "Define consistent.");
        Assert.Equal("Define consistent.", made.Contents);
        Assert.NotNull(made.ModifiedUtc);

        session.Undo();
        Assert.Null(made.Contents);
        Assert.Null(made.ModifiedUtc);
        session.Undo();
        Assert.Empty(document.File.Pages[0]);
        Assert.Empty(document.Links);
        Assert.False(session.CanUndo);

        session.Redo();
        session.Redo();
        Assert.Same(made, Assert.Single(document.File.Pages[0]));
        Assert.Equal("Define consistent.", made.Contents);
        Assert.Single(document.Links);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void WhatIsAlreadyMadeOnASentenceIsFound_NotTheSameWordsInAnotherScope()
    {
        var (session, _) = Session(out _);
        var sentence = Find("s0", ["a1"]);
        Assert.Null(session.MadeOn(sentence));

        var made = session.Highlight(sentence);
        Assert.Same(made, session.MadeOn(Find("s0", ["a1"])));
        Assert.Null(session.MadeOn(Find("s0", ["a0"])));               // the other sentence
        Assert.Null(session.MadeOn(Find("s0", ["a1"], whole: true)));  // the whole passage

        var across = session.Highlight(Find("s1", ["a2"]));
        Assert.Same(across, session.MadeOn(Find("s1", ["a3"])));       // the group's first, from either page

        session.Undo();
        Assert.Null(session.MadeOn(Find("s1", ["a2"])));
    }

    [Fact]
    public void ANewChangeClearsWhatCouldBeRedone_AndAnUnchangedNoteIsNoStep()
    {
        var (session, _) = Session(out _);
        var made = session.Highlight(Find("s0", ["a0"]), "one");
        session.SetNote(made, "one");
        session.SetNote(made, "  ");   // removes the note
        Assert.Null(made.Contents);
        session.Undo();
        Assert.Equal("one", made.Contents);

        session.Highlight(Find("s0", ["a1"]));
        Assert.False(session.CanRedo);
    }
}
