using RailNarrator.Domain;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Regressions found by driving the real application: the window froze during ordinary playback, and
/// Play after Pause restarted the passage from its first word.
/// </summary>
public sealed class CoordinatorLockingTests
{
    private static NarrationPlan PlanWith(int segments)
    {
        var units = new List<ReadingUnit>();
        for (int i = 0; i < segments; i++)
        {
            var anchor = SyntheticNarration.Anchor($"a{i}", 0, new PageRect(72, 100 + i * 16, 300, 112 + i * 16));
            units.Add(SyntheticNarration.ProseUnit($"u{i}", $"Sentence number {i} of the document.", anchor));
        }
        return SyntheticNarration.Plan(units);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out after {timeoutMs} ms waiting for {what}.");
    }

    /// <summary>
    /// A transport whose every read checks, from another thread, that the coordinator's lock is free.
    /// </summary>
    /// <remarks>
    /// The real engine guards its state with its own lock and raises events from its own threads. If
    /// the coordinator reads it while holding the coordinator lock, any engine event whose handler
    /// needs that lock closes the cycle and both threads stop for good. The probe makes that
    /// ordering observable without having to win a race.
    /// </remarks>
    private sealed class LockProbeTransport : IAudioTransport
    {
        private readonly FakeAudioTransport _inner = new();
        public NarrationCoordinator? Coordinator { get; set; }
        public int Reads;
        public int ReadsUnderCoordinatorLock;

        public LockProbeTransport()
        {
            _inner.StateChanged += s => StateChanged?.Invoke(s);
            _inner.ClipCompleted += s => ClipCompleted?.Invoke(s);
            _inner.Underflow += s => Underflow?.Invoke(s);
            _inner.DeviceFailed += s => DeviceFailed?.Invoke(s);
        }

        private T Probe<T>(T value)
        {
            Interlocked.Increment(ref Reads);
            if (Coordinator is { } coordinator && !Task.Run(() => coordinator.Snapshot).Wait(1500))
                Interlocked.Increment(ref ReadsUnderCoordinatorLock);
            return value;
        }

        public PlaybackState State => _inner.State;
        public double PositionSeconds => Probe(_inner.PositionSeconds);
        public double ClipDurationSeconds => Probe(_inner.ClipDurationSeconds);
        public double BufferedAheadSeconds => Probe(_inner.BufferedAheadSeconds);
        public double PlaybackRate => _inner.PlaybackRate;
        public Task LoadAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default) => _inner.LoadAsync(segmentId, asset, pcm, ct);
        public Task PlayAsync(CancellationToken ct = default) => _inner.PlayAsync(ct);
        public Task PauseAsync(CancellationToken ct = default) => _inner.PauseAsync(ct);
        public Task ResumeAsync(CancellationToken ct = default) => _inner.ResumeAsync(ct);
        public Task StopAsync(CancellationToken ct = default) => _inner.StopAsync(ct);
        public Task SeekAsync(double seconds, CancellationToken ct = default) => _inner.SeekAsync(seconds, ct);
        public void SetPlaybackRate(double rate) => _inner.SetPlaybackRate(rate);
        public event Action<PlaybackState>? StateChanged;
        public event Action<string>? ClipCompleted;
        public event Action<string>? Underflow;
        public event Action<string>? DeviceFailed;
    }

    [Fact]
    public async Task TheTransportIsNeverReadWhileTheCoordinatorLockIsHeld()
    {
        var transport = new LockProbeTransport();
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), transport);
        transport.Coordinator = coordinator;

        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Playing, "playback to start");
        coordinator.Tick();                 // the frame loop's path
        coordinator.SuspendFollow();        // a UI-thread snapshot publication
        await coordinator.GoToSegmentAsync(plan.Segments[1].Id);
        await coordinator.StopAsync();

        Assert.True(transport.Reads > 0, "the probe was never exercised");
        Assert.Equal(0, transport.ReadsUnderCoordinatorLock);
    }

    [Fact]
    public async Task NaturalAdvance_SlidesTheLookaheadWindow()
    {
        // Lookahead used to be restarted only by Play and by seeks. Once playback ran past the
        // passages prepared at Play, each later one was synthesised when it was already due.
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3, LookaheadSeconds: 0));
        var plan = PlanWith(8);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount == 1 && tts.Requests.Count == 3, "the first window (0-2)");

        transport.RaiseClipCompleted(plan.Segments[0].Id);

        // Now reading passage 1; the window must reach passage 3 before playback gets there.
        await WaitUntil(() => tts.Requests.Any(r => r.Text == plan.Segments[3].SpokenText), "passage 3 to be prepared ahead");
        Assert.Equal(plan.Segments[1].Id, coordinator.Snapshot.SegmentId);
        Assert.Equal(4, tts.Requests.Count);            // bounded: nothing beyond the window
        Assert.Equal(tts.Requests.Count, tts.Requests.Select(r => r.Text).Distinct().Count());   // and nothing redone
    }

    [Fact]
    public async Task PlayAfterPause_ResumesTheClipInsteadOfRestartingThePassage()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        await coordinator.LoadPlanAsync(PlanWith(3));

        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount == 1, "playback to start");
        transport.PositionSeconds = 0.4;
        await coordinator.PauseAsync();
        long generationWhilePaused = coordinator.Generation;

        await coordinator.PlayAsync();

        Assert.Equal(1, transport.ResumeCount);
        Assert.Single(transport.Loaded);                // not reloaded, so not rewound to zero
        Assert.Equal(1, transport.PlayCount);
        Assert.Equal(0.4, transport.PositionSeconds);
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
        // Resuming is not a seek: in-flight lookahead for this position stays valid.
        Assert.Equal(generationWhilePaused, coordinator.Generation);
    }

    [Fact]
    public async Task PlayWhileAlreadyPlaying_DoesNotRestartThePassage()
    {
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), transport);
        await coordinator.LoadPlanAsync(PlanWith(3));
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount == 1, "playback to start");
        long generation = coordinator.Generation;

        await coordinator.PlayAsync();

        Assert.Single(transport.Loaded);
        Assert.Equal(1, transport.PlayCount);
        Assert.Equal(generation, coordinator.Generation);
    }

    [Fact]
    public async Task ALoadedDocument_IsIdle_NotStillPreparing()
    {
        // Lookahead is background work. A document that sat at "Preparing" until Play looked like
        // one that had not finished loading.
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), transport);

        await coordinator.LoadPlanAsync(PlanWith(3));

        Assert.Equal(PlaybackState.Idle, coordinator.Snapshot.State);
        Assert.Equal(0, transport.PlayCount);
    }

    [Fact]
    public async Task ClipsFarBehindTheReadingPosition_AreReleased_AndPreparedAgainOnDemand()
    {
        // Prepared audio used to be kept for the whole document. With no asset store the only way
        // back to a released clip is to synthesise it again, which makes the release observable.
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 2, LookaheadSeconds: 0));
        var plan = PlanWith(8);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount == 1, "playback to start");

        for (int i = 0; i < 4; i++)
        {
            int loads = transport.Loaded.Count;
            transport.RaiseClipCompleted(plan.Segments[i].Id);
            await WaitUntil(() => transport.Loaded.Count == loads + 1, $"passage {i + 1} to start");
        }
        int firstPassageRequests() => tts.Requests.Count(r => r.Text == plan.Segments[0].SpokenText);
        Assert.Equal(1, firstPassageRequests());

        // One back is still held: Previous and Replay must not wait for synthesis.
        int before = tts.Requests.Count;
        await coordinator.PreviousSegmentAsync();
        Assert.Equal(plan.Segments[3].Id, coordinator.Snapshot.SegmentId);
        Assert.DoesNotContain(tts.Requests.Skip(before), r => r.Text == plan.Segments[3].SpokenText);

        // Four back was released, and comes back.
        await coordinator.GoToSegmentAsync(plan.Segments[0].Id);
        await WaitUntil(() => transport.Loaded[^1].SegmentId == plan.Segments[0].Id, "the first passage to load again");
        Assert.Equal(2, firstPassageRequests());
    }

    [Fact]
    public async Task PlayAfterPause_FollowingACorrection_SpeaksTheCorrectedText()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);

        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount == 1, "playback to start");
        await coordinator.PauseAsync();
        await coordinator.ApplyScriptEditAsync(plan.Segments[0].Id, "A corrected sentence.");

        await coordinator.PlayAsync();
        await WaitUntil(() => transport.Loaded.Count == 2, "the corrected clip to load");

        Assert.Equal(0, transport.ResumeCount);         // the paused clip held the old wording
        Assert.Contains(tts.Requests, r => r.Text == "A corrected sentence.");
    }
}
