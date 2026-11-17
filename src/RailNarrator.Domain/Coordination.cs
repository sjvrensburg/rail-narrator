using System.Collections.Concurrent;

namespace RailNarrator.Domain;

/// <summary>
/// The audio transport boundary the coordinator drives. Implemented by the real playback engine and
/// by a controllable fake in tests.
/// </summary>
/// <remarks>
/// <see cref="PositionSeconds"/> must be a media position — frames the output device has actually
/// presented — and not elapsed wall-clock time, bytes received, or samples merely handed to the
/// device. Highlights are driven from it, so a clock that runs ahead shows the next sentence while
/// the current one is still being spoken, and a clock that freezes on an underflow correctly stops
/// the reading position rather than letting it drift on.
/// </remarks>
public interface IAudioTransport
{
    PlaybackState State { get; }

    /// <summary>Media position within the loaded clip, in seconds of source audio.</summary>
    double PositionSeconds { get; }

    /// <summary>Duration of the loaded clip, or 0 when nothing is loaded.</summary>
    double ClipDurationSeconds { get; }

    /// <summary>Audio ready beyond the current position, for the buffering indicator.</summary>
    double BufferedAheadSeconds { get; }

    /// <summary>Pitch-preserving playback rate.</summary>
    double PlaybackRate { get; }

    /// <summary>Loads a clip. Replaces anything previously loaded and discards its audio.</summary>
    Task LoadAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default);

    Task PlayAsync(CancellationToken ct = default);

    Task PauseAsync(CancellationToken ct = default);

    Task ResumeAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    /// <summary>Seeks within the loaded clip. Must flush stale audio before presenting new samples.</summary>
    Task SeekAsync(double seconds, CancellationToken ct = default);

    /// <summary>Sets a pitch-preserving rate. Changing the sample rate alone is not a valid implementation.</summary>
    void SetPlaybackRate(double rate);

    /// <summary>Raised on every state transition, on the coordinator's thread.</summary>
    event Action<PlaybackState>? StateChanged;

    /// <summary>Raised when the clip reaches its end and has drained.</summary>
    event Action<string>? ClipCompleted;

    /// <summary>Raised when the buffer ran dry mid-clip.</summary>
    event Action<string>? Underflow;

    /// <summary>Raised when the output device fails. Playback cannot continue without intervention.</summary>
    event Action<string>? DeviceFailed;
}

/// <summary>Coordinator tuning.</summary>
/// <param name="LookaheadSegments">
/// How many segments to prepare ahead of playback. Bounded on purpose: synthesising a whole book
/// before playing it wastes minutes of inference and hundreds of megabytes, and the first audible
/// output is what the user is waiting for.
/// </param>
/// <param name="MaxConcurrentSynthesis">
/// Synthesis jobs in flight at once. One where memory is constrained; OCR, vision inference, LLM
/// inference and TTS all compete for the same RAM and GPU.
/// </param>
/// <param name="VoiceId">Voice to synthesise with.</param>
/// <param name="Format">Negotiated output format.</param>
/// <param name="PlaybackRate">Initial pitch-preserving rate.</param>
/// <param name="FollowMode">Initial camera policy.</param>
/// <param name="RequestTimings">Ask providers for timing evidence. Ignored when they have none.</param>
/// <param name="InterpretAheadSegments">
/// How far ahead of the reading position pending maths passages are interpreted. Further than the
/// synthesis lookahead, because a local model takes several seconds per passage and a run of
/// equations would otherwise stall playback.
/// </param>
public sealed record NarrationCoordinatorOptions(
    int LookaheadSegments = 4,
    int MaxConcurrentSynthesis = 1,
    string? VoiceId = null,
    AudioFormat? Format = null,
    double PlaybackRate = 1.0,
    FollowMode FollowMode = FollowMode.KeepVisible,
    bool RequestTimings = true,
    int InterpretAheadSegments = 40)
{
    public static NarrationCoordinatorOptions Default { get; } = new();
}

/// <summary>
/// The single authority for narration state.
/// </summary>
/// <remarks>
/// <para>
/// Everything that moves with the reading position — highlights, the camera, the transcript cursor,
/// the progress bar — reads this object. Nothing else runs a reading clock. A time-based auto-scroll
/// left running alongside narration would fight the audio clock and visibly disagree with it, so
/// taking over follow mode means disabling any competing scroller.
/// </para>
/// <para>
/// A <b>generation token</b> advances on document change, seek, script edit and voice change. Every
/// in-flight job captures the token it started with and its result is discarded if the token has
/// moved on, which is what stops a late synthesis from the previous document playing over the new
/// one — a race that is otherwise very hard to reproduce and very obvious when it happens.
/// </para>
/// </remarks>
public sealed class NarrationCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Peak amplitude below which a clip counts as silence rather than speech. Matches
    /// <see cref="ProviderSelfTestResult.SilencePeakThreshold"/>; measured against a known-silent
    /// Vulkan decoder that peaked at 0.0017 and healthy speech that peaks between 0.3 and 0.7.
    /// </summary>
    public const float SilencePeakThreshold = 0.005f;

    private readonly ITtsProvider _tts;
    private readonly IAudioTransport _audio;
    private readonly ISpeechAssetStore? _assetStore;
    private readonly IForcedAligner? _aligner;
    private readonly ISegmentResolver? _resolver;
    private readonly IMonotonicClock _clock;
    private readonly object _gate = new();

    private NarrationCoordinatorOptions _options;
    private NarrationPlan? _plan;
    private CueScheduler? _scheduler;
    private int _segmentIndex = -1;
    private long _generation;
    private CancellationTokenSource? _prepareCts;
    private Task? _prepareLoop;
    private bool _disposed;
    private bool _followSuspended;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Idle;
    private ActiveCue? _lastCue;
    private ActiveCue? _pendingFollow;
    private double _lastFollowAt = double.NegativeInfinity;
    private readonly Dictionary<string, PreparedClip> _prepared = [];
    // In-flight preparations, keyed by generation + content-addressed asset id. The lookahead loop
    // and the playback path both prepare the current segment; without this they synthesise and store
    // it twice concurrently. Entries are removed on completion and the map is cleared whenever the
    // generation advances, so a voice switch or script edit never waits on a stale preparation.
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inflight = new();
    private readonly object _prepareGate = new();
    // Interpretations in flight, keyed by generation + segment id + revision, shared by the
    // interpret-ahead loop and the synthesis path so a passage is never sent to the model twice.
    private readonly ConcurrentDictionary<string, Lazy<Task<NarrationSegment>>> _resolving = new();
    private CancellationTokenSource? _interpretCts;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _interpretLoop;
    private long _interpretGeneration = -1;
    private int _interpretFrom = -1;

    public NarrationCoordinator(
        ITtsProvider tts,
        IAudioTransport audio,
        ISpeechAssetStore? assetStore = null,
        IMonotonicClock? clock = null,
        NarrationCoordinatorOptions? options = null,
        IForcedAligner? aligner = null,
        ISegmentResolver? resolver = null)
    {
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _assetStore = assetStore;
        _aligner = aligner;
        _resolver = resolver;
        _clock = clock ?? new SystemMonotonicClock();
        _options = options ?? NarrationCoordinatorOptions.Default;

        _audio.StateChanged += OnTransportStateChanged;
        _audio.ClipCompleted += OnClipCompleted;
        _audio.Underflow += OnUnderflow;
        _audio.DeviceFailed += OnDeviceFailed;
    }

    /// <summary>Advances on every invalidating action. Jobs compare against it before applying results.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public NarrationPlan? Plan => _plan;

    public CueScheduler? Scheduler => _scheduler;

    public NarrationCoordinatorOptions Options => _options;

    public PlaybackSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    /// <summary>Raised whenever the published snapshot changes.</summary>
    public event Action<PlaybackSnapshot>? SnapshotChanged;

    /// <summary>
    /// A pending segment received its final words (a maths interpretation, or its fallback). Raised
    /// outside the coordinator lock, from a background thread.
    /// </summary>
    public event Action<NarrationSegment>? SegmentResolved;

    /// <summary>Raised when the cue to highlight changes. Carries the cue and the anchors to draw.</summary>
    public event Action<ActiveCue>? ActiveCueChanged;

    /// <summary>Raised when follow mode should be re-applied, with the rects to frame.</summary>
    public event Action<ActiveCue, IReadOnlyList<PageRect>, int>? FollowRequested;

    /// <summary>True when manual browsing has suspended camera following. Audio keeps playing.</summary>
    public bool FollowSuspended { get { lock (_gate) return _followSuspended; } }

    public FollowMode FollowMode => _options.FollowMode;

    // ---- lifecycle --------------------------------------------------------------------

    /// <summary>
    /// Loads a plan and positions the reader at <paramref name="resume"/> if supplied. Does not
    /// start playback: a document must never begin reading itself on open.
    /// </summary>
    public async Task LoadPlanAsync(NarrationPlan plan, ReaderPosition? resume = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfDisposed();
        long gen = AdvanceGeneration();

        await _audio.StopAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _plan = plan;
            _scheduler = new CueScheduler(plan);
            _prepared.Clear();
            _segmentIndex = Math.Clamp(
                resume?.SegmentId is { } id ? Math.Max(0, plan.Segments.ToList().FindIndex(s => s.Id == id)) : 0,
                0, Math.Max(0, plan.SegmentCount - 1));
            if (plan.SegmentCount == 0) _segmentIndex = -1;
        }
        if (resume is { } r)
        {
            _options = _options with
            {
                VoiceId = r.VoiceId ?? _options.VoiceId,
                FollowMode = r.FollowMode,
                PlaybackRate = r.PlaybackRate <= 0 ? 1.0 : r.PlaybackRate,
            };
            _audio.SetPlaybackRate(_options.PlaybackRate);
        }

        // Idle, not Preparing: nothing has been asked to play. Lookahead runs in the background, and
        // a state that never left Preparing told the reader the document was still loading.
        Publish(PlaybackState.Idle, null);
        StartPreparation(gen);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Starts playback from the current segment.</summary>
    public async Task PlayAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        // Play after Pause continues from where the pause left off. Reloading the clip here would
        // restart the passage from its first word, which for a forty-second paragraph is a long way
        // back. Anything that invalidates the paused clip (a seek, a voice change, an edit) stops
        // the transport first, so a transport still in Paused holds the current segment's audio.
        if (_plan is not null && _audio.State == PlaybackState.Paused)
        {
            await ResumeAsync(ct).ConfigureAwait(false);
            return;
        }
        // Play while already playing is not a request to start the passage again; Replay is.
        if (_plan is not null && _audio.State == PlaybackState.Playing) return;
        long gen = AdvanceGeneration();
        if (_plan is null || _segmentIndex < 0)
        {
            Publish(PlaybackState.Error, null, NarrationError.ProviderUnavailable("No document is loaded."));
            return;
        }
        Publish(PlaybackState.Preparing, "Preparing speech");
        StartPreparation(gen);
        if (!await EnsureSegmentReadyAsync(_segmentIndex, gen, ct).ConfigureAwait(false)) return;
        await StartCurrentSegmentAsync(gen, ct).ConfigureAwait(false);
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _audio.PauseAsync(ct).ConfigureAwait(false);
        Publish(PlaybackState.Paused, "Paused");
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        long gen = Generation;
        await _audio.ResumeAsync(ct).ConfigureAwait(false);
        Publish(PlaybackState.Playing, null);
        StartPreparation(gen);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        AdvanceGeneration();
        await _audio.StopAsync(ct).ConfigureAwait(false);
        lock (_gate) _segmentIndex = Math.Max(0, _segmentIndex);
        Publish(PlaybackState.Idle, "Stopped");
    }

    /// <summary>
    /// Moves to a segment. Cancels obsolete work, flushes stale audio and advances the generation,
    /// so a synthesis that finishes after the seek cannot play.
    /// </summary>
    public async Task GoToSegmentAsync(string segmentId, double withinSegmentSeconds = 0, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_plan is null) return;
        int index = _plan.Segments.ToList().FindIndex(s => s.Id == segmentId);
        if (index < 0) return;
        long gen = AdvanceGeneration();
        Publish(PlaybackState.Seeking, "Seeking");
        await _audio.StopAsync(ct).ConfigureAwait(false);
        lock (_gate) _segmentIndex = index;
        StartPreparation(gen);
        if (!await EnsureSegmentReadyAsync(index, gen, ct).ConfigureAwait(false)) return;
        await StartCurrentSegmentAsync(gen, ct, withinSegmentSeconds).ConfigureAwait(false);
    }

    public Task NextSegmentAsync(CancellationToken ct = default)
    {
        var plan = _plan;
        if (plan is null) return Task.CompletedTask;
        int next;
        lock (_gate) next = Math.Min(_segmentIndex + 1, plan.SegmentCount - 1);
        return next < 0 ? Task.CompletedTask : GoToSegmentAsync(plan.Segments[next].Id, 0, ct);
    }

    public Task PreviousSegmentAsync(CancellationToken ct = default)
    {
        var plan = _plan;
        if (plan is null || plan.SegmentCount == 0) return Task.CompletedTask;
        int prev;
        lock (_gate)
        {
            if (_segmentIndex < 0) return Task.CompletedTask;
            prev = Math.Max(_segmentIndex - 1, 0);
        }
        return GoToSegmentAsync(plan.Segments[prev].Id, 0, ct);
    }

    /// <summary>Replays the current segment from its start.</summary>
    public Task ReplaySegmentAsync(CancellationToken ct = default)
    {
        var plan = _plan;
        if (plan is null) return Task.CompletedTask;
        int i;
        lock (_gate) i = _segmentIndex;
        return i < 0 || i >= plan.SegmentCount ? Task.CompletedTask : GoToSegmentAsync(plan.Segments[i].Id, 0, ct);
    }

    public void SetPlaybackRate(double rate)
    {
        double clamped = Math.Clamp(rate, 0.5, 3.0);
        _options = _options with { PlaybackRate = clamped };
        _audio.SetPlaybackRate(clamped);
        PublishSnapshotOnly();
    }

    public void SetFollowMode(FollowMode mode)
    {
        _options = _options with { FollowMode = mode };
        lock (_gate) _followSuspended = false;
        PublishSnapshotOnly();
        RaiseFollowForCurrentCue();
    }

    /// <summary>
    /// Suspends camera following without touching audio. Panning or navigating to look at something
    /// else must not stop the reading, and must not be undone a moment later by the follow policy.
    /// </summary>
    public void SuspendFollow()
    {
        lock (_gate) _followSuspended = true;
        PublishSnapshotOnly();
    }

    /// <summary>Restores following explicitly, on the user's request.</summary>
    public void ResumeFollow()
    {
        lock (_gate) _followSuspended = false;
        PublishSnapshotOnly();
        RaiseFollowForCurrentCue();
    }

    /// <summary>
    /// Switches voice. Every prepared clip was synthesised by the previous voice, so all of them are
    /// discarded and the generation token advances: a synthesis that completes after the switch must
    /// not be played over the new voice.
    /// </summary>
    public async Task SetVoiceAsync(string voiceId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(voiceId)) return;
        if (string.Equals(_options.VoiceId, voiceId, StringComparison.Ordinal)) return;

        _options = _options with { VoiceId = voiceId };
        long generation = AdvanceGeneration();
        bool wasPlaying = _audio.State == PlaybackState.Playing;
        await _audio.StopAsync(ct).ConfigureAwait(false);

        lock (_gate) _prepared.Clear();
        _lastCue = null;

        StartPreparation(generation);
        if (_plan is not null && wasPlaying)
        {
            if (await EnsureSegmentReadyAsync(Math.Max(0, _segmentIndex), generation, ct).ConfigureAwait(false)
                && generation == Generation)
                await StartCurrentSegmentAsync(generation, ct).ConfigureAwait(false);
        }
        PublishSnapshotOnly();
    }

    /// <summary>
    /// Asks the maths interpreter again for one passage, ignoring its cached answer, and puts the
    /// new words in place the way an edit is: that segment's speech is invalidated and prepared
    /// again. Returns the segment as it now stands, or null when there is nothing to regenerate.
    /// </summary>
    public async Task<NarrationSegment?> RegenerateSegmentAsync(string segmentId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var plan = _plan;
        if (plan is null || _resolver is null) return null;
        var segment = plan.Segments.FirstOrDefault(s => s.Id == segmentId);
        if (segment is null) return null;
        var fresh = segment.Provenance == ScriptProvenance.PendingInterpretation
            ? await _resolver.ResolveAsync(plan, segment, ct).ConfigureAwait(false)
            : await _resolver.RefreshAsync(plan, segment, ct).ConfigureAwait(false);
        if (ReferenceEquals(fresh, segment)) return segment;

        lock (_gate)
        {
            if (_plan is not { } current || current.Header != plan.Header) return null;
            _plan = current with { Segments = current.Segments.Select(s => s.Id == segmentId ? fresh : s).ToList() };
        }
        long gen = AdvanceGeneration();
        lock (_gate)
        {
            _scheduler = new CueScheduler(_plan!);
            _prepared.Clear();
        }
        if (_audio.State == PlaybackState.Paused) await _audio.StopAsync(ct).ConfigureAwait(false);
        StartPreparation(gen);
        PublishSnapshotOnly();
        SegmentResolved?.Invoke(fresh);
        return fresh;
    }

    /// <summary>
    /// Applies a user edit to a segment's spoken text. Invalidates that segment's speech and
    /// timings, and everything after it on the timeline, because their positions shift.
    /// </summary>
    public async Task ApplyScriptEditAsync(string segmentId, string newSpokenText, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_plan is null) return;
        var segments = _plan.Segments.ToList();
        int index = segments.FindIndex(s => s.Id == segmentId);
        if (index < 0) return;

        segments[index] = segments[index] with
        {
            SpokenText = newSpokenText,
            Provenance = ScriptProvenance.UserEdited,
            ScriptRevision = segments[index].ScriptRevision + 1,
            SourceLinks = [new SourceLink(new TextRange(0, newSpokenText.Length), segments[index].AnchorIds)],
        };
        _plan = _plan with { Segments = segments };

        long gen = AdvanceGeneration();
        lock (_gate)
        {
            _scheduler = new CueScheduler(_plan);
            _prepared.Clear();
        }
        // A paused transport still holds the old wording. Play resumes a paused clip, so it must be
        // stopped here or the correction would not be heard until the passage was reached again.
        if (_audio.State == PlaybackState.Paused) await _audio.StopAsync(ct).ConfigureAwait(false);
        if (_assetStore is not null)
            await _assetStore.InvalidateAsync(_plan.Header.Extraction.DocumentContentHash, ct).ConfigureAwait(false);
        StartPreparation(gen);
        PublishSnapshotOnly();
    }

    // ---- preparation ------------------------------------------------------------------

    private void StartPreparation(long generation)
    {
        lock (_prepareGate)
        {
            _prepareCts?.Cancel();
            _prepareCts = new CancellationTokenSource();
            var ct = _prepareCts.Token;
            _prepareLoop = Task.Run(() => PrepareLoopAsync(generation, ct), ct);
        }
        InterpretAhead(generation);
    }

    /// <summary>
    /// Slides the lookahead window forward after playback advances on its own.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="StartPreparation"/> this cancels nothing: the generation has not changed,
    /// so whatever is being synthesised is still wanted, and restarting it would throw the work
    /// away at every passage. The next pass is chained behind the one in flight, which keeps the
    /// one-synthesis-at-a-time bound. Without this the window only ever covered the passages
    /// after the last Play or seek, and everything beyond them was synthesised at the moment it
    /// was due — a silence as long as the synthesis before each passage.
    /// </remarks>
    private void ExtendPreparation(long generation)
    {
        lock (_prepareGate)
        {
            if (_prepareCts is not { IsCancellationRequested: false } cts)
            {
                _prepareCts = cts = new CancellationTokenSource();
                _prepareLoop = null;
            }
            var ct = cts.Token;
            var previous = _prepareLoop;
            _prepareLoop = Task.Run(async () =>
            {
                if (previous is not null)
                {
                    try { await previous.ConfigureAwait(false); }
                    catch (Exception) { /* its own failure was already published */ }
                }
                await PrepareLoopAsync(generation, ct).ConfigureAwait(false);
            });
        }
        InterpretAhead(generation);
    }

    /// <summary>
    /// Synthesises ahead of playback within the lookahead bound. Results from a superseded
    /// generation are dropped rather than stored, so they cannot be played later.
    /// </summary>
    private async Task PrepareLoopAsync(long generation, CancellationToken ct)
    {
        // This task is fire-and-forget, so a fault outside the per-segment synthesis try (a
        // capabilities probe, a voice list, a cache write) must be published as an error here —
        // otherwise the snapshot stays at Preparing forever with no explanation.
        try
        {
            await PrepareLoopCoreAsync(generation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == Generation && !_disposed)
                Publish(PlaybackState.Error, null, NarrationError.SpeechSynthesisFailed(ex.Message));
        }
    }

    private async Task PrepareLoopCoreAsync(long generation, CancellationToken ct)
    {
        var plan = _plan;
        if (plan is null || plan.SegmentCount == 0) return;
        var caps = await _tts.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!caps.IsAvailable)
        {
            Publish(PlaybackState.Error, null, NarrationError.ProviderUnavailable(caps.Unavailable ?? "Unknown reason."));
            return;
        }

        var format = _options.Format ?? caps.PreferredFormat ?? AudioFormat.Mono44100Float32;
        var voice = _options.VoiceId;
        if (voice is null)
        {
            var voices = await _tts.ListVoicesAsync(ct).ConfigureAwait(false);
            voice = voices.FirstOrDefault(v => v.IsDefault)?.Id ?? voices.FirstOrDefault()?.Id;
            if (voice is null)
            {
                Publish(PlaybackState.Error, null, NarrationError.ProviderUnavailable("The speech provider has no voices installed."));
                return;
            }
            _options = _options with { VoiceId = voice };
        }

        int start;
        lock (_gate) start = Math.Max(0, _segmentIndex);
        int end = Math.Min(plan.SegmentCount, start + Math.Max(1, _options.LookaheadSegments));

        // One expensive inference job at a time by default: OCR, vision, LLM and TTS contend for the
        // same memory, and tuning concurrency before measuring is how a reader runs out of RAM.
        using var limiter = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentSynthesis));
        for (int i = start; i < end && !ct.IsCancellationRequested; i++)
        {
            if (generation != Generation) return;
            var segment = plan.Segments[i];
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PrepareSegmentAsync(segment, caps, format, voice, generation, ct).ConfigureAwait(false);
            }
            finally { limiter.Release(); }
        }
    }

    /// <summary>
    /// Gives a pending segment its final words and puts them in the plan.
    /// </summary>
    /// <remarks>
    /// Keyed by plan, segment and revision rather than by generation: an interpretation does not
    /// depend on where playback is, so a seek must neither cancel a model call that is nearly done
    /// nor start a second one for the same passage. It runs on the coordinator's lifetime token and
    /// is accepted whenever the same plan is still loaded.
    /// </remarks>
    private async Task<NarrationSegment> ResolveSegmentAsync(NarrationSegment segment, CancellationToken ct)
    {
        var plan = _plan;
        if (plan is null || _resolver is null) return segment;
        string key = $"{plan.Header.PlanId}:{plan.Header.InputFingerprint}:{segment.Id}:{segment.ScriptRevision}";
        var lazy = _resolving.GetOrAdd(key, _ => new Lazy<Task<NarrationSegment>>(
            () => _resolver.ResolveAsync(plan, segment, _lifetime.Token)));
        NarrationSegment resolved;
        try { resolved = await lazy.Value.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw;   // this caller stopped waiting; the interpretation carries on for the next one
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            _resolving.TryRemove(key, out _);
            // The resolver promises a fallback rather than a throw; if one escapes anyway the
            // placeholder is still an honest reading.
            resolved = segment with { Provenance = ScriptProvenance.SourceVerbatim };
        }

        bool replaced = false;
        lock (_gate)
        {
            if (_plan is { } current && current.Header == plan.Header)
            {
                var segments = current.Segments.ToList();
                int index = segments.FindIndex(s => s.Id == resolved.Id);
                if (index >= 0 && segments[index].Provenance == ScriptProvenance.PendingInterpretation
                    && segments[index].ScriptRevision == segment.ScriptRevision)
                {
                    segments[index] = resolved;
                    _plan = current with { Segments = segments };
                    replaced = true;
                }
            }
        }
        _resolving.TryRemove(key, out _);
        if (replaced) SegmentResolved?.Invoke(resolved);
        return resolved;
    }

    /// <summary>
    /// Interprets pending passages ahead of the reading position, one at a time, so they are ready
    /// before synthesis reaches them. Restarted when the generation or the position moves on.
    /// </summary>
    private void InterpretAhead(long generation)
    {
        if (_resolver is null) return;
        int from;
        lock (_gate) from = Math.Max(0, _segmentIndex);
        lock (_prepareGate)
        {
            bool running = _interpretLoop is { IsCompleted: false } && _interpretGeneration == generation;
            // Still working on this generation from an earlier position: let it carry on unless the
            // reader has jumped outside the window it is covering.
            if (running && from >= _interpretFrom && from < _interpretFrom + _options.InterpretAheadSegments / 2) return;
            _interpretCts?.Cancel();
            _interpretCts = new CancellationTokenSource();
            var ct = _interpretCts.Token;
            _interpretGeneration = generation;
            _interpretFrom = from;
            _interpretLoop = Task.Run(async () =>
            {
                try
                {
                    var plan = _plan;
                    if (plan is null) return;
                    int end = Math.Min(plan.SegmentCount, from + Math.Max(1, _options.InterpretAheadSegments));
                    for (int i = from; i < end && !ct.IsCancellationRequested && ReferenceEquals(_plan?.Header, plan.Header); i++)
                    {
                        var segment = _plan?.Segments[i];
                        if (segment is { Provenance: ScriptProvenance.PendingInterpretation })
                            await ResolveSegmentAsync(segment, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { /* each passage falls back on its own; nothing to publish here */ }
            });
        }
    }

    private async Task PrepareSegmentAsync(
        NarrationSegment segment, TtsCapabilities caps, AudioFormat format, string voice, long generation, CancellationToken ct)
    {
        if (segment.Provenance == ScriptProvenance.PendingInterpretation && _resolver is not null)
        {
            segment = await ResolveSegmentAsync(segment, ct).ConfigureAwait(false);
            if (generation != Generation) return;
        }
        // Dedupe concurrent preparations of the same asset (lookahead loop vs. playback path):
        // both callers await the one synthesis instead of each synthesising and storing it.
        string assetId = CacheKey.ForSpeechAsset(
            segment.Id, segment.ScriptRevision, segment.SpokenText, segment.Language,
            caps.ProviderId, caps.ModelRevision, voice, SynthesisOptionsToken(format), format);
        string key = $"{generation}:{assetId}";
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task>(
            () => PrepareSegmentCoreAsync(segment, caps, format, voice, generation, ct)));
        try
        {
            await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task>>>)_inflight)
                .Remove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
        }
    }

    private async Task PrepareSegmentCoreAsync(
        NarrationSegment segment, TtsCapabilities caps, AudioFormat format, string voice, long generation, CancellationToken ct)
    {
        string assetId = CacheKey.ForSpeechAsset(
            segment.Id, segment.ScriptRevision, segment.SpokenText, segment.Language,
            caps.ProviderId, caps.ModelRevision, voice, SynthesisOptionsToken(format), format);

        lock (_gate) if (_prepared.ContainsKey(segment.Id)) return;

        if (_assetStore is not null)
        {
            var cached = await _assetStore.TryGetAsync(assetId, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                var cachedPcm = await _assetStore.TryLoadPcmAsync(assetId, ct).ConfigureAwait(false);
                if (cachedPcm is { } pcm && generation == Generation)
                {
                    var beforeAlignment = cached;
                    cached = await AlignAssetAsync(segment, cached, pcm, ct).ConfigureAwait(false);
                    if (generation != Generation) return;
                    if (!ReferenceEquals(beforeAlignment, cached)) await _assetStore.StoreAsync(cached, pcm, ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (generation != Generation) return;
                        _prepared[segment.Id] = new PreparedClip(cached, pcm.ToArray());
                        AttachTimings(segment, cached);
                    }
                    return;
                }
            }
        }

        int maxChars = caps.MaxTextChars > 0 ? caps.MaxTextChars : int.MaxValue;
        if (segment.SpokenText.Length > maxChars)
        {
            Publish(PlaybackState.Error, null, NarrationError.SpeechSynthesisFailed(
                $"This passage exceeds the provider's {maxChars}-character limit. Split or shorten it before narration.", segment.Id));
            return;
        }
        string text = segment.SpokenText;
        var warnings = new List<string>();

        TtsSynthesisResult result;
        try
        {
            result = await _tts.SynthesizeAsync(new TtsSynthesisRequest(
                text, voice, segment.Language, format, 1.0, Seed: null, _options.RequestTimings), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (generation != Generation) return;
            Publish(PlaybackState.Error, null, NarrationError.SpeechSynthesisFailed(ex.Message, segment.Id));
            return;
        }

        if (generation != Generation) return;   // a late result from a superseded plan

        if (result.IsSilentOrEmpty || AudioSamples.Peak(result.Pcm.Span, result.Format) < SilencePeakThreshold)
        {
            // Frame count alone cannot detect a silent decoder: it returns a correctly sized,
            // correctly formatted buffer of zeros. See AudioSamples for why this is verified here
            // rather than trusted from the provider's own self-test.
            warnings.Add("The provider returned audio that is effectively silent.");
            Publish(PlaybackState.Error, null, NarrationError.SpeechSynthesisFailed(
                "The speech provider produced silence for this segment.", segment.Id));
            return;
        }

        var knownAnchors = (_scheduler?.SourceMap.KnownAnchorIds ?? new HashSet<string>());
        var timings = BindTimings(segment, result.Timings ?? []);
        // Native timings are evidence, not truth: they only become cues after range, monotonicity,
        // coverage and confidence checks. Otherwise a dropped word shifts every later highlight.
        var validated = timings.Count > 0
            ? TimedCueValidator.Validate(timings, result.FrameCount, segment.SpokenText, knownAnchors, provenance: result.TimingProvenance)
            : new CueValidationResult([], [], 0);

        var asset = new SpeechAsset(
            Id: assetId,
            SegmentId: segment.Id,
            AudioLocation: assetId,
            Format: result.Format,
            FrameCount: result.FrameCount,
            ProviderId: caps.ProviderId,
            ModelRevision: caps.ModelRevision,
            VoiceId: voice,
            SynthesisOptions: SynthesisOptionsToken(format),
            Timings: validated.IsUsable ? validated.Accepted : [],
            TimingProvenance: validated.IsUsable ? result.TimingProvenance : TimingProvenance.ClipBoundary,
            ScriptRevision: segment.ScriptRevision)
        {
            PcmHash = CacheKey.Sha256Hex(result.Pcm.Span),
            TimingWarnings = result.Warnings.Concat(validated.Rejected.Select(r => r.Reason)).ToArray(),
        };

        asset = await AlignAssetAsync(segment, asset, result.Pcm, ct).ConfigureAwait(false);

        if (_assetStore is not null)
            await _assetStore.StoreAsync(asset, result.Pcm, ct).ConfigureAwait(false);

        if (generation != Generation) return;
        // The clip is held in memory as well as in the store. Playback must not depend on a cache
        // being configured, and re-reading from disk to play what was just synthesised is waste.
        // The lookahead bound is what keeps this from growing: only prepared segments are retained.
        lock (_gate)
        {
            if (generation != Generation) return;
            _prepared[segment.Id] = new PreparedClip(asset, result.Pcm.ToArray());
            AttachTimings(segment, asset, warnings);
        }
    }

    private void AttachTimings(NarrationSegment segment, SpeechAsset asset, IReadOnlyList<string>? extra = null)
    {
        var scheduler = _scheduler;
        if (scheduler is null) return;
        var known = scheduler.SourceMap.KnownAnchorIds;
        var diagnostics = new List<string>(extra ?? []);
        diagnostics.AddRange(asset.TimingWarnings);

        if (_options.RequestTimings && asset.HasTimings)
        {
            var revalidated = TimedCueValidator.Validate(BindTimings(segment, asset.Timings), asset.FrameCount, segment.SpokenText, known,
                provenance: asset.TimingProvenance);
            diagnostics.AddRange(revalidated.Rejected.Select(r => $"cue rejected: {r.Reason}"));
            if (!revalidated.IsUsable) diagnostics.Add("Incomplete or invalid timing evidence; highlighting the sentence.");
            scheduler.AttachClip(asset, revalidated.IsUsable ? revalidated.Accepted : [], diagnostics);
        }
        else
        {
            // No timing evidence. The whole clip is the cue at sentence or equation granularity —
            // never interpolated word boundaries, which would look measured and be invented.
            diagnostics.Add($"No sub-clip timing from provider ({asset.TimingProvenance}); highlighting the whole unit.");
            scheduler.AttachClip(asset, [], diagnostics);
        }
    }

    private IReadOnlyList<TimedCue> BindTimings(NarrationSegment segment, IReadOnlyList<TimedCue> timings)
        => timings.Select(c => c.SpokenRange is { } range
            ? c with { AnchorIds = segment.SourceLinks.Where(l => l.SpokenRange.Overlaps(range))
                .SelectMany(l => l.AnchorIds).Distinct().ToArray(), Granularity = segment.IsMaths ? CueGranularity.Equation : c.Granularity }
            : c).ToArray();

    private async Task<SpeechAsset> AlignAssetAsync(NarrationSegment segment, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        // A paraphrase or an announcement has no word-for-word link to the page: highlight the whole
        // passage rather than align words that would point nowhere.
        if (!_options.RequestTimings || segment.IsMaths || _aligner is null
            || segment.Provenance is not (ScriptProvenance.SourceVerbatim or ScriptProvenance.UserEdited)) return asset;
        // The initial bundled dictionary/acoustic model is English. Never apply it to other languages.
        if (!segment.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return asset;
        string hash = CacheKey.Sha256Hex(pcm.Span);
        try
        {
            if (!await _aligner.IsAvailableAsync(ct).ConfigureAwait(false))
                return asset with { TimingWarnings = asset.TimingWarnings.Append("Local word aligner is unavailable.").Distinct().ToArray() };
            string revision = CacheKey.ForAlignment(hash, "forced", _aligner.Revision, CacheKey.Sha256Hex(segment.SpokenText));
            if (asset.AlignmentRevision == revision && asset.PcmHash == hash && asset.HasTimings) return asset;
            var aligned = await _aligner.AlignAsync(pcm, asset.Format, segment.SpokenText, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var cues = BindTimings(segment, aligned.Words.Select(w => new TimedCue(w.StartSample, w.EndSample,
                w.SpokenRange, [], CueGranularity.Word, w.Confidence, [])).ToArray());
            var validated = TimedCueValidator.Validate(cues, asset.FrameCount, segment.SpokenText,
                segment.SourceLinks.SelectMany(l => l.AnchorIds).ToHashSet(), provenance: TimingProvenance.ForcedAlignment);
            if (!validated.IsUsable) throw new InvalidDataException("Word alignment failed script coverage, range or confidence checks.");
            asset = asset with { Timings = validated.Accepted, TimingProvenance = TimingProvenance.ForcedAlignment,
                AlignmentRevision = revision, PcmHash = hash, TimingWarnings = aligned.Warnings };
            return asset;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return asset with { Timings = [], TimingProvenance = TimingProvenance.ClipBoundary,
                AlignmentRevision = null, PcmHash = hash, TimingWarnings = ["Word alignment unavailable: " + ex.Message] };
        }
    }

    private static string SynthesisOptionsToken(AudioFormat format)
        => $"rate={format.SampleRate};ch={format.Channels};fmt={(int)format.SampleFormat};pv={NarrationVersions.ProsePipeline}";

    /// <summary>Waits until a segment has audio, preparing it directly if the lookahead has not reached it.</summary>
    private async Task<bool> EnsureSegmentReadyAsync(int index, long generation, CancellationToken ct)
    {
        var plan = _plan;
        if (plan is null || index < 0 || index >= plan.SegmentCount) return false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            lock (_gate) if (_prepared.ContainsKey(plan.Segments[index].Id)) return true;
            if (generation != Generation) return false;

            var caps = await _tts.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (!caps.IsAvailable)
            {
                Publish(PlaybackState.Error, null, NarrationError.ProviderUnavailable(caps.Unavailable ?? "Unknown reason."));
                return false;
            }
            var format = _options.Format ?? caps.PreferredFormat ?? AudioFormat.Mono44100Float32;
            string voice;
            lock (_gate) voice = _options.VoiceId ?? "";
            if (voice.Length == 0)
            {
                var voices = await _tts.ListVoicesAsync(ct).ConfigureAwait(false);
                voice = voices.FirstOrDefault(v => v.IsDefault)?.Id ?? voices.FirstOrDefault()?.Id ?? "";
                if (voice.Length == 0)
                {
                    Publish(PlaybackState.Error, null, NarrationError.ProviderUnavailable("The speech provider has no voices installed."));
                    return false;
                }
                _options = _options with { VoiceId = voice };
            }
            await PrepareSegmentAsync(plan.Segments[index], caps, format, voice, generation, ct).ConfigureAwait(false);
        }
        lock (_gate) return _prepared.ContainsKey(plan.Segments[index].Id);
    }

    private async Task StartCurrentSegmentAsync(long generation, CancellationToken ct, double withinSeconds = 0)
    {
        var plan = _plan;
        if (plan is null) return;
        PreparedClip prepared;
        string segmentId;
        lock (_gate)
        {
            if (_segmentIndex < 0 || _segmentIndex >= plan.SegmentCount) return;
            segmentId = plan.Segments[_segmentIndex].Id;
            if (!_prepared.TryGetValue(segmentId, out prepared!)) return;
        }
        if (generation != Generation) return;

        await _audio.LoadAsync(segmentId, prepared.Asset, prepared.Pcm, ct).ConfigureAwait(false);
        if (withinSeconds > 0) await _audio.SeekAsync(withinSeconds, ct).ConfigureAwait(false);
        await _audio.PlayAsync(ct).ConfigureAwait(false);
        Publish(PlaybackState.Playing, null);
        RaiseCueForCurrentPosition();
    }

    // ---- transport callbacks -----------------------------------------------------------

    private void OnTransportStateChanged(PlaybackState state)
    {
        if (state == PlaybackState.Playing) RaiseCueForCurrentPosition();
        PublishSnapshotOnly();
    }

    private void OnClipCompleted(string segmentId)
    {
        long gen = Generation;
        var plan = _plan;
        if (plan is null) return;
        int index;
        lock (_gate) index = _segmentIndex;
        if (index < 0 || index >= plan.SegmentCount || plan.Segments[index].Id != segmentId) return;

        if (index + 1 >= plan.SegmentCount)
        {
            // End of document fires only after the last audible samples have drained, which the
            // transport signals by raising this event rather than by reaching the end of a buffer.
            Publish(PlaybackState.Completed, "Finished");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // Check the generation and that the index has not moved *before* mutating, in the
                // same lock: a completion queued before a seek must not clobber the seek's index.
                lock (_gate)
                {
                    if (gen != Generation || _segmentIndex != index) return;
                    _segmentIndex = index + 1;
                    ReleaseClipsOutsideWindow(plan, index + 1);
                }
                ExtendPreparation(gen);
                if (!await EnsureSegmentReadyAsync(index + 1, gen, CancellationToken.None).ConfigureAwait(false)) return;
                if (gen != Generation) return;
                await StartCurrentSegmentAsync(gen, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Publish(PlaybackState.Error, null, NarrationError.SpeechSynthesisFailed(ex.Message, segmentId));
            }
        });
    }

    /// <summary>How many passages behind the current one keep their audio in memory, for Previous and Replay.</summary>
    public const int RetainedPassagesBehind = 2;

    /// <summary>
    /// Drops prepared audio that is neither about to be played nor just was. Call with the lock held.
    /// </summary>
    /// <remarks>
    /// Prepared clips were only ever added. A 27-page paper is some 830 passages of about eight
    /// seconds each, which at 24 kHz float is roughly 650 MB by the last page. A released clip is
    /// not lost: it is still in the asset store when there is one, and is synthesised again when
    /// there is not.
    /// </remarks>
    private void ReleaseClipsOutsideWindow(NarrationPlan plan, int current)
    {
        int first = Math.Max(0, current - RetainedPassagesBehind);
        int last = Math.Min(plan.SegmentCount - 1, current + Math.Max(1, _options.LookaheadSegments));
        var keep = new HashSet<string>(StringComparer.Ordinal);
        for (int i = first; i <= last; i++) keep.Add(plan.Segments[i].Id);
        foreach (string id in _prepared.Keys.Where(id => !keep.Contains(id)).ToList()) _prepared.Remove(id);
    }

    private void OnUnderflow(string segmentId)
    {
        // An underflow freezes the reading position: the media clock stops, so cues stop with it.
        Publish(PlaybackState.Buffering, "Buffering");
        StartPreparation(Generation);
    }

    private void OnDeviceFailed(string detail)
        => Publish(PlaybackState.Error, null, NarrationError.AudioDeviceFailed(detail));

    // ---- cue publication ---------------------------------------------------------------

    /// <summary>
    /// Polls the media clock and publishes the cue it implies. Called by the host's frame loop;
    /// resolution is pure, so calling it while the clock is stationary cannot advance the reading
    /// position.
    /// </summary>
    public void Tick()
    {
        if (_plan is null || _scheduler is null) return;
        if (_audio.State is not PlaybackState.Playing) return;
        RaiseCueForCurrentPosition();
        if (_pendingFollow is { } follow && _clock.NowSeconds - _lastFollowAt >= 0.12) RaiseFollow(follow);
        PublishSnapshotOnly();
    }

    private void RaiseCueForCurrentPosition()
    {
        long generation = Generation;
        var scheduler = _scheduler;
        var plan = _plan;
        if (scheduler is null || plan is null) return;

        string segmentId;
        PreparedClip? prepared;
        lock (_gate)
        {
            if (_segmentIndex < 0 || _segmentIndex >= plan.SegmentCount) return;
            segmentId = plan.Segments[_segmentIndex].Id;
            prepared = _prepared.GetValueOrDefault(segmentId);
        }
        if (prepared is null) return;
        var asset = prepared.Asset;

        long sample = asset.Format.ToFrames(_audio.PositionSeconds);
        var cue = scheduler.Resolve(segmentId, sample) with { Generation = generation };
        bool changed;
        lock (_gate)
        {
            if (generation != Generation) return;
            changed = _lastCue is null
                || _lastCue.Generation != cue.Generation
                || _lastCue.SegmentId != cue.SegmentId
                || _lastCue.Cue?.StartSample != cue.Cue?.StartSample
                || _lastCue.Granularity != cue.Granularity;
            _lastCue = cue;
        }
        if (!changed) return;

        ActiveCueChanged?.Invoke(cue);
        RaiseFollow(cue);
    }

    private void RaiseFollowForCurrentCue()
    {
        _lastFollowAt = double.NegativeInfinity;
        if (_lastCue is { } cue) RaiseFollow(cue);
    }

    private void RaiseFollow(ActiveCue cue)
    {
        if (_options.FollowMode == FollowMode.Off) return;
        if (FollowSuspended) return;
        if (cue.Generation != Generation) return;
        if (_clock.NowSeconds - _lastFollowAt < 0.12) { _pendingFollow = cue; return; }
        var scheduler = _scheduler;
        if (scheduler is null) return;

        var rectsByPage = scheduler.SourceMap.HighlightRectsByPage(cue.AnchorIds);
        var pages = scheduler.SourceMap.PagesFor(cue.AnchorIds);
        if (pages.Count == 0) return;
        int page = pages[0];
        var rects = rectsByPage.TryGetValue(page, out var r) ? r : [];
        if (rects.Count == 0) return;
        _lastFollowAt = _clock.NowSeconds;
        _pendingFollow = null;
        FollowRequested?.Invoke(cue, rects, page);
    }

    // ---- snapshots ---------------------------------------------------------------------

    private void Publish(PlaybackState state, string? message, NarrationError? error = null)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { State = state, Message = message, Error = error };
        }
        PublishSnapshotOnly();
    }

    private void PublishSnapshotOnly()
    {
        var plan = _plan;
        PlaybackSnapshot snap;
        // Read the transport before taking the coordinator lock, never inside it. The transport has
        // its own lock and raises its events from its own threads; calling into it while holding
        // this lock is a lock-order inversion that froze the UI during ordinary playback.
        double positionSeconds = _audio.PositionSeconds;
        double clipDurationSeconds = _audio.ClipDurationSeconds;
        double bufferedAheadSeconds = _audio.BufferedAheadSeconds;
        lock (_gate)
        {
            int index = _segmentIndex;
            string? segmentId = plan is not null && index >= 0 && index < plan.SegmentCount ? plan.Segments[index].Id : null;
            snap = _snapshot with
            {
                Generation = Generation,
                SegmentId = segmentId,
                SegmentIndex = Math.Max(0, index),
                SegmentCount = plan?.SegmentCount ?? 0,
                PositionSeconds = positionSeconds,
                ClipDurationSeconds = clipDurationSeconds,
                ActiveCue = _lastCue?.Cue,
                ActiveAnchorIds = _lastCue?.AnchorIds ?? [],
                CueGranularity = _lastCue?.Granularity ?? CueGranularity.Sentence,
                PlaybackRate = _options.PlaybackRate,
                FollowMode = _options.FollowMode,
                FollowSuspended = _followSuspended,
                BufferedAheadSeconds = bufferedAheadSeconds,
            };
            _snapshot = snap;
        }
        SnapshotChanged?.Invoke(snap);
    }

    private long AdvanceGeneration()
    {
        lock (_gate)
        {
            _inflight.Clear();
            _lastCue = null;
            _pendingFollow = null;
            _lastFollowAt = double.NegativeInfinity;
            return Interlocked.Increment(ref _generation);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _prepareCts?.Cancel();
        _interpretCts?.Cancel();
        _lifetime.Cancel();
        try { if (_prepareLoop is not null) await _prepareLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* a failing preparation must not mask disposal */ }
        _audio.StateChanged -= OnTransportStateChanged;
        _audio.ClipCompleted -= OnClipCompleted;
        _audio.Underflow -= OnUnderflow;
        _audio.DeviceFailed -= OnDeviceFailed;
        _prepareCts?.Dispose();
        if (_aligner is IAsyncDisposable disposableAligner) await disposableAligner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A synthesised clip held ready for playback: its metadata and its samples.</summary>
internal sealed record PreparedClip(SpeechAsset Asset, byte[] Pcm);

/// <summary>Wall-clock monotonic time. Replaceable in tests so timing logic needs no waiting.</summary>
public sealed class SystemMonotonicClock : IMonotonicClock
{
    private static readonly long Origin = System.Diagnostics.Stopwatch.GetTimestamp();
    public double NowSeconds => (System.Diagnostics.Stopwatch.GetTimestamp() - Origin) / (double)System.Diagnostics.Stopwatch.Frequency;
}
