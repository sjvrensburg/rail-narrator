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

/// <summary>
/// Opt-in: Docling Heron (ADR-0008) through the adapter, ordered by Core's XY-cut++ since Heron
/// gives no reading order of its own; and on a GPU, the same reading as on the CPU.
/// </summary>
public sealed class HeronModelTests
{
    private static string TwoColumns(string name) => SyntheticPdf.WriteTwoColumn(SyntheticPdf.TempPath(name),
        ["The left column opens the argument of the paper.", "It continues on a second line of text."],
        ["The right column closes the argument again.", "Nothing follows the right column at all."]);

    private static readonly ExtractionOptions Options = new("heron", OcrPolicy.Off, 1024, false, ExtractorVersions.Current);

    private static async Task<ExtractionSnapshot> ExtractAsync(string path, ExtractionBackend backend)
    {
        using var source = new RailReaderExtractionSource();
        source.WithBackend(backend);
        return await source.ExtractAsync(new ExtractionRequest(path, null, null, Options, NarrationFilterSettings.Default));
    }

    private static LayoutModelFiles Heron(string? gpu, string cpu)
        => new(RailReader.Core.Models.LayoutModelArchitecture.Heron, "heron", gpu, cpu);

    [HeronModelFact]
    public async Task Heron_ReadsTheLeftColumnBeforeTheRight()
    {
        string model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_HERON_MODEL")!;
        var snapshot = await ExtractAsync(TwoColumns("heron"), ExtractionBackend.OnnxLayout(Heron(null, model)));

        Assert.DoesNotContain(snapshot.AllWarnings(), w => w.Contains("Layout analysis failed", StringComparison.Ordinal));
        string text = string.Join(" ", new ReadingUnitBuilder().Build(snapshot).Select(u => u.OriginalText));
        int left = text.IndexOf("left column opens", StringComparison.Ordinal);
        int right = text.IndexOf("right column closes", StringComparison.Ordinal);
        Assert.True(left >= 0 && right > left, $"left column first: {text}");
    }

    /// <summary>
    /// Heron's FP32 export, the file Core routes a GPU to, gives the same reading on the GPU as on
    /// the CPU. Core measured the same on a 42-page corpus (0 missed, 0 extra detections).
    /// </summary>
    [HeronWebGpuFact]
    public async Task OnTheGpu_HeronGivesTheSameReadingAsOnTheCpu()
    {
        string fp32 = Environment.GetEnvironmentVariable("RAIL_NARRATOR_HERON_GPU_MODEL")!;
        string path = TwoColumns("heron-gpu");

        var gpuBackend = ExtractionBackend.OnnxLayout(Heron(fp32, fp32), acceleration: LayoutAcceleration.Auto);
        Assert.True(gpuBackend.ResolvedDevice.IsGpu, gpuBackend.ResolvedDevice.FallbackReason);
        var gpu = await ExtractAsync(path, gpuBackend);
        var cpu = await ExtractAsync(path, ExtractionBackend.OnnxLayout(Heron(null, fp32), acceleration: LayoutAcceleration.Cpu));

        Assert.Empty(gpu.AllWarnings());
        for (int p = 0; p < cpu.Pages.Count; p++)
        {
            Assert.Equal(cpu.Pages[p].OrderedBlocks.Select(b => b.Kind), gpu.Pages[p].OrderedBlocks.Select(b => b.Kind));
            Assert.Equal(cpu.Pages[p].OrderedBlocks.Select(b => b.LineIndices.Count), gpu.Pages[p].OrderedBlocks.Select(b => b.LineIndices.Count));
        }
        Assert.Equal(new ReadingUnitBuilder().Build(cpu).Select(u => u.OriginalText),
            new ReadingUnitBuilder().Build(gpu).Select(u => u.OriginalText));
    }
}

public sealed class HeronModelFactAttribute : FactAttribute
{
    public HeronModelFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("RAIL_NARRATOR_HERON_MODEL")))
            Skip = "Set RAIL_NARRATOR_HERON_MODEL to docling-layout-heron-int8.onnx to run Heron through the adapter.";
    }
}

public sealed class HeronWebGpuFactAttribute : FactAttribute
{
    public HeronWebGpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RN_WEBGPU_INTEGRATION") != "1"
            || !File.Exists(Environment.GetEnvironmentVariable("RAIL_NARRATOR_HERON_GPU_MODEL")))
            Skip = "Set RN_WEBGPU_INTEGRATION=1 and RAIL_NARRATOR_HERON_GPU_MODEL (docling-layout-heron.onnx) on a machine with a WebGPU-capable GPU.";
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
