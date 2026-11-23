using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>
/// Running Kokoro on a GPU through ONNX Runtime's WebGPU execution provider (Vulkan on Linux,
/// Metal or D3D12 elsewhere). See ADR-0007.
/// </summary>
/// <param name="ModelPath">
/// A Kokoro fp32 export whose <c>tokens</c> input has been rewritten to int32
/// (<c>tools/kokoro-webgpu-model.sh</c>). The WebGPU plugin aborts the process on an int64 graph
/// input, and the fp16 export produces non-finite samples on it, so neither stock file can be used.
/// </param>
/// <param name="ConfigureSession">
/// Adds the WebGPU execution provider to a session's options. Supplied by the composition root from
/// RailReaderCore's <c>WebGpuAccelerator.TryBuildSessionHook()</c>, so this assembly needs no
/// reference to Core and shares the one GPU binding per process with layout analysis.
/// </param>
/// <param name="DeviceDescription">The GPU's name, for diagnostics.</param>
public sealed record KokoroGpuOptions(string ModelPath, Action<SessionOptions> ConfigureSession, string? DeviceDescription);

/// <summary>Audio from one synthesis, before trimming, with any phoneme timestamps the model gave.</summary>
/// <param name="Samples">24 kHz mono samples, the segments concatenated.</param>
/// <param name="Timestamps">Phoneme cues in that raw timeline; empty when the model has no durations output.</param>
public sealed record KokoroRunOutput(float[] Samples, IReadOnlyList<TimedCue> Timestamps)
{
    /// <summary>
    /// What was spoken, phoneme by phoneme, with the samples each occupies in <see cref="Samples"/>:
    /// from the durations output of a model that has one. Empty otherwise.
    /// </summary>
    public IReadOnlyList<TimedPhoneme> Phonemes { get; init; } = [];
}

/// <summary>One way of running the Kokoro graph: KokoroSharp's engine on the CPU, or a session on the GPU.</summary>
public interface IKokoroRunner : IDisposable
{
    /// <summary>"cpu" or "webgpu", for diagnostics and the model revision.</summary>
    string Device { get; }

    /// <summary>Identifies the model and the device, for the speech-cache key.</summary>
    string Revision { get; }

    /// <summary>Synthesises pre-segmented tokens with a voice, in order.</summary>
    Task<KokoroRunOutput> RunAsync(IReadOnlyList<int[]> segments, float[,,] voiceStyle, CancellationToken ct);
}

/// <summary>
/// Decides whether a clip is real audio. A GPU path that silently computes garbage is the failure
/// that matters: the fp16 export gives NaN samples on WebGPU, and a Vulkan decoder elsewhere gave
/// silence (ADR-0002).
/// </summary>
public static class KokoroOutputCheck
{
    /// <summary>Peak amplitude below which a whole clip counts as silent.</summary>
    public const float SilentPeak = 1e-3f;

    /// <summary>Why the samples cannot be used, or null when they can.</summary>
    public static string? Problem(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) return "no samples";
        float peak = 0;
        foreach (float s in samples)
        {
            if (!float.IsFinite(s)) return "non-finite samples (NaN or infinity)";
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak < SilentPeak ? $"silent output (peak {peak:G3})" : null;
    }
}

/// <summary>
/// The graph inputs for one segment, built exactly as KokoroSharp's <c>KokoroModel.Infer</c> builds
/// them, so the CPU and GPU paths give the same audio for the same text.
/// </summary>
public static class KokoroInputs
{
    /// <summary>The longest token sequence the model accepts; KokoroSharp truncates to it.</summary>
    public const int MaxTokens = 510;

    /// <summary>The token KokoroSharp substitutes for an unknown (negative) one: '.'.</summary>
    public const int UnknownTokenReplacement = 4;

    /// <summary>
    /// <c>&lt;pad&gt;{tokens}&lt;pad&gt;</c> with unknown tokens replaced, and the voice's style row for
    /// this length (row <c>T - 1</c>, T the token count before padding).
    /// </summary>
    public static (int[] Tokens, float[] Style) Build(int[] tokens, float[,,] voiceStyle)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(voiceStyle);
        int t = Math.Min(tokens.Length, MaxTokens);
        var padded = new int[t + 2];
        for (int i = 0; i < t; i++) padded[i + 1] = tokens[i] >= 0 ? tokens[i] : UnknownTokenReplacement;
        int channels = voiceStyle.GetLength(2);
        var style = new float[channels];
        int row = Math.Clamp(t - 1, 0, voiceStyle.GetLength(0) - 1);
        for (int j = 0; j < channels; j++) style[j] = voiceStyle[row, 0, j];
        return (padded, style);
    }
}

/// <summary>
/// A Kokoro graph in its own ONNX Runtime session: the WebGPU path. KokoroSharp's engine cannot be
/// used there because it always feeds int64 tokens.
/// </summary>
public sealed class KokoroSessionRunner : IKokoroRunner
{
    /// <summary>Samples of audio in one frame of a duration: 25 ms at 24 kHz.</summary>
    public const int DurationFrameSamples = 600;

    /// <summary>Frames the audio runs ahead of the durations' count; see <see cref="PhonemeWordTiming.FromDurations"/>.</summary>
    public const int LeadFrames = 3;

    private readonly InferenceSession _session;
    private readonly bool _int32Tokens;
    private readonly string _tokensInput;
    private readonly bool _hasDurations;
    private readonly object _gate = new();

    /// <param name="modelPath">The model file.</param>
    /// <param name="options">Session options, with any execution provider already added.</param>
    /// <param name="device">"webgpu" or "cpu".</param>
    /// <param name="fingerprint">The model file's digest, for <see cref="Revision"/>.</param>
    public KokoroSessionRunner(string modelPath, SessionOptions options, string device, string fingerprint)
    {
        _session = new InferenceSession(modelPath, options);
        var inputs = _session.InputMetadata;
        // KokoroSharp's exports call the phoneme ids "tokens"; onnx-community's, which include the
        // one with a durations output, call them "input_ids". The tensors are the same.
        _tokensInput = KokoroTtsProvider.TokenInputNames.FirstOrDefault(inputs.ContainsKey) ?? "";
        foreach (string name in KokoroTtsProvider.RequiredModelInputs.Where(n => n != "tokens").Append(_tokensInput))
            if (name.Length == 0 || !inputs.ContainsKey(name))
            {
                _session.Dispose();
                throw new InvalidOperationException($"The model at '{modelPath}' has no '{(name.Length == 0 ? "tokens" : name)}' input; it is not a Kokoro export this runtime can drive.");
            }
        _int32Tokens = inputs[_tokensInput].ElementType == typeof(int);
        _hasDurations = _session.OutputMetadata.ContainsKey(KokoroTtsProvider.DurationsOutputName);
        Device = device;
        Revision = $"{device}{(_int32Tokens ? "-i32" : "")}:{fingerprint}";
    }

    public string Device { get; }

    public string Revision { get; }

    /// <summary>True when the model takes int32 tokens, i.e. it was converted for WebGPU.</summary>
    public bool TakesInt32Tokens => _int32Tokens;

    public Task<KokoroRunOutput> RunAsync(IReadOnlyList<int[]> segments, float[,,] voiceStyle, CancellationToken ct)
        => Task.Run(() =>
        {
            var samples = new List<float>();
            var phonemes = new List<TimedPhoneme>();
            bool timed = _hasDurations;
            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                if (segment.Length == 0) continue;
                var (audio, padded, durations) = Infer(segment, voiceStyle);
                // Durations that do not add up to the audio are not trusted for any of the clip.
                if (timed && durations is not null
                    && PhonemeWordTiming.FromDurations(padded.Select(Symbol).ToArray(), padded.Select((_, i) => i == 0 || i == padded.Length - 1).ToArray(),
                        durations, DurationFrameSamples, audio.Length, samples.Count, LeadFrames) is { } segmentPhonemes)
                    phonemes.AddRange(segmentPhonemes);
                else timed = false;
                samples.AddRange(audio);
            }
            return new KokoroRunOutput(samples.ToArray(), []) { Phonemes = timed ? phonemes : [] };
        }, ct);

    private static char Symbol(int token) => Tokenizer.TokenToChar.TryGetValue(token, out char c) ? c : ' ';

    private (float[] Audio, int[] Padded, float[]? Durations) Infer(int[] tokens, float[,,] voiceStyle)
    {
        var (padded, style) = KokoroInputs.Build(tokens, voiceStyle);
        var tokenValue = _int32Tokens
            ? NamedOnnxValue.CreateFromTensor(_tokensInput, new DenseTensor<int>(padded, [1, padded.Length]))
            : NamedOnnxValue.CreateFromTensor(_tokensInput, new DenseTensor<long>(padded.Select(v => (long)v).ToArray(), [1, padded.Length]));
        var inputs = new List<NamedOnnxValue>
        {
            tokenValue,
            NamedOnnxValue.CreateFromTensor("style", new DenseTensor<float>(style, [1, style.Length])),
            NamedOnnxValue.CreateFromTensor("speed", new DenseTensor<float>(new[] { 1f }, [1])),
        };
        lock (_gate)
        {
            using var results = _session.Run(inputs);
            float[]? durations = _hasDurations
                ? Copy(results.First(r => r.Name == KokoroTtsProvider.DurationsOutputName).AsTensor<float>())
                : null;
            // The audio is the output that is not the durations, whatever the export calls it.
            float[] audio = Copy(results.First(r => r.Name != KokoroTtsProvider.DurationsOutputName).AsTensor<float>());
            return (audio, padded, durations);

            // The tensor's memory in one copy; enumerating it is a virtual call for every sample.
            static float[] Copy(Tensor<float> tensor) => tensor is DenseTensor<float> dense ? dense.Buffer.ToArray() : [.. tensor];
        }
    }

    public void Dispose()
    {
        lock (_gate) _session.Dispose();
    }
}

/// <summary>
/// KokoroSharp's engine on the CPU: the default path, unchanged from before the GPU path existed so
/// that its cached audio stays valid.
/// </summary>
public sealed class KokoroEngineRunner : IKokoroRunner
{
    /// <summary>
    /// Upper bound on one synthesis. KokoroSharp gives no awaitable completion, so a worker thread
    /// that dies mid-job would otherwise leave the caller polling forever.
    /// </summary>
    public const int SynthesisTimeoutMs = 120_000;

    private readonly KokoroEngine _engine;

    public KokoroEngineRunner(string modelPath, SessionOptions options, string revision)
    {
        _engine = new KokoroEngine(modelPath, options);
        Revision = revision;
    }

    public string Device => "cpu";

    public string Revision { get; }

    public async Task<KokoroRunOutput> RunAsync(IReadOnlyList<int[]> segments, float[,,] voiceStyle, CancellationToken ct)
    {
        var samples = new List<float>();
        var timestamps = new List<TimedCue>();
        var gate = new object();

        var job = KokoroJob.Create(segments.ToList(), voiceStyle, 1.0f, null);
        // Callbacks are wired before the job is queued: KokoroSharp's engine dispatches from a
        // background thread as soon as it is enqueued, so attaching afterwards is a race that would
        // silently drop the first segment's audio.
        foreach (var step in job.Steps)
        {
            step.OnStepComplete = stepSamples =>
            {
                lock (gate)
                {
                    long offsetBefore = samples.Count;
                    samples.AddRange(stepSamples);
                    // Steps are concatenated without trimming. Offset in that raw timeline; the single
                    // actual clip trim is then applied exactly once by the provider.
                    if (step.Timestamps is { Length: > 0 } ts)
                        foreach (var t in ts)
                            timestamps.Add(KokoroTtsProvider.PhonemeCue(t, offsetBefore));
                }
            };
        }

        // Cancellation disposes the registration whether or not the job finished, and cancelling a
        // completed job is harmless.
        var registration = ct.CanBeCanceled
            ? ct.Register(() => { try { job.Cancel(); } catch { /* already finished */ } })
            : default(CancellationTokenRegistration);
        try
        {
            _engine.EnqueueJob(job);
            await WaitForJobAsync(job, ct).ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }

        ct.ThrowIfCancellationRequested();
        if (job.State == KokoroJobState.Canceled) throw new OperationCanceledException(ct);
        lock (gate) return new KokoroRunOutput(samples.ToArray(), timestamps.ToList());
    }

    private static async Task WaitForJobAsync(KokoroJob job, CancellationToken ct)
    {
        // KokoroSharp exposes no awaitable completion, so the dispatch thread is polled. The delay is
        // short enough to keep first-audio latency low and long enough not to spin a core.
        long deadline = Environment.TickCount64 + SynthesisTimeoutMs;
        while (!job.isDone)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
            {
                // A worker that threw leaves isDone false forever; without this bound the caller hangs
                // instead of reporting a failure the reader can retry.
                try { job.Cancel(); } catch { /* already finished */ }
                throw new InvalidOperationException(
                    $"Kokoro did not finish synthesising within {SynthesisTimeoutMs / 1000} s. The model may be " +
                    "incompatible with this runtime, or the machine may be out of memory.");
            }
            await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Dispose() => _engine.Dispose();
}

/// <summary>
/// Runs on the GPU while it gives real audio, and on the CPU from the first clip it does not.
/// </summary>
/// <remarks>
/// Every GPU clip is checked (<see cref="KokoroOutputCheck"/>). A failed check, or an exception from
/// the GPU session, moves this provider to the CPU for the rest of the process, says why, and
/// synthesises the same request again there, so the listener never hears the bad clip. The CPU
/// runner is created only when first needed.
/// </remarks>
public sealed class KokoroRunnerChain : IDisposable
{
    private readonly Func<IKokoroRunner> _createCpu;
    private readonly object _gate = new();
    private IKokoroRunner? _gpu;
    private IKokoroRunner? _cpu;

    /// <param name="gpu">The GPU runner, or null to run on the CPU only.</param>
    /// <param name="createCpu">Builds the CPU runner on first use.</param>
    /// <param name="gpuUnavailableReason">Why there is no GPU runner, when one was wanted.</param>
    public KokoroRunnerChain(IKokoroRunner? gpu, Func<IKokoroRunner> createCpu, string? gpuUnavailableReason = null)
    {
        _gpu = gpu;
        _createCpu = createCpu ?? throw new ArgumentNullException(nameof(createCpu));
        FallbackReason = gpuUnavailableReason;
    }

    /// <summary>Why synthesis is on the CPU although a GPU was wanted; null otherwise.</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>Raised once, when synthesis moves from the GPU to the CPU, with the reason.</summary>
    public event Action<string>? FellBack;

    /// <summary>The runner that will take the next request.</summary>
    public IKokoroRunner Active
    {
        get { lock (_gate) return _gpu ?? (_cpu ??= _createCpu()); }
    }

    public async Task<KokoroRunOutput> RunAsync(IReadOnlyList<int[]> segments, float[,,] voiceStyle, CancellationToken ct)
    {
        IKokoroRunner? gpu;
        lock (_gate) gpu = _gpu;
        if (gpu is not null)
        {
            string reason;
            try
            {
                var output = await gpu.RunAsync(segments, voiceStyle, ct).ConfigureAwait(false);
                if (KokoroOutputCheck.Problem(output.Samples) is not { } problem) return output;
                reason = $"the GPU produced {problem}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                reason = $"GPU synthesis failed: {ex.GetType().Name}: {ex.Message}";
            }
            FallBack(gpu, reason);
        }
        return await Active.RunAsync(segments, voiceStyle, ct).ConfigureAwait(false);
    }

    private void FallBack(IKokoroRunner gpu, string reason)
    {
        bool first;
        lock (_gate)
        {
            first = ReferenceEquals(_gpu, gpu);
            if (first)
            {
                _gpu = null;
                FallbackReason = reason;
            }
        }
        if (!first) return;
        try { gpu.Dispose(); } catch { /* releasing a failed device must not mask the fallback */ }
        FellBack?.Invoke(reason);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _gpu?.Dispose();
            _cpu?.Dispose();
            _gpu = _cpu = null;
        }
    }
}
