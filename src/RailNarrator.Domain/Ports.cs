namespace RailNarrator.Domain;

/// <summary>
/// What a speech provider can actually do. Discovered, never assumed.
/// </summary>
/// <param name="ProviderId">Stable identifier used in cache keys and diagnostics.</param>
/// <param name="ModelRevision">Revision of the underlying model. A change invalidates cached speech.</param>
/// <param name="Formats">Sample formats the provider can return. A caller must negotiate one, not guess.</param>
/// <param name="SupportsStreaming">True when incremental delivery is implemented. Raw HTTP chunk boundaries are never treated as frame or word boundaries.</param>
/// <param name="NativeTiming">What timing evidence the provider can supply, if any.</param>
/// <param name="SupportsSynthesisRate">
/// True when the provider can synthesise at a different speaking rate. Distinct from playback rate:
/// a synthesis-rate change re-renders the audio, a playback-rate change time-stretches what exists.
/// A provider that rejects a rate field must report false here rather than silently ignoring it.
/// </param>
/// <param name="MaxTextChars">Longest text the provider accepts in one request. Longer segments are split by the caller.</param>
/// <param name="Languages">Languages the provider claims. Claimed support is not evidence of correct reading order or RTL handling.</param>
/// <param name="Unavailable">Human-readable reason the provider cannot be used, or null when it can.</param>
public sealed record TtsCapabilities(
    string ProviderId,
    string ModelRevision,
    IReadOnlyList<AudioFormat> Formats,
    bool SupportsStreaming,
    TimingProvenance NativeTiming,
    bool SupportsSynthesisRate,
    int MaxTextChars,
    IReadOnlyList<string> Languages,
    string? Unavailable)
{
    public bool IsAvailable => Unavailable is null;

    /// <summary>True when the provider can return this exact format.</summary>
    public bool Supports(AudioFormat format) => Formats.Contains(format);

    /// <summary>The preferred format: highest rate first, then float, then fewest channels.</summary>
    public AudioFormat? PreferredFormat => Formats
        .OrderByDescending(f => f.SampleRate)
        .ThenByDescending(f => f.SampleFormat == SampleFormat.Float32LittleEndian)
        .ThenBy(f => f.Channels)
        .FirstOrDefault();

    /// <summary>True when the provider can supply word-level timing evidence itself.</summary>
    public bool CanSupplyWordTiming => NativeTiming is TimingProvenance.NativeDuration or TimingProvenance.ForcedAlignment;
}

/// <summary>A voice a provider can synthesise with.</summary>
/// <param name="Id">Provider-specific identifier passed back in a request.</param>
/// <param name="DisplayName">Shown in the voice picker.</param>
/// <param name="Language">BCP-47, when the provider reports one.</param>
/// <param name="Gender">Provider-reported; may be null.</param>
/// <param name="IsDefault">The provider's own default. Used when the user has not chosen.</param>
public sealed record TtsVoice(string Id, string DisplayName, string? Language, string? Gender, bool IsDefault);

/// <summary>
/// One synthesis request.
/// </summary>
/// <param name="Text">The exact script to speak. Alignment is against this string.</param>
/// <param name="VoiceId">A voice from <see cref="ITtsProvider.ListVoicesAsync"/>.</param>
/// <param name="Language">BCP-47.</param>
/// <param name="Format">The negotiated output format.</param>
/// <param name="Speed">
/// Synthesis speaking rate, 1.0 normal. Only meaningful when
/// <see cref="TtsCapabilities.SupportsSynthesisRate"/>; a provider that cannot honour it must fail
/// the request rather than ignore the field.
/// </param>
/// <param name="Seed">Deterministic seed where supported, so a retry reproduces a take. Null lets the provider choose.</param>
/// <param name="RequestTimings">Ask for whatever timing evidence the provider has.</param>
public sealed record TtsSynthesisRequest(
    string Text,
    string VoiceId,
    string Language,
    AudioFormat Format,
    double Speed = 1.0,
    long? Seed = null,
    bool RequestTimings = true);

/// <summary>
/// The audio a provider returned, with truthful metadata about it.
/// </summary>
/// <param name="Pcm">Raw samples in <paramref name="Format"/>. Contiguous, no header.</param>
/// <param name="Format">What the bytes actually are. Verified against the request; a provider that resampled says so here.</param>
/// <param name="FrameCount">Real frame count derived from <see cref="Pcm"/>'s length, not from a header the provider sent.</param>
/// <param name="Timings">Timing evidence in this clip's own timebase, empty when there is none.</param>
/// <param name="TimingProvenance">How <paramref name="Timings"/> were produced.</param>
/// <param name="SeedUsed">The seed actually used, when the provider reports one. Needed to reproduce a take.</param>
/// <param name="DecoderId">Provider-side decoder or model variant, when it reports one.</param>
/// <param name="Warnings">Non-fatal problems, e.g. text that was truncated to the provider's limit.</param>
public sealed record TtsSynthesisResult(
    ReadOnlyMemory<byte> Pcm,
    AudioFormat Format,
    long FrameCount,
    IReadOnlyList<TimedCue> Timings,
    TimingProvenance TimingProvenance,
    long? SeedUsed,
    string? DecoderId,
    IReadOnlyList<string> Warnings)
{
    public double DurationSeconds => Format.ToSeconds(FrameCount);

    /// <summary>True when the provider returned no audible samples. Treated as a failure, not as silence to play.</summary>
    public bool IsSilentOrEmpty => FrameCount <= 0;
}

/// <summary>
/// A text-to-speech provider. Asynchronous throughout, cancellable, and honest about its
/// capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Implementations exist for an in-process Kokoro runtime, a ZONOS3 sidecar over HTTP, and a
/// deterministic fake for tests. Nothing here may block a caller's thread: synthesis is the slowest
/// step in the pipeline and runs under bounded lookahead.
/// </para>
/// <para>
/// A provider must not be listed in the UI unless it is implemented. An inert selector implying a
/// backend works is worse than no selector.
/// </para>
/// </remarks>
public interface ITtsProvider : IAsyncDisposable
{
    /// <summary>Stable identifier, used in cache keys.</summary>
    string ProviderId { get; }

    /// <summary>Probes the runtime and reports what it can do. Must not throw for an absent runtime; report it in <see cref="TtsCapabilities.Unavailable"/>.</summary>
    Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>The voices that can actually be selected right now.</summary>
    Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Synthesises one clip. Returns complete PCM; a streaming provider may fill it incrementally
    /// but must not expose chunk boundaries as frame or word boundaries.
    /// </summary>
    Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct = default);

    /// <summary>
    /// Synthesises a short probe utterance and reports whether it was audible. Called before
    /// document playback is enabled, so a missing model or a silent decoder is found here rather
    /// than as an inaudible reading session.
    /// </summary>
    Task<ProviderSelfTestResult> SelfTestAsync(string voiceId, CancellationToken ct = default);
}

/// <summary>Outcome of a provider self-test.</summary>
/// <param name="Succeeded">Audio came back and was not silent.</param>
/// <param name="DurationSeconds">Length of the probe audio.</param>
/// <param name="PeakAmplitude">Peak absolute sample value. A near-zero peak means the decoder produced silence.</param>
/// <param name="ElapsedMilliseconds">Wall time, for a first-audio-latency measurement.</param>
/// <param name="Detail">Failure reason or a short note. Must not contain document text or credentials.</param>
public sealed record ProviderSelfTestResult(bool Succeeded, double DurationSeconds, double PeakAmplitude, long ElapsedMilliseconds, string? Detail)
{
    /// <summary>Below this peak the audio is effectively silence even though a buffer came back.</summary>
    public const double SilencePeakThreshold = 0.005;

    public bool IsEffectivelySilent => PeakAmplitude < SilencePeakThreshold;
}

/// <summary>An audio output device as the engine sees it.</summary>
/// <param name="Id">Stable identifier for selection and persistence.</param>
/// <param name="Name">Human-readable.</param>
/// <param name="IsDefault">The system default at enumeration time.</param>
/// <param name="PreferredSampleRate">The rate the device runs at natively. Opening at another rate can fail outright.</param>
/// <param name="Channels">Output channel count.</param>
/// <param name="OutputLatencySeconds">Reported latency, or null when the backend cannot report it.</param>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    int PreferredSampleRate,
    int Channels,
    double? OutputLatencySeconds);

/// <summary>
/// The audio output boundary. Implemented by a PortAudio device on Linux and by a controllable
/// fake in tests.
/// </summary>
/// <remarks>
/// <para>
/// The contract that matters is <see cref="PresentedFrames"/>: the number of frames the device has
/// actually consumed, not the number written to it and not the wall-clock time since playback
/// began. Highlights are driven from this, so a clock that ran ahead of the audio would show the
/// next sentence while the current one was still being spoken.
/// </para>
/// <para>
/// Opening at the device's <see cref="AudioDeviceInfo.PreferredSampleRate"/> is the caller's job.
/// Backends that do not resample reject other rates outright — measured on the reference machine,
/// where PortAudio's ALSA host returns <c>paInvalidSampleRate</c> for a 24 kHz stream on a 44.1 kHz
/// device — so the engine resamples and keeps the mapping.
/// </para>
/// </remarks>
public interface IAudioOutputDevice : IDisposable
{
    /// <summary>Devices available right now. Enumerating must not throw when there is no audio server.</summary>
    IReadOnlyList<AudioDeviceInfo> Enumerate();

    /// <summary>Opens the device for playback. Fails loudly rather than silently producing no sound.</summary>
    void Open(string deviceId, AudioFormat format);

    void Start();

    /// <summary>Stops presentation but keeps the position, so playback can resume.</summary>
    void Pause();

    /// <summary>Discards queued audio immediately. Used for seek and for cancelling a stale generation.</summary>
    void Flush();

    void Close();

    /// <summary>True between <see cref="Start"/> and the end of the last drain.</summary>
    bool IsRunning { get; }

    /// <summary>Frames the device has consumed. The media clock's only legitimate input.</summary>
    long PresentedFrames { get; }

    /// <summary>Frames written but not yet presented. Used to detect an underflow and to correct latency.</summary>
    long QueuedFrames { get; }

    /// <summary>
    /// Latency between a frame being presented by this object and reaching the listener, where the
    /// backend can report it. Null means unknown, and the caller must document the residual error
    /// rather than assume zero.
    /// </summary>
    double? ReportedLatencySeconds { get; }

    /// <summary>
    /// Enqueues samples. Blocks or returns a short count when the device buffer is full, which is
    /// how backpressure reaches the engine.
    /// </summary>
    /// <returns>Frames actually accepted.</returns>
    int Write(ReadOnlySpan<byte> pcm, int frames);

    /// <summary>True when everything written has been presented. Gate end-of-document on this.</summary>
    bool HasDrained { get; }

    /// <summary>Raised when the device fails during playback, e.g. unplugged or taken over.</summary>
    event Action<string>? DeviceFailed;
}

/// <summary>
/// The playback position, in a declared timebase. One implementation is authoritative; the camera
/// and the overlays subscribe to it and never run their own reading clocks.
/// </summary>
public interface IMediaClock
{
    /// <summary>Current position in source-audio frames.</summary>
    long PositionSamples { get; }

    /// <summary>Current position in seconds of source audio, i.e. after any playback-rate mapping.</summary>
    double PositionSeconds { get; }

    /// <summary>True when the clock is advancing. False while paused, seeking or frozen by an underflow.</summary>
    bool IsAdvancing { get; }

    /// <summary>The timebase <see cref="PositionSamples"/> is counted in.</summary>
    AudioTimebase Timebase { get; }
}

/// <summary>A monotonic clock, injectable so timing logic is testable without waiting.</summary>
public interface IMonotonicClock
{
    /// <summary>Seconds from an arbitrary fixed origin. Must never go backwards.</summary>
    double NowSeconds { get; }
}

/// <summary>
/// Recognises a mathematical region and returns an inspectable representation of it. Stage one of
/// the two-stage maths path.
/// </summary>
/// <remarks>
/// RailReaderCore's <c>IVlmService.DescribeBlockAsync</c> with <c>BlockAction.LaTeX</c> already does
/// crop-to-LaTeX and is reused as the transport. What Core does <b>not</b> do is source-linked
/// narration planning, so that lives in <see cref="IMathVerbaliser"/> rather than in a changed
/// meaning for an existing Core API.
/// </remarks>
public interface IMathTranscriber
{
    /// <summary>True when a vision endpoint is configured and reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Transcribes a high-resolution crop, optionally seeded with the text layer beneath it.</summary>
    Task<MathTranscriptionResult> TranscribeAsync(MathTranscriptionRequest request, CancellationToken ct = default);
}

/// <summary>A crop to transcribe.</summary>
/// <param name="PngBytes">High-resolution crop. Resolution matters: a low-DPI crop loses sub- and superscripts.</param>
/// <param name="SourceTextHint">Text layer under the crop, when there is one. A hint, never the answer.</param>
/// <param name="ContextBefore">Bounded preceding text.</param>
/// <param name="ContextAfter">Bounded following text.</param>
/// <param name="AnchorIds">Ids the transcription may cite. Anything else is rejected.</param>
public sealed record MathTranscriptionRequest(
    ReadOnlyMemory<byte> PngBytes,
    string? SourceTextHint,
    string ContextBefore,
    string ContextAfter,
    IReadOnlyList<string> AnchorIds);

/// <summary>Outcome of a transcription.</summary>
/// <param name="Representation">LaTeX or another inspectable form. Null on failure.</param>
/// <param name="Confidence">The model's own estimate. Not proof the expression was recognised correctly.</param>
/// <param name="Warnings">Ambiguities the model reported, e.g. an unclear subscript.</param>
/// <param name="Error">Failure reason, or null on success.</param>
/// <param name="CropReference">Where the crop came from, so the review panel can show the original.</param>
public sealed record MathTranscriptionResult(
    string? Representation,
    double? Confidence,
    IReadOnlyList<string> Warnings,
    string? Error,
    string? CropReference)
{
    public bool Succeeded => Representation is not null && Error is null;
}

/// <summary>
/// Turns a recognised mathematical representation into a faithful spoken script. Stage two of the
/// two-stage maths path.
/// </summary>
/// <remarks>
/// Separate from transcription on purpose. Recognition produces an inspectable artefact a user can
/// correct; verbalisation produces speech. Coupling them would make a fluent-sounding but
/// wrong reading uncorrectable, and would put the burden of not changing a bound or a quantifier on
/// a model that was only asked to describe pixels.
/// </remarks>
public interface IMathVerbaliser
{
    /// <summary>True when a text LLM endpoint is configured and reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Verbalises one expression. Never throws for a malformed model response; returns a rejection.</summary>
    Task<MathVerbalisationResult> VerbaliseAsync(MathVerbalisationRequest request, CancellationToken ct = default);
}

/// <summary>A verbalisation request.</summary>
/// <param name="Representation">The recognised expression, e.g. LaTeX.</param>
/// <param name="Mode">Faithful, detailed-symbolic or explain.</param>
/// <param name="ContextBefore">Bounded preceding source text. Untrusted data, never an instruction.</param>
/// <param name="ContextAfter">Bounded following source text. Untrusted data, never an instruction.</param>
/// <param name="AnchorIds">The only source ids a response may cite.</param>
/// <param name="Language">Target spoken language.</param>
public sealed record MathVerbalisationRequest(
    string Representation,
    MathMode Mode,
    string ContextBefore,
    string ContextAfter,
    IReadOnlyList<string> AnchorIds,
    string Language);

/// <summary>
/// A validated verbalisation.
/// </summary>
/// <param name="Faithful">The spoken rendering that preserves operators, bounds, indices, negation, conditioning and quantifiers.</param>
/// <param name="Explanation">Optional interpretation, only for <see cref="MathMode.Explain"/>. Marked separately as generated so it is never confused with the faithful reading.</param>
/// <param name="CitedAnchorIds">Ids the response actually cited, all verified to exist.</param>
/// <param name="Warnings">Ambiguities the model reported. Present means the user should review before listening.</param>
/// <param name="Rejected">True when validation failed. <see cref="RejectionReasons"/> says why.</param>
/// <param name="RejectionReasons">Validation failures: unknown ids, over-length, malformed JSON, schema violation.</param>
/// <param name="PromptVersion">The prompt that produced this. Part of every cache key.</param>
/// <param name="ModelRevision">The model that produced this.</param>
/// <param name="Attempts">Calls made, including bounded retries of malformed output.</param>
public sealed record MathVerbalisationResult(
    string? Faithful,
    string? Explanation,
    IReadOnlyList<string> CitedAnchorIds,
    IReadOnlyList<string> Warnings,
    bool Rejected,
    IReadOnlyList<string> RejectionReasons,
    string PromptVersion,
    string ModelRevision,
    int Attempts)
{
    public bool Succeeded => !Rejected && Faithful is { Length: > 0 };

    public static MathVerbalisationResult Reject(IReadOnlyList<string> reasons, string promptVersion, string modelRevision, int attempts)
        => new(null, null, [], [], Rejected: true, reasons, promptVersion, modelRevision, attempts);
}

/// <summary>
/// Optional forced alignment of generated speech against the script that was synthesised.
/// </summary>
/// <remarks>
/// Probed, never required. A synthesis-only installation may have no aligner at all — ZONOS3's
/// <c>/v2/edit/align</c> belongs to a separate editing feature set needing extra models and a Python
/// worker, so its presence in documentation does not mean this install can use it. Sentence and
/// equation highlighting ships without any aligner; word highlighting is capability-gated on top.
/// <para>
/// Alignment establishes that audio matches a script. It cannot establish that a model spoke the
/// correct mathematics.
/// </para>
/// </remarks>
public interface IForcedAligner
{
    /// <summary>True when an aligner is installed and usable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Aligns <paramref name="pcm"/> against <paramref name="script"/>. Returns candidate cues that still need <see cref="TimedCueValidator"/>.</summary>
    Task<IReadOnlyList<TimedCue>> AlignAsync(
        ReadOnlyMemory<byte> pcm, AudioFormat format, string script, IReadOnlyList<string> anchorIds, CancellationToken ct = default);
}

/// <summary>
/// Persistence for speech assets. Keyed so that an upstream change invalidates downstream stages
/// without a manual cache clear.
/// </summary>
public interface ISpeechAssetStore
{
    Task<SpeechAsset?> TryGetAsync(string assetId, CancellationToken ct = default);

    Task StoreAsync(SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default);

    /// <summary>Loads the PCM for an asset. Null when the asset was evicted or its key no longer matches.</summary>
    Task<ReadOnlyMemory<byte>?> TryLoadPcmAsync(string assetId, CancellationToken ct = default);

    /// <summary>Drops every asset belonging to a superseded extraction, script revision, voice or model.</summary>
    Task InvalidateAsync(string documentContentHash, CancellationToken ct = default);
}

/// <summary>Persistence for narration plans and the reader's position.</summary>
public interface IPlanStore
{
    Task<NarrationPlan?> TryGetPlanAsync(string documentContentHash, CancellationToken ct = default);

    Task StorePlanAsync(NarrationPlan plan, CancellationToken ct = default);

    Task<ReaderPosition?> TryGetPositionAsync(string documentContentHash, CancellationToken ct = default);

    Task SavePositionAsync(ReaderPosition position, CancellationToken ct = default);
}

/// <summary>
/// Where the reader left off. Restored on open; never triggers auto-play.
/// </summary>
/// <param name="DocumentContentHash">Content identity, so a moved file keeps its position.</param>
/// <param name="SegmentId">Unit to resume from.</param>
/// <param name="PageIndex">Zero-based page.</param>
/// <param name="PositionInSegmentSeconds">Offset within the segment's clip.</param>
/// <param name="VoiceId">Selected voice.</param>
/// <param name="MathMode">Selected maths mode.</param>
/// <param name="Zoom">The user's preferred magnification. Restored, not reset.</param>
/// <param name="FollowMode">Follow preference.</param>
/// <param name="PlaybackRate">Pitch-preserving speed.</param>
public sealed record ReaderPosition(
    string DocumentContentHash,
    string? SegmentId,
    int PageIndex,
    double PositionInSegmentSeconds,
    string? VoiceId,
    MathMode MathMode,
    double Zoom,
    FollowMode FollowMode,
    double PlaybackRate);

/// <summary>
/// The extraction boundary. Implemented by the RailReaderCore adapter; consumed by the planner.
/// </summary>
/// <remarks>
/// Kept as a port so planning is testable against a synthetic snapshot with known text and geometry,
/// with no PDF, no PDFium and no ONNX model present.
/// </remarks>
public interface IExtractionSource
{
    /// <summary>Extracts a page range. Runs off the caller's thread; never blocks the UI.</summary>
    Task<ExtractionSnapshot> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Renders a high-resolution crop of a region, for maths transcription.</summary>
    Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default);
}

/// <summary>What to extract.</summary>
/// <param name="FilePath">The document.</param>
/// <param name="Password">For an encrypted document.</param>
/// <param name="Pages">Pages to extract, or null for all.</param>
/// <param name="Options">Analyser, OCR policy and render size.</param>
/// <param name="Filters">Which block kinds to keep.</param>
public sealed record ExtractionRequest(
    string FilePath,
    string? Password,
    IReadOnlyList<int>? Pages,
    ExtractionOptions Options,
    NarrationFilterSettings Filters);

/// <summary>Extraction progress, for a UI that says what it is doing.</summary>
public sealed record ExtractionProgress(int PagesDone, int PagesTotal, string Stage);
