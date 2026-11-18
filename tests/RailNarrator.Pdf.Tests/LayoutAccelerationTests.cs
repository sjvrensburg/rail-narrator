using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// Layout analysis that starts on one device and has to finish on another: the switch is made once,
/// from the page that failed, and is reported in that page's warnings rather than made silently.
/// </summary>
/// <remarks>
/// The primary analyzer here is a stand-in that throws, so these run with no GPU and no model. The
/// fallback is Core's model-free analyzer, the same shape as the CPU analyzer a GPU backend falls
/// back to.
/// </remarks>
public sealed class LayoutAccelerationTests
{
    private static readonly ExtractionOptions Options = new(
        "text-layout", OcrPolicy.Off, 1024, DeskewLines: false, ExtractorVersions.Current);

    private static string TwoPages() => SyntheticPdf.WriteCrossPageSentence(SyntheticPdf.TempPath("accel"),
        "The first page carries the opening half of this sentence", "and the second page carries its end.");

    private static ExtractionBackend Backend(Func<ILayoutAnalyzer> primary, bool withFallback)
    {
        var modelFree = ExtractionBackend.ModelFree();
        return modelFree with
        {
            AnalyzerFactory = primary,
            Device = new LayoutDevice("webgpu", "Test GPU"),
            FallbackAnalyzerFactory = withFallback ? modelFree.AnalyzerFactory : null,
        };
    }

    private static async Task<ExtractionSnapshot> ExtractAsync(string path, ExtractionBackend backend)
        => (await ExtractWithSourceAsync(path, backend)).Snapshot;

    private static async Task<(ExtractionSnapshot Snapshot, bool FellBack)> ExtractWithSourceAsync(string path, ExtractionBackend backend)
    {
        using var source = new RailReaderExtractionSource();
        source.WithBackend(backend);
        Assert.False(source.LayoutDeviceFellBack);
        var snapshot = await source.ExtractAsync(new ExtractionRequest(path, null, null, Options, NarrationFilterSettings.Default));
        return (snapshot, source.LayoutDeviceFellBack);
    }

    [Fact]
    public async Task ASourceThatFellBack_SaysSo_SoItsSnapshotsAreNotCachedUnderTheGpuKey()
    {
        var (_, fellBack) = await ExtractWithSourceAsync(TwoPages(), Backend(() => new FailingAnalyzer(), withFallback: true));
        Assert.True(fellBack);

        var (_, healthy) = await ExtractWithSourceAsync(TwoPages(), ExtractionBackend.ModelFree());
        Assert.False(healthy);
    }

    [Fact]
    public async Task AnAnalyzerThatCannotBeStarted_IsReplacedByTheFallback_AndSaysSo()
    {
        var snapshot = await ExtractAsync(TwoPages(),
            Backend(() => throw new InvalidOperationException("no adapter"), withFallback: true));

        Assert.All(snapshot.Pages, p => Assert.NotEmpty(p.Blocks));
        var warnings = snapshot.AllWarnings().ToList();
        var note = Assert.Single(warnings);
        Assert.StartsWith("Page 1: The layout model could not be started on Test GPU (no adapter)", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnalyzerThatFailsMidDocument_FinishesOnTheFallback_FromThePageThatFailed()
    {
        var failing = new FailingAnalyzer();
        var snapshot = await ExtractAsync(TwoPages(), Backend(() => failing, withFallback: true));

        // The page that failed is analysed again on the fallback rather than left without blocks.
        Assert.All(snapshot.Pages, p => Assert.NotEmpty(p.Blocks));
        Assert.Equal(1, failing.Calls);
        Assert.True(failing.Disposed);
        var note = Assert.Single(snapshot.AllWarnings());
        Assert.StartsWith("Page 1: Layout analysis failed on Test GPU (InvalidOperationException: device lost)", note, StringComparison.Ordinal);
        Assert.Contains("the rest of the document were analysed on the CPU", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAFallback_AFailingPageIsReported_AndLeftWithoutBlocks()
    {
        var snapshot = await ExtractAsync(TwoPages(), Backend(() => new FailingAnalyzer(), withFallback: false));

        Assert.All(snapshot.Pages, p => Assert.Empty(p.Blocks));
        Assert.Equal(2, snapshot.AllWarnings().Count(w => w.Contains("Layout analysis failed", StringComparison.Ordinal)));
    }

    /// <summary>Opt-in: the CPU setting never touches the GPU, even where there is one.</summary>
    [LayoutModelFact]
    public void TheCpuSetting_RunsTheModelOnTheCpu()
    {
        var backend = ExtractionBackend.OnnxLayout(Environment.GetEnvironmentVariable("RAIL_NARRATOR_LAYOUT_MODEL")!,
            acceleration: LayoutAcceleration.Cpu);

        Assert.Equal(LayoutDevice.Cpu, backend.ResolvedDevice);
        Assert.Null(backend.FallbackAnalyzerFactory);
    }

    /// <summary>
    /// Opt-in, needs a WebGPU-capable GPU: the GPU gives the same reading as the CPU. Blocks, kinds
    /// and reading sequence must match exactly; geometry may differ by float rounding between the
    /// two execution providers (up to 0.02 pt measured on a real chapter, ADR-0006).
    /// </summary>
    [WebGpuFact]
    public async Task OnTheGpu_TheModelGivesTheSameReadingAsOnTheCpu()
    {
        string model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_LAYOUT_MODEL")!;
        string path = SyntheticPdf.WriteTwoColumn(SyntheticPdf.TempPath("accel-gpu"),
            ["The left column opens the argument of the paper.", "It continues on a second line of text."],
            ["The right column closes the argument again.", "Nothing follows the right column at all."]);
        var options = Options with { AnalyzerId = "pp-doclayout-v3" };

        async Task<ExtractionSnapshot> Run(ExtractionBackend backend)
        {
            using var source = new RailReaderExtractionSource();
            source.WithBackend(backend);
            return await source.ExtractAsync(new ExtractionRequest(path, null, null, options, NarrationFilterSettings.Default));
        }

        var gpuBackend = ExtractionBackend.OnnxLayout(model, acceleration: LayoutAcceleration.Auto);
        Assert.True(gpuBackend.ResolvedDevice.IsGpu, gpuBackend.ResolvedDevice.FallbackReason);
        var gpu = await Run(gpuBackend);
        var cpu = await Run(ExtractionBackend.OnnxLayout(model, acceleration: LayoutAcceleration.Cpu));

        Assert.Empty(gpu.AllWarnings());
        for (int p = 0; p < cpu.Pages.Count; p++)
        {
            var c = cpu.Pages[p].OrderedBlocks.ToList();
            var g = gpu.Pages[p].OrderedBlocks.ToList();
            Assert.NotEmpty(c);
            Assert.Equal(c.Select(b => b.Kind), g.Select(b => b.Kind));
            Assert.Equal(c.Select(b => b.LineIndices.Count), g.Select(b => b.LineIndices.Count));
            for (int i = 0; i < c.Count; i++)
            {
                Assert.InRange(Math.Abs(c[i].BBox.Left - g[i].BBox.Left), 0, 0.1);
                Assert.InRange(Math.Abs(c[i].BBox.Top - g[i].BBox.Top), 0, 0.1);
                Assert.InRange(Math.Abs(c[i].BBox.Right - g[i].BBox.Right), 0, 0.1);
                Assert.InRange(Math.Abs(c[i].BBox.Bottom - g[i].BBox.Bottom), 0, 0.1);
            }
        }
        Assert.Equal(new ReadingUnitBuilder().Build(cpu).Select(u => u.OriginalText),
            new ReadingUnitBuilder().Build(gpu).Select(u => u.OriginalText));
    }

    private sealed class FailingAnalyzer : ILayoutAnalyzer
    {
        private readonly TextLayoutAnalyzer _shape = new();
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public LayoutModelCapabilities Capabilities => _shape.Capabilities;

        public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
            IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("device lost");
        }

        public void Dispose()
        {
            Disposed = true;
            _shape.Dispose();
        }
    }
}

public sealed class WebGpuFactAttribute : FactAttribute
{
    public WebGpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RN_WEBGPU_INTEGRATION") != "1"
            || !File.Exists(Environment.GetEnvironmentVariable("RAIL_NARRATOR_LAYOUT_MODEL")))
            Skip = "Set RN_WEBGPU_INTEGRATION=1 and RAIL_NARRATOR_LAYOUT_MODEL on a machine with a WebGPU-capable GPU.";
    }
}
