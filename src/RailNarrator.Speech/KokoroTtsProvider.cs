using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>Configuration for the in-process Kokoro provider.</summary>
/// <param name="ModelPath">Path to a Kokoro ONNX model. Required; the provider never downloads implicitly.</param>
/// <param name="VoicesDirectory">Directory of <c>*.npy</c> voice files. Defaults to the voices shipped inside the KokoroSharp package.</param>
/// <param name="DefaultVoice">Voice used when the caller does not choose one.</param>
/// <param name="IntraOpThreads">ONNX intra-op threads. Explicit because an inherited OMP_NUM_THREADS of 1 would otherwise make synthesis appear five times slower than it is.</param>
/// <param name="TrimLeadingSilence">Remove silence before the first sample so a clip's start is really the start of speech.</param>
/// <param name="SilenceThreshold">Amplitude below which a frame counts as silence.</param>
public sealed record KokoroProviderSettings(
    string ModelPath,
    string? VoicesDirectory = null,
    string DefaultVoice = "af_heart",
    int IntraOpThreads = 0,
    bool TrimLeadingSilence = true,
    float SilenceThreshold = 0.005f)
{
    /// <summary>
    /// KokoroSharp ships every released voice inside its NuGet package under <c>content/voices</c>.
    /// PackageReference does not copy <c>content</c> to the output directory, so the package folder
    /// in the global packages cache is the reliable default location.
    /// </summary>
    public static string? LocatePackageVoices(string packageVersion = "0.8.4")
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? "",
            Path.Combine(home, ".nuget", "packages"),
        ];
        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string dir = Path.Combine(root, "kokorosharp", packageVersion, "content", "voices");
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }
}

/// <summary>
/// In-process Kokoro text-to-speech, via KokoroSharp's ONNX inference engine.
/// </summary>
/// <remarks>
/// <para>
/// Chosen as the default provider because it was measured fastest on the reference machine: RTF
/// 0.20 with a 398 ms cold start, against RTF 0.58–0.92 and a multi-second model load for the
/// ZONOS3 sidecar. It also needs no child process, no HTTP server and no 3.2 GB of GGUF weights,
/// so the local-first offline story is a single 164 MB model file. See ADR-0002.
/// </para>
/// <para>
/// KokoroSharp's own playback path (NAudio on Windows, OpenAL elsewhere) is never used — Rail
/// Narrator plays audio through <see cref="IAudioOutputDevice"/> so the media clock stays under its
/// own control. Those assemblies arrive transitively and stay unloaded.
/// </para>
/// <para>
/// Output is 24 kHz mono. Whether the model can supply per-phoneme durations depends on the export:
/// the widely distributed <c>kokoro-fp16.onnx</c> has no <c>durations</c> output, so
/// <see cref="TtsCapabilities.NativeTiming"/> starts as <see cref="TimingProvenance.None"/> and is
/// upgraded when the graph or a result supplies durations. These phoneme timestamps do not include
/// script-to-word correspondence; usable word cues come from the optional forced aligner.
/// </para>
/// </remarks>
public sealed class KokoroTtsProvider : ITtsProvider
{
    /// <summary>Kokoro's fixed output sample rate.</summary>
    public const int OutputSampleRate = 24000;

    /// <summary>Longest text accepted per request. Longer passages are split by the caller, not here.</summary>
    public const int MaxRequestChars = 1200;

    /// <summary>
    /// Inputs KokoroSharp feeds the graph. An export that does not declare them cannot be used, and
    /// must be rejected before it reaches the engine — see <see cref="ProbeModel"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredModelInputs = ["tokens", "style", "speed"];

    /// <summary>
    /// What an export may call its phoneme-id input: "tokens" in KokoroSharp's, "input_ids" in
    /// onnx-community's (among them the one with a durations output). KokoroSharp's own engine
    /// feeds "tokens" only, so the other is run in a session of this application's.
    /// </summary>
    public static readonly IReadOnlyList<string> TokenInputNames = ["tokens", "input_ids"];

    /// <summary>Bump when how word times are made from durations changes: they are stored with the audio.</summary>
    public const string PhonemeTimingVersion = "durations/1";

    /// <summary>The graph output that carries per-phoneme durations, when an export provides it.</summary>
    public const string DurationsOutputName = "durations";

    /// <summary>Upper bound on one synthesis on the CPU engine. See <see cref="KokoroEngineRunner"/>.</summary>
    public const int SynthesisTimeoutMs = KokoroEngineRunner.SynthesisTimeoutMs;

    private readonly KokoroProviderSettings _settings;
    private readonly KokoroGpuOptions? _gpu;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private KokoroRunnerChain? _chain;
    private SessionOptions? _cpuOptions;
    private string? _initError;
    private bool _voicesLoaded;
    private TimingProvenance _observedTiming = TimingProvenance.None;
    private bool _modelReportsDurations;
    private string? _tokensInput;
    private bool _ownSession;
    private string _modelFingerprint = "unavailable";
    private bool _disposed;

    /// <param name="settings">Model, voices and CPU threading.</param>
    /// <param name="gpu">
    /// Run on a GPU through WebGPU when this is given, the converted model exists and a warm-up clip
    /// comes out as real audio; otherwise, and from the first bad GPU clip, on the CPU. ADR-0007.
    /// </param>
    public KokoroTtsProvider(KokoroProviderSettings settings, KokoroGpuOptions? gpu = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _gpu = gpu;
    }

    /// <summary>"webgpu" or "cpu": where synthesis runs now. "cpu" until initialised.</summary>
    public string Device => _chain?.Active.Device ?? "cpu";

    /// <summary>The GPU's name when synthesis runs on it.</summary>
    public string? GpuDevice => Device == "webgpu" ? _gpu?.DeviceDescription : null;

    /// <summary>Why synthesis is on the CPU although a GPU was configured; null otherwise.</summary>
    public string? GpuFallbackReason => _chain?.FallbackReason;

    /// <summary>Raised once if synthesis moves from the GPU to the CPU, with the reason.</summary>
    public event Action<string>? FellBackToCpu;

    /// <summary>Raised once the model is loaded and the device is settled (see <see cref="Device"/>).</summary>
    public event Action? Initialised;

    public string ProviderId => "kokoro";

    /// <summary>Null until the engine is created; exposed for diagnostics.</summary>
    public string? InitError => _initError;

    /// <summary>
    /// True when the loaded model's graph declares a <c>durations</c> output, i.e. it can supply
    /// native phoneme timings. Determined by probing the model at initialisation rather than waiting
    /// to see whether timings happen to arrive, so capability discovery is accurate up front.
    /// </summary>
    public bool ModelReportsDurations => _modelReportsDurations;

    public async Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        string? error = await EnsureInitialisedAsync(ct).ConfigureAwait(false);
        if (error is not null)
            return new TtsCapabilities(ProviderId, ModelRevision, [], false, TimingProvenance.None, false, 0, [], error);

        var voices = await ListVoicesAsync(ct).ConfigureAwait(false);
        return new TtsCapabilities(
            ProviderId,
            ModelRevision,
            // Float32 is what the model produces; Int16 is offered because conversion is exact for
            // cache files and halves their size. The sample rate is never offered as a choice:
            // resampling belongs to the audio engine, which tracks the mapping.
            [AudioFormat.Mono24000Float32, new AudioFormat(OutputSampleRate, 1, SampleFormat.Int16LittleEndian)],
            SupportsStreaming: false,
            // The timing of the runner in use: the GPU path has its own model, which has no
            // durations, whatever the CPU export beside it declares.
            _chain?.Active is { Device: not "cpu" } ? TimingProvenance.None : _observedTiming,
            // Kokoro takes a speed input, but changing it re-renders the audio rather than stretching
            // it, and the resulting timing evidence would no longer match a cached clip. Rail
            // Narrator controls rate at playback instead, so this is reported as unsupported.
            SupportsSynthesisRate: false,
            MaxRequestChars,
            voices.Select(v => v.Language ?? "en-US").Where(l => l.Length > 0).Distinct().ToList(),
            Unavailable: null);
    }

    /// <summary>
    /// What produced the audio, for the speech-cache key. The CPU form is unchanged from before the
    /// GPU path, so audio already cached stays valid; GPU audio is close to the CPU's but not the
    /// same bytes, so it is cached apart.
    /// </summary>
    private string ModelRevision => _chain?.Active is { Device: not "cpu" } gpu
        ? $"kokoro-sharp/{KokoroSharpVersion}/trim-2/{gpu.Revision}"
        : _ownSession
            // Not KokoroSharp's engine, and its word times are stored with the audio.
            ? $"kokoro-session/trim-2/{PhonemeTimingVersion}:{_modelFingerprint}"
            : $"kokoro-sharp/{KokoroSharpVersion}/trim-2:{_modelFingerprint}";

    private const string KokoroSharpVersion = "0.8.4";

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default)
    {
        string? error = await EnsureInitialisedAsync(ct).ConfigureAwait(false);
        if (error is not null) return [];
        lock (_initGate)
        {
            return KokoroVoiceManager.Voices
                .Select(v => new TtsVoice(
                    v.Name ?? "",
                    v.Name ?? "",
                    LanguageOf(v),
                    GenderOf(v),
                    string.Equals(v.Name, _settings.DefaultVoice, StringComparison.Ordinal)))
                .Where(v => v.Id.Length > 0)
                .OrderByDescending(v => v.IsDefault)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static string? LanguageOf(KokoroVoice v)
    {
        try { return v.Language.ToString(); }
        catch { return null; }
    }

    /// <summary>Kokoro encodes gender in the voice name's second character.</summary>
    private static string? GenderOf(KokoroVoice v)
        => v.Name is { Length: >= 2 } n ? n[1] switch { 'f' => "female", 'm' => "male", _ => null } : null;

    public async Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? error = await EnsureInitialisedAsync(ct).ConfigureAwait(false);
        if (error is not null) throw new InvalidOperationException($"Kokoro is unavailable: {error}");

        var chain = _chain ?? throw new InvalidOperationException("Kokoro engine was not created.");
        KokoroVoice voice;
        lock (_initGate)
        {
            voice = KokoroVoiceManager.GetVoice(request.VoiceId)
                ?? throw new InvalidOperationException($"Unknown Kokoro voice '{request.VoiceId}'.");
        }
        if (request.Format.SampleRate != OutputSampleRate)
            throw new InvalidOperationException(
                $"Kokoro synthesises at {OutputSampleRate} Hz; the caller asked for {request.Format.SampleRate} Hz. Resampling belongs to the audio engine.");
        if (request.Speed is < 0.99 or > 1.01)
            throw new InvalidOperationException("Kokoro synthesis rate changes are disabled; use playback rate instead.");

        string text = request.Text.Trim();
        if (text.Length == 0)
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, request.Format, 0, [], TimingProvenance.None, request.Seed, null, ["Empty text."]);

        var warnings = new List<string>();
        if (text.Length > MaxRequestChars)
            throw new ArgumentException($"Text exceeds the {MaxRequestChars}-character limit; split it before synthesis.", nameof(request));

        int[] tokens;
        List<int[]> segments;
        try
        {
            tokens = Tokenizer.Tokenize(text, voice.GetLangCode(), true);
            var pipeline = new KokoroTTSPipelineConfig(new DefaultSegmentationConfig());
            segments = pipeline.SegmentationFunc(tokens);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Kokoro phonemisation failed: {ex.Message}", ex);
        }
        if (segments.Count == 0 || segments.All(s => s.Length == 0))
            throw new InvalidOperationException("Kokoro produced no phonemes for this text; it may be entirely unpunctuated symbols.");

        var output = await chain.RunAsync(segments, voice.Features, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        float[] audio = output.Samples;
        var timestamps = output.Timestamps.ToList();

        // Word times from the model's own durations, when it reports them: each word of the script
        // counted off against the phoneme string's words. Replaces the bare phoneme cues, which
        // carry no script range and so could never be shown.
        if (output.Phonemes.Count > 0 && request.RequestTimings)
        {
            string language = voice.GetLangCode();
            var words = PhonemeWordTiming.Words(text, output.Phonemes, part => Tokenizer.Phonemize(part, language, true), out string? problem);
            if (words is null) warnings.Add($"No word timing from the model's durations: {problem}.");
            int lead = request.Text.Length - request.Text.TrimStart().Length;
            timestamps = words is null ? [] : words.Select(c => c with { SpokenRange = new TextRange(c.SpokenRange!.Value.Start + lead, c.SpokenRange.Value.Length) }).ToList();
        }
        else if (output.Phonemes.Count > 0) timestamps = [];
        bool timingsObserved = timestamps.Count > 0;
        if (timingsObserved) _observedTiming = TimingProvenance.NativeDuration;

        if (audio.Length == 0)
            throw new InvalidOperationException("Kokoro returned no samples. The model may have failed to load or the text may be unspeakable.");

        // Trim leading silence so the clip's first frame is the start of speech, which is what makes
        // a clip-boundary cue line up with what the listener hears.
        int trimmedFrames = 0;
        if (_settings.TrimLeadingSilence)
        {
            int first = 0;
            while (first < audio.Length && Math.Abs(audio[first]) < _settings.SilenceThreshold) first++;
            // Never trim the whole clip: a very quiet passage would otherwise vanish.
            if (first > 0 && first < audio.Length * 0.9)
            {
                trimmedFrames = first;
                audio = audio[trimmedFrames..];
            }
        }

        var targetFormat = new AudioFormat(OutputSampleRate, request.Format.Channels <= 0 ? 1 : request.Format.Channels, request.Format.SampleFormat);
        byte[] pcm = ToPcm(audio, targetFormat.SampleFormat);

        IReadOnlyList<TimedCue> cues = timingsObserved
            ? ShiftCues(timestamps, trimmedFrames, audio.LongLength)
            : [];

        return new TtsSynthesisResult(
            pcm, targetFormat, audio.LongLength, cues,
            cues.Count > 0 ? TimingProvenance.NativeDuration : TimingProvenance.None,
            request.Seed, "kokoro-onnx", warnings);
    }

    /// <summary>Raw phoneme evidence; a word mapper or forced aligner must attach script ranges.</summary>
    public static TimedCue PhonemeCue(PhonemeTimestamp t, long sampleOffset)
    {
        long start = (long)Math.Round(t.StartSecond * OutputSampleRate) + sampleOffset;
        long end = (long)Math.Round(t.EndSecond * OutputSampleRate) + sampleOffset;
        return new TimedCue(start, end, null, [], CueGranularity.Phrase, null, [$"phoneme '{t.Phoneme}'"]);
    }

    public static IReadOnlyList<TimedCue> ShiftCues(List<TimedCue> cues, int trimmedFrames, long totalFrames)
    {
        var result = new List<TimedCue>(cues.Count);
        foreach (var c in cues)
        {
            long start = Math.Clamp(c.StartSample - trimmedFrames, 0, totalFrames);
            long end = Math.Clamp(c.EndSample - trimmedFrames, 0, totalFrames);
            if (end <= start) continue;
            result.Add(c with { StartSample = start, EndSample = end });
        }
        return result;
    }

    private static byte[] ToPcm(float[] samples, SampleFormat format)
    {
        int stride = Pcm.BytesPerSample(format);
        var buffer = new byte[samples.Length * stride];
        for (int i = 0; i < samples.Length; i++) Pcm.WriteSample(buffer, i * stride, format, samples[i]);
        return buffer;
    }

    /// <summary>
    /// Opens the model once to check it is an export this runtime can actually drive.
    /// </summary>
    /// <remarks>
    /// This is not defensive padding; it prevents a process crash. KokoroSharp runs inference on its
    /// own dispatch thread and does not catch ONNX errors, so feeding it an incompatible export
    /// surfaces as an unhandled exception on a background thread and takes the whole application
    /// down. Measured with <c>kokoro-v1.0.onnx</c> (the kokoro-onnx export): it loads fine, reports
    /// itself available, and then aborts the process with
    /// <c>Input name: 'tokens' is not in the metadata</c> on the first synthesis. A reader who
    /// downloaded the wrong file would lose the application rather than see an error.
    /// <para>
    /// The probe costs one extra model load at startup and is disposed before the engine is created.
    /// Reading the output metadata also settles whether the model can supply phoneme durations, which
    /// is what makes word-level highlighting capability-gated on evidence rather than on hope.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Kokoro's sessions report errors only. At the default level ONNX Runtime printed a screenful
    /// at every start: it cannot constant-fold the fp16 model's Reciprocal nodes on the CPU, and it
    /// says where it placed nodes. Neither is actionable; a session that cannot be built still
    /// throws, and an output that is wrong is caught by KokoroOutputCheck.
    /// </summary>
    internal const OrtLoggingLevel QuietSessionLog = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;

    private string? ProbeModel(string modelPath, SessionOptions options)
    {
        try
        {
            _modelFingerprint = CacheKey.Sha256HexOfFile(modelPath);
            using var probe = new InferenceSession(modelPath, options);
            var inputs = probe.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
            var outputs = probe.OutputMetadata.Keys.ToHashSet(StringComparer.Ordinal);

            // Either name for the phoneme ids; an export with the other one is run by KokoroSessionRunner.
            _tokensInput = TokenInputNames.FirstOrDefault(inputs.Contains);
            var missing = RequiredModelInputs.Where(name => name == "tokens" ? _tokensInput is null : !inputs.Contains(name)).ToList();
            if (missing.Count > 0)
                return $"This ONNX model is not a KokoroSharp-compatible Kokoro export: it declares no " +
                       $"'{string.Join("', '", missing)}' input (its inputs are: " +
                       $"{(inputs.Count == 0 ? "none" : string.Join(", ", inputs.OrderBy(i => i, StringComparer.Ordinal)))}). " +
                       "Use kokoro-fp16.onnx from the KokoroSharpBinaries release. Exports built for " +
                       "kokoro-onnx name their tensors differently and cannot be driven by this runtime.";

            _modelReportsDurations = outputs.Contains(DurationsOutputName);
            if (_modelReportsDurations) _observedTiming = TimingProvenance.NativeDuration;
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not open the Kokoro model at '{modelPath}': {ex.GetType().Name}: {ex.Message}";
        }
    }

    public async Task<ProviderSelfTestResult> SelfTestAsync(string voiceId, CancellationToken ct = default)
    {
        var start = Environment.TickCount64;
        try
        {
            var caps = await GetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (!caps.IsAvailable)
                return new ProviderSelfTestResult(false, 0, 0, Environment.TickCount64 - start, caps.Unavailable);

            var result = await SynthesizeAsync(new TtsSynthesisRequest(
                "Testing one, two, three.", voiceId, "en-US", AudioFormat.Mono24000Float32), ct).ConfigureAwait(false);

            float peak = Pcm.Peak(result.Pcm.Span, result.Format);
            string? detail = result.IsSilentOrEmpty ? "The model produced no samples."
                : peak < ProviderSelfTestResult.SilencePeakThreshold ? $"Audio is effectively silent (peak {peak:F5})."
                : null;
            return new ProviderSelfTestResult(detail is null, result.DurationSeconds, peak,
                Environment.TickCount64 - start, detail);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ProviderSelfTestResult(false, 0, 0, Environment.TickCount64 - start, ex.Message);
        }
    }

    private async Task<string?> EnsureInitialisedAsync(CancellationToken ct)
    {
        if (_chain is not null) return null;
        if (_initError is not null) return _initError;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_chain is not null) return null;
            if (_initError is not null) return _initError;

            if (!File.Exists(_settings.ModelPath))
            {
                _initError = $"Kokoro model not found at '{_settings.ModelPath}'. Download it before use; see docs/model-manifest.json.";
                return _initError;
            }

            string? voicesDir = _settings.VoicesDirectory ?? KokoroProviderSettings.LocatePackageVoices();
            if (voicesDir is null || !Directory.Exists(voicesDir))
            {
                _initError = "No Kokoro voice files were found. Set VoicesDirectory to a folder of .npy voices.";
                return _initError;
            }

            try
            {
                if (!_voicesLoaded)
                {
                    KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
                    _voicesLoaded = true;
                }
                if (KokoroVoiceManager.Voices.Count == 0)
                {
                    _initError = $"No voices were loaded from '{voicesDir}'.";
                    return _initError;
                }

                // Session options are set explicitly rather than inherited from the environment: an
                // ambient OMP_NUM_THREADS of 1 makes synthesis several times slower and the cause is
                // invisible from inside the process.
                var options = new SessionOptions { EnableMemoryPattern = true, LogSeverityLevel = QuietSessionLog };
                int threads = _settings.IntraOpThreads > 0
                    ? _settings.IntraOpThreads
                    : Math.Max(1, Math.Min(8, Environment.ProcessorCount));
                options.IntraOpNumThreads = threads;
                options.InterOpNumThreads = Math.Max(1, threads / 2);

                // Validate the graph before handing it to KokoroSharp. See ProbeModel.
                string? probeError = ProbeModel(_settings.ModelPath, options);
                if (probeError is not null)
                {
                    _initError = probeError;
                    return _initError;
                }

                _cpuOptions = options;
                var (gpuRunner, gpuReason) = await TryCreateGpuRunnerAsync(ct).ConfigureAwait(false);
                // KokoroSharp's engine for the export it was made for, unchanged so that its cached
                // audio stays valid. An export it cannot feed ("input_ids"), or one with a durations
                // output, which its engine does not read, runs in a session of our own on the CPU.
                bool ownSession = _ownSession = _tokensInput != "tokens" || _modelReportsDurations;
                var chain = new KokoroRunnerChain(gpuRunner,
                    () => ownSession
                        ? new KokoroSessionRunner(_settings.ModelPath, _cpuOptions!, "cpu", $"trim-2/{PhonemeTimingVersion}:{_modelFingerprint}")
                        : new KokoroEngineRunner(_settings.ModelPath, _cpuOptions!,
                            $"kokoro-sharp/{KokoroSharpVersion}/trim-2:{_modelFingerprint}"),
                    gpuReason);
                chain.FellBack += reason => FellBackToCpu?.Invoke(reason);
                if (gpuRunner is null) _ = chain.Active;   // build the CPU engine now, as before
                _chain = chain;
                Initialised?.Invoke();
                return null;
            }
            catch (Exception ex)
            {
                _initError = $"Failed to initialise Kokoro: {ex.Message}";
                return _initError;
            }
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Builds the WebGPU session and proves it with a warm-up clip, or says why not. The warm-up
    /// also compiles the GPU's shaders (several seconds), so the first real passage does not pay it.
    /// </summary>
    private async Task<(IKokoroRunner? Runner, string? Reason)> TryCreateGpuRunnerAsync(CancellationToken ct)
    {
        if (_gpu is null) return (null, null);
        if (!File.Exists(_gpu.ModelPath))
            return (null, $"the WebGPU Kokoro model is not at '{_gpu.ModelPath}' (make it with tools/kokoro-webgpu-model.sh)");
        KokoroSessionRunner? runner = null;
        try
        {
            var options = new SessionOptions { EnableMemoryPattern = true, LogSeverityLevel = QuietSessionLog };
            _gpu.ConfigureSession(options);
            string fingerprint = await Task.Run(() => CacheKey.Sha256HexOfFile(_gpu.ModelPath), ct).ConfigureAwait(false);
            runner = await Task.Run(() => new KokoroSessionRunner(_gpu.ModelPath, options, "webgpu", fingerprint), ct).ConfigureAwait(false);
            if (!runner.TakesInt32Tokens)
                throw new InvalidOperationException("the model still takes int64 tokens, which the WebGPU plugin cannot accept; convert it with tools/kokoro-webgpu-model.sh");

            var voice = KokoroVoiceManager.GetVoice(_settings.DefaultVoice) ?? KokoroVoiceManager.Voices[0];
            int[] tokens = Tokenizer.Tokenize("Ready to read.", voice.GetLangCode(), true);
            var warmUp = await runner.RunAsync([tokens], voice.Features, ct).ConfigureAwait(false);
            if (KokoroOutputCheck.Problem(warmUp.Samples) is { } problem)
                throw new InvalidOperationException($"the warm-up clip was unusable: {problem}");
            return (runner, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            runner?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            runner?.Dispose();
            return (null, $"Kokoro on the GPU is unavailable, using the CPU: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var chain = _chain;
        _chain = null;
        if (chain is not null)
        {
            // Disposal touches native ONNX state; keep it off the caller's thread.
            await Task.Run(chain.Dispose).ConfigureAwait(false);
        }
        _initGate.Dispose();
    }
}
