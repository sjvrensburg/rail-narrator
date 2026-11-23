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

    // ---- gapless continuation (optional) ----------------------------------------------
    // A transport that cannot queue keeps these defaults, and the coordinator loads each clip after
    // the previous one completes, as before.

    /// <summary>
    /// Prepares a clip to follow the loaded one with no gap. Returns false when it is too late or not
    /// supported; the caller then loads the clip when <see cref="ClipCompleted"/> fires.
    /// </summary>
    Task<bool> QueueNextAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>Forgets the queued clip, making sure none of it is heard.</summary>
    Task ClearQueuedAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Raised when a queued clip took over without a gap: (the clip that finished, the one now playing).
    /// Replaces <see cref="ClipCompleted"/> for that boundary.
    /// </summary>
    event Action<string, string>? ClipAdvanced { add { } remove { } }
}

/// <summary>Coordinator tuning.</summary>
/// <param name="LookaheadSegments">
/// The fewest passages to prepare ahead of playback, however long they are.
/// </param>
/// <param name="LookaheadSeconds">
/// How much listening to have ready beyond the passage being read: passages are prepared until
/// they would take this long to hear (at the playback rate), or until
/// <paramref name="MaxLookaheadSegments"/>. Counted in time, not passages: four short sentences
/// were 15 s, less than one slow preparation, and reading stopped mid-paragraph to wait for one.
/// </param>
/// <param name="MaxLookaheadSegments">
/// The most passages prepared ahead, and kept in memory, whatever their length. Bounded on
/// purpose: synthesising a whole book before playing it wastes minutes of inference and hundreds
/// of megabytes, and the first audible output is what the user is waiting for.
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
/// <param name="PreRollSeconds">
/// On Play, wait until the passages ready ahead of the reading position (no maths still waiting
/// for interpretation) would take this long to speak — so a run of equations at the start is
/// interpreted before reading begins, not one stall at a time. Zero turns it off.
/// </param>
/// <param name="PreRollTimeoutSeconds">The longest Play waits for that before starting anyway.</param>
public sealed record NarrationCoordinatorOptions(
    int LookaheadSegments = 4,
    int MaxConcurrentSynthesis = 1,
    string? VoiceId = null,
    AudioFormat? Format = null,
    double PlaybackRate = 1.0,
    FollowMode FollowMode = FollowMode.Follow,
    bool RequestTimings = true,
    int InterpretAheadSegments = 40,
    double PreRollSeconds = 60,
    double PreRollTimeoutSeconds = 20,
    double LookaheadSeconds = 60,
    int MaxLookaheadSegments = 40,
    WordTimingSource WordTiming = WordTimingSource.Aligner)
{
    /// <summary>Speaking rate used to estimate how long a passage takes, from its length (Kokoro at 1×).</summary>
    public const double EstimatedCharactersPerSecond = 15;

    public static NarrationCoordinatorOptions Default { get; } = new();
}

/// <summary>Where a passage's word timing is taken from when there is a choice.</summary>
public enum WordTimingSource
{
    /// <summary>
    /// The forced aligner when one is installed. What the speech engine reports with its audio is
    /// shown until the aligner's measurement arrives, and stands where there is no aligner.
    /// </summary>
    Aligner,

    /// <summary>
    /// The speech engine's own report (its attention, or its durations) when it gives a time for
    /// every word; the aligner is then not run for that passage. Sooner and with nothing to
    /// install, but coarser. A passage the engine could not time still goes to the aligner.
    /// </summary>
    Engine,
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
    // What each passage's preparation is doing right now, by segment id, so a wait the reader asked
    // for can say what it is waiting on. Written by whichever path is doing the work.
    private readonly ConcurrentDictionary<string, PreparationStage> _stages = new(StringComparer.Ordinal);

    /// <summary>Word alignments in flight, by segment: run after a passage is playable (AlignInBackground).</summary>
    private readonly ConcurrentDictionary<string, Task> _aligning = new(StringComparer.Ordinal);
    private long _loadSequence;
    private CancellationTokenSource? _interpretCts;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _interpretLoop;
    private long _interpretGeneration = -1;
    private int _interpretFrom = -1;
    // The passage handed to the transport to follow the current one without a gap, if any.
    private string? _queuedSegmentId;
    // The passage a seek in flight is heading to, valid only while that seek's generation is current
    // and it has not yet arrived. Next and Previous count from it, so presses made while a seek waits
    // for audio step on from where the reader is going, not from the passage still playing.
    private int _seekIndex = -1;
    private long _seekGeneration = -1;

    // A narration made earlier for the plan now loaded; null when there is none or the reader chose
    // another voice.
    private volatile IRecordedNarration? _recording;

    /// <summary>True while passages are played from a saved narration rather than spoken afresh.</summary>
    public bool PlaysFromRecording => _recording is not null;

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
        _audio.ClipAdvanced += OnClipAdvanced;
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

    /// <summary>Raised when follow mode should be re-applied, with the cue and passage to frame.</summary>
    public event Action<ActiveCue, FollowTarget>? FollowRequested;

    /// <summary>True when manual browsing has suspended camera following. Audio keeps playing.</summary>
    public bool FollowSuspended { get { lock (_gate) return _followSuspended; } }

    public FollowMode FollowMode => _options.FollowMode;

    // ---- lifecycle --------------------------------------------------------------------

    /// <summary>
    /// Loads a plan and positions the reader at <paramref name="resume"/> if supplied. Does not
    /// start playback: a document must never begin reading itself on open.
    /// </summary>
    /// <param name="recording">
    /// A narration of this plan made earlier: its passages are played from it, with no engine and
    /// no model asked, for as long as the reader keeps its voice.
    /// </param>
    public async Task LoadPlanAsync(NarrationPlan plan, ReaderPosition? resume = null, CancellationToken ct = default,
        IRecordedNarration? recording = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfDisposed();
        long load = Interlocked.Increment(ref _loadSequence);
        AdvanceGeneration();   // work for the document being replaced stops now

        await _audio.StopAsync(ct).ConfigureAwait(false);
        // A later load that finished while this one waited for the transport has the document the
        // reader asked for last; this one must not put its plan back over it.
        if (load != Interlocked.Read(ref _loadSequence)) return;
        long gen;
        lock (_gate)
        {
            // A new plan is a new generation from here. Anything that began while the transport
            // was stopping (a voice change is not awaited by the application) belongs to the
            // document before, and must neither prepare for it nor cancel this plan's lookahead.
            gen = AdvanceGeneration();
            _plan = plan;
            _recording = recording;
            _scheduler = new CueScheduler(plan);
            _prepared.Clear();
            _stages.Clear();
            // Browsing the previous document must not leave the camera paused in this one.
            _followSuspended = false;
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
        WarmUpAligner();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private int _alignerWarming;

    /// <summary>
    /// Starts the word aligner while the reader is still looking at the page, once per session,
    /// rather than leaving it to the first passage that needs fresh audio.
    /// </summary>
    private void WarmUpAligner()
    {
        if (!_options.RequestTimings || _aligner is not { } aligner || Interlocked.Exchange(ref _alignerWarming, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await aligner.WarmUpAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (Exception) { /* the first alignment reports what is wrong */ }
        });
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
        // A fresh start is a request to read from here: following resumes, as for a seek.
        lock (_gate) _followSuspended = false;
        Publish(PlaybackState.Preparing, "Preparing speech");
        StartPreparation(gen);
        await PreRollAsync(gen, ct).ConfigureAwait(false);
        if (gen != Generation) return;
        if (!await WaitForSegmentAsync(_segmentIndex, gen, PlaybackState.Preparing, ct).ConfigureAwait(false)) return;
        await StartCurrentSegmentAsync(gen, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Seconds of narration from the reading position before the first passage still waiting for
    /// interpretation, estimated from its length; infinity when none is waiting within the window.
    /// </summary>
    private double ReadySecondsAhead()
    {
        var plan = _plan;
        if (plan is null) return double.PositiveInfinity;
        int from;
        lock (_gate) from = Math.Max(0, _segmentIndex);
        double rate = _options.PlaybackRate <= 0 ? 1 : _options.PlaybackRate;
        double seconds = 0;
        int end = Math.Min(plan.SegmentCount, from + Math.Max(1, _options.InterpretAheadSegments));
        for (int i = from; i < end; i++)
        {
            var segment = plan.Segments[i];
            if (segment.Provenance == ScriptProvenance.PendingInterpretation) return seconds;
            seconds += segment.SpokenText.Length / NarrationCoordinatorOptions.EstimatedCharactersPerSecond / rate;
        }
        return double.PositiveInfinity;
    }

    /// <summary>
    /// Waits, bounded, for interpretation to get ahead of the reading position (see
    /// <see cref="NarrationCoordinatorOptions.PreRollSeconds"/>). Returns at once when nothing is
    /// pending — the usual case once a document's maths is cached.
    /// </summary>
    private async Task PreRollAsync(long generation, CancellationToken ct)
    {
        if (_resolver is null || _options.PreRollSeconds <= 0) return;
        // Wall time, not the injected clock: a bound on how long the reader waits.
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (generation == Generation && !_disposed)
        {
            double ready = ReadySecondsAhead();
            if (ready >= _options.PreRollSeconds) return;
            double waited = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (waited >= _options.PreRollTimeoutSeconds) return;
            var plan = _plan;
            int index;
            lock (_gate) index = Math.Max(0, _segmentIndex);
            var progress = plan is null || index >= plan.SegmentCount ? null : new PreparationProgress(
                plan.Segments[index].Id, index, PreparationStage.InterpretingAhead, waited, ready, _options.PreRollSeconds);
            Publish(PlaybackState.Buffering, progress?.Describe()
                ?? $"Interpreting maths before reading ({ready:F0} of {_options.PreRollSeconds:F0} s ready)", preparation: progress);
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
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
        // The reader asked to read from this passage (Next, Previous, a click, the script list), so
        // a pause in following from browsing ends here rather than leaving the view where it was
        // while the reading starts somewhere else.
        lock (_gate) { _seekIndex = index; _seekGeneration = gen; _followSuspended = false; }
        Publish(PlaybackState.Seeking, "Seeking");
        await _audio.StopAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (gen != Generation) return;   // superseded by a later seek while stopping
            _segmentIndex = index;
            _seekGeneration = -1;
        }
        StartPreparation(gen);
        if (!await WaitForSegmentAsync(index, gen, PlaybackState.Seeking, ct).ConfigureAwait(false)) return;
        await StartCurrentSegmentAsync(gen, ct, withinSegmentSeconds).ConfigureAwait(false);
    }

    public Task NextSegmentAsync(CancellationToken ct = default)
    {
        var plan = _plan;
        if (plan is null) return Task.CompletedTask;
        int next;
        lock (_gate) next = Math.Min(StepOriginCore() + 1, plan.SegmentCount - 1);
        return next < 0 ? Task.CompletedTask : GoToSegmentAsync(plan.Segments[next].Id, 0, ct);
    }

    public Task PreviousSegmentAsync(CancellationToken ct = default)
    {
        var plan = _plan;
        if (plan is null || plan.SegmentCount == 0) return Task.CompletedTask;
        int prev;
        lock (_gate)
        {
            int origin = StepOriginCore();
            if (origin < 0) return Task.CompletedTask;
            prev = Math.Max(origin - 1, 0);
        }
        return GoToSegmentAsync(plan.Segments[prev].Id, 0, ct);
    }

    /// <summary>
    /// Where Next and Previous count from: the target of a seek still in flight, else the current
    /// passage. Call with the lock held.
    /// </summary>
    private int StepOriginCore() => _seekGeneration == Generation && _seekIndex >= 0 ? _seekIndex : _segmentIndex;

    /// <summary>Replays the current segment from its start.</summary>
    public Task ReplaySegmentAsync(CancellationToken ct = default)
    {
        var plan = _plan;
        if (plan is null) return Task.CompletedTask;
        int i;
        lock (_gate) i = StepOriginCore();
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

    /// <summary>
    /// Re-checks the camera against the current cue now, for a change of view that does not leave the
    /// reading position (a zoom). Leaves a suspension from panning in place, and does not wait for the
    /// follow throttle or for the next cue.
    /// </summary>
    public void RefollowCurrentCue() => RaiseFollowForCurrentCue();

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

        bool wasPlaying = _audio.State == PlaybackState.Playing;
        _options = _options with { VoiceId = voiceId };
        // Another voice was asked for: the recording is in its own, so the engine speaks from here.
        _recording = null;
        long generation = AdvanceGeneration();
        // Discarded now, with the change, not once the transport has stopped: a seek that finishes
        // while it is stopping would otherwise find the old voice's clip ready and play it.
        lock (_gate) _prepared.Clear();
        await _audio.StopAsync(ct).ConfigureAwait(false);

        // A load or a seek that finished while the transport was stopping prepares for itself, in
        // the new voice. Carrying on here would clear what it prepared.
        if (generation != Generation)
        {
            PublishSnapshotOnly();
            return;
        }
        _lastCue = null;

        StartPreparation(generation);
        if (_plan is not null && wasPlaying)
        {
            if (await WaitForSegmentAsync(Math.Max(0, _segmentIndex), generation, PlaybackState.Preparing, ct).ConfigureAwait(false)
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
        // A queued clip may be this passage in its old words.
        else await _audio.ClearQueuedAsync(ct).ConfigureAwait(false);
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
        // A queued clip may be the edited passage in its old words.
        else await _audio.ClearQueuedAsync(ct).ConfigureAwait(false);
        if (_assetStore is not null)
            await _assetStore.InvalidateAsync(_plan.Header.Extraction.DocumentContentHash, ct).ConfigureAwait(false);
        StartPreparation(gen);
        PublishSnapshotOnly();
    }

    // ---- preparation ------------------------------------------------------------------

    private void StartPreparation(long generation)
    {
        // An operation overtaken while it awaited (the transport stopping, a cache being cleared)
        // must not start preparing: a loop for an older generation prepares nothing, and starting
        // it cancels the lookahead of whatever overtook it.
        if (generation != Generation) return;
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
        // Asked for only when a passage has to be spoken: a document with a saved narration is
        // read with no speech engine installed at all.
        (TtsCapabilities Caps, AudioFormat Format, string Voice)? engine = null;

        int start;
        lock (_gate) start = Math.Max(0, _segmentIndex);
        int end = Math.Min(plan.SegmentCount, start + LookaheadCap);
        int fewest = Math.Max(1, _options.LookaheadSegments);

        // One expensive inference job at a time by default: OCR, vision, LLM and TTS contend for the
        // same memory, and tuning concurrency before measuring is how a reader runs out of RAM.
        using var limiter = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentSynthesis));
        double ready = 0;   // listening time prepared beyond the passage being read
        for (int i = start; i < end && !ct.IsCancellationRequested; i++)
        {
            if (generation != Generation) return;
            if (i - start >= fewest && ready >= _options.LookaheadSeconds) return;
            var segment = plan.Segments[i];
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!await PrepareFromRecordingAsync(segment, generation, ct).ConfigureAwait(false))
                {
                    engine ??= await EngineAsync(ct).ConfigureAwait(false);
                    if (engine is not { } e) return;   // said by EngineAsync
                    await PrepareSegmentAsync(segment, e.Caps, e.Format, e.Voice, generation, ct).ConfigureAwait(false);
                    // Its word timing is the next expensive job: wait for it here, so the bound holds,
                    // while playback (which does not wait for it) can already play the passage.
                    if (_aligning.TryGetValue(segment.Id, out var aligning)) await aligning.ConfigureAwait(false);
                }
            }
            finally { limiter.Release(); }
            if (i > start) ready += PreparedListeningSeconds(segment.Id);
        }
    }

    /// <summary>Passages that may be prepared ahead and held, beyond the one being read.</summary>
    private int LookaheadCap => Math.Max(Math.Max(1, _options.LookaheadSegments), _options.MaxLookaheadSegments);

    /// <summary>How long a prepared passage takes to hear at the playback rate; zero when it is not prepared.</summary>
    private double PreparedListeningSeconds(string segmentId)
    {
        lock (_gate)
        {
            if (!_prepared.TryGetValue(segmentId, out var clip) || clip.Asset.Format.SampleRate <= 0) return 0;
            return clip.Asset.FrameCount / (double)clip.Asset.Format.SampleRate / Math.Max(0.25, _options.PlaybackRate);
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

    /// <summary>
    /// The engine, the format and the voice to speak with, or null when there is no usable engine or
    /// voice, which is then published as the error.
    /// </summary>
    private async Task<(TtsCapabilities Caps, AudioFormat Format, string Voice)?> EngineAsync(CancellationToken ct)
    {
        var caps = await _tts.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!caps.IsAvailable)
        {
            Publish(PlaybackState.Error, null, NarrationError.ProviderUnavailable(caps.Unavailable ?? "Unknown reason."));
            return null;
        }
        var format = _options.Format ?? caps.PreferredFormat ?? AudioFormat.Mono44100Float32;
        string? voice;
        lock (_gate) voice = _options.VoiceId;
        if (string.IsNullOrEmpty(voice))
        {
            var voices = await _tts.ListVoicesAsync(ct).ConfigureAwait(false);
            voice = voices.FirstOrDefault(v => v.IsDefault)?.Id ?? voices.FirstOrDefault()?.Id;
            if (string.IsNullOrEmpty(voice))
            {
                Publish(PlaybackState.Error, null, NarrationError.ProviderUnavailable("The speech provider has no voices installed."));
                return null;
            }
            _options = _options with { VoiceId = voice };
        }
        return (caps, format, voice);
    }

    /// <summary>
    /// Takes the passage from the saved narration when it is there as scripted. False when there is
    /// no recording or it does not hold this passage; the engine then speaks it.
    /// </summary>
    private async Task<bool> PrepareFromRecordingAsync(NarrationSegment segment, long generation, CancellationToken ct)
    {
        var recording = _recording;
        if (recording is null) return false;
        lock (_gate) if (_prepared.ContainsKey(segment.Id)) return true;
        RecordedClip? clip;
        try { clip = await recording.TryGetAsync(segment, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { clip = null; }   // a damaged file is a passage not recorded, not a stop
        if (clip is null) return false;
        lock (_gate)
        {
            if (generation != Generation || !ReferenceEquals(recording, _recording)) return true;
            _prepared[segment.Id] = new PreparedClip(clip.Asset, clip.Pcm.ToArray());
            AttachTimings(segment, clip.Asset);
        }
        await TryQueueNextAsync(generation).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Narrates the whole plan, first passage to last: each is interpreted if it is maths, spoken,
    /// and its words timed when an aligner is installed, then handed to <paramref name="sink"/>
    /// with its final script. For a coordinator of its own (see <see cref="NullAudioTransport"/>):
    /// passages are released as they are handed over, and a seek or a voice change ends it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No engine, or a passage could not be spoken; the message says which.</exception>
    public async Task NarrateAllAsync(
        Func<NarrationSegment, SpeechAsset, ReadOnlyMemory<byte>, CancellationToken, Task> sink,
        IProgress<NarrationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ThrowIfDisposed();
        var plan = _plan ?? throw new InvalidOperationException("No document is loaded.");
        long generation = Generation;
        var engine = await EngineAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(Snapshot.Error?.Message ?? "No speech engine is available.");
        double seconds = 0;
        for (int i = 0; i < plan.SegmentCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            string id = plan.Segments[i].Id;
            await PrepareSegmentAsync(plan.Segments[i], engine.Caps, engine.Format, engine.Voice, generation, ct).ConfigureAwait(false);
            if (_aligning.TryGetValue(id, out var aligning)) await aligning.WaitAsync(ct).ConfigureAwait(false);
            if (generation != Generation) throw new InvalidOperationException("The narration was interrupted by another request.");

            PreparedClip? clip;
            NarrationSegment resolved;
            lock (_gate)
            {
                _prepared.TryGetValue(id, out clip);
                // Interpretation replaced the passage in the plan: that is the script that was spoken.
                resolved = (_plan ?? plan).Segments.FirstOrDefault(s => s.Id == id) ?? plan.Segments[i];
            }
            if (clip is null)
                throw new InvalidOperationException(
                    $"Passage {i + 1} of {plan.SegmentCount} could not be spoken: {Snapshot.Error?.Message ?? "no audio was produced"}");
            await sink(resolved, clip.Asset, clip.Pcm, ct).ConfigureAwait(false);
            lock (_gate) _prepared.Remove(id);
            seconds += clip.Asset.DurationSeconds;
            progress?.Report(new NarrationProgress(i + 1, plan.SegmentCount, resolved, seconds));
        }
    }

    private async Task<NarrationSegment> ResolveSegmentAsync(NarrationSegment segment, CancellationToken ct)
    {
        var plan = _plan;
        if (plan is null || _resolver is null) return segment;
        string key = $"{plan.Header.PlanId}:{plan.Header.InputFingerprint}:{segment.Id}:{segment.ScriptRevision}";
        _stages[segment.Id] = PreparationStage.Interpreting;
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
        _stages.TryRemove(new KeyValuePair<string, PreparationStage>(segment.Id, PreparationStage.Interpreting));
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
            _stages.TryRemove(segment.Id, out _);
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
            _stages[segment.Id] = PreparationStage.LoadingCached;
            var cached = await _assetStore.TryGetAsync(assetId, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                var cachedPcm = await _assetStore.TryLoadPcmAsync(assetId, ct).ConfigureAwait(false);
                if (cachedPcm is { } pcm && generation == Generation)
                {
                    // Word timing from another aligner revision is not used while it is measured
                    // again: the passage is highlighted whole until the new timing arrives.
                    if (cached.HasTimings && cached.TimingProvenance == TimingProvenance.ForcedAlignment
                        && !IsCurrentAlignment(segment, cached, pcm))
                        cached = cached with { Timings = [], TimingProvenance = TimingProvenance.ClipBoundary };
                    lock (_gate)
                    {
                        if (generation != Generation) return;
                        _prepared[segment.Id] = new PreparedClip(cached, pcm.ToArray());
                        AttachTimings(segment, cached);
                    }
                    await TryQueueNextAsync(generation).ConfigureAwait(false);
                    AlignInBackground(segment, cached, pcm, extra: null);
                    return;
                }
            }
        }

        int maxChars = caps.MaxTextChars > 0 ? caps.MaxTextChars : int.MaxValue;
        // The engine is given letter names for lone symbols; the script and its offsets stay as written.
        var speech = segment.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? LetterNames.ForSpeech(segment.SpokenText)
            : new LetterNames.SpeechForm(segment.SpokenText, []);
        string text = speech.Text;
        var warnings = new List<string>();

        TtsSynthesisResult result;
        _stages[segment.Id] = PreparationStage.Synthesising;
        try
        {
            result = await SynthesizeWithinLimitAsync(text, voice, segment.Language, format, maxChars, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (generation != Generation) return;
            Publish(PlaybackState.Error, null, NarrationError.SpeechSynthesisFailed(ex.Message, segment.Id));
            return;
        }

        if (generation != Generation) return;   // a late result from a superseded plan
        if (result.Timings is { Count: > 0 } native)
        {
            // A provider's own timings point into the text it was given, which names lone letters.
            if (speech.Changes.Count > 0)
                native = native.Select(c => c.SpokenRange is { } r ? c with { SpokenRange = speech.ToScript(r) } : c).ToArray();
            // Word cues belong to the script's words, and only where the script is the page's words.
            result = result with { Timings = SpeaksItsSourceWordForWord(segment) ? ToScriptWords(native, segment.SpokenText) : WithoutWordCues(native) };
        }

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
        await TryQueueNextAsync(generation).ConfigureAwait(false);
        AlignInBackground(segment, asset, result.Pcm, warnings);
    }

    /// <summary>
    /// Times the words of a passage that can already be played, and attaches the timing when it
    /// arrives. Until then the passage is highlighted whole, which is honest: nothing is guessed.
    /// </summary>
    /// <remarks>
    /// A passage used to become playable only once its words were aligned, so alignment sat
    /// between synthesis and playback. The first alignment of a session starts the aligner (23 to
    /// 56 s, measured), and reading stopped mid-paragraph for it. Timing that arrives while the
    /// passage plays takes effect from then: cue resolution is a pure function of the position.
    /// </remarks>
    private void AlignInBackground(NarrationSegment segment, SpeechAsset asset, ReadOnlyMemory<byte> pcm,
        IReadOnlyList<string>? extra)
    {
        // Not the caller's token: Play's ends when the passage starts, and this outlives it. A
        // result for a superseded generation is dropped below instead.
        var ct = _lifetime.Token;
        if (!_options.RequestTimings || _aligner is null || !SpeaksItsSourceWordForWord(segment)) return;
        if (EngineTimingStands(asset)) return;
        var task = Task.Run(async () =>
        {
            try
            {
                var aligned = await AlignAssetAsync(segment, asset, pcm, ct).ConfigureAwait(false);
                if (ReferenceEquals(aligned, asset)) return;
                if (_assetStore is not null) await _assetStore.StoreAsync(aligned, pcm, ct).ConfigureAwait(false);
                // Not the generation: a Play or a seek moves it on without changing this audio, and
                // the passage, already prepared, is not aligned again. The timing belongs to the
                // clip held for the passage if it is this audio (same words, revision and voice);
                // a new document clears the held clips, so nothing stale can match.
                lock (_gate)
                {
                    if (!_prepared.TryGetValue(segment.Id, out var clip) || clip.Asset.Id != aligned.Id) return;
                    _prepared[segment.Id] = clip with { Asset = aligned };
                    AttachTimings(segment, aligned, extra);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* the passage keeps its whole-sentence highlight */ }
            finally
            {
                _stages.TryRemove(new KeyValuePair<string, PreparationStage>(segment.Id, PreparationStage.Aligning));
            }
        });
        _aligning[segment.Id] = task;
        _ = task.ContinueWith(t => ((ICollection<KeyValuePair<string, Task>>)_aligning)
            .Remove(new KeyValuePair<string, Task>(segment.Id, t)), TaskScheduler.Default);
    }

    /// <summary>
    /// Synthesises <paramref name="text"/>, in pieces when it is longer than the provider accepts,
    /// joined into one clip. The pieces' timings are moved to their place in the clip and the text.
    /// </summary>
    /// <remarks>
    /// The length of a passage is not known until it is spoken: an interpretation is written by the
    /// model just before synthesis. One of 1,239 characters (a sentence leading into a display
    /// equation) met Kokoro's 1,200-character limit and was refused with an error, and reading
    /// stopped there for good: every Play reached the same passage. Splitting where a sentence, then a
    /// clause, then a word ends keeps the voice's phrasing; the pieces are one passage to the reader.
    /// </remarks>
    private async Task<TtsSynthesisResult> SynthesizeWithinLimitAsync(
        string text, string voice, string language, AudioFormat format, int maxChars, CancellationToken ct)
    {
        TtsSynthesisRequest Request(string piece) => new(piece, voice, language, format, 1.0, Seed: null, _options.RequestTimings);
        if (text.Length <= maxChars) return await _tts.SynthesizeAsync(Request(text), ct).ConfigureAwait(false);

        var pieces = SynthesisPieces(text, maxChars);
        var pcm = new List<ReadOnlyMemory<byte>>();
        var cues = new List<TimedCue>();
        long frames = 0;
        AudioFormat? produced = null;
        TimingProvenance provenance = TimingProvenance.None;
        var warnings = new List<string>();
        foreach (var piece in pieces)
        {
            var part = await _tts.SynthesizeAsync(Request(text.Substring(piece.Start, piece.Length)), ct).ConfigureAwait(false);
            if (part.IsSilentOrEmpty) return part;   // reported as silence by the caller, as for a whole passage
            if (produced is { } first && first != part.Format)
                throw new InvalidOperationException("The speech provider returned the pieces of one passage in different audio formats.");
            produced = part.Format;
            foreach (var cue in part.Timings)
                cues.Add(cue with
                {
                    StartSample = cue.StartSample + frames,
                    EndSample = cue.EndSample + frames,
                    SpokenRange = cue.SpokenRange is { } r ? new TextRange(r.Start + piece.Start, r.Length) : null,
                });
            if (part.Timings.Count > 0) provenance = part.TimingProvenance;
            frames += part.FrameCount;
            pcm.Add(part.Pcm);
            warnings.AddRange(part.Warnings);
        }
        var joined = new byte[pcm.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in pcm) { p.Span.CopyTo(joined.AsSpan(offset)); offset += p.Length; }
        warnings.Add($"Synthesised in {pieces.Count} pieces: longer than the speech provider's {maxChars}-character limit.");
        return new TtsSynthesisResult(joined, produced!.Value, frames, cues, provenance, null, null, warnings);
    }

    /// <summary>
    /// Cuts <paramref name="text"/> into consecutive ranges of at most <paramref name="maxChars"/>,
    /// each ending where a sentence ends if one does, else a clause, else a word, else at the limit.
    /// A range never starts with whitespace (providers trim it, which would shift timings).
    /// </summary>
    public static IReadOnlyList<TextRange> SynthesisPieces(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxChars < 1) throw new ArgumentOutOfRangeException(nameof(maxChars));
        var pieces = new List<TextRange>();
        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        while (start < text.Length)
        {
            int end;
            if (text.Length - start <= maxChars) end = text.Length;
            else
            {
                int limit = start + maxChars;   // exclusive
                end = LastBreak(text, start, limit, ".?!") ?? LastBreak(text, start, limit, ";:,") ?? LastBreak(text, start, limit, null) ?? limit;
            }
            int trimmedEnd = end;
            while (trimmedEnd > start && char.IsWhiteSpace(text[trimmedEnd - 1])) trimmedEnd--;
            if (trimmedEnd > start) pieces.Add(new TextRange(start, trimmedEnd - start));
            start = end;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        }
        return pieces;

        // The end (exclusive) of the last break in [from, limit): after a mark in "marks" followed by
        // whitespace, or after whitespace itself when "marks" is null. Breaks in the first half are
        // ignored, so a piece is never cut to a sliver.
        static int? LastBreak(string s, int from, int limit, string? marks)
        {
            int floor = from + (limit - from) / 2;
            for (int i = Math.Min(limit, s.Length) - 1; i > floor; i--)
            {
                if (!char.IsWhiteSpace(s[i])) continue;
                if (marks is null) return i;
                if (marks.Contains(s[i - 1])) return i;
            }
            return null;
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

    private static IReadOnlyList<TimedCue> WithoutWordCues(IReadOnlyList<TimedCue> cues)
        => cues.Any(c => c.Granularity == CueGranularity.Word) ? cues.Where(c => c.Granularity != CueGranularity.Word).ToArray() : cues;

    /// <summary>
    /// An engine's word cues as cues for the script's own words (<see cref="SpokenWord.Tokenize"/>):
    /// each takes the range of the script word it falls in, and cues that fall in one word (a
    /// letter spoken by a two-word name, "double you") become one. Empty when a cue belongs to no
    /// word of the script: a timing that cannot be placed is not shown.
    /// </summary>
    public static IReadOnlyList<TimedCue> ToScriptWords(IReadOnlyList<TimedCue> cues, string script)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (!cues.Any(c => c.Granularity == CueGranularity.Word)) return cues;
        var words = SpokenWord.Tokenize(script);
        var placed = new List<TimedCue>(cues.Count);
        int w = 0;
        foreach (var cue in cues.OrderBy(c => c.StartSample))
        {
            if (cue.Granularity != CueGranularity.Word) { placed.Add(cue); continue; }
            if (cue.SpokenRange is not { } range) return [];
            while (w < words.Count && words[w].Range.End <= range.Start) w++;
            if (w >= words.Count || words[w].Range.Start > range.Start) return [];
            if (placed.Count > 0 && placed[^1].Granularity == CueGranularity.Word && placed[^1].SpokenRange == words[w].Range)
                placed[^1] = placed[^1] with { EndSample = Math.Max(placed[^1].EndSample, cue.EndSample) };
            else
                placed.Add(cue with { SpokenRange = words[w].Range });
        }
        return placed;
    }

    /// <summary>True when the engine timed every word of the passage and the reader prefers that to the aligner.</summary>
    private bool EngineTimingStands(SpeechAsset asset)
        => _options.WordTiming == WordTimingSource.Engine
           && asset.TimingProvenance is TimingProvenance.ModelAttention or TimingProvenance.NativeDuration
           && asset.Timings.Count > 0 && asset.Timings.All(c => c.Granularity == CueGranularity.Word);

    private IReadOnlyList<TimedCue> BindTimings(NarrationSegment segment, IReadOnlyList<TimedCue> timings)
        => timings.Select(c => c.SpokenRange is { } range
            ? c with { AnchorIds = segment.SourceLinks.Where(l => l.SpokenRange.Overlaps(range))
                .SelectMany(l => l.AnchorIds).Distinct().ToArray(), Granularity = segment.IsMaths ? CueGranularity.Equation : c.Granularity }
            : c).ToArray();

    /// <summary>
    /// True when a passage's words are the page's words in order, so each can be timed and shown
    /// where it is printed. A paraphrase, an interpretation still pending or an announcement is
    /// linked to its region as a whole and is highlighted, and framed, as a whole.
    /// </summary>
    public static bool SpeaksItsSourceWordForWord(NarrationSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return !segment.IsMaths
            && segment.Provenance is ScriptProvenance.SourceVerbatim or ScriptProvenance.UserEdited;
    }

    private async Task<SpeechAsset> AlignAssetAsync(NarrationSegment segment, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        // A paraphrase or an announcement has no word-for-word link to the page: highlight the whole
        // passage rather than align words that would point nowhere.
        if (!_options.RequestTimings || _aligner is null || !SpeaksItsSourceWordForWord(segment)) return asset;
        // The initial bundled dictionary/acoustic model is English. Never apply it to other languages.
        if (!segment.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return asset;
        string hash = CacheKey.Sha256Hex(pcm.Span);
        try
        {
            _stages[segment.Id] = PreparationStage.Aligning;
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
            // What the engine itself reported is still evidence when the aligner fails.
            if (asset.HasTimings && asset.TimingProvenance is TimingProvenance.ModelAttention or TimingProvenance.NativeDuration)
                return asset with { TimingWarnings = asset.TimingWarnings.Append("Word alignment unavailable: " + ex.Message).Distinct().ToArray() };
            return asset with { Timings = [], TimingProvenance = TimingProvenance.ClipBoundary,
                AlignmentRevision = null, PcmHash = hash, TimingWarnings = ["Word alignment unavailable: " + ex.Message] };
        }
    }

    /// <summary>Whether a clip's word timing was measured by the aligner configured now, on this audio and script.</summary>
    private bool IsCurrentAlignment(NarrationSegment segment, SpeechAsset asset, ReadOnlyMemory<byte> pcm)
    {
        if (_aligner is null) return true;   // nothing to measure it again with: it is the best evidence there is
        string hash = CacheKey.Sha256Hex(pcm.Span);
        return asset.PcmHash == hash
            && asset.AlignmentRevision == CacheKey.ForAlignment(hash, "forced", _aligner.Revision, CacheKey.Sha256Hex(segment.SpokenText));
    }

    private static string SynthesisOptionsToken(AudioFormat format)
        => $"rate={format.SampleRate};ch={format.Channels};fmt={(int)format.SampleFormat};pv={NarrationVersions.ProsePipeline}";

    /// <summary>How long a wait runs before it is reported, so a passage already prepared does not flash a message.</summary>
    private static readonly TimeSpan WaitReportDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// <see cref="EnsureSegmentReadyAsync"/> for a start the reader asked for, publishing what the wait
    /// is for (<see cref="PlaybackSnapshot.Preparation"/>) and how long it has lasted, about four
    /// times a second, until the audio is ready, the wait is superseded or preparation fails.
    /// </summary>
    private async Task<bool> WaitForSegmentAsync(int index, long generation, PlaybackState waitState, CancellationToken ct)
    {
        var plan = _plan;
        if (plan is null || index < 0 || index >= plan.SegmentCount) return false;
        string segmentId = plan.Segments[index].Id;
        lock (_gate) if (_prepared.ContainsKey(segmentId)) return true;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var stop = new CancellationTokenSource();
        var reporter = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WaitReportDelay, stop.Token).ConfigureAwait(false);
                while (!stop.IsCancellationRequested && generation == Generation && !_disposed)
                {
                    // A failure published by the preparation itself stands; do not paper over it.
                    if (Snapshot.State == PlaybackState.Error) return;
                    var stage = _stages.TryGetValue(segmentId, out var s) ? s
                        : plan.Segments[index].Provenance == ScriptProvenance.PendingInterpretation
                            ? PreparationStage.Interpreting : PreparationStage.Synthesising;
                    var progress = new PreparationProgress(segmentId, index, stage,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
                    Publish(waitState, progress.Describe(), preparation: progress);
                    await Task.Delay(250, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            return await EnsureSegmentReadyAsync(index, generation, ct).ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            await reporter.ConfigureAwait(false);
        }
    }

    /// <summary>Waits until a segment has audio, preparing it directly if the lookahead has not reached it.</summary>
    private async Task<bool> EnsureSegmentReadyAsync(int index, long generation, CancellationToken ct)
    {
        var plan = _plan;
        if (plan is null || index < 0 || index >= plan.SegmentCount) return false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            lock (_gate) if (_prepared.ContainsKey(plan.Segments[index].Id)) return true;
            if (generation != Generation) return false;

            if (await PrepareFromRecordingAsync(plan.Segments[index], generation, ct).ConfigureAwait(false)) continue;
            if (await EngineAsync(ct).ConfigureAwait(false) is not { } engine) return false;
            await PrepareSegmentAsync(plan.Segments[index], engine.Caps, engine.Format, engine.Voice, generation, ct).ConfigureAwait(false);
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
        await TryQueueNextAsync(generation).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the next passage to the transport to follow the current one without a gap, once its
    /// audio is prepared. Called when a passage starts and whenever a clip is prepared; does nothing
    /// when the next passage is not ready yet, is already queued, or the generation has moved on.
    /// </summary>
    private async Task TryQueueNextAsync(long generation)
    {
        var plan = _plan;
        if (plan is null) return;
        string nextId;
        PreparedClip? clip;
        lock (_gate)
        {
            if (generation != Generation) return;
            int index = _segmentIndex;
            if (index < 0 || index + 1 >= plan.SegmentCount) return;
            nextId = plan.Segments[index + 1].Id;
            if (_queuedSegmentId == nextId) return;
            clip = _prepared.GetValueOrDefault(nextId);
            if (clip is null) return;
            _queuedSegmentId = nextId;
        }
        // Outside the coordinator lock: never call the transport while holding it.
        bool queued;
        try { queued = await _audio.QueueNextAsync(nextId, clip.Asset, clip.Pcm).ConfigureAwait(false); }
        catch (Exception) { queued = false; }   // the clip is loaded the old way when this one completes
        if (!queued)
            lock (_gate) if (_queuedSegmentId == nextId) _queuedSegmentId = null;
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
                if (!await WaitForSegmentAsync(index + 1, gen, PlaybackState.Buffering, CancellationToken.None).ConfigureAwait(false)) return;
                if (gen != Generation) return;
                await StartCurrentSegmentAsync(gen, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Publish(PlaybackState.Error, null, NarrationError.SpeechSynthesisFailed(ex.Message, segmentId));
            }
        });
    }

    /// <summary>
    /// The transport moved from one passage to the queued next one without a gap. The reading
    /// position follows; nothing is loaded, since the audio is already playing.
    /// </summary>
    private void OnClipAdvanced(string completedId, string startedId)
    {
        long gen = Generation;
        var plan = _plan;
        if (plan is null) return;
        lock (_gate)
        {
            int index = _segmentIndex;
            // Only the boundary the coordinator expects: the current passage giving way to the next.
            // Anything else is a clip from before a seek or a stop, and must not move the position.
            if (gen != Generation || _seekGeneration == gen) return;
            if (index < 0 || index + 1 >= plan.SegmentCount
                || plan.Segments[index].Id != completedId || plan.Segments[index + 1].Id != startedId) return;
            int started = index + 1;
            _segmentIndex = started;
            _queuedSegmentId = null;
            ReleaseClipsOutsideWindow(plan, started);
        }
        ExtendPreparation(gen);
        RaiseCueForCurrentPosition();
        PublishSnapshotOnly();
        _ = Task.Run(() => TryQueueNextAsync(gen));
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
        int last = Math.Min(plan.SegmentCount - 1, current + LookaheadCap);
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

        if (BuildFollowTarget(scheduler, cue) is not { } target) return;
        _lastFollowAt = _clock.NowSeconds;
        _pendingFollow = null;
        FollowRequested?.Invoke(cue, target);
    }

    private static FollowTarget? BuildFollowTarget(CueScheduler scheduler, ActiveCue cue)
    {
        var map = scheduler.SourceMap;
        // A word is shown on the page it is printed on. A coarser cue has no timing inside it, so
        // when it crosses a page break the page holding most of it is shown (SourceMap.FramingPage).
        if (map.FramingPage(cue.AnchorIds, wholePassage: cue.Granularity != CueGranularity.Word) is not { } page) return null;
        var rects = map.HighlightRects(cue.AnchorIds, page);
        if (rects.Count == 0) return null;
        // The passage's own lines on that page, so the camera can tell whether the line being read
        // fits in the view or has to be panned along.
        var passage = map.Segment(cue.SegmentId) is { } segment ? map.HighlightRects(segment.AnchorIds, page) : rects;
        var target = new FollowTarget(page, rects, passage, cue.Granularity);
        if (cue.Granularity == CueGranularity.Word && cue.Cue is { } timed
            && scheduler.WordProgress(cue.SegmentId, timed, cue.SampleInClip) is { } progress)
        {
            target = target with { NextCueRects = map.HighlightRects(progress.NextAnchorIds, page), Progress = progress.Progress };
        }
        return target;
    }

    /// <summary>
    /// Where the camera should be following right now, from the media clock: the word being spoken
    /// and how far the clock is toward the next measured word. For the host's frame loop, so the
    /// camera moves continuously at the measured pace; null when not playing, when following is off
    /// or suspended, or when the position has no geometry.
    /// </summary>
    public FollowTarget? CurrentFollowTarget()
    {
        if (_options.FollowMode == FollowMode.Off) return null;
        // The transport is read before the coordinator lock is taken (lock order).
        if (_audio.State is not PlaybackState.Playing) return null;
        double position = _audio.PositionSeconds;
        var scheduler = _scheduler;
        var plan = _plan;
        if (scheduler is null || plan is null) return null;
        string segmentId;
        PreparedClip? prepared;
        lock (_gate)
        {
            if (_followSuspended || _segmentIndex < 0 || _segmentIndex >= plan.SegmentCount) return null;
            segmentId = plan.Segments[_segmentIndex].Id;
            prepared = _prepared.GetValueOrDefault(segmentId);
        }
        if (prepared is null) return null;
        var cue = scheduler.Resolve(segmentId, prepared.Asset.Format.ToFrames(position));
        return cue.AnchorIds.Count == 0 ? null : BuildFollowTarget(scheduler, cue);
    }

    // ---- snapshots ---------------------------------------------------------------------

    private void Publish(PlaybackState state, string? message, NarrationError? error = null, PreparationProgress? preparation = null)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { State = state, Message = message, Error = error, Preparation = preparation };
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
        bool transportPlaying = _audio.State == PlaybackState.Playing;
        lock (_gate)
        {
            int index = _segmentIndex;
            string? segmentId = plan is not null && index >= 0 && index < plan.SegmentCount ? plan.Segments[index].Id : null;
            snap = _snapshot with
            {
                TransportPlaying = transportPlaying,
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
            _queuedSegmentId = null;
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
        _audio.ClipAdvanced -= OnClipAdvanced;
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
