using RailNarrator.Domain;
using RailNarrator.Pdf.Annotations;
using RailNarrator.TestFixtures;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// The reader's annotations on disk and on the page (ADR-0014): kept in Core's format, never lost,
/// drawn from a copy, and highlighted from the cached extraction's own character boxes.
/// </summary>
public sealed class ReviewAnnotationTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rn-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private static ReviewDocument OneOfEachKind()
    {
        var file = new AnnotationFile();
        file.Pages[0] =
        [
            new HighlightAnnotation { NativeId = "h1", Color = "#00A000", Opacity = 0.35f, Contents = "Is this claim supported?",
                Rects = [new HighlightRect(72, 80, 200, 12), new HighlightRect(72, 96, 120, 12)], Author = "Reviewer",
                CreatedUtc = new DateTimeOffset(2026, 11, 24, 9, 30, 0, TimeSpan.Zero) },
            new UnderlineAnnotation { NativeId = "u1", Rects = [new HighlightRect(72, 120, 50, 12)] },
            new StrikeOutAnnotation { NativeId = "s1", Rects = [new HighlightRect(72, 140, 50, 12)] },
            new SquigglyAnnotation { NativeId = "q1", Rects = [new HighlightRect(72, 160, 50, 12)] },
            new TextNoteAnnotation { NativeId = "n1", X = 540, Y = 90, Text = "Cite the original." },
        ];
        file.Pages[2] =
        [
            new FreehandAnnotation { NativeId = "f1", StrokeWidth = 2, Points = [new PointF(10, 10), new PointF(20, 25), new PointF(40, 30)] },
            new RectAnnotation { NativeId = "r1", X = 100, Y = 200, W = 150, H = 80, StrokeWidth = 2 },
            new FreeTextAnnotation { NativeId = "t1", X = 300, Y = 400, W = 120, H = 40, FontSize = 12, Contents = "See figure 2" },
        ];
        var link = new ReviewLink("h1", "seg-0004", "unit-0012", 3,
            [new LinkedRange(0, 120, 64)], "The estimator is consistent under weak conditions.",
            new DateTimeOffset(2026, 11, 24, 9, 30, 0, TimeSpan.Zero));
        return new ReviewDocument(Hash, "/papers/garch.pdf", file, [link]);
    }

    [Fact]
    public async Task EveryKindAndItsLinkComeBackAsSaved()
    {
        var store = new ReviewAnnotationStore(_root);
        var document = OneOfEachKind();
        document.MarkChanged();
        await store.SaveAsync(document);

        var loaded = store.Load(Hash, "/papers/garch.pdf");

        Assert.Null(loaded.Problem);
        var pages = loaded.Document.File.Pages;
        Assert.Equal(
            ["HighlightAnnotation", "UnderlineAnnotation", "StrikeOutAnnotation", "SquigglyAnnotation", "TextNoteAnnotation"],
            pages[0].Select(a => a.GetType().Name));
        Assert.Equal(["FreehandAnnotation", "RectAnnotation", "FreeTextAnnotation"], pages[2].Select(a => a.GetType().Name));
        var highlight = Assert.IsType<HighlightAnnotation>(pages[0][0]);
        Assert.Equal("Is this claim supported?", highlight.Contents);
        Assert.Equal(new HighlightRect(72, 96, 120, 12), highlight.Rects[1]);
        Assert.Equal(0.35f, highlight.Opacity);
        Assert.Equal("Cite the original.", Assert.IsType<TextNoteAnnotation>(pages[0][4]).EffectiveContents);
        Assert.Equal(3, Assert.IsType<FreehandAnnotation>(pages[2][0]).Points.Count);

        var link = Assert.Single(loaded.Document.Links.Values);
        Assert.Equal(document.Links["h1"] with { Ranges = link.Ranges }, link);
        Assert.Equal([new LinkedRange(0, 120, 64)], link.Ranges);
        Assert.Equal("garch.pdf", loaded.Document.File.SourcePdf);
    }

    [Fact]
    public async Task CoreReadsWhatIsSavedAndWhatCoreWritesIsRead()
    {
        // RailReader2 exchanges annotations in this format, written by Core's own internal context.
        var store = new ReviewAnnotationStore(_root);
        var document = OneOfEachKind();
        document.MarkChanged();
        await store.SaveAsync(document);

        string ours = Path.Combine(store.DirectoryFor(Hash), "annotations.json");
        var readByCore = AnnotationService.ImportJson(ours);
        Assert.NotNull(readByCore);
        Assert.Equal(8, readByCore.Pages.Values.Sum(p => p.Count));
        Assert.Equal("See figure 2", readByCore.Pages[2][2].Contents);

        string theirs = Path.Combine(_root, "core.json");
        AnnotationService.ExportJson(document.File, theirs);
        Assert.Equal(File.ReadAllText(theirs), File.ReadAllText(ours));
        var readByUs = ReviewAnnotationJson.Deserialize(File.ReadAllText(theirs));
        Assert.NotNull(readByUs);
        Assert.Equal("h1", readByUs.Pages[0][0].NativeId);
    }

    [Fact]
    public async Task AFileThatCannotBeParsedIsSetAsideNotOverwritten()
    {
        var store = new ReviewAnnotationStore(_root);
        string dir = store.DirectoryFor(Hash);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "annotations.json"), "{ \"pages\": { \"0\": [ { \"type\": \"highl");

        var loaded = store.Load(Hash, null);

        Assert.NotNull(loaded.Problem);
        Assert.True(loaded.Document.IsEmpty);
        string aside = Assert.Single(Directory.GetFiles(dir, "annotations.unreadable-*.json"));
        Assert.StartsWith("{ \"pages\"", File.ReadAllText(aside));

        // Saving afterwards writes a new file and leaves the one set aside alone.
        loaded.Document.File.Pages[1] = [new TextNoteAnnotation { NativeId = "n", X = 1, Y = 1, Text = "new" }];
        loaded.Document.MarkChanged();
        await store.SaveAsync(loaded.Document);
        Assert.Single(store.Load(Hash, null).Document.File.Pages[1]);
        Assert.True(File.Exists(aside));
    }

    [Fact]
    public async Task AFileThatCannotBeReadIsNeverSavedOver()
    {
        var store = new ReviewAnnotationStore(_root);
        var document = OneOfEachKind();
        document.MarkChanged();
        await store.SaveAsync(document);
        string path = Path.Combine(store.DirectoryFor(Hash), "annotations.json");
        string before = File.ReadAllText(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        if (CanRead(path)) return;   // running as root: the mode does not stop a read

        var loaded = store.Load(Hash, null);
        Assert.NotNull(loaded.Problem);
        Assert.True(loaded.Document.File.Pages.Count == 0);

        loaded.Document.File.Pages[0] = [new TextNoteAnnotation { NativeId = "x" }];
        loaded.Document.MarkChanged();
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(loaded.Document));

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(before, File.ReadAllText(path));
    }

    private static bool CanRead(string path)
    {
        try { File.ReadAllBytes(path); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    [Fact]
    public async Task TheNewestSaveIsTheOneOnDiskAndNoTemporaryIsLeft()
    {
        var store = new ReviewAnnotationStore(_root);
        var document = new ReviewDocument(Hash, "/papers/a.pdf");
        var saves = new List<Task>();
        for (int i = 1; i <= 20; i++)
        {
            document.File.Pages[0] = [new TextNoteAnnotation { NativeId = "n", Text = $"draft {i}" }];
            document.MarkChanged();
            saves.Add(store.SaveAsync(document));
        }
        await Task.WhenAll(saves);
        await store.FlushAsync();

        var note = Assert.IsType<TextNoteAnnotation>(Assert.Single(store.Load(Hash, null).Document.File.Pages[0]));
        Assert.Equal("draft 20", note.Text);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(Hash, store.LastHashFor("/papers/a.pdf"));
    }

    [Fact]
    public void NothingKeptLoadsAsAnEmptyDocumentWithoutAProblem()
    {
        var loaded = new ReviewAnnotationStore(_root).Load(Hash, "/papers/none.pdf");
        Assert.Null(loaded.Problem);
        Assert.True(loaded.Document.IsEmpty);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ASnapshotIsACopyTheDocumentCannotChange()
    {
        var document = OneOfEachKind();
        var layer = new AnnotationLayer(document);
        var live = (TextNoteAnnotation)document.File.Pages[0][4];
        live.IsExpanded = true;
        var before = layer.ForPage(0, selected: live);

        live.X = 10;
        live.Text = "changed";
        document.MarkChanged();

        var copy = before.Annotations.OfType<TextNoteAnnotation>().Single();
        Assert.Equal(540, copy.X);
        Assert.Equal("Cite the original.", copy.Text);
        Assert.True(copy.IsExpanded);
        Assert.NotSame(live, copy);
        // Markup is drawn first, notes last (Core's z-order).
        Assert.IsType<TextNoteAnnotation>(before.Annotations[^1]);

        var after = layer.ForPage(0, selected: live);
        Assert.NotSame(before, after);
        Assert.Equal(10, after.Annotations.OfType<TextNoteAnnotation>().Single().X);
        Assert.Same(after, layer.ForPage(0, selected: live));
    }

    [Fact]
    public void AHighlightIsDrawnWhereItsPagePointsAreAtTheZoomGiven()
    {
        var document = new ReviewDocument(Hash, null);
        document.File.Pages[0] =
            [new HighlightAnnotation { Color = "#0066FF", Opacity = 1f, Rects = [new HighlightRect(10, 10, 20, 10)] }];
        var snapshot = new AnnotationLayer(document).ForPage(0);

        using var bitmap = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            snapshot.Draw(canvas, new PageTransform(Zoom: 2, OffsetX: 50, OffsetY: 50), darkBackdrop: false, expandAllNotes: false);
        }

        // Page (10..30, 10..20) at 2x from (50, 50) is screen (70..110, 70..90).
        var inside = bitmap.GetPixel(90, 80);
        Assert.True(inside.Red < 100 && inside.Blue > 200, $"inside the highlight: {inside}");
        Assert.Equal(SKColors.White, bitmap.GetPixel(60, 80));
        Assert.Equal(SKColors.White, bitmap.GetPixel(90, 100));
    }

    [Fact]
    public async Task ARangeOfTwoLinesGivesOneBoxPerLineOverItsGlyphs()
    {
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("review"),
        [
            "The quick brown fox jumps over the lazy dog.",
            "Pack my box with five dozen liquor jugs.",
        ]);
        using var source = new RailReaderExtractionSource();
        source.WithBackend(ExtractionBackend.ModelFree());
        var snapshot = await source.ExtractAsync(new ExtractionRequest(path, null, null,
            new ExtractionOptions("text-layout", OcrPolicy.Off, 1024, DeskewLines: false, ExtractorVersions.Current),
            NarrationFilterSettings.Default));
        var page = snapshot.Pages[0]!;
        var index = PageTextIndex.Build(page);
        Assert.Equal(page.Text.Length, index.Text.CharBoxes.Count);

        int start = page.Text.IndexOf("brown", StringComparison.Ordinal);
        int end = page.Text.IndexOf("liquor", StringComparison.Ordinal) + "liquor".Length;
        var rects = index.Rects(new TextRange(start, end - start));

        Assert.Equal(2, rects.Count);
        var lines = page.Lines.OrderBy(l => l.Rect.Top).ToList();
        for (int i = 0; i < 2; i++)
        {
            Assert.InRange(rects[i].Y + rects[i].H / 2, lines[i].Rect.Top, lines[i].Rect.Bottom);
            Assert.True(rects[i].W > 20, $"line {i} box is {rects[i].W} wide");
        }
        // The first line's box starts at "brown", not at the line's start; the second ends at "liquor".
        var brown = page.Chars.First(c => c.Index == start).Rect;
        Assert.InRange(rects[0].X, brown.Left - 1.5f, brown.Left - 0.5f);
        Assert.True(rects[1].X + rects[1].W < lines[1].Rect.Right - 10);

        Assert.Empty(index.Rects(new TextRange(page.Text.Length, 5)));
    }

    [Fact]
    public async Task ADocumentOpenedAgainInTheSameSessionIsSaved()
    {
        var store = new ReviewAnnotationStore(_root);
        var first = store.Load(Hash, null).Document;
        for (int i = 0; i < 40; i++) first.MarkViewChanged();
        first.File.Pages[0] = [new HighlightAnnotation { NativeId = "a", Rects = [new HighlightRect(1, 1, 1, 1)] }];
        first.MarkChanged();
        await store.SaveAsync(first);

        // Opened again: its versions start at 0, below the first instance's.
        var second = store.Load(Hash, null).Document;
        second.File.Pages[0].Add(new HighlightAnnotation { NativeId = "b", Rects = [new HighlightRect(2, 2, 1, 1)] });
        second.MarkChanged();
        await store.SaveAsync(second);

        Assert.Equal(["a", "b"], store.Load(Hash, null).Document.File.Pages[0].Select(a => a.NativeId));
    }

    [Fact]
    public async Task NotesNotPlacedAreKeptWhenTheRecordOfTheirVersionIsLost_AndAPeekMovesNothing()
    {
        var store = new ReviewAnnotationStore(_root);
        var unplaced = new AnnotationFile();
        unplaced.Pages[2] = [new TextNoteAnnotation { NativeId = "n", X = 1, Y = 1, Text = "Waiting." }];
        var document = new ReviewDocument(Hash, null, unplaced: unplaced);   // no Earlier: its earlier.json was unreadable
        document.MarkChanged();
        await store.SaveAsync(document);
        Assert.True(store.Load(Hash, null).Document.HasUnplaced);

        string annotations = Path.Combine(store.DirectoryFor(Hash), "annotations.json");
        await File.WriteAllTextAsync(annotations, "{ not json");
        var peeked = store.Peek(Hash);
        Assert.NotNull(peeked.Problem);
        Assert.True(File.Exists(annotations));
        Assert.Empty(Directory.GetFiles(store.DirectoryFor(Hash), "*unreadable*"));
    }
}
