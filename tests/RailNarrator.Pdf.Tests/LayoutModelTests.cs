using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>Opt-in: the real PP-DocLayoutV3 model through the adapter.</summary>
public sealed class LayoutModelTests
{
    [LayoutModelFact]
    public async Task ALayoutModelAnalysesEveryPage_AndSuppliesBlocks()
    {
        // The adapter used to hand the model the high-resolution raster used for line detection
        // (up to 8192 px). The model accepts only its input size (800 px) and threw on every page,
        // so a configured layout model produced no blocks and nothing was narrated.
        string path = SyntheticPdf.WriteTwoColumn(SyntheticPdf.TempPath("layout-model"),
            ["The left column opens the argument of the paper.", "It continues on a second line of text."],
            ["The right column closes the argument again.", "Nothing follows the right column at all."]);
        string model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_LAYOUT_MODEL")!;

        using var source = new RailReaderExtractionSource();
        source.WithBackend(ExtractionBackend.OnnxLayout(model));
        var snapshot = await source.ExtractAsync(new ExtractionRequest(path, null, null,
            new ExtractionOptions("pp-doclayout-v3", OcrPolicy.Off, 1024, false, ExtractorVersions.Current),
            NarrationFilterSettings.Default));

        Assert.DoesNotContain(snapshot.AllWarnings(), w => w.Contains("Layout analysis failed", StringComparison.Ordinal));
        Assert.NotEmpty(snapshot.Pages[0].Blocks);
        Assert.NotEmpty(snapshot.Pages[0].Lines);
        var units = new ReadingUnitBuilder().Build(snapshot);
        Assert.Contains(units, u => u.OriginalText.Contains("left column opens", StringComparison.Ordinal));
    }
}

public sealed class LayoutModelFactAttribute : FactAttribute
{
    public LayoutModelFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("RAIL_NARRATOR_LAYOUT_MODEL")))
            Skip = "Set RAIL_NARRATOR_LAYOUT_MODEL to a PP-DocLayoutV3.onnx to run the layout model through the adapter.";
    }
}
