using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Infrastructure.Tests;

/// <summary>
/// The extraction cache answers repeat opens from disk, and never answers from an entry made under
/// different inputs, from a degraded run, or for an encrypted document.
/// </summary>
public sealed class CachingExtractionSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rn-extraction-cache-unit", Guid.NewGuid().ToString("N"));
    private readonly string _pdf;

    public CachingExtractionSourceTests()
    {
        Directory.CreateDirectory(_root);
        _pdf = Path.Combine(_root, "doc.pdf");
        File.WriteAllText(_pdf, "%PDF-1.4 stand-in bytes");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CacheDir => Path.Combine(_root, "cache");

    private ExtractionRequest Request(OcrPolicy ocr = OcrPolicy.Off, int rotation = 0, string? password = null,
        NarrationFilterSettings? filters = null)
        => new(_pdf, password, null, new ExtractionOptions("text-layout", ocr, 1024, false, ExtractorVersions.Current),
            filters ?? NarrationFilterSettings.Default, rotation);

    [Fact]
    public async Task SecondOpen_IsServedFromDisk()
    {
        var inner = new CountingSource();
        var cache = new CachingExtractionSource(inner, CacheDir);

        await cache.ExtractAsync(Request());
        // A new instance: the entry must survive a restart, not just live in memory.
        var reopened = new CachingExtractionSource(inner, CacheDir);
        var snapshot = await reopened.ExtractAsync(Request());

        Assert.Equal(1, inner.Calls);
        Assert.True(reopened.LastWasCacheHit);
        Assert.Equal("Café\u0002 ﬁ", snapshot.Pages[0].Text);
        Assert.Equal(CountingSource.Char, snapshot.Pages[0].Chars[0]);
        Assert.Equal(BlockKind.Text, snapshot.Pages[0].Blocks[0].Kind);
        Assert.True(snapshot.Pages[0].Lines[0].FromOcr);
    }

    [Fact]
    public async Task ChangedInputs_MissTheCache()
    {
        var inner = new CountingSource();
        var cache = new CachingExtractionSource(inner, CacheDir);

        await cache.ExtractAsync(Request());
        await cache.ExtractAsync(Request(ocr: OcrPolicy.Full));
        await cache.ExtractAsync(Request(rotation: 1));
        await cache.ExtractAsync(Request(filters: NarrationFilterSettings.Default with { IncludeFootnotes = true }));
        await new CachingExtractionSource(inner, CacheDir, backendFingerprint: "other-model").ExtractAsync(Request());
        File.AppendAllText(_pdf, " edited");
        await cache.ExtractAsync(Request());

        Assert.Equal(6, inner.Calls);
    }

    [Fact]
    public async Task DegradedExtraction_IsNotStored()
    {
        var inner = new CountingSource();
        bool degraded = true;
        var cache = new CachingExtractionSource(inner, CacheDir, isDegraded: () => degraded);

        await cache.ExtractAsync(Request(ocr: OcrPolicy.Full));
        degraded = false;
        await cache.ExtractAsync(Request(ocr: OcrPolicy.Full));

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task EncryptedDocument_IsNeverWrittenToDisk()
    {
        var inner = new CountingSource();
        var cache = new CachingExtractionSource(inner, CacheDir);

        await cache.ExtractAsync(Request(password: "secret"));
        await cache.ExtractAsync(Request(password: "secret"));

        Assert.Equal(2, inner.Calls);
        Assert.Empty(Directory.EnumerateFiles(CacheDir));
    }

    [Fact]
    public async Task CorruptEntry_IsAMiss()
    {
        var inner = new CountingSource();
        var cache = new CachingExtractionSource(inner, CacheDir);
        await cache.ExtractAsync(Request());
        foreach (string f in Directory.EnumerateFiles(CacheDir)) File.WriteAllText(f, "not gzip");

        var snapshot = await cache.ExtractAsync(Request());

        Assert.Equal(2, inner.Calls);
        Assert.False(cache.LastWasCacheHit);
        Assert.Single(snapshot.Pages);
    }

    private sealed class CountingSource : IExtractionSource
    {
        public static readonly ExtractedChar Char = new(0, new PageRect(10, 20, 16, 30), 90);
        public int Calls { get; private set; }

        public Task<ExtractionSnapshot> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            var page = new PageExtraction(0, 612, 792, "Café\u0002 ﬁ",
                [new ExtractedBlock("b0", BlockKind.Text, 0, new PageRect(10, 20, 100, 30), [0], 0.9f, 1)],
                [new ExtractedLine("l0", 0, new TextRange(0, 8), new PageRect(10, 20, 100, 30), FromOcr: true)],
                [Char],
                new TextCoverage(true, true, 8, 612 * 792, 60, ["one warning"]));
            return Task.FromResult(new ExtractionSnapshot(
                new ExtractionIdentity("hash", request.Revision), CoordinateFrameId.DisplayedPagePoints(0, request.ViewRotation),
                request.Options, [page]));
        }

        public Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default)
            => Task.FromResult<ReadOnlyMemory<byte>?>(null);
    }
}
