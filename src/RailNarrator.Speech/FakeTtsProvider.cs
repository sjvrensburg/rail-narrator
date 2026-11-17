using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>
/// A deterministic speech provider for tests and offline development.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic tests must run without a GPU, a network, a sidecar or a 164 MB model, so the whole
/// narration pipeline — planning, lookahead, cancellation, cue resolution, playback state — is
/// exercised against this. It is not a stub that returns success: it produces real PCM with a
/// reproducible waveform, a duration derived from the text, and optionally real timing cues, so
/// downstream code has something meaningful to check.
/// </para>
/// <para>
/// It can also be told to misbehave on purpose — return silence, fail, stall, or emit timings that
/// violate monotonicity or cite unknown anchors — because those are exactly the cases the validator
/// and the coordinator must survive.
/// </para>
/// </remarks>
public sealed class FakeTtsProvider : ITtsProvider
{
    /// <summary>Sample rate the fake produces. Distinct from the real providers' so a test cannot accidentally pass by matching a hard-coded rate.</summary>
    public const int DefaultSampleRate = 24000;

    /// <summary>Approximate speaking rate used to size a clip: characters per second of audio.</summary>
    public const double CharsPerSecond = 15.0;

    private readonly FakeTtsOptions _options;
    private readonly List<TtsSynthesisRequest> _requests = [];
    private int _synthesizeCount;
    private bool _disposed;

    public FakeTtsProvider(FakeTtsOptions? options = null) => _options = options ?? new FakeTtsOptions();

    public string ProviderId => _options.ProviderId;

    /// <summary>Every request received, so a test can assert what was actually asked for.</summary>
    public IReadOnlyList<TtsSynthesisRequest> Requests { get { lock (_requests) return _requests.ToList(); } }

    /// <summary>Number of completed synthesis calls.</summary>
    public int SynthesizeCount => Volatile.Read(ref _synthesizeCount);

    public Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        if (_options.UnavailableReason is { } reason)
            return Task.FromResult(new TtsCapabilities(ProviderId, _options.ModelRevision, [], false, TimingProvenance.None, false, 0, [], reason));

        return Task.FromResult(new TtsCapabilities(
            ProviderId, _options.ModelRevision,
            [new AudioFormat(_options.SampleRate, 1, SampleFormat.Float32LittleEndian),
             new AudioFormat(_options.SampleRate, 1, SampleFormat.Int16LittleEndian)],
            _options.SupportsStreaming,
            _options.EmitTimings ? TimingProvenance.NativeDuration : TimingProvenance.None,
            SupportsSynthesisRate: false,
            _options.MaxTextChars,
            ["en-GB"],
            Unavailable: null));
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TtsVoice>>(_options.VoiceList.Count > 0
            ? _options.VoiceList
            : [new TtsVoice("fake-default", "Fake Default Voice", "en-GB", "neutral", true),
               new TtsVoice("fake-alternate", "Fake Alternate Voice", "en-GB", "neutral", false)]);

    public async Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_requests) _requests.Add(request);

        if (_options.Latency is { } latency && latency > TimeSpan.Zero) await Task.Delay(latency, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (_options.FailWith is { } failure) throw new InvalidOperationException(failure);

        var format = new AudioFormat(
            request.Format.SampleRate > 0 ? request.Format.SampleRate : _options.SampleRate,
            1,
            request.Format.SampleFormat == SampleFormat.Int16LittleEndian
                ? SampleFormat.Int16LittleEndian
                : SampleFormat.Float32LittleEndian);

        // Duration is a function of the text, so two calls with the same text produce the same clip
        // length — which is what makes cue arithmetic reproducible in tests.
        long frames = Math.Max(1, (long)Math.Round(request.Text.Length / CharsPerSecond * format.SampleRate));
        if (_options.FixedFrameCount is { } fixedFrames) frames = fixedFrames;

        byte[] pcm;
        if (_options.ReturnSilence)
        {
            pcm = new byte[frames * format.BytesPerFrame];
        }
        else
        {
            pcm = new byte[frames * format.BytesPerFrame];
            int stride = format.BytesPerFrame;
            for (long i = 0; i < frames; i++)
            {
                // A reproducible waveform seeded by the text, so identical input yields identical bytes.
                double phase = 2 * Math.PI * 220 * i / format.SampleRate;
                float sample = (float)(0.25 * Math.Sin(phase) * Envelope(i, frames));
                Pcm.WriteSample(pcm, (int)(i * stride), format.SampleFormat, sample);
            }
        }

        var warnings = new List<string>();
        if (request.Text.Length > _options.MaxTextChars && _options.MaxTextChars > 0)
            warnings.Add($"Text exceeded the fake provider's {_options.MaxTextChars}-character limit.");

        IReadOnlyList<TimedCue> timings = [];
        TimingProvenance provenance = TimingProvenance.None;
        if (_options.EmitTimings)
        {
            timings = _options.TimingFactory is { } factory
                ? factory(request.Text, frames)
                : EvenPhraseTimings(request.Text, frames);
            provenance = TimingProvenance.NativeDuration;
        }

        Interlocked.Increment(ref _synthesizeCount);
        return new TtsSynthesisResult(pcm, format, frames, timings, provenance, request.Seed, "fake", warnings);
    }

    /// <summary>
    /// Splits the text into phrases at punctuation and spaces and spreads them evenly over the clip.
    /// Uniform spacing is honest here because it is labelled <see cref="TimingProvenance.NativeDuration"/>
    /// only in a fake provider, and the validator still checks it. It is never a model of real
    /// alignment: real word timings must come from durations or an aligner.
    /// </summary>
    public static IReadOnlyList<TimedCue> EvenPhraseTimings(string text, long frames)
    {
        var cues = new List<TimedCue>();
        if (string.IsNullOrWhiteSpace(text) || frames <= 0) return cues;

        var phrases = new List<TextRange>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is ',' or ';' or '.' or '!' or '?' or ' ' && i > start)
            {
                phrases.Add(TextRange.FromBounds(start, i));
                start = i + 1;
            }
        }
        if (start < text.Length) phrases.Add(TextRange.FromBounds(start, text.Length));
        phrases = phrases.Where(p => p.Length > 0).ToList();
        if (phrases.Count == 0) return cues;

        long per = frames / phrases.Count;
        for (int i = 0; i < phrases.Count; i++)
        {
            long s = i * per;
            long e = i == phrases.Count - 1 ? frames : (i + 1) * per;
            if (e <= s) continue;
            cues.Add(new TimedCue(s, e, phrases[i], [], CueGranularity.Phrase, 0.9, []));
        }
        return cues;
    }

    /// <summary>A fade at both ends, so silence trimming has something real to trim.</summary>
    private static double Envelope(long i, long frames)
    {
        int fade = (int)(frames * 0.05);
        if (fade <= 0) return 1;
        if (i < fade) return i / (double)fade;
        if (i > frames - fade) return Math.Max(0, (frames - i) / (double)fade);
        return 1;
    }

    public Task<ProviderSelfTestResult> SelfTestAsync(string voiceId, CancellationToken ct = default)
        => Task.FromResult(_options.UnavailableReason is { } reason
            ? new ProviderSelfTestResult(false, 0, 0, 0, reason)
            : _options.ReturnSilence
                ? new ProviderSelfTestResult(false, 1.0, 0.0, 1, "Fake provider returned silence.")
                : new ProviderSelfTestResult(true, 1.0, 0.25, 1, null));

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>True once disposed, so a test can assert the coordinator released the provider.</summary>
    public bool IsDisposed => _disposed;
}

/// <summary>Knobs for <see cref="FakeTtsProvider"/>.</summary>
/// <param name="ProviderId">Reported provider id.</param>
/// <param name="ModelRevision">Reported model revision. Changing it must invalidate cached speech.</param>
/// <param name="SampleRate">Rate of generated audio.</param>
/// <param name="MaxTextChars">Advertised request limit.</param>
/// <param name="Voices">Voice list, or empty for the built-in two.</param>
/// <param name="EmitTimings">Whether to produce timing cues.</param>
/// <param name="TimingFactory">Custom cue generator, for tests that need malformed timings.</param>
/// <param name="ReturnSilence">Produce an all-zero buffer, to exercise silent-decoder handling.</param>
/// <param name="FailWith">Throw this message instead of synthesising.</param>
/// <param name="Latency">Artificial delay per request, to exercise lookahead and cancellation.</param>
/// <param name="FixedFrameCount">Force an exact clip length, for deterministic cue arithmetic.</param>
/// <param name="SupportsStreaming">Advertise streaming support.</param>
/// <param name="UnavailableReason">Report the provider as unavailable.</param>
public sealed record FakeTtsOptions(
    string ProviderId = "fake",
    string ModelRevision = "fake/1",
    int SampleRate = FakeTtsProvider.DefaultSampleRate,
    int MaxTextChars = 1200,
    IReadOnlyList<TtsVoice>? Voices = null,
    bool EmitTimings = false,
    Func<string, long, IReadOnlyList<TimedCue>>? TimingFactory = null,
    bool ReturnSilence = false,
    string? FailWith = null,
    TimeSpan? Latency = null,
    long? FixedFrameCount = null,
    bool SupportsStreaming = false,
    string? UnavailableReason = null)
{
    public IReadOnlyList<TtsVoice> VoiceList => Voices ?? [];
}
