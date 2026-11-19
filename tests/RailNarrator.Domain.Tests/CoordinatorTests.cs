using RailNarrator.Domain;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// The coordinator is the single authority for narration state. These tests cover the invariants that
/// are invisible in normal use and obvious the moment they break: stale work must not play, lookahead
/// must stay bounded, completion must wait for the audio, and manual browsing must not be undone.
/// </summary>
public sealed class NarrationCoordinatorTests
{
    private static NarrationPlan PlanWith(int segments, int framesPerClip = 24000)
    {
        var units = new List<ReadingUnit>();
        for (int i = 0; i < segments; i++)
        {
            var anchor = SyntheticNarration.Anchor($"a{i}", 0, new PageRect(72, 100 + i * 16, 300, 112 + i * 16));
            units.Add(SyntheticNarration.ProseUnit($"u{i}", $"Sentence number {i} of the document.", anchor));
        }
        return SyntheticNarration.Plan(units);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000, string what = "condition")
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out after {timeoutMs} ms waiting for {what}.");
    }

    private sealed class CountingStore : ISpeechAssetStore
    {
        private readonly Dictionary<string, (SpeechAsset Asset, byte[] Pcm)> _items = new(StringComparer.Ordinal);
        public int StoreCount;
        public int InvalidateCount;

        public Task<SpeechAsset?> TryGetAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult(_items.TryGetValue(assetId, out var v) ? v.Asset : null);

        public Task StoreAsync(SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            _items[asset.Id] = (asset, pcm.ToArray());
            Interlocked.Increment(ref StoreCount);
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>?> TryLoadPcmAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult(_items.TryGetValue(assetId, out var v) ? (ReadOnlyMemory<byte>?)v.Pcm : null);

        public Task InvalidateAsync(string documentContentHash, CancellationToken ct = default)
        {
            Interlocked.Increment(ref InvalidateCount);
            _items.Clear();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Play_SynthesisesTheFirstSegmentAndStartsPlayback()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        var store = new CountingStore();
        await using var coordinator = new NarrationCoordinator(tts, transport, store);

        var plan = PlanWith(4);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();

        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
        Assert.Equal(plan.Segments[0].Id, coordinator.Snapshot.SegmentId);
        Assert.Single(transport.Loaded);
        Assert.Equal(plan.Segments[0].SpokenText, tts.Requests[0].Text);
    }

    [Fact]
    public async Task PreviousAndNext_OnAnEmptyPlan_AreNoOpsNotThrows()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        await coordinator.LoadPlanAsync(PlanWith(0));

        // Previously indexed Segments[0] on an empty plan and threw ArgumentOutOfRangeException.
        await coordinator.PreviousSegmentAsync();
        await coordinator.NextSegmentAsync();
        await coordinator.ReplaySegmentAsync();
        Assert.Equal(0, transport.PlayCount);
    }

    [Fact]
    public async Task Play_PreparesTheCurrentSegmentOnlyOnce()
    {
        // Play both starts the lookahead loop and ensures the current segment; with a slow
        // provider the two used to synthesise (and store) the same segment concurrently.
        var tts = new FakeTtsProvider(new FakeTtsOptions(Latency: TimeSpan.FromMilliseconds(100)));
        var transport = new FakeAudioTransport();
        var store = new CountingStore();
        await using var coordinator = new NarrationCoordinator(tts, transport, store,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 1, LookaheadSeconds: 0));

        await coordinator.LoadPlanAsync(PlanWith(1));
        await coordinator.PlayAsync();

        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");
        Assert.Equal(1, tts.SynthesizeCount);
        Assert.Equal(1, Volatile.Read(ref store.StoreCount));
    }

    /// <summary>Delegates to the fake provider except <see cref="ListVoicesAsync"/>, which throws.</summary>
    private sealed class ThrowingVoicesProvider : ITtsProvider
    {
        private readonly FakeTtsProvider _inner = new();
        public string ProviderId => _inner.ProviderId;
        public Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => _inner.GetCapabilitiesAsync(ct);
        public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("voice list exploded");
        public Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct = default)
            => _inner.SynthesizeAsync(request, ct);
        public Task<ProviderSelfTestResult> SelfTestAsync(string voiceId, CancellationToken ct = default)
            => _inner.SelfTestAsync(voiceId, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Fact]
    public async Task PreparationFault_PublishesAnErrorInsteadOfStickingAtPreparing()
    {
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(new ThrowingVoicesProvider(), transport);

        await coordinator.LoadPlanAsync(PlanWith(2));

        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Error,
            what: "the preparation fault to be published as an error");
        Assert.NotNull(coordinator.Snapshot.Error);
    }

    /// <summary>
    /// A passage longer than the provider accepts used to stop reading for good: an interpretation
    /// of 1,239 characters met Kokoro's 1,200 and every Play stopped at it. It is spoken in pieces,
    /// joined into one clip.
    /// </summary>
    [Fact]
    public async Task APassageLongerThanTheProviderAccepts_IsSpokenInPieces_AsOneClip()
    {
        string longText = string.Join(" ", Enumerable.Range(1, 8).Select(i => $"This is sentence {i} of a long interpreted passage about equations."));
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 300, 112));
        var plan = SyntheticNarration.Plan([SyntheticNarration.ProseUnit("u0", longText, anchor)]);
        var tts = new FakeTtsProvider(new FakeTtsOptions(MaxTextChars: 150));
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();

        await WaitUntil(() => transport.Loaded.Count == 1, what: "the passage to be loaded");
        Assert.NotEqual(PlaybackState.Error, coordinator.Snapshot.State);
        Assert.True(tts.Requests.Count > 1, "the passage was not split");
        Assert.All(tts.Requests, r => Assert.True(r.Text.Length <= 150, $"a piece of {r.Text.Length} characters was sent"));
        Assert.Equal(plan.Segments[0].SpokenText.Replace(" ", ""), string.Concat(tts.Requests.Select(r => r.Text)).Replace(" ", ""));
    }

    [Fact]
    public void SynthesisPieces_EndAtSentencesThenClausesThenWords_AndCoverTheText()
    {
        const string text = "First sentence here. Second one, with a clause; and more words follow it. Third.";
        var pieces = NarrationCoordinator.SynthesisPieces(text, 30);
        Assert.All(pieces, p => Assert.InRange(p.Length, 1, 30));
        Assert.All(pieces, p => Assert.False(char.IsWhiteSpace(text[p.Start])));
        Assert.Equal("First sentence here.", text.Substring(pieces[0].Start, pieces[0].Length));
        Assert.Equal("Second one, with a clause;", text.Substring(pieces[1].Start, pieces[1].Length));
        Assert.Equal(text.Replace(" ", ""), string.Concat(pieces.Select(p => text.Substring(p.Start, p.Length))).Replace(" ", ""));

        // No break at all: cut at the limit rather than refuse.
        var word = NarrationCoordinator.SynthesisPieces(new string('x', 70), 30);
        Assert.Equal([30, 30, 10], word.Select(p => p.Length));
        Assert.Single(NarrationCoordinator.SynthesisPieces("  short  ", 30));
        Assert.Empty(NarrationCoordinator.SynthesisPieces("   ", 30));
    }

    [Fact]
    public async Task Lookahead_IsBoundedSoABookIsNotSynthesisedBeforePlayback()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3, LookaheadSeconds: 0));

        await coordinator.LoadPlanAsync(PlanWith(20));
        await coordinator.PlayAsync();

        await WaitUntil(() => tts.SynthesizeCount >= 3, what: "lookahead to fill");
        // Give any runaway preparation a chance to overshoot before asserting.
        await Task.Delay(250);
        Assert.True(tts.SynthesizeCount <= 4,
            $"lookahead should stay near the bound of 3, but {tts.SynthesizeCount} segments were synthesised");
    }

    /// <summary>
    /// The lookahead is listening time, not a count of passages: four short sentences were 15 s,
    /// less than one slow preparation. Here each passage is 1 s, so 5 s ahead is about six
    /// passages, never all twenty, and never fewer than the minimum count.
    /// </summary>
    [Fact]
    public async Task Lookahead_IsListeningTime_BoundedByACount()
    {
        var tts = new FakeTtsProvider(new FakeTtsOptions(FixedFrameCount: FakeTtsProvider.DefaultSampleRate));
        await using var coordinator = new NarrationCoordinator(tts, new FakeAudioTransport(),
            options: new NarrationCoordinatorOptions(LookaheadSegments: 2, LookaheadSeconds: 5));
        await coordinator.LoadPlanAsync(PlanWith(20));
        await coordinator.PlayAsync();
        await WaitUntil(() => tts.SynthesizeCount >= 6, what: "five seconds ahead to be prepared");
        await Task.Delay(250);
        Assert.InRange(tts.SynthesizeCount, 6, 7);

        var capped = new FakeTtsProvider(new FakeTtsOptions(FixedFrameCount: FakeTtsProvider.DefaultSampleRate));
        await using var bounded = new NarrationCoordinator(capped, new FakeAudioTransport(),
            options: new NarrationCoordinatorOptions(LookaheadSegments: 2, LookaheadSeconds: 600, MaxLookaheadSegments: 8));
        await bounded.LoadPlanAsync(PlanWith(20));
        await bounded.PlayAsync();
        await WaitUntil(() => capped.SynthesizeCount >= 8, what: "the capped lookahead to fill");
        await Task.Delay(250);
        Assert.InRange(capped.SynthesizeCount, 8, 9);
    }

    [Fact]
    public async Task ClipCompletion_AdvancesToTheNextSegment()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");

        transport.RaiseClipCompleted(plan.Segments[0].Id);
        await WaitUntil(() => coordinator.Snapshot.SegmentId == plan.Segments[1].Id, what: "advance to segment 2");
        Assert.Equal(1, coordinator.Snapshot.SegmentIndex);
    }

    [Fact]
    public async Task EndOfDocument_FiresOnlyAfterTheLastClipCompletes()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        var plan = PlanWith(1);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");

        // Playing is not completion: the audio has not drained yet.
        Assert.NotEqual(PlaybackState.Completed, coordinator.Snapshot.State);

        transport.RaiseClipCompleted(plan.Segments[0].Id);
        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Completed, what: "completion");
    }

    [Fact]
    public async Task ASeek_AdvancesTheGenerationAndDiscardsLateResults()
    {
        // A slow provider guarantees an in-flight synthesis is still running when the seek happens.
        var tts = new FakeTtsProvider(new FakeTtsOptions(Latency: TimeSpan.FromMilliseconds(250)));
        var transport = new FakeAudioTransport();
        var store = new CountingStore();
        await using var coordinator = new NarrationCoordinator(tts, transport, store,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 2, LookaheadSeconds: 0));

        var plan = PlanWith(5);
        await coordinator.LoadPlanAsync(plan);
        long generationAfterLoad = coordinator.Generation;

        await coordinator.GoToSegmentAsync(plan.Segments[3].Id);

        Assert.True(coordinator.Generation > generationAfterLoad,
            "a seek must advance the generation token so in-flight work can be recognised as stale");
        await WaitUntil(() => coordinator.Snapshot.SegmentId == plan.Segments[3].Id, what: "the seek to land");

        // Whatever the abandoned preparation produced, playback must be at the seek target.
        Assert.Equal(plan.Segments[3].Id, coordinator.Snapshot.SegmentId);
        Assert.All(transport.Loaded, l => Assert.Equal(plan.Segments[3].Id, l.SegmentId));
    }

    [Fact]
    public async Task UnavailableProvider_ReportsARecoverableErrorInsteadOfSilence()
    {
        var tts = new FakeTtsProvider(new FakeTtsOptions(UnavailableReason: "no model installed"));
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        await coordinator.LoadPlanAsync(PlanWith(2));
        await coordinator.PlayAsync();

        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Error, what: "the error state");
        var error = coordinator.Snapshot.Error;
        Assert.NotNull(error);
        Assert.Equal(NarrationErrorCode.SpeechProviderUnavailable, error!.Code);
        Assert.True(error.CanRetry);
        Assert.Contains("no model installed", error.Message);
    }

    [Fact]
    public async Task SilentAudio_IsTreatedAsAFailureNotAsSomethingToPlay()
    {
        var tts = new FakeTtsProvider(new FakeTtsOptions(ReturnSilence: true));
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        await coordinator.LoadPlanAsync(PlanWith(2));
        await coordinator.PlayAsync();

        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Error, what: "the error state");
        Assert.Equal(NarrationErrorCode.SpeechSynthesisFailed, coordinator.Snapshot.Error!.Code);
        Assert.Empty(transport.Loaded);   // silent audio must never reach the transport
    }

    [Fact]
    public async Task Underflow_FreesesReadingProgressInTheBufferingState()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        var plan = PlanWith(2);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");

        transport.RaiseUnderflow(plan.Segments[0].Id);
        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Buffering, what: "buffering");
        // The segment must not have advanced: an underrun is a stall, not progress.
        Assert.Equal(plan.Segments[0].Id, coordinator.Snapshot.SegmentId);
    }

    [Fact]
    public async Task DeviceFailure_IsSurfacedWithItsDetail()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        await coordinator.LoadPlanAsync(PlanWith(2));
        transport.RaiseDeviceFailed("device unplugged");

        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Error, what: "the error state");
        Assert.Equal(NarrationErrorCode.AudioDeviceFailure, coordinator.Snapshot.Error!.Code);
        Assert.Contains("device unplugged", coordinator.Snapshot.Error!.Message);
    }

    [Fact]
    public async Task ManualBrowsing_SuspendsFollowingWithoutStoppingAudio()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");

        coordinator.SuspendFollow();
        Assert.True(coordinator.FollowSuspended);
        Assert.True(coordinator.Snapshot.FollowSuspended);
        // Audio keeps playing: looking at something else must not stop the reading.
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);

        coordinator.ResumeFollow();
        Assert.False(coordinator.FollowSuspended);
    }

    /// <summary>
    /// Zooming changes what is on screen without leaving the reading position, so the camera checks
    /// the current cue at once, at the new magnification, instead of waiting for the next word, or the
    /// next sentence when there is no word timing. A reader who has panned away stays where they are.
    /// </summary>
    [Fact]
    public async Task RefollowingTheCurrentCue_MovesTheCameraNow_UnlessFollowIsSuspended()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var follows = new List<string>();
        coordinator.FollowRequested += (cue, _) => { lock (follows) follows.Add(cue.SegmentId); };

        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");
        await WaitUntil(() => { coordinator.Tick(); lock (follows) return follows.Count > 0; }, what: "the first follow");

        int before;
        lock (follows) before = follows.Count;
        coordinator.RefollowCurrentCue();   // well inside the 0.12 s follow throttle
        lock (follows) Assert.Equal(before + 1, follows.Count);
        lock (follows) Assert.Equal(plan.Segments[0].Id, follows[^1]);

        coordinator.SuspendFollow();
        coordinator.RefollowCurrentCue();
        lock (follows) Assert.Equal(before + 1, follows.Count);
        Assert.True(coordinator.FollowSuspended);
    }

    /// <summary>
    /// A seek to a passage that is not ready says what it is waiting on and for how long, and the
    /// wait is cleared once the passage plays: the on-page card and the Play button read this.
    /// </summary>
    [Fact]
    public async Task ASeekToAnUnpreparedPassage_ReportsWhatItIsWaitingOn_ThenClears()
    {
        var tts = new FakeTtsProvider(new FakeTtsOptions(Latency: TimeSpan.FromMilliseconds(700)));
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var seen = new List<PreparationProgress>();
        coordinator.SnapshotChanged += s => { if (s.Preparation is { } p) lock (seen) seen.Add(p); };

        var plan = PlanWith(12);
        await coordinator.LoadPlanAsync(plan);
        string target = plan.Segments[9].Id;   // beyond the lookahead window
        var seek = coordinator.GoToSegmentAsync(target);

        await WaitUntil(() => { lock (seen) return seen.Count > 0; }, what: "the wait to be reported");
        PreparationProgress first;
        lock (seen) first = seen[0];
        Assert.Equal(target, first.SegmentId);
        Assert.Equal(9, first.SegmentIndex);
        Assert.Equal(PreparationStage.Synthesising, first.Stage);
        Assert.Equal(PlaybackState.Seeking, coordinator.Snapshot.State);
        Assert.Contains("Synthesising speech", coordinator.Snapshot.Message);

        await seek;
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.Preparation);
    }

    [Fact]
    public async Task APassageAlreadyPrepared_StartsWithoutAWaitBeingReported()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        await WaitUntil(() => tts.SynthesizeCount >= 3, what: "lookahead");
        await Task.Delay(50);

        bool reported = false;
        coordinator.SnapshotChanged += s => { if (s.Preparation is not null) reported = true; };
        await coordinator.GoToSegmentAsync(plan.Segments[1].Id);

        Assert.False(reported);
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
    }

    /// <summary>
    /// The frame loop's follow target comes from the media clock while audio plays, and from nowhere
    /// otherwise: paused, browsing or with following off, the camera is the reader's.
    /// </summary>
    [Fact]
    public async Task TheFrameLoopFollowTarget_ExistsOnlyWhilePlayingAndFollowing()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        Assert.Null(coordinator.CurrentFollowTarget());

        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");
        var target = coordinator.CurrentFollowTarget();
        Assert.NotNull(target);
        Assert.Equal(0, target!.PageIndex);
        Assert.NotEmpty(target.CueRects);

        coordinator.SuspendFollow();
        Assert.Null(coordinator.CurrentFollowTarget());
        coordinator.ResumeFollow();
        coordinator.SetFollowMode(FollowMode.Off);
        Assert.Null(coordinator.CurrentFollowTarget());
        coordinator.SetFollowMode(FollowMode.Follow);

        await coordinator.PauseAsync();
        Assert.Null(coordinator.CurrentFollowTarget());
    }

    /// <summary>
    /// An interpreted lead-in and its equation are one passage with no timing inside it. When its
    /// first words are at the foot of one page and the equation is overleaf, the camera shows the
    /// page with the equation, not the last line of the page before, for as long as it is read.
    /// </summary>
    [Fact]
    public async Task APassageAcrossAPageBreak_WithNoWordTiming_IsFollowedOnThePageHoldingMostOfIt()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var follows = new List<FollowTarget>();
        coordinator.FollowRequested += (_, target) => { lock (follows) follows.Add(target); };

        var foot = SyntheticNarration.Anchor("foot", 2, new PageRect(312, 650, 456, 658));
        var head = SyntheticNarration.Anchor("head", 3, new PageRect(48, 53, 168, 60));
        var equation = SyntheticNarration.Anchor("eq", 3, new PageRect(170, 68, 332, 97));
        var leadIn = new ReadingUnit("u0", ReadingUnitKind.Prose, [foot, head],
            "It is a linear combination of bounded transition functions defined as", null, "", "", [], 2);
        var maths = SyntheticNarration.MathsUnit("u1", "g_t", equation);
        const string spoken = "It is a weighted sum of bounded transition functions, g of t.";
        var plan = SyntheticNarration.Plan([leadIn, maths]) with
        {
            Segments =
            [
                new NarrationSegment("s00000", ["u0", "u1"], ReadingUnitKind.Prose, spoken, "en-GB",
                    [new SourceLink(TextRange.OfString(spoken), ["foot", "head", "eq"])],
                    MathMode.Interpret, ScriptProvenance.ModelGenerated, [], 1),
            ],
        };

        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");

        var now = coordinator.CurrentFollowTarget();
        Assert.NotNull(now);
        Assert.Equal(3, now!.PageIndex);
        Assert.All(now.CueRects, r => Assert.True(r.Top < 100, "only the rectangles on the framed page"));
        await WaitUntil(() => { lock (follows) return follows.Count > 0; }, what: "a follow request");
        lock (follows) Assert.All(follows, t => Assert.Equal(3, t.PageIndex));
    }

    /// <summary>
    /// Browsing pauses the camera. Asking to read from a passage (Next, Previous, a click, the script
    /// list) or pressing Play from a stop is a request to see that reading, so following resumes at
    /// once instead of leaving the view where it was until the pause times out. A new document does
    /// not inherit the pause either.
    /// </summary>
    [Fact]
    public async Task ASeek_AFreshStart_AndANewDocument_EndAPauseInFollowing()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var follows = new List<string>();
        coordinator.FollowRequested += (cue, _) => { lock (follows) follows.Add(cue.SegmentId); };

        var plan = PlanWith(4);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");

        coordinator.SuspendFollow();
        await coordinator.NextSegmentAsync();
        Assert.False(coordinator.FollowSuspended);
        Assert.False(coordinator.Snapshot.FollowSuspended);
        await WaitUntil(() => { lock (follows) return follows.Contains(plan.Segments[1].Id); }, what: "the camera to follow the seek");

        await coordinator.StopAsync();
        coordinator.SuspendFollow();
        await coordinator.PlayAsync();
        Assert.False(coordinator.FollowSuspended);

        // Pausing and resuming is not a request to move the view: a reader who paused to look at a
        // figure and presses Play keeps looking at it until the pause in following times out.
        await coordinator.PauseAsync();
        coordinator.SuspendFollow();
        await coordinator.PlayAsync();
        Assert.True(coordinator.FollowSuspended);

        await coordinator.LoadPlanAsync(PlanWith(2));
        Assert.False(coordinator.FollowSuspended);
    }

    /// <summary>
    /// Gapless: once the next passage is prepared it is handed to the transport to follow the current
    /// one, and when the transport moves on to it the reading position advances without loading or
    /// starting anything — the audio is already playing. Then the passage after that is queued.
    /// </summary>
    [Fact]
    public async Task TheNextPassage_IsQueuedAndFollowsWithoutBeingLoaded()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var plan = PlanWith(4);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.QueuedSegmentId == plan.Segments[1].Id, what: "the second passage to be queued");

        int loads = transport.Loaded.Count, plays = transport.PlayCount;
        Assert.True(transport.RaiseClipAdvanced());

        await WaitUntil(() => coordinator.Snapshot.SegmentId == plan.Segments[1].Id, what: "the position to advance");
        Assert.Equal(loads, transport.Loaded.Count);
        Assert.Equal(plays, transport.PlayCount);
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
        await WaitUntil(() => transport.QueuedSegmentId == plan.Segments[2].Id, what: "the third passage to be queued");
    }

    /// <summary>A transport that cannot queue still gets each passage loaded after the last completes.</summary>
    [Fact]
    public async Task WithoutQueueing_PassagesAreStillLoadedOnCompletion()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport { AcceptQueue = false };
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.Loaded.Count == 1, what: "the first load");
        await WaitUntil(() => tts.SynthesizeCount >= 2, what: "the second passage to be prepared");

        transport.RaiseClipCompleted(plan.Segments[0].Id);
        await WaitUntil(() => transport.Loaded.Count == 2, what: "the second passage to be loaded");
        Assert.Equal(plan.Segments[1].Id, transport.Loaded[1].SegmentId);
    }

    /// <summary>An edit must not let the queued clip play in its old words.</summary>
    [Fact]
    public async Task AScriptEditWhilePlaying_ClearsTheQueuedClip()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);
        var plan = PlanWith(3);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.QueuedSegmentId == plan.Segments[1].Id, what: "the second passage to be queued");

        await coordinator.ApplyScriptEditAsync(plan.Segments[1].Id, "A corrected second sentence.");

        Assert.True(transport.ClearQueuedCount > 0);
        // Re-prepared in its new words, it is queued again.
        await WaitUntil(() => transport.QueuedSegmentId == plan.Segments[1].Id, what: "the corrected passage to be queued");
    }

    /// <summary>
    /// Next pressed again while the last Next is still waiting for audio steps on from where the
    /// reader is going: three quick presses from the first passage land on the fourth, not the second.
    /// </summary>
    [Fact]
    public async Task NextPressedWhileASeekWaits_CountsFromTheSeeksTarget()
    {
        var tts = new FakeTtsProvider(new FakeTtsOptions(Latency: TimeSpan.FromMilliseconds(300)));
        // Stop yields, as the real engine's does: the window in which a second press used to count
        // from the passage still playing.
        var transport = new FakeAudioTransport { StopDelay = TimeSpan.FromMilliseconds(30) };
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 1, LookaheadSeconds: 0));
        var plan = PlanWith(8);
        await coordinator.LoadPlanAsync(plan);

        var first = coordinator.NextSegmentAsync();
        var second = coordinator.NextSegmentAsync();
        var third = coordinator.NextSegmentAsync();
        await Task.WhenAll(first, second, third);

        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Playing, what: "playback of the last target");
        Assert.Equal(plan.Segments[3].Id, coordinator.Snapshot.SegmentId);
        Assert.Equal(plan.Segments[3].Id, transport.Loaded[^1].SegmentId);
    }

    [Fact]
    public async Task PreviousPressedWhileASeekWaits_CountsFromTheSeeksTarget()
    {
        var tts = new FakeTtsProvider(new FakeTtsOptions(Latency: TimeSpan.FromMilliseconds(300)));
        // Stop yields, as the real engine's does: the window in which a second press used to count
        // from the passage still playing.
        var transport = new FakeAudioTransport { StopDelay = TimeSpan.FromMilliseconds(30) };
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 1, LookaheadSeconds: 0));
        var plan = PlanWith(8);
        await coordinator.LoadPlanAsync(plan);

        var forward = coordinator.GoToSegmentAsync(plan.Segments[6].Id);
        var back = coordinator.PreviousSegmentAsync();
        await Task.WhenAll(forward, back);

        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Playing, what: "playback");
        Assert.Equal(plan.Segments[5].Id, coordinator.Snapshot.SegmentId);
    }

    /// <summary>
    /// The application sets the voice from a selector's setter, without awaiting it, and loads the
    /// plan next. A voice change still waiting for the transport to stop when the load finishes
    /// used to start preparation for its own, older generation afterwards: that cancelled the
    /// load's lookahead and prepared nothing, so nothing was ready until Play.
    /// </summary>
    [Fact]
    public async Task AVoiceChangeStillStopping_DoesNotCancelTheLookaheadOfALoadThatOvertookIt()
    {
        // Synthesis outlasts the voice change's stop, so the load's lookahead is still at work on
        // its first passage when the voice change resumes.
        var tts = new FakeTtsProvider(new FakeTtsOptions(Latency: TimeSpan.FromMilliseconds(300)));
        var transport = new FakeAudioTransport { StopDelay = TimeSpan.FromMilliseconds(150) };
        var store = new CountingStore();
        await using var coordinator = new NarrationCoordinator(tts, transport, store,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 2, LookaheadSeconds: 0));

        var voiceChange = coordinator.SetVoiceAsync("fake-alternate");
        transport.StopDelay = TimeSpan.Zero;   // the load's own stop completes at once
        await coordinator.LoadPlanAsync(PlanWith(4));
        await voiceChange;

        await WaitUntil(() => store.StoreCount >= 2, timeoutMs: 2000, what: "the lookahead after the load");
        Assert.Equal(PlaybackState.Idle, coordinator.Snapshot.State);
    }

    /// <summary>
    /// The same overtaking by a seek: what the seek plays must be in the new voice. The clips
    /// prepared in the old voice are discarded when the voice changes, not when its stop completes.
    /// </summary>
    [Fact]
    public async Task AVoiceChangeStillStopping_DoesNotLetASeekPlayTheOldVoice()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3, LookaheadSeconds: 0, VoiceId: "fake-default"));
        var plan = PlanWith(4);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.Loaded.Count == 1 && coordinator.Snapshot.State == PlaybackState.Playing, what: "playback");
        await Task.Delay(100);   // the lookahead prepares the next passages in the first voice

        transport.StopDelay = TimeSpan.FromMilliseconds(150);
        var voiceChange = coordinator.SetVoiceAsync("fake-alternate");
        transport.StopDelay = TimeSpan.Zero;
        await coordinator.NextSegmentAsync();
        await voiceChange;

        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Playing
                              && transport.Loaded[^1].SegmentId == plan.Segments[1].Id, what: "playback of the next passage");
        Assert.Equal("fake-alternate", transport.Loaded[^1].Asset.VoiceId);
    }

    /// <summary>The document asked for last is the one loaded, whichever load's stop finishes last.</summary>
    [Fact]
    public async Task ALoadOvertakenByALaterLoad_DoesNotPutItsPlanBack()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport { StopDelay = TimeSpan.FromMilliseconds(150) };
        var store = new CountingStore();
        await using var coordinator = new NarrationCoordinator(tts, transport, store,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 1, LookaheadSeconds: 0));
        var first = PlanWith(2);
        var second = PlanWith(5);

        var slow = coordinator.LoadPlanAsync(first);
        transport.StopDelay = TimeSpan.Zero;
        await coordinator.LoadPlanAsync(second);
        await slow;

        Assert.Same(second, coordinator.Plan);
        await WaitUntil(() => store.StoreCount >= 1, timeoutMs: 2000, what: "the second document's lookahead");
        Assert.Equal(5, coordinator.Snapshot.SegmentCount);
    }

    [Fact]
    public async Task AScriptEdit_InvalidatesTheCachedSpeechForThatDocument()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        var store = new CountingStore();
        await using var coordinator = new NarrationCoordinator(tts, transport, store);

        var plan = PlanWith(2);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => store.StoreCount > 0, what: "the first clip to be cached");

        string segmentId = plan.Segments[0].Id;
        await coordinator.ApplyScriptEditAsync(segmentId, "A corrected sentence.");

        Assert.True(store.InvalidateCount > 0, "an edit must invalidate cached speech, not reuse the old clip");
        var edited = coordinator.Plan!.Segment(segmentId)!;
        Assert.Equal("A corrected sentence.", edited.SpokenText);
        Assert.Equal(ScriptProvenance.UserEdited, edited.Provenance);
        Assert.Equal(2, edited.ScriptRevision);
    }

    [Fact]
    public async Task PlaybackRate_IsClampedAndForwardedToTheTransport()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        coordinator.SetPlaybackRate(1.5);
        Assert.Equal(1.5, transport.PlaybackRate, 3);

        coordinator.SetPlaybackRate(99);
        Assert.True(transport.PlaybackRate <= 3.0, "an absurd rate must be clamped, not applied");

        coordinator.SetPlaybackRate(0);
        Assert.True(transport.PlaybackRate >= 0.5, "a rate below the usable range must be clamped");
    }

    [Fact]
    public async Task Loading_DoesNotAutoPlay()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        await coordinator.LoadPlanAsync(PlanWith(3));
        await Task.Delay(200);

        // Restoring a session must never start reading aloud on its own.
        Assert.Equal(0, transport.PlayCount);
        Assert.NotEqual(PlaybackState.Playing, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task Snapshot_ExposesProgressAndGranularityForTheUi()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport);

        var plan = PlanWith(4);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, what: "playback to start");

        var snapshot = coordinator.Snapshot;
        Assert.Equal(4, snapshot.SegmentCount);
        Assert.Equal(0, snapshot.SegmentIndex);
        Assert.Equal(CueGranularity.Sentence, snapshot.CueGranularity);
        Assert.InRange(snapshot.PlanProgress, 0, 1);
    }
}

/// <summary>
/// Cache keys must change when, and only when, the cached artefact would differ.
/// </summary>
public sealed class CacheKeyTests
{
    private static readonly AudioFormat Format = AudioFormat.Mono24000Float32;

    [Fact]
    public void SpeechKey_ChangesWithVoiceRateModelAndScript()
    {
        string Base() => CacheKey.ForSpeechAsset("s1", 1, "hello", "en-GB", "kokoro", "m1", "af_heart", "rate=24000", Format);

        Assert.Equal(Base(), Base());
        Assert.NotEqual(Base(), CacheKey.ForSpeechAsset("s1", 1, "hello", "en-GB", "kokoro", "m1", "af_sarah", "rate=24000", Format));
        Assert.NotEqual(Base(), CacheKey.ForSpeechAsset("s1", 1, "hello", "en-GB", "kokoro", "m2", "af_heart", "rate=24000", Format));
        Assert.NotEqual(Base(), CacheKey.ForSpeechAsset("s1", 1, "hello!", "en-GB", "kokoro", "m1", "af_heart", "rate=24000", Format));
        Assert.NotEqual(Base(), CacheKey.ForSpeechAsset("s1", 2, "hello", "en-GB", "kokoro", "m1", "af_heart", "rate=24000", Format));
        Assert.NotEqual(Base(), CacheKey.ForSpeechAsset("s1", 1, "hello", "en-GB", "zonos3", "m1", "af_heart", "rate=24000", Format));
    }

    [Fact]
    public void SpeechKey_CatchesATextChangeTheRevisionMissed()
    {
        // Both guards matter: the revision catches a bump, the text hash catches a change that did not
        // bump it. Either alone would let a stale clip play against new words.
        Assert.NotEqual(
            CacheKey.ForSpeechAsset("s1", 1, "original", "en-GB", "p", "m", "v", "o", Format),
            CacheKey.ForSpeechAsset("s1", 1, "corrected", "en-GB", "p", "m", "v", "o", Format));
    }

    [Fact]
    public void AlignmentKey_DerivesFromTheAssetSoRetimedAudioCannotKeepOldCues()
    {
        string assetA = CacheKey.ForSpeechAsset("s1", 1, "hello", "en-GB", "kokoro", "m1", "af_heart", "o", Format);
        string assetB = CacheKey.ForSpeechAsset("s1", 1, "hello", "en-GB", "kokoro", "m1", "af_sarah", "o", Format);

        Assert.NotEqual(
            CacheKey.ForAlignment(assetA, "aligner", "1", "h"),
            CacheKey.ForAlignment(assetB, "aligner", "1", "h"));
    }

    [Fact]
    public void VerbalisationKey_ChangesWithModePromptModelAndContext()
    {
        string Base() => CacheKey.ForMathVerbalisation("doc", "a0", "x^2", MathMode.NaturalFaithful, "before", "after", "en-GB", "p1", "m1");

        Assert.Equal(Base(), Base());
        Assert.NotEqual(Base(), CacheKey.ForMathVerbalisation("doc", "a0", "x^2", MathMode.DetailedSymbolic, "before", "after", "en-GB", "p1", "m1"));
        Assert.NotEqual(Base(), CacheKey.ForMathVerbalisation("doc", "a0", "x^2", MathMode.NaturalFaithful, "before", "after", "en-GB", "p2", "m1"));
        Assert.NotEqual(Base(), CacheKey.ForMathVerbalisation("doc", "a0", "x^2", MathMode.NaturalFaithful, "before", "after", "en-GB", "p1", "m2"));
        Assert.NotEqual(Base(), CacheKey.ForMathVerbalisation("doc", "a0", "x^3", MathMode.NaturalFaithful, "before", "after", "en-GB", "p1", "m1"));
        Assert.NotEqual(Base(), CacheKey.ForMathVerbalisation("doc", "a0", "x^2", MathMode.NaturalFaithful, "OTHER", "after", "en-GB", "p1", "m1"));
    }

    [Fact]
    public void ExtractionKey_ChangesWithOcrPolicyAndFilters()
    {
        var options = ExtractionOptions.ModelFree;
        var filters = NarrationFilterSettings.Default;
        string Base() => CacheKey.ForExtraction("hash", options, filters);

        Assert.Equal(Base(), Base());
        Assert.NotEqual(Base(), CacheKey.ForExtraction("hash", options with { Ocr = OcrPolicy.Full }, filters));
        Assert.NotEqual(Base(), CacheKey.ForExtraction("hash", options, filters with { IncludeFootnotes = true }));
        Assert.NotEqual(Base(), CacheKey.ForExtraction("other", options, filters));
    }
}
