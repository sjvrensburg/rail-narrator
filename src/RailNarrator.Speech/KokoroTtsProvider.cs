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
/// upgraded the first time a model actually returns timings. Word-level highlighting is therefore
/// capability-gated rather than assumed.
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

    /// <summary>The graph output that carries per-phoneme durations, when an export provides it.</summary>
    public const string DurationsOutputName = "durations";

    /// <summary>
    /// Upper bound on one synthesis. KokoroSharp gives no awaitable completion, so a worker thread
    /// that dies mid-job would otherwise leave the caller polling forever.
    /// </summary>
    public const int SynthesisTimeoutMs = 120_000;

    private readonly KokoroProviderSettings _settings;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private KokoroEngine? _engine;
    private string? _initError;
    private bool _voicesLoaded;
    private TimingProvenance _observedTiming = TimingProvenance.None;
    private bool _modelReportsDurations;
    private bool _disposed;

    public KokoroTtsProvider(KokoroProviderSettings settings)
        => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

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
            _observedTiming,
            // Kokoro takes a speed input, but changing it re-renders the audio rather than stretching
            // it, and the resulting timing evidence would no longer match a cached clip. Rail
            // Narrator controls rate at playback instead, so this is reported as unsupported.
            SupportsSynthesisRate: false,
            MaxRequestChars,
            voices.Select(v => v.Language ?? "en-US").Where(l => l.Length > 0).Distinct().ToList(),
            Unavailable: null);
    }

    private string ModelRevision =>
        $"kokoro-sharp/{KokoroSharpVersion}:{Path.GetFileName(_settings.ModelPath)}";

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

        var engine = _engine ?? throw new InvalidOperationException("Kokoro engine was not created.");
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
        {
            warnings.Add($"Text exceeded {MaxRequestChars} characters and was truncated for this request.");
            text = text[..MaxRequestChars];
        }

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

        var samples = new List<float>();
        var timestamps = new List<TimedCue>();
        bool sawTimings = false;
        var gate = new object();

        var job = KokoroJob.Create(segments, voice, 1.0f, null);
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
                    if (step.Timestamps is { Length: > 0 } ts)
                    {
                        sawTimings = true;
                        // Timestamps are per-step and relative to that step's raw audio, so they are
                        // shifted by the samples already collected and by the silence trimmed off
                        // this step's start.
                        var (trimmed, leadFrames) = TrimStep(stepSamples);
                        double leadSeconds = leadFrames / (double)OutputSampleRate;
                        foreach (var t in ts)
                            timestamps.Add(PhonemeCue(t, offsetBefore - (trimmed ? leadFrames : 0), leadSeconds));
                    }
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
            engine.EnqueueJob(job);
            await WaitForJobAsync(job, ct).ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }

        ct.ThrowIfCancellationRequested();
        if (job.State == KokoroJobState.Canceled) throw new OperationCanceledException(ct);

        float[] audio;
        bool timingsObserved;
        lock (gate)
        {
            audio = samples.ToArray();
            timingsObserved = sawTimings;
        }
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

    private static (bool Trimmed, int LeadFrames) TrimStep(float[] stepSamples)
    {
        int first = 0;
        while (first < stepSamples.Length && Math.Abs(stepSamples[first]) < 0.005f) first++;
        return first > 0 && first < stepSamples.Length * 0.9 ? (true, first) : (false, 0);
    }

    /// <summary>
    /// Converts one Kokoro phoneme timestamp into a candidate cue.
    /// </summary>
    /// <remarks>
    /// Phonemes are grouped into words on the spaces between them, which is the pairing Kokoro's own
    /// documentation uses. It is emitted at <see cref="CueGranularity.Phrase"/> rather than
    /// <see cref="CueGranularity.Word"/> because phoneme grouping does not establish which source
    /// characters a word came from — that mapping is the plan's job, and it is only exact down to a
    /// source line. Claiming word accuracy here would be a lie the highlight would repeat.
    /// </remarks>
    private static TimedCue PhonemeCue(PhonemeTimestamp t, long sampleOffset, double leadSeconds)
    {
        long start = (long)Math.Max(0, Math.Round((t.StartSecond - leadSeconds) * OutputSampleRate)) + sampleOffset;
        long end = (long)Math.Max(0, Math.Round((t.EndSecond - leadSeconds) * OutputSampleRate)) + sampleOffset;
        return new TimedCue(start, Math.Max(end, start), null, [], CueGranularity.Phrase, null,
            [$"phoneme '{t.Phoneme}'"]);
    }

    private static IReadOnlyList<TimedCue> ShiftCues(List<TimedCue> cues, int trimmedFrames, long totalFrames)
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
    private string? ProbeModel(string modelPath, SessionOptions options)
    {
        try
        {
            using var probe = new InferenceSession(modelPath, options);
            var inputs = probe.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
            var outputs = probe.OutputMetadata.Keys.ToHashSet(StringComparer.Ordinal);

            var missing = RequiredModelInputs.Where(name => !inputs.Contains(name)).ToList();
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
        if (_engine is not null) return null;
        if (_initError is not null) return _initError;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null) return null;
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
                var options = new SessionOptions { EnableMemoryPattern = true };
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

                _engine = new KokoroEngine(_settings.ModelPath, options);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            // Disposal touches native ONNX state; keep it off the caller's thread.
            await Task.Run(engine.Dispose).ConfigureAwait(false);
        }
        _initGate.Dispose();
    }
}
