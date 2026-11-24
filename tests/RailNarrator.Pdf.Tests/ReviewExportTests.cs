using System.Security.Cryptography;
using RailNarrator.Pdf.Annotations;
using RailNarrator.TestFixtures;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>The reader's notes leaving the application (ADR-0014): an annotated copy, Markdown, RailReader2's JSON.</summary>
public sealed class ReviewExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rn-export-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Pdf() => SyntheticPdf.WriteSingleColumn(Path.Combine(_dir, "paper.pdf"),
        ["The estimator is consistent under weak conditions.", "Its variance is bounded."], pageCount: 2);

    private static ReviewDocument OneOfEach()
    {
        var document = new ReviewDocument("0123", null);
        var session = new ReviewSession(document) { Author = "A Reviewer" };
        var target = new ReviewTarget("s0", "u0",
            [new ReviewTargetPage(0, [new HighlightRect(71, 86, 200, 12)], [])], "The estimator is consistent", 1);
        session.Highlight(target, "Under which conditions?");
        session.AddNote(0, 500, 100, "Cite the original.");
        session.AddText(1, 72, 600, 180, 40, "Fig. 2 lacks units", "#000000");
        session.AddRectangle(1, 60, 80, 200, 60, "#0066FF", 2);
        session.AddStroke(1, [new PointF(100, 300), new PointF(140, 320), new PointF(180, 300)], "#FF0000", 2);
        return document;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void TheCopyCarriesEveryAnnotationWithItsIdAndWords_AndThePdfIsUntouched()
    {
        string pdf = Pdf();
        string before = Hash(pdf);
        var document = OneOfEach();
        string copy = Path.Combine(_dir, "paper (annotated).pdf");

        var result = AnnotatedPdfExport.Write(pdf, document, copy);

        Assert.Equal(5, result.Written);
        Assert.Null(result.Warning);
        Assert.Equal(before, Hash(pdf));
        var read = AnnotatedPdfExport.ReadExisting(copy);
        var all = read.Pages.Values.SelectMany(p => p).ToList();
        Assert.Equal(5, all.Count);
        Assert.All(all, a => Assert.Equal(AnnotationSource.InPdf, a.Source));
        var ours = document.File.Pages.Values.SelectMany(p => p).ToDictionary(a => a.NativeId!);
        Assert.All(all, a => Assert.True(ours.ContainsKey(a.NativeId!), $"{a.GetType().Name} {a.NativeId} kept its /NM"));
        Assert.Equal("Under which conditions?", all.OfType<HighlightAnnotation>().Single().Contents);
        Assert.Equal("Cite the original.", all.OfType<TextNoteAnnotation>().Single().EffectiveContents);
        Assert.Equal("Fig. 2 lacks units", all.OfType<FreeTextAnnotation>().Single().Contents);
        Assert.Equal("A Reviewer", all.OfType<HighlightAnnotation>().Single().Author);
        Assert.Equal([0, 1], read.Pages.Where(p => p.Value.Count > 0).Select(p => p.Key).OrderBy(p => p));

        // A second export starts again from the original: nothing doubled.
        AnnotatedPdfExport.Write(pdf, document, copy);
        Assert.Equal(5, AnnotatedPdfExport.ReadExisting(copy).Pages.Values.Sum(p => p.Count));
    }

    [Fact]
    public void AnnotationsAlreadyInThePdfAreKeptInTheCopy_AndTheCopyNeverReplacesThePdf()
    {
        string pdf = Pdf();
        string first = Path.Combine(_dir, "first.pdf");
        AnnotatedPdfExport.Write(pdf, OneOfEach(), first);   // a PDF that already carries five annotations

        var mine = new ReviewDocument("4567", null);
        new ReviewSession(mine).AddNote(0, 300, 300, "Mine");
        string second = Path.Combine(_dir, "second.pdf");
        AnnotatedPdfExport.Write(first, mine, second);
        Assert.Equal(6, AnnotatedPdfExport.ReadExisting(second).Pages.Values.Sum(p => p.Count));

        Assert.Throws<IOException>(() => AnnotatedPdfExport.Write(pdf, mine, pdf));
        Assert.Throws<IOException>(() => AnnotatedPdfExport.Write(pdf, mine, Path.Combine(_dir, ".", "paper.pdf")));
    }

    [Fact]
    public void RailReader2sJsonIsImportedOnce_AsOneUndoableStep()
    {
        string json = Path.Combine(_dir, "exchange.json");
        AnnotationService.ExportJson(OneOfEach().File, json);

        var document = new ReviewDocument("89ab", null);
        var session = new ReviewSession(document);
        Assert.Equal((5, 0), session.Import(AnnotationService.ImportJson(json)!));
        Assert.Equal((0, 5), session.Import(AnnotationService.ImportJson(json)!));
        Assert.Equal(5, document.File.Pages.Values.Sum(p => p.Count));
        Assert.All(document.File.Pages.Values.SelectMany(p => p), a => Assert.Equal(AnnotationSource.RailReader, a.Source));
        Assert.Empty(document.Links);   // tied to no passage here

        session.Undo();   // the second import added nothing and is no step; this undoes the first
        Assert.Equal(0, document.File.Pages.Values.Sum(p => p.Count));
    }

    [Fact]
    public void ThePdfsOwnAnnotationsAreShownButNeitherSavedNorEdited()
    {
        string pdf = Pdf();
        string carried = Path.Combine(_dir, "carried.pdf");
        AnnotatedPdfExport.Write(pdf, OneOfEach(), carried);

        var document = new ReviewDocument("cdef", carried);
        document.SetPdfAnnotations(AnnotatedPdfExport.ReadExisting(carried));
        var inPdf = document.PdfAnnotations(1);
        Assert.Equal(3, inPdf.Count);
        Assert.True(document.IsFromPdf(inPdf[0]));
        Assert.True(document.IsEmpty);   // nothing of the reader's to save
        Assert.Equal(5, new AnnotationLayer(document).ForPage(1).Count + new AnnotationLayer(document).ForPage(0).Count);
        Assert.DoesNotContain("text_note", ReviewAnnotationJson.Serialize(document.File));
    }

    [Fact]
    public void MarkdownListsNotesByPageWithTheirQuotes_ThenThoseAlreadyInThePdf()
    {
        var notes = new List<ExportedNote>
        {
            new(1, "Highlight", "Error", "The vari*ance is [bounded]", "Which bound?", "A Reviewer", false),
            new(1, "Sticky note", "Note", "", "Check table 3.", null, false),
            new(4, "Box", "Question", "", null, null, false),
            new(2, "Highlight", null, "# not a heading", "Agreed.", "Co-author", true),
        };

        string md = ReviewMarkdown.Build("paper.pdf", notes, new DateOnly(2026, 11, 24));

        Assert.Equal("""
            # Review notes: paper.pdf

            24 November 2026 · 3 annotations, and 1 already in the PDF

            ## Page 2

            **Highlight · Error** (A Reviewer)

            > The vari\*ance is \[bounded\]

            Which bound?

            **Sticky note · Note**

            Check table 3.

            ## Page 5

            **Box · Question**

            ## Already in the PDF

            **Highlight · page 3** (Co-author)

            > \# not a heading

            Agreed.

            """.Replace("\r\n", "\n"), md);
    }

    [Fact]
    public void NotesNotPlacedFromAnEarlierVersionAreListedApart_WithTheirPageThere()
    {
        var notes = new List<ExportedNote>
        {
            new(0, "Highlight", null, "The estimator is consistent.", "Under which conditions?", null, false),
            new(3, "Highlight", null, "We drop the outliers.", "Why drop them?", null, false) { Unplaced = true },
        };

        string md = ReviewMarkdown.Build("paper.pdf", notes, new DateOnly(2026, 11, 24));

        Assert.Equal("""
            # Review notes: paper.pdf

            24 November 2026 · 1 annotation, and 1 from an earlier version not placed in this one

            ## Page 1

            **Highlight**

            > The estimator is consistent.

            Under which conditions?

            ## From an earlier version, not found in this one

            **Highlight · earlier page 4**

            > We drop the outliers.

            Why drop them?

            """.Replace("\r\n", "\n"), md);
    }
}
