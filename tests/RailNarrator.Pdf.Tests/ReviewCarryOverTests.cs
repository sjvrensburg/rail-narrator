using RailNarrator.Domain;
using RailNarrator.Pdf.Annotations;
using RailReader.Core.Models;
using Xunit;
using static RailNarrator.TestFixtures.SyntheticNarration;

namespace RailNarrator.Pdf.Tests;

/// <summary>Notes on an earlier version of a PDF brought to the revised one by their quoted text (ADR-0014).</summary>
public sealed class ReviewCarryOverTests : IDisposable
{
    private const string First = "The estimator is consistent.";
    private const string Second = "Its variance is bounded.";
    private const string Third = "We drop the outliers.";
    private const string EarlierHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RevisedHash = "2222222222222222222222222222222222222222222222222222222222222222";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rn-carry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>A document whose pages are lists of lines at 100, 120, 140…, each line one sentence and one passage.</summary>
    private static (ExtractionSnapshot Snapshot, NarrationPlan Plan) Version(params string[][] pages)
    {
        var extracted = pages.Select((lines, p) => Page(p, lines.Select((t, i) => new LineSpec(t, 100 + 20 * i)).ToList())).ToList();
        var units = new List<ReadingUnit>();
        foreach (var page in extracted)
            for (int i = 0; i < page.Lines.Count; i++)
            {
                var line = page.Lines[i];
                string text = page.Text.Substring(line.Text.Start, line.Text.Length);
                units.Add(ProseUnit($"u{page.PageIndex}-{i}", text, Anchor($"a{page.PageIndex}-{i}", page.PageIndex, line.Rect, line.Text)));
            }
        var snapshot = new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree, extracted);
        return (snapshot, Plan(units));
    }

    private static string SegmentOf(NarrationPlan plan, string unitId) => plan.Segments.Single(s => s.ReadingUnitIds.Contains(unitId)).Id;

    private static ReviewTarget Sentence(ExtractionSnapshot snapshot, NarrationPlan plan, string unitId)
        => ReviewTarget.Find(plan, snapshot, new PageTextCache(snapshot), SegmentOf(plan, unitId), [plan.Unit(unitId)!.AnchorIds[0]], false)!;

    private static ReviewTarget Words(ExtractionSnapshot snapshot, NarrationPlan plan, int page, string words)
    {
        var index = PageTextIndex.Build(snapshot.Page(page)!);
        int start = index.Text.Text.IndexOf(words, StringComparison.Ordinal);
        return ReviewTarget.ForRange(plan, snapshot, index, new TextRange(start, words.Length))!;
    }

    [Fact]
    public void NotesFollowTheirWordsToWhereTheyAreNow_AndTheRestWaitUnplaced()
    {
        var (v1, plan1) = Version([First, Second, Third]);
        var earlier = new ReviewDocument(EarlierHash, "/papers/paper.pdf");
        var before = new ReviewSession(earlier) { Author = "Reviewer" };
        before.Highlight(Sentence(v1, plan1, "u0-1"), "Bounded by what?");
        before.Markup(Words(v1, plan1, 0, "estimator"), AnnotationTool.Underline, null, "#00A000", 1f);
        before.Highlight(Sentence(v1, plan1, "u0-2"), "Why drop them?");
        before.AddNote(0, 540, 100, "Cite the original.");

        // The revision adds a sentence at the top, moves the second to a new page and cuts the third.
        var (v2, plan2) = Version(["A new opening sentence.", First], [Second]);
        var rectsBefore = earlier.File.Pages[0].OfType<TextMarkupAnnotation>().SelectMany(a => a.Rects).ToList();
        var carried = ReviewCarryOver.Match(earlier, plan2, v2);

        Assert.Equal((2, 2), (carried.NotesPlaced, carried.NotesUnplaced));
        var highlight = Assert.IsType<HighlightAnnotation>(carried.Placed.Single(p => p.Annotation.Contents == "Bounded by what?").Annotation);
        Assert.Equal(1, carried.Placed.Single(p => ReferenceEquals(p.Annotation, highlight)).Page);
        Assert.Equal([100f], highlight.Rects.Select(r => r.Y));
        var link = carried.PlacedLinks.Single(l => l.NativeId == highlight.NativeId);
        Assert.Equal((SegmentOf(plan2, "u1-0"), "u1-0", Second), (link.SegmentId, link.UnitId, link.QuotedText));
        Assert.False(link.ByHand);

        var underline = carried.Placed.Single(p => p.Annotation is UnderlineAnnotation);
        Assert.Equal((0, 120f), (underline.Page, ((UnderlineAnnotation)underline.Annotation).Rects.Single().Y));
        Assert.True(carried.PlacedLinks.Single(l => l.NativeId == underline.Annotation.NativeId).ByHand);

        // The cut sentence's note and the sticky note, which is on no text, are kept as they were.
        var waiting = carried.Unplaced.Pages[0];
        Assert.Equal(["Why drop them?", "Cite the original."], waiting.Select(a => a is TextNoteAnnotation n ? n.Text : a.Contents));
        Assert.Single(carried.UnplacedLinks);
        // Copies: the earlier version is untouched.
        Assert.All(carried.Placed, p => Assert.DoesNotContain(earlier.File.Pages[0], a => ReferenceEquals(a, p.Annotation)));
        Assert.Equal(rectsBefore, earlier.File.Pages[0].OfType<TextMarkupAnnotation>().SelectMany(a => a.Rects));
    }

    [Fact]
    public void AWordHyphenatedAtALineEndAndSpacingDifferencesStillMatch()
    {
        var (v1, plan1) = Version([First]);
        var earlier = new ReviewDocument(EarlierHash, null);
        new ReviewSession(earlier).Highlight(Sentence(v1, plan1, "u0-0"), "Under which conditions?");

        // PDFium's line-end hyphen marker, and the sentence now over two lines.
        var (v2, plan2) = Version(["The estimator is consis\u0002", "tent ."]);
        var carried = ReviewCarryOver.Match(earlier, plan2, v2);

        var placed = Assert.Single(carried.Placed);
        Assert.Equal([100f, 120f], ((HighlightAnnotation)placed.Annotation).Rects.Select(r => r.Y));
        Assert.Equal(0, carried.NotesUnplaced);
    }

    [Fact]
    public void ASentenceThatCrossedAPageBreakAndNoLongerDoesIsOneAnnotationWithItsNote()
    {
        var p0 = Page(0, [new LineSpec("The estimator is", 700)]);
        var p1 = Page(1, [new LineSpec("consistent.", 100)]);
        var v1 = new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree, [p0, p1]);
        var unit = new ReadingUnit("u0", ReadingUnitKind.Prose,
            [Anchor("a0", 0, p0.Lines[0].Rect, p0.Lines[0].Text), Anchor("a1", 1, p1.Lines[0].Rect, p1.Lines[0].Text)],
            "The estimator is consistent.", null, "", "", [], 0);
        var plan1 = Plan([unit]);
        var earlier = new ReviewDocument(EarlierHash, null);
        new ReviewSession(earlier).Highlight(ReviewTarget.Find(plan1, v1, new PageTextCache(v1), "s00000", ["a0"], false)!, "Proof?");
        Assert.Equal(2, earlier.Links.Count);

        var (v2, plan2) = Version([First]);
        var carried = ReviewCarryOver.Match(earlier, plan2, v2);

        var placed = Assert.Single(carried.Placed);
        Assert.Equal("Proof?", placed.Annotation.Contents);
        Assert.Null(Assert.Single(carried.PlacedLinks).GroupId);
        Assert.Equal(1, carried.NotesPlaced);
    }

    [Fact]
    public void AWordIsFoundAsAWordBeforeInsideALongerOne()
    {
        var (v1, plan1) = Version([First, Second]);
        var earlier = new ReviewDocument(EarlierHash, null);
        new ReviewSession(earlier).Markup(Words(v1, plan1, 0, "Its"), AnnotationTool.Highlight, "Whose?", "#FFFF00", 0.35f);

        // "Itself" is where "Its" was (the second line); the word is on the first.
        var (v2, plan2) = Version(["The estimator is consistent; its variance is bounded.", "Itself a claim."]);
        var placed = Assert.Single(ReviewCarryOver.Match(earlier, plan2, v2).Placed);

        var rect = ((HighlightAnnotation)placed.Annotation).Rects.Single();
        Assert.Equal(100f, rect.Y);
        int column = "The estimator is consistent; ".Length;
        Assert.True(rect.X > LeftMargin + (column - 1) * 5.5f, $"placed at x = {rect.X}");
    }

    [Fact]
    public void BringingNotesOverIsOneStep_AndAWaitingNoteIsPlacedAsBeforeOrDiscarded()
    {
        var (v1, plan1) = Version([First, Third]);
        var earlier = new ReviewDocument(EarlierHash, null);
        var before = new ReviewSession(earlier);
        before.Highlight(Sentence(v1, plan1, "u0-0"), "Under which conditions?");
        before.Highlight(Sentence(v1, plan1, "u0-1"), "Why drop them?");
        before.AddNote(0, 540, 100, "Cite the original.");
        var (v2, plan2) = Version([First]);

        var document = new ReviewDocument(RevisedHash, null) { Earlier = new EarlierVersion(EarlierHash) };
        var session = new ReviewSession(document);
        session.CarryOver(ReviewCarryOver.Match(earlier, plan2, v2), EarlierHash);
        Assert.Single(document.File.Pages[0]);
        Assert.Equal(2, document.Unplaced.Pages[0].Count);
        Assert.NotNull(document.Earlier!.CarriedUtc);
        Assert.Equal(3, ReviewCarryOver.CountNotes(document));

        session.Undo();
        Assert.True(document.IsEmpty);
        Assert.False(document.HasUnplaced);
        Assert.Empty(document.UnplacedLinks);
        Assert.Null(document.Earlier!.CarriedUtc);
        session.Redo();
        Assert.True(document.HasUnplaced);

        var cut = document.Unplaced.Pages[0].Single(a => a.Contents == "Why drop them?");
        Assert.True(session.PlaceAsBefore(cut, pageCount: 1));
        Assert.Contains(cut, document.File.Pages[0]);
        Assert.DoesNotContain(cut, document.Unplaced.Pages[0]);
        var link = session.LinkOf(cut)!;
        Assert.Equal((null, true, "We drop the outliers."), (link.SegmentId, link.ByHand, link.QuotedText));
        Assert.Empty(document.UnplacedLinks);
        session.Undo();
        Assert.Contains(cut, document.Unplaced.Pages[0]);
        Assert.Null(session.LinkOf(cut));
        Assert.NotNull(session.UnplacedLinkOf(cut));

        var sticky = document.Unplaced.Pages[0].OfType<TextNoteAnnotation>().Single();
        session.Discard(sticky);
        Assert.DoesNotContain(sticky, document.Unplaced.Pages[0]);
        session.Undo();
        Assert.Contains(sticky, document.Unplaced.Pages[0]);
    }

    [Fact]
    public async Task ARevisedFileAtTheSamePathIsOfferedTheEarlierNotes_AndWhatWasBroughtOverIsKept()
    {
        var store = new ReviewAnnotationStore(_root);
        string path = Path.Combine(_root, "paper.pdf");
        var (v1, plan1) = Version([First, Third]);
        var earlier = store.Load(EarlierHash, path).Document;
        var before = new ReviewSession(earlier);
        before.Highlight(Sentence(v1, plan1, "u0-0"), "Under which conditions?");
        before.Highlight(Sentence(v1, plan1, "u0-1"), "Why drop them?");
        await store.SaveAsync(earlier);

        var revised = store.Load(RevisedHash, path).Document;
        Assert.Equal(EarlierHash, revised.Earlier?.DocumentContentHash);
        Assert.Null(revised.Earlier!.CarriedUtc);
        // Another file has no earlier version, and the earlier version itself has none.
        Assert.Null(store.Load(RevisedHash, Path.Combine(_root, "other.pdf")).Document.Earlier);
        Assert.Null(store.Load(EarlierHash, path).Document.Earlier);

        var (v2, plan2) = Version([First]);
        new ReviewSession(revised).CarryOver(ReviewCarryOver.Match(store.Load(EarlierHash, null).Document, plan2, v2), EarlierHash);
        await store.SaveAsync(revised);

        var again = store.Load(RevisedHash, path).Document;
        Assert.NotNull(again.Earlier!.CarriedUtc);
        Assert.Equal("Under which conditions?", again.File.Pages[0].Single().Contents);
        var waiting = again.Unplaced.Pages[0].Single();
        Assert.Equal("Why drop them?", waiting.Contents);
        Assert.Equal("We drop the outliers.", again.UnplacedLinks[waiting.NativeId!].QuotedText);
        // The earlier version's own notes are as they were.
        Assert.Equal(2, store.Load(EarlierHash, null).Document.File.Pages[0].Count);
    }

    [Fact]
    public async Task UndoingTheOnlyCarryOverOfAnotherFileLeavesNoRecordOfIt()
    {
        var store = new ReviewAnnotationStore(_root);
        var (v1, plan1) = Version([First, Third]);
        var earlier = store.Load(EarlierHash, null).Document;
        new ReviewSession(earlier).Highlight(Sentence(v1, plan1, "u0-1"), "Why drop them?");
        await store.SaveAsync(earlier);

        var revised = store.Load(RevisedHash, null).Document;
        var session = new ReviewSession(revised);
        var (v2, plan2) = Version([First]);
        session.CarryOver(ReviewCarryOver.Match(store.Load(EarlierHash, null).Document, plan2, v2), EarlierHash);
        await store.SaveAsync(revised);
        Assert.True(File.Exists(Path.Combine(store.DirectoryFor(RevisedHash), "unplaced.json")));

        session.Undo();
        await store.SaveAsync(revised);
        Assert.False(File.Exists(Path.Combine(store.DirectoryFor(RevisedHash), "earlier.json")));
        Assert.False(File.Exists(Path.Combine(store.DirectoryFor(RevisedHash), "unplaced.json")));
        Assert.Null(store.Load(RevisedHash, null).Document.Earlier);
    }

    [Fact]
    public async Task ANoteOnNoTextIsPlacedAsBeforeOnThePageItsPageBecame_WhenNotesFoundThereAgree()
    {
        // A cover page is dropped: the earlier page 1 is page 0 now.
        var (v1, plan1) = Version(["Downloaded from the publisher."], [First, Second]);
        var earlier = new ReviewDocument(EarlierHash, null);
        var before = new ReviewSession(earlier);
        before.Highlight(Sentence(v1, plan1, "u1-0"), "Under which conditions?");
        before.AddStroke(1, [new PointF(500, 90), new PointF(520, 130)], "#FF0000", 2f);
        before.AddNote(0, 540, 100, "Cover page?");
        var (v2, plan2) = Version([First, Second]);

        var carried = ReviewCarryOver.Match(earlier, plan2, v2);
        Assert.Equal(new Dictionary<int, int> { [1] = 0 }, carried.PageMap);

        var store = new ReviewAnnotationStore(_root);
        var document = new ReviewDocument(RevisedHash, null);
        var session = new ReviewSession(document);
        session.CarryOver(carried, EarlierHash);
        // Kept by the page it goes on now, and labelled with the page it was on.
        var stroke = document.Unplaced.Pages[0].OfType<FreehandAnnotation>().Single();
        Assert.Equal(1, session.EarlierPageOf(stroke));
        // No note on the cover page was found, so its page number is kept.
        var cover = document.Unplaced.Pages[0].OfType<TextNoteAnnotation>().Single();
        Assert.Equal(0, session.EarlierPageOf(cover));
        Assert.True(session.PlaceAsBefore(stroke, pageCount: 1));
        Assert.Contains(stroke, document.File.Pages[0]);

        await store.SaveAsync(document);
        var again = store.Load(RevisedHash, null).Document;
        var kept = again.Unplaced.Pages[0].Single();
        Assert.Equal(0, again.UnplacedEarlierPages[kept.NativeId!]);
    }

    [Fact]
    public void ASentenceAcrossAPageBreakIsFoundPastThePageNumberAndRunningHeadBetweenItsHalves()
    {
        var p0 = Page(0, [new LineSpec("The estimator is", 700), new LineSpec("3", 760)]);
        var p1 = Page(1, [new LineSpec("Journal of Econometrics", 40), new LineSpec("consistent.", 100)]);
        var v = new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree, [p0, p1]);
        var unit = new ReadingUnit("u0", ReadingUnitKind.Prose,
            [Anchor("a0", 0, p0.Lines[0].Rect, p0.Lines[0].Text), Anchor("a1", 1, p1.Lines[1].Rect, p1.Lines[1].Text)],
            "The estimator is consistent.", null, "", "", [], 0);
        var plan = Plan([unit]);
        var earlier = new ReviewDocument(EarlierHash, null);
        new ReviewSession(earlier).Highlight(ReviewTarget.Find(plan, v, new PageTextCache(v), "s00000", ["a0"], false)!, "Proof?");

        var carried = ReviewCarryOver.Match(earlier, plan, v);

        Assert.Equal((1, 0), (carried.NotesPlaced, carried.NotesUnplaced));
        Assert.Equal([0, 1], carried.Placed.Select(p => p.Page));
        Assert.Equal("Proof?", carried.Placed[0].Annotation.Contents);
    }

    [Fact]
    public void NotesBroughtTwiceAreCopiesWithIdsOfTheirOwn_AndUndoingOneLeavesTheOther()
    {
        var (v1, plan1) = Version([First, Third]);
        var earlier = new ReviewDocument(EarlierHash, null);
        var before = new ReviewSession(earlier);
        before.Highlight(Sentence(v1, plan1, "u0-0"), "Under which conditions?");
        before.AddNote(0, 540, 100, "Cite the original.");
        var (v2, plan2) = Version([First]);

        var document = new ReviewDocument(RevisedHash, null);
        var session = new ReviewSession(document);
        session.CarryOver(ReviewCarryOver.Match(earlier, plan2, v2), EarlierHash);
        session.CarryOver(ReviewCarryOver.Match(earlier, plan2, v2), "3333");

        var all = document.File.Pages.Values.SelectMany(p => p).Concat(document.Unplaced.Pages.Values.SelectMany(p => p)).ToList();
        Assert.Equal(4, all.Count);
        Assert.Equal(4, all.Select(a => a.NativeId).Distinct().Count());
        Assert.DoesNotContain(all, a => earlier.File.Pages[0].Any(e => e.NativeId == a.NativeId));
        Assert.Equal(2, document.Links.Count);

        session.Undo();
        var first = Assert.Single(document.File.Pages[0]);
        Assert.NotNull(session.LinkOf(first));
        Assert.Single(document.Unplaced.Pages[0]);
    }

    [Fact]
    public void PlacingANoteAsBeforeTakesItsRepliesAlong_AndRefusesAPageThisVersionLacks()
    {
        var earlier = new ReviewDocument(EarlierHash, null);
        var note = new TextNoteAnnotation { NativeId = "n1", X = 540, Y = 100, Text = "Cite the original." };
        var reply = new TextNoteAnnotation { NativeId = "r1", X = 540, Y = 100, Text = "Done.", InReplyTo = "n1" };
        earlier.File.Pages[3] = [note, reply];
        var (v2, plan2) = Version([First]);

        var document = new ReviewDocument(RevisedHash, null);
        var session = new ReviewSession(document);
        session.CarryOver(ReviewCarryOver.Match(earlier, plan2, v2), EarlierHash);
        var head = document.Unplaced.Pages[3].Single(a => a.InReplyTo is null);
        var answer = document.Unplaced.Pages[3].Single(a => a.InReplyTo is not null);
        Assert.Equal(head.NativeId, answer.InReplyTo);

        Assert.False(session.PlaceAsBefore(head, pageCount: 1));
        Assert.Equal(2, document.Unplaced.Pages[3].Count);
        Assert.Equal([3], session.PlacesAsBefore(head));

        Assert.True(session.PlaceAsBefore(head, pageCount: 4));
        Assert.Equal([head, answer], document.File.Pages[3]);
        Assert.Empty(document.Unplaced.Pages[3]);
        session.Undo();
        Assert.Equal([head, answer], document.Unplaced.Pages[3]);
    }
}
