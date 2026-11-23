using RailNarrator.Domain;
using RailNarrator.Pdf;
using RailNarrator.TestFixtures;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// A page with little text is recognised only when it has ink that the text does not account for.
/// </summary>
/// <remarks>
/// Recognition here is a stand-in that counts its calls: the question is whether it is asked, not
/// what a real model reads, so these need no OCR model.
/// </remarks>
public sealed class SparsePageOcrTests
{
    private static ExtractionOptions Options { get; } = new(
        "text-layout", OcrPolicy.Full, 1024, DeskewLines: false, ExtractorVersions.Current);

    private const string Recognised = "Words printed on the scanned page that its stray text layer does not hold.";

    private sealed class CountingOcr : IOcrService
    {
        public int Calls;

        public OcrPage Recognize(byte[] rgbBytes, int width, int height, OcrMode mode, CancellationToken ct = default)
        {
            Calls++;
            var chars = new List<CharBox>();
            for (int i = 0; i < Recognised.Length; i++)
                if (Recognised[i] != ' ') chars.Add(new CharBox(i, 300 + i * 20, 300, 316 + i * 20, 330, 0));
            return new OcrPage([new OcrLine(new BBox(300, 300, Recognised.Length * 20, 30), Recognised, chars, 0.9f)]);
        }

        public void Dispose() { }
    }

    private static async Task<(ExtractionSnapshot Snapshot, int OcrCalls)> ExtractAsync(string path)
    {
        var ocr = new CountingOcr();
        using var source = new RailReaderExtractionSource();
        source.WithBackend(ExtractionBackend.ModelFree(() => ocr));
        var snapshot = await source.ExtractAsync(new ExtractionRequest(
            path, null, null, Options, NarrationFilterSettings.Default));
        return (snapshot, ocr.Calls);
    }

    [Fact]
    public async Task NearlyEmptyPage_IsNotRecognised()
    {
        // The case that cost about 20 s a page: one short line and nothing else.
        string path = SyntheticPdf.WriteCrossPageSentence(SyntheticPdf.TempPath("sparse-empty"),
            "A sentence that begins here", "and ends on the next page.");

        var (snapshot, calls) = await ExtractAsync(path);

        Assert.Equal(0, calls);
        foreach (var page in snapshot.Pages)
        {
            Assert.False(page.Coverage.OcrUsed);
            Assert.DoesNotContain(page.Coverage.Warnings, w => w.Contains("sparse", StringComparison.Ordinal));
        }
        Assert.Contains("begins here", snapshot.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanWithAStrayTextLayer_IsStillRecognised()
    {
        string path = SyntheticPdf.WriteScanWithStrayText(SyntheticPdf.TempPath("sparse-scan"), "Digitised by the library");

        var (snapshot, calls) = await ExtractAsync(path);

        Assert.Equal(1, calls);
        var page = Assert.Single(snapshot.Pages);
        Assert.True(page.Coverage.OcrUsed);
        Assert.Contains("scanned page", page.Text, StringComparison.Ordinal);
        Assert.Contains(page.Coverage.Warnings, w => w.Contains("sparse", StringComparison.Ordinal));
    }

    // --- the measurement itself -----------------------------------------------------------

    private static byte[] Paper(int width, int height, byte shade = 255)
    {
        var rgb = new byte[width * height * 3];
        Array.Fill(rgb, shade);
        return rgb;
    }

    private static void Ink(byte[] rgb, int width, int x0, int y0, int x1, int y1, byte shade = 0)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                rgb.AsSpan((y * width + x) * 3, 3).Fill(shade);
    }

    [Fact]
    public void InkInsideTheTextsBoxes_IsNotCounted()
    {
        // A 200 x 100 raster of a 100 x 50 pt page: two pixels to the point.
        var rgb = Paper(200, 100);
        Ink(rgb, 200, 40, 40, 60, 60);
        CharBox[] chars = [new CharBox(0, 20, 20, 30, 30, 0)];

        Assert.Equal(0, PageInk.FractionOutsideText(rgb, 200, 100, chars, 100, 50));
    }

    [Fact]
    public void InkOutsideTheTextsBoxes_IsCountedAsAShareOfThePage()
    {
        var rgb = Paper(200, 100);
        Ink(rgb, 200, 40, 40, 60, 60);        // the text's own ink
        Ink(rgb, 200, 120, 10, 160, 20);      // 400 pixels of something else
        CharBox[] chars = [new CharBox(0, 20, 20, 30, 30, 0)];

        Assert.Equal(400.0 / 20000, PageInk.FractionOutsideText(rgb, 200, 100, chars, 100, 50), 6);
    }

    [Fact]
    public void PaperThatIsNotWhite_IsNotInk()
    {
        // A scan's paper is grey or yellowed; only what stands out from it counts.
        var rgb = Paper(200, 100, shade: 205);
        Ink(rgb, 200, 120, 10, 160, 20, shade: 40);
        Ink(rgb, 200, 10, 70, 190, 90, shade: 190);   // a faint stain, within the paper's grain

        Assert.Equal(400.0 / 20000, PageInk.FractionOutsideText(rgb, 200, 100, [], 100, 50), 6);
    }

    [Fact]
    public void AnEmptyOrMalformedRaster_HasNoInk()
    {
        Assert.Equal(0, PageInk.FractionOutsideText([], 0, 0, [], 100, 50));
        Assert.Equal(0, PageInk.FractionOutsideText(new byte[10], 200, 100, [], 100, 50));
    }
}
