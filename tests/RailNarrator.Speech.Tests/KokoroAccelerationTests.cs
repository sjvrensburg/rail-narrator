using RailNarrator.Domain;
using RailNarrator.Speech;
using Xunit;

namespace RailNarrator.Speech.Tests;

/// <summary>
/// Kokoro on the GPU (ADR-0007): the inputs match KokoroSharp's, every GPU clip is checked, and the
/// first bad one moves synthesis to the CPU for good without the listener hearing it.
/// </summary>
public sealed class KokoroAccelerationTests
{
    private static float[,,] Style(int rows = 512, int channels = 4)
    {
        var style = new float[rows, 1, channels];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < channels; c++) style[r, 0, c] = r * 10 + c;
        return style;
    }

    /// <summary>The same padding, unknown-token substitution and style row as KokoroSharp's KokoroModel.Infer.</summary>
    [Fact]
    public void Inputs_AreBuiltExactlyAsKokoroSharpBuildsThem()
    {
        var (tokens, style) = KokoroInputs.Build([5, -1, 7], Style());

        Assert.Equal([0, 5, KokoroInputs.UnknownTokenReplacement, 7, 0], tokens);
        Assert.Equal([20f, 21f, 22f, 23f], style);   // row T - 1 = 2 for three tokens
    }

    [Fact]
    public void Inputs_AreTruncatedToTheModelsLimit()
    {
        var (tokens, style) = KokoroInputs.Build(Enumerable.Repeat(9, 600).ToArray(), Style());

        Assert.Equal(KokoroInputs.MaxTokens + 2, tokens.Length);
        Assert.Equal(0, tokens[0]);
        Assert.Equal(0, tokens[^1]);
        Assert.Equal((KokoroInputs.MaxTokens - 1) * 10f, style[0]);
    }

    [Theory]
    [InlineData(new[] { 0.1f, float.NaN, 0.2f }, "non-finite")]
    [InlineData(new[] { 0.1f, float.PositiveInfinity }, "non-finite")]
    [InlineData(new[] { 0.0001f, -0.0002f }, "silent")]
    [InlineData(new float[0], "no samples")]
    public void BadAudio_IsRecognised(float[] samples, string expected)
        => Assert.Contains(expected, KokoroOutputCheck.Problem(samples));

    [Fact]
    public void RealAudio_PassesTheCheck()
        => Assert.Null(KokoroOutputCheck.Problem([0.1f, -0.3f, 0.2f]));

    private sealed class FakeRunner(string device, Func<float[]> output) : IKokoroRunner
    {
        public int Runs;
        public bool Disposed;
        public string Device { get; } = device;
        public string Revision => Device;
        public Task<KokoroRunOutput> RunAsync(IReadOnlyList<int[]> segments, float[,,] voiceStyle, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(new KokoroRunOutput(output(), []));
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task WhileTheGpuGivesRealAudio_TheCpuIsNeverBuilt()
    {
        var gpu = new FakeRunner("webgpu", () => [0.2f, -0.1f]);
        int cpuBuilt = 0;
        using var chain = new KokoroRunnerChain(gpu, () => { cpuBuilt++; return new FakeRunner("cpu", () => [0.3f]); });

        var output = await chain.RunAsync([[1, 2]], Style(), CancellationToken.None);

        Assert.Equal([0.2f, -0.1f], output.Samples);
        Assert.Equal("webgpu", chain.Active.Device);
        Assert.Equal(0, cpuBuilt);
        Assert.Null(chain.FallbackReason);
    }

    /// <summary>
    /// The fp16 export gives NaN on WebGPU. The bad clip is replaced by the CPU's for the same request,
    /// the GPU is released, and every later request goes to the CPU.
    /// </summary>
    [Fact]
    public async Task ABadGpuClip_IsRedoneOnTheCpu_AndTheGpuIsDropped()
    {
        var gpu = new FakeRunner("webgpu", () => [float.NaN, 0.2f]);
        var cpu = new FakeRunner("cpu", () => [0.3f, -0.2f]);
        using var chain = new KokoroRunnerChain(gpu, () => cpu);
        string? reported = null;
        chain.FellBack += reason => reported = reason;

        var first = await chain.RunAsync([[1]], Style(), CancellationToken.None);
        var second = await chain.RunAsync([[2]], Style(), CancellationToken.None);

        Assert.Equal([0.3f, -0.2f], first.Samples);
        Assert.Equal([0.3f, -0.2f], second.Samples);
        Assert.Equal(1, gpu.Runs);
        Assert.Equal(2, cpu.Runs);
        Assert.True(gpu.Disposed);
        Assert.Equal("cpu", chain.Active.Device);
        Assert.Contains("non-finite", reported);
        Assert.Equal(reported, chain.FallbackReason);
    }

    [Fact]
    public async Task AGpuThatThrows_FallsBackToo()
    {
        var gpu = new ThrowingRunner();
        var cpu = new FakeRunner("cpu", () => [0.3f]);
        using var chain = new KokoroRunnerChain(gpu, () => cpu);

        var output = await chain.RunAsync([[1]], Style(), CancellationToken.None);

        Assert.Equal([0.3f], output.Samples);
        Assert.Contains("device lost", chain.FallbackReason);
    }

    [Fact]
    public async Task CancellingAGpuRun_DoesNotCountAsAFailure()
    {
        var gpu = new CancellingRunner();
        using var chain = new KokoroRunnerChain(gpu, () => new FakeRunner("cpu", () => [0.3f]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chain.RunAsync([[1]], Style(), cts.Token));
        Assert.Equal("webgpu", chain.Active.Device);
        Assert.Null(chain.FallbackReason);
    }

    [Fact]
    public void WithNoGpu_TheReasonIsKept_AndTheCpuRuns()
    {
        using var chain = new KokoroRunnerChain(null, () => new FakeRunner("cpu", () => [0.3f]), "no WebGPU device");
        Assert.Equal("cpu", chain.Active.Device);
        Assert.Equal("no WebGPU device", chain.FallbackReason);
    }

    private sealed class ThrowingRunner : IKokoroRunner
    {
        public string Device => "webgpu";
        public string Revision => "webgpu";
        public Task<KokoroRunOutput> RunAsync(IReadOnlyList<int[]> segments, float[,,] voiceStyle, CancellationToken ct)
            => throw new InvalidOperationException("device lost");
        public void Dispose() { }
    }

    private sealed class CancellingRunner : IKokoroRunner
    {
        public string Device => "webgpu";
        public string Revision => "webgpu";
        public Task<KokoroRunOutput> RunAsync(IReadOnlyList<int[]> segments, float[,,] voiceStyle, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new KokoroRunOutput([0.2f], []));
        }
        public void Dispose() { }
    }

    /// <summary>
    /// Opt-in, on a real WebGPU device: the converted model gives the CPU's audio (same length, a
    /// loudness envelope that matches) and the provider reports the GPU. Measured once by hand on the
    /// RTX A2000: envelope correlation 1.000.
    /// </summary>
    [KokoroWebGpuFact]
    public async Task OnTheGpu_KokoroGivesTheCpusAudio()
    {
        string cpuModel = Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL")!;
        string gpuModel = Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_GPU_MODEL")!;
        var hook = RailReader.Core.Analysis.WebGpu.WebGpuAccelerator.TryBuildSessionHook();
        Assert.NotNull(hook);
        const string text = "Running headers repeat on every page and carry no content.";

        await using var gpu = new KokoroTtsProvider(new KokoroProviderSettings(cpuModel),
            new KokoroGpuOptions(gpuModel, hook!, "test GPU"));
        await using var cpu = new KokoroTtsProvider(new KokoroProviderSettings(cpuModel));
        var request = new TtsSynthesisRequest(text, "af_heart", "en-US", AudioFormat.Mono24000Float32);
        var onGpu = await gpu.SynthesizeAsync(request);
        var onCpu = await cpu.SynthesizeAsync(request);

        Assert.Equal("webgpu", gpu.Device);
        Assert.Null(gpu.GpuFallbackReason);
        float[] g = Floats(onGpu.Pcm), c = Floats(onCpu.Pcm);
        Assert.Null(KokoroOutputCheck.Problem(g));
        Assert.InRange(g.Length, c.Length * 0.98, c.Length * 1.02);
        Assert.True(EnvelopeCorrelation(g, c) > 0.98, "the GPU's audio must follow the CPU's");
    }

    private static float[] Floats(ReadOnlyMemory<byte> pcm)
        => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcm.Span).ToArray();

    private static double EnvelopeCorrelation(float[] a, float[] b, int frame = 480)
    {
        static double[] Envelope(float[] x, int n) => Enumerable.Range(0, x.Length / n)
            .Select(i => Math.Sqrt(x.AsSpan(i * n, n).ToArray().Sum(v => (double)v * v) / n)).ToArray();
        double[] ea = Envelope(a, frame), eb = Envelope(b, frame);
        int k = Math.Min(ea.Length, eb.Length);
        double ma = ea.Take(k).Average(), mb = eb.Take(k).Average();
        double cov = 0, va = 0, vb = 0;
        for (int i = 0; i < k; i++) { cov += (ea[i] - ma) * (eb[i] - mb); va += (ea[i] - ma) * (ea[i] - ma); vb += (eb[i] - mb) * (eb[i] - mb); }
        return cov / Math.Sqrt(va * vb);
    }
}

public sealed class KokoroWebGpuFactAttribute : FactAttribute
{
    public KokoroWebGpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RN_WEBGPU_INTEGRATION") != "1"
            || !File.Exists(Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL"))
            || !File.Exists(Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_GPU_MODEL")))
            Skip = "Set RN_WEBGPU_INTEGRATION=1, RAIL_NARRATOR_KOKORO_MODEL and RAIL_NARRATOR_KOKORO_GPU_MODEL " +
                   "(from tools/kokoro-webgpu-model.sh) on a machine with a WebGPU-capable GPU.";
    }
}
