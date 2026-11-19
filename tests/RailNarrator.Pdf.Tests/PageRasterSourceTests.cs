using RailNarrator.Pdf;
using RailNarrator.TestFixtures;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// The page rasteriser the reading surface draws from: pages rendered on a worker, in the order
/// asked, with a render the reader has paged past abandoned.
/// </summary>
/// <remarks>
/// Real PDFium renders of <see cref="SyntheticPdf"/> pages; no model, GPU or display. The ordering
/// tests hold the renderer at a gate so that "while page 1 is rendering" is a state the test
/// controls, not a race it hopes to win.
/// </remarks>
public sealed class PageRasterSourceTests
{
    private static readonly string[] Lines =
    [
        "The quick brown fox jumps over the lazy dog.",
        "Pack my box with five dozen liquor jugs.",
    ];

    private static PageRasterKey Key(int page, int dpi = 72, ColourEffect effect = ColourEffect.None, float intensity = 1f)
        => new(page, dpi, effect, intensity);

    /// <summary>Collects what a source hands over, on whatever thread it does so.</summary>
    private sealed class Receiver : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<PageRaster> _rasters = [];
        private readonly List<(PageRasterKey Key, Exception Error)> _failures = [];

        public Receiver(PageRasterSource source)
        {
            source.Ready += r => { lock (_gate) { _rasters.Add(r); Monitor.PulseAll(_gate); } };
            source.Failed += (k, e) => { lock (_gate) { _failures.Add((k, e)); Monitor.PulseAll(_gate); } };
        }

        public IReadOnlyList<PageRasterKey> Delivered { get { lock (_gate) return _rasters.Select(r => r.Key).ToList(); } }

        public IReadOnlyList<(PageRasterKey Key, Exception Error)> Failures { get { lock (_gate) return _failures.ToList(); } }

        public PageRaster Raster(PageRasterKey key) { lock (_gate) return _rasters.First(r => r.Key == key); }

        /// <summary>Waits until <paramref name="count"/> rasters and failures have arrived in total.</summary>
        public void WaitFor(int count, int timeoutMs = 10000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            lock (_gate)
            {
                while (_rasters.Count + _failures.Count < count)
                {
                    long left = deadline - Environment.TickCount64;
                    Assert.True(left > 0, $"Timed out waiting for {count} results; got {_rasters.Count} rasters and {_failures.Count} failures.");
                    Monitor.Wait(_gate, (int)left);
                }
            }
        }

        public void Dispose() { lock (_gate) foreach (var r in _rasters) r.Dispose(); }
    }

    [Fact]
    public async Task OpeningADocument_ReadsItsPageSizes_AndAPageRendersAtTheResolutionAsked()
    {
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("raster"), Lines, pageCount: 3);
        using var source = await PageRasterSource.OpenAsync(path);
        using var receiver = new Receiver(source);

        Assert.Equal(3, source.PageCount);
        var (w, h) = source.PageSize(1);
        Assert.Equal(SyntheticPdf.PageWidth, w, 0.5);
        Assert.Equal(SyntheticPdf.PageHeight, h, 0.5);
        Assert.Equal((0d, 0d), source.PageSize(99));

        source.Request([Key(1, dpi: 144)]);
        receiver.WaitFor(1);

        var raster = receiver.Raster(Key(1, dpi: 144));
        Assert.InRange(raster.Bitmap.Width, 2 * 612 - 2, 2 * 612 + 2);
        Assert.InRange(raster.Bitmap.Height, 2 * 792 - 2, 2 * 792 + 2);
        Assert.Equal(4, raster.Bitmap.BytesPerPixel);
        Assert.True(raster.RenderTime > TimeSpan.Zero, "the render was not timed");
        // Paper in the corner, ink somewhere on the first line of text.
        Assert.Equal(SKColors.White, raster.Bitmap.GetPixel(4, 4));
        bool ink = false;
        for (int y = 2 * 72; y < 2 * 100 && !ink; y++)
            for (int x = 2 * 72; x < 2 * 300 && !ink; x++)
                ink = raster.Bitmap.GetPixel(x, y).Red < 96;
        Assert.True(ink, "the first line of text was not rendered");
    }

    [Fact]
    public async Task Rasters_AreDeliveredInTheOrderAsked_EachOnce()
    {
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("raster-order"), Lines, pageCount: 4);
        using var source = await PageRasterSource.OpenAsync(path);
        using var receiver = new Receiver(source);

        source.Request([Key(2), Key(3), Key(1), Key(2), Key(99), Key(0, dpi: 0)]);
        receiver.WaitFor(3);
        await Task.Delay(150);   // anything asked for twice, or out of range, would arrive now

        Assert.Equal([Key(2), Key(3), Key(1)], receiver.Delivered);
        Assert.Empty(receiver.Failures);
    }

    [Fact]
    public async Task PagingOn_AbandonsTheRendersNoLongerWanted()
    {
        // Paging quickly through pages 1 to 4 and stopping on page 8: only the page the reader
        // stopped on is worth rendering. Page 1 is already in the renderer and cannot be stopped
        // there, but it is not handed over, and pages 2 to 4 are never rendered at all.
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("raster-paging"), Lines, pageCount: 8);
        var factory = new GatedFactory();
        using var source = await PageRasterSource.OpenAsync(path, factory: factory);
        using var receiver = new Receiver(source);

        source.Request([Key(0), Key(1), Key(2), Key(3)]);
        Assert.True(factory.Service!.Entered.Wait(10000), "the first render never started");

        source.Request([Key(7)]);
        factory.Service.Release.Set();
        receiver.WaitFor(1);
        await Task.Delay(150);

        Assert.Equal([Key(7)], receiver.Delivered);
        Assert.Equal([0, 7], factory.Service.Rendered);
    }

    [Fact]
    public async Task ARenderStillWanted_CarriesOn_WhenTheRequestChangesAroundIt()
    {
        // A preview arriving makes the host send a new list. The page being rendered is still in
        // it, so its render is not thrown away and started again.
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("raster-keep"), Lines, pageCount: 4);
        var factory = new GatedFactory();
        using var source = await PageRasterSource.OpenAsync(path, factory: factory);
        using var receiver = new Receiver(source);

        source.Request([Key(0), Key(1)]);
        Assert.True(factory.Service!.Entered.Wait(10000), "the first render never started");
        source.Request([Key(0), Key(2)]);
        factory.Service.Release.Set();
        receiver.WaitFor(2);

        Assert.Equal([Key(0), Key(2)], receiver.Delivered);
        Assert.Equal([0, 2], factory.Service.Rendered);
    }

    [Fact]
    public async Task APageThatCannotBeRendered_IsReportedOnce_AndTheRestStillRender()
    {
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("raster-fail"), Lines, pageCount: 3);
        var factory = new GatedFactory { FailPage = 1 };
        using var source = await PageRasterSource.OpenAsync(path, factory: factory);
        using var receiver = new Receiver(source);
        factory.Service!.Release.Set();

        source.Request([Key(1), Key(2)]);
        receiver.WaitFor(2);
        await Task.Delay(150);   // a failed page tried again would fail again now

        var failure = Assert.Single(receiver.Failures);
        Assert.Equal(Key(1), failure.Key);
        Assert.Equal([Key(2)], receiver.Delivered);
        Assert.Equal([1, 2], factory.Service.Rendered);
    }

    [Theory]
    [InlineData(ColourEffect.HighContrast, 1f)]
    [InlineData(ColourEffect.HighVisibility, 1f)]
    [InlineData(ColourEffect.Invert, 1f)]
    [InlineData(ColourEffect.Invert, 0.5f)]
    [InlineData(ColourEffect.Amber, 1f)]
    [InlineData(ColourEffect.Amber, 0.4f)]
    [InlineData(ColourEffect.None, 1f)]
    public async Task AColourFilter_IsAppliedOnTheWorker_AndBlankPaperHasTheColourPredictedForIt(ColourEffect effect, float intensity)
    {
        // A page whose raster has not arrived is drawn as blank paper in PaperColour. If Core's
        // filters change, that placeholder would flash the wrong colour: this pins the two together.
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("raster-filter"), Lines);
        using var source = await PageRasterSource.OpenAsync(path);
        using var receiver = new Receiver(source);

        var key = Key(0, effect: effect, intensity: intensity);
        source.Request([key]);
        receiver.WaitFor(1);

        var paper = receiver.Raster(key).Bitmap.GetPixel(4, 4);
        var predicted = PageRasterSource.PaperColour(effect, intensity);
        Assert.InRange(paper.Red, predicted.Red - 3, predicted.Red + 3);
        Assert.InRange(paper.Green, predicted.Green - 3, predicted.Green + 3);
        Assert.InRange(paper.Blue, predicted.Blue - 3, predicted.Blue + 3);
        if (effect is ColourEffect.Invert or ColourEffect.HighContrast or ColourEffect.HighVisibility && intensity >= 1f)
            Assert.True(paper.Red < 8 && paper.Blue < 8, "white paper should be black under this filter");
    }

    [Fact]
    public async Task ADisposedSource_HandsOverNothingMore()
    {
        string path = SyntheticPdf.WriteSingleColumn(SyntheticPdf.TempPath("raster-dispose"), Lines, pageCount: 3);
        var factory = new GatedFactory();
        var source = await PageRasterSource.OpenAsync(path, factory: factory);
        using var receiver = new Receiver(source);

        source.Request([Key(0), Key(1)]);
        Assert.True(factory.Service!.Entered.Wait(10000), "the first render never started");
        source.Dispose();
        factory.Service.Release.Set();
        source.Request([Key(2)]);
        await Task.Delay(300);

        Assert.Empty(receiver.Delivered);
        Assert.Empty(receiver.Failures);
        source.Dispose();   // twice is harmless
    }

    /// <summary>
    /// Wraps the real renderer so a test can hold a render in progress and see which pages were
    /// rendered. It does not offer the tiled renderer, so a render in progress cannot be cancelled:
    /// the harder case for the source.
    /// </summary>
    private sealed class GatedFactory : IPdfServiceFactory
    {
        private readonly SkiaPdfServiceFactory _inner = new();

        public GatedService? Service { get; private set; }

        public int? FailPage { get; init; }

        public IPdfService CreatePdfService(string filePath, string? password = null)
            => Service = new GatedService(_inner.CreatePdfService(filePath, password), FailPage);

        public IPdfTextService CreatePdfTextService() => _inner.CreatePdfTextService();

        public IPdfLinkService CreatePdfLinkService() => _inner.CreatePdfLinkService();
    }

    private sealed class GatedService(IPdfService inner, int? failPage) : IPdfService
    {
        private readonly List<int> _rendered = [];

        /// <summary>Set when the first render has started.</summary>
        public ManualResetEventSlim Entered { get; } = new();

        /// <summary>Renders wait here until the test lets them through.</summary>
        public ManualResetEventSlim Release { get; } = new();

        public IReadOnlyList<int> Rendered { get { lock (_rendered) return _rendered.ToList(); } }

        public byte[] PdfBytes => inner.PdfBytes;
        public int PageCount => inner.PageCount;
        public List<OutlineEntry> Outline => inner.Outline;
        public (double Width, double Height) GetPageSize(int pageIndex) => inner.GetPageSize(pageIndex);
        public IRenderedPage RenderThumbnail(int pageIndex) => inner.RenderThumbnail(pageIndex);
        public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize) => inner.RenderPagePixmap(pageIndex, targetSize);

        public IRenderedPage RenderPage(int pageIndex, int dpi = 200)
        {
            lock (_rendered) _rendered.Add(pageIndex);
            Entered.Set();
            Assert.True(Release.Wait(10000), "the test never released the renderer");
            if (pageIndex == failPage) throw new InvalidOperationException("This page is damaged.");
            return inner.RenderPage(pageIndex, dpi);
        }
    }
}
