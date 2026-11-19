using RailNarrator.Domain;
using RailNarrator.Pdf;
using RailNarrator.TestFixtures;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// Maths crops are rendered as a region since Core 0.93.0, instead of a whole page cut down. The
/// model must see the same picture: the size to within a pixel, and the pixels nearly alike. Not
/// byte for byte (measured: the whole-page render's two axes are scaled a hair apart), which is why
/// maths answers cached against the old crops are asked for again once.
/// </summary>
public sealed class CropRenderTests
{
    [Theory]
    [InlineData(72, 100, 300, 112, 1024)]   // a line of text, the width an equation crop is asked at
    [InlineData(72, 120, 400, 160, 768)]
    [InlineData(10.5f, 5.25f, 600.75f, 780.5f, 512)]   // nearly the whole page, fractional points
    public async Task TheRegionRender_GivesTheWholePageRendersCrop_ToWithinAPixel(float left, float top, float right, float bottom, int width)
    {
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("crop"),
            ["The quick brown fox jumps over the lazy dog.", "Pack my box with five dozen liquor jugs.", "x² + y² = z²"]);
        using var source = new RailReaderExtractionSource();
        source.AttachDocument(path);
        var region = new PageRect(left, top, right, bottom);

        var byRegion = await source.RenderCropPngAsync(new ExtractionIdentity("doc", 1), 0, region, width);
        var pdf = new SkiaPdfServiceFactory().CreatePdfService(path, null);
        using var byPage = CropByWholePage(pdf, 0, region, width);
        (pdf as IDisposable)?.Dispose();

        Assert.NotNull(byRegion);
        using var regionImage = SKBitmap.Decode(byRegion.Value.ToArray());
        Assert.InRange(regionImage.Width, byPage.Width - 1, byPage.Width + 1);
        Assert.InRange(regionImage.Height, byPage.Height - 1, byPage.Height + 1);
        Assert.InRange(regionImage.Width, width - 2, width + 2);   // the width the model is shown

        int w = Math.Min(regionImage.Width, byPage.Width), h = Math.Min(regionImage.Height, byPage.Height);
        double difference = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                difference += Math.Abs(regionImage.GetPixel(x, y).Red - byPage.GetPixel(x, y).Red);
        double mean = difference / (w * h);
        Assert.True(mean < 8, $"the crops differ by {mean:F1} grey levels on average");
    }

    /// <summary>
    /// A narrow equation: the whole-page render held the page to 8192 px, so this 80 pt crop came out
    /// 828 px wide. Rendered as a region it gets the 1024 px it is asked for.
    /// </summary>
    [Fact]
    public async Task ANarrowEquation_GetsTheFullWidthItIsAskedFor()
    {
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("crop-narrow"), ["x² + y² = z²"]);
        using var source = new RailReaderExtractionSource();
        source.AttachDocument(path);
        var crop = await source.RenderCropPngAsync(new ExtractionIdentity("doc", 1), 0, new PageRect(80, 98, 160, 114), 1024);
        Assert.NotNull(crop);
        using var image = SKBitmap.Decode(crop.Value.ToArray());
        Assert.InRange(image.Width, 1023, 1025);
        Assert.InRange(image.Height, 204, 206);   // 16 pt at 12.8 px/pt
    }

    /// <summary>The crop as it was made before Core 0.93.0: the whole page at the crop's scale, then cut.</summary>
    private static SKBitmap CropByWholePage(IPdfService pdf, int pageIndex, PageRect region, int targetPixelWidth)
    {
        var (pageW, pageH) = pdf.GetPageSize(pageIndex);
        double wanted = targetPixelWidth / Math.Max(1.0, region.Width);
        int targetSize = (int)Math.Clamp(Math.Round(Math.Max(pageW, pageH) * wanted), 64, 8192);
        var (rgb, pxW, pxH) = pdf.RenderPagePixmap(pageIndex, targetSize);
        float sx = pxW / (float)pageW, sy = pxH / (float)pageH;
        int x0 = (int)Math.Clamp(Math.Floor(region.Left * sx), 0, pxW - 1), y0 = (int)Math.Clamp(Math.Floor(region.Top * sy), 0, pxH - 1);
        int x1 = (int)Math.Clamp(Math.Ceiling(region.Right * sx), 1, pxW), y1 = (int)Math.Clamp(Math.Ceiling(region.Bottom * sy), 1, pxH);
        var bitmap = new SKBitmap(x1 - x0, y1 - y0);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * pxW + x) * 3;
                bitmap.SetPixel(x - x0, y - y0, new SKColor(rgb[i], rgb[i + 1], rgb[i + 2]));
            }
        return bitmap;
    }
}
