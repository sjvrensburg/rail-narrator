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
    public async Task Lookahead_IsBoundedSoABookIsNotSynthesisedBeforePlayback()
    {
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3));

        await coordinator.LoadPlanAsync(PlanWith(20));
        await coordinator.PlayAsync();

        await WaitUntil(() => tts.SynthesizeCount >= 3, what: "lookahead to fill");
        // Give any runaway preparation a chance to overshoot before asserting.
        await Task.Delay(250);
        Assert.True(tts.SynthesizeCount <= 4,
            $"lookahead should stay near the bound of 3, but {tts.SynthesizeCount} segments were synthesised");
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
            options: new NarrationCoordinatorOptions(LookaheadSegments: 2));

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
