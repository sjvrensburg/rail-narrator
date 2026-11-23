using RailNarrator.Domain;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

public sealed class WordHighlightTests
{
    private static async Task<NarrationPlan> Plan(params string[] lines)
    {
        var snapshot = SyntheticNarration.Snapshot(lines.Select((text, i) => new SyntheticNarration.LineSpec(text, 100 + i * 20)).ToArray());
        var units = new ReadingUnitBuilder().Build(snapshot);
        return await new NarrationPlanner().BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default);
    }

    /// <summary>
    /// Accent marks from maths fonts (U+02C6) and variation selectors (U+FE01) reach the script on
    /// their own. They are not words: nobody says them and no pronunciation dictionary has them, so
    /// counting them made every sentence holding one fall back to sentence highlighting.
    /// </summary>
    [Theory]
    [InlineData("is \u02C6 \uFE01", new[] { "is" })]
    [InlineData("rule: \u02C6\uFE01(x", new[] { "rule", "x" })]
    [InlineData("caf\u00E9 na\u0131ve don\u2019t", new[] { "caf\u00E9", "na\u0131ve", "don\u2019t" })]
    [InlineData("ggplot2 and 1c", new[] { "ggplot2", "and", "1c" })]
    public void TokensAreWordsWithALetterOrDigit(string escaped, string[] expected)
    {
        static string Unescape(string s) => System.Text.RegularExpressions.Regex.Unescape(s);
        var words = SpokenWord.Tokenize(Unescape(escaped));
        Assert.Equal(expected.Select(Unescape), words.Select(w => w.Text));
        Assert.Equal(Enumerable.Range(0, words.Count), words.Select(w => w.Index));
    }

    [Fact]
    public async Task RepeatedWordsHaveDifferentGeometry()
    {
        var plan = await Plan("The small dog saw the small dog.");
        var segment = Assert.Single(plan.Segments);
        var map = new SourceMap(plan);
        var dogs = SpokenWord.Tokenize(segment.SpokenText).Where(w => w.Text == "dog").ToArray();
        Assert.Equal(2, dogs.Length);
        var first = map.AnchorsForSpokenRange(segment.Id, dogs[0].Range);
        var second = map.AnchorsForSpokenRange(segment.Id, dogs[1].Range);
        Assert.NotEqual(Assert.Single(first), Assert.Single(second));
        Assert.True(map.HighlightRects(first, 0)[0].Right < map.HighlightRects(second, 0)[0].Left);
    }

    [Fact]
    public async Task ExpandedNumbersKeepTheOriginalNumberHighlighted()
    {
        var plan = await Plan("The measured value was 12.5 today.");
        var segment = Assert.Single(plan.Segments);
        Assert.Contains("twelve point five", segment.SpokenText);
        var map = new SourceMap(plan);
        var words = SpokenWord.Tokenize(segment.SpokenText).Where(w => new[] { "twelve", "point", "five" }.Contains(w.Text)).ToArray();
        Assert.Equal(3, words.Length);
        var anchors = words.Select(w => Assert.Single(map.AnchorsForSpokenRange(segment.Id, w.Range))).ToArray();
        Assert.Single(anchors.Distinct());
    }

    [Fact]
    public async Task LigaturesAndDehyphenationPreserveWordRectsAcrossLines()
    {
        var plan = await Plan("The ofﬁce has infor-", "mation about this topic.");
        var segment = Assert.Single(plan.Segments);
        var map = new SourceMap(plan);
        var words = SpokenWord.Tokenize(segment.SpokenText);
        var office = Assert.Single(words, w => w.Text == "office");
        Assert.Single(map.AnchorsForSpokenRange(segment.Id, office.Range));
        var information = Assert.Single(words, w => w.Text == "information");
        var rects = map.HighlightRects(map.AnchorsForSpokenRange(segment.Id, information.Range), 0);
        Assert.Equal(2, rects.Count);
        Assert.True(rects[0].Bottom < rects[1].Top);
    }

    [Fact]
    public async Task CompleteWordCoverageAcceptsPausesButMissingWordsFallBack()
    {
        var plan = await Plan("Go home.");
        var segment = Assert.Single(plan.Segments);
        var map = new SourceMap(plan);
        var cues = SpokenWord.Tokenize(segment.SpokenText).Select((w, i) => new TimedCue(
            i * 12000 + 1000, i * 12000 + 3000, w.Range, map.AnchorsForSpokenRange(segment.Id, w.Range), CueGranularity.Word, null, [])).ToArray();
        var checkedCues = TimedCueValidator.Validate(cues, 24000, segment.SpokenText, map.KnownAnchorIds, provenance: TimingProvenance.ForcedAlignment);
        Assert.True(checkedCues.IsUsable);
        Assert.True(checkedCues.CoverageFraction < 0.2);
        var scheduler = new CueScheduler(plan);
        var asset = SyntheticNarration.Clip(segment.Id, 24000, timings: cues);
        scheduler.AttachClip(asset, cues);
        Assert.Empty(scheduler.Resolve(segment.Id, 0).AnchorIds);
        Assert.Equal(CueGranularity.Word, scheduler.Resolve(segment.Id, 2000).Granularity);
        scheduler.AttachClip(asset, [cues[0]]);
        Assert.Equal(CueGranularity.Sentence, scheduler.Resolve(segment.Id, 20000).Granularity);
    }

    [Fact]
    public async Task CoordinatorAlignsAndReusesAudioWhenAlignerRevisionChanges()
    {
        var plan = await Plan("Go home.");
        var store = new InMemorySpeechAssetStore();
        var tts = new FakeTtsProvider(new FakeTtsOptions(FixedFrameCount: 24000));
        var aligner = new TestAligner();
        async Task Run()
        {
            var transport = new FakeAudioTransport();
            await using var coordinator = new NarrationCoordinator(tts, transport, store, aligner: aligner);
            await coordinator.LoadPlanAsync(plan);
            await coordinator.PlayAsync();
            Assert.Single(transport.Loaded);
            // Word timing arrives after the passage is playable (it does not hold playback up).
            await WaitUntil(() => coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Granularity == CueGranularity.Word,
                "word timing to attach");
        }
        await Run();
        int syntheses = tts.SynthesizeCount;
        int alignments = aligner.Calls;
        await Run();
        Assert.Equal(syntheses, tts.SynthesizeCount);
        Assert.Equal(alignments, aligner.Calls);
        aligner.Revision = "test/2";
        await Run();
        Assert.Equal(syntheses, tts.SynthesizeCount);
        Assert.Equal(alignments + 1, aligner.Calls);
    }

    [Fact]
    public async Task DisablingWordHighlightingAlsoDisablesCachedWordCues()
    {
        var plan = await Plan("Go home.");
        var store = new InMemorySpeechAssetStore();
        var tts = new FakeTtsProvider(new FakeTtsOptions(FixedFrameCount: 24000));
        var aligner = new TestAligner();
        await using (var first = new NarrationCoordinator(tts, new FakeAudioTransport(), store, aligner: aligner))
        {
            await first.LoadPlanAsync(plan);
            await first.PlayAsync();
        }
        int calls = aligner.Calls;
        await using var disabled = new NarrationCoordinator(tts, new FakeAudioTransport(), store,
            options: new NarrationCoordinatorOptions(RequestTimings: false), aligner: aligner);
        await disabled.LoadPlanAsync(plan);
        await disabled.PlayAsync();
        Assert.Equal(calls, aligner.Calls);
        Assert.Equal(CueGranularity.Sentence, disabled.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Granularity);
    }

    [Fact]
    public async Task BrokenAlignmentAvailabilityProbeCannotPreventSpeech()
    {
        var plan = await Plan("Go home.");
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), new FakeAudioTransport(),
            aligner: new TestAligner { FailProbe = true });
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
        Assert.Equal(CueGranularity.Sentence, coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Granularity);
    }

    [Fact]
    public async Task AlignmentFailurePreservesPlaybackAndReportsCoarseHighlighting()
    {
        var plan = await Plan("Go home.");
        var transport = new FakeAudioTransport();
        var aligner = new TestAligner { Fail = true };
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), transport, aligner: aligner);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
        await WaitUntil(() => aligner.Calls > 0 && coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Diagnostics
            .Any(d => d.Contains("alignment unavailable", StringComparison.OrdinalIgnoreCase)), "the failure to be reported");
        Assert.Equal(CueGranularity.Sentence, coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Granularity);
    }

    /// <summary>
    /// The first alignment of a session starts the aligner, 23 to 56 s on the reference machine;
    /// a passage waiting for its words to be timed stopped reading mid-paragraph. It plays at once,
    /// highlighted whole, and its words are highlighted from when their timing arrives.
    /// </summary>
    [Fact]
    public async Task APassagePlays_WithoutWaitingForItsWordsToBeTimed()
    {
        var plan = await Plan("Go home.");
        var transport = new FakeAudioTransport();
        var aligner = new TestAligner { Hold = new TaskCompletionSource() };
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(new FakeTtsOptions(FixedFrameCount: 24000)), transport, aligner: aligner);
        await coordinator.LoadPlanAsync(plan);

        var play = coordinator.PlayAsync();
        Assert.True(await Task.WhenAny(play, Task.Delay(5000)) == play, "Play waited for word alignment");
        Assert.Single(transport.Loaded);
        Assert.Equal(CueGranularity.Sentence, coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Granularity);

        aligner.Hold.SetResult();
        await WaitUntil(() => coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Granularity == CueGranularity.Word,
            "word timing to attach");
    }

    [Fact]
    public async Task LoadingADocument_StartsTheAligner_BeforeAnyPassageNeedsIt()
    {
        var plan = await Plan("Go home.");
        var aligner = new TestAligner();
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), new FakeAudioTransport(), aligner: aligner);
        await coordinator.LoadPlanAsync(plan);
        await WaitUntil(() => aligner.WarmUps == 1, "the aligner to be warmed up");
        await coordinator.LoadPlanAsync(plan);
        await Task.Delay(100);
        Assert.Equal(1, aligner.WarmUps);   // once a session
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    // ---- the speech engine's own word timing ---------------------------------------------

    /// <summary>A word cue for every word of the text the engine was given, half a second each.</summary>
    private static IReadOnlyList<TimedCue> EngineWords(string text, long frames)
        => SpokenWord.Tokenize(text).Select((w, i) => new TimedCue(i * 2000L, i * 2000L + 1800, w.Range, [], CueGranularity.Word, null, [])).ToArray();

    private static async Task<(SpeechAsset Asset, TestAligner Aligner)> SpeakWith(WordTimingSource source, bool alignerFails = false)
    {
        var plan = await Plan("The small dog saw the small dog today.");
        var tts = new FakeTtsProvider(new FakeTtsOptions(EmitTimings: true, TimingFactory: EngineWords));
        var transport = new FakeAudioTransport();
        var aligner = new TestAligner { Fail = alignerFails };
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: NarrationCoordinatorOptions.Default with { WordTiming = source }, aligner: aligner);
        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        for (int i = 0; i < 300 && transport.Loaded.Count == 0; i++) await Task.Delay(10);
        // The aligner, when it runs, runs after the passage can be played.
        for (int i = 0; i < 100 && source == WordTimingSource.Aligner && aligner.Calls == 0; i++) await Task.Delay(10);
        await Task.Delay(100);
        return (Assert.Single(transport.Loaded).Asset, aligner);
    }

    [Fact]
    public async Task WithTheEnginePreferred_ItsWordTimingStands_AndTheAlignerIsNotRun()
    {
        var (asset, aligner) = await SpeakWith(WordTimingSource.Engine);

        Assert.Equal(TimingProvenance.NativeDuration, asset.TimingProvenance);
        Assert.Equal(8, asset.Timings.Count);
        Assert.All(asset.Timings, c => Assert.Equal(CueGranularity.Word, c.Granularity));
        Assert.All(asset.Timings, c => Assert.NotEmpty(c.AnchorIds));
        Assert.Equal(0, aligner.Calls);
        // Nor is it started ahead of need: its start-up would compete with the speech.
        Assert.Equal(0, aligner.WarmUps);
    }

    [Fact]
    public async Task WithTheAlignerPreferred_TheEnginesTimingIsThereAtOnce_AndTheAlignerStillRuns()
    {
        var (asset, aligner) = await SpeakWith(WordTimingSource.Aligner);

        // What the transport was given when the passage became playable: the engine's words.
        Assert.Equal(TimingProvenance.NativeDuration, asset.TimingProvenance);
        Assert.Equal(8, asset.Timings.Count);
        Assert.Equal(1, aligner.Calls);
        Assert.Equal(1, aligner.WarmUps);
    }

    [Fact]
    public async Task WithTheEnginePreferred_APassageItCannotTime_StartsTheAligner_WithoutHoldingUpWhatFollows()
    {
        // The engine gives no timing here, so the aligner is needed after all; it is started for
        // the load, and while it starts (half a minute for the real one) the passages ahead are
        // still prepared and no alignment is asked of a worker that is not up.
        var plan = await Plan("The first sentence is read aloud here.", "A second sentence follows it closely.", "And a third one ends the page.");
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        var aligner = new TestAligner { WarmUpHold = new TaskCompletionSource() };
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: NarrationCoordinatorOptions.Default with { WordTiming = WordTimingSource.Engine }, aligner: aligner);

        await coordinator.LoadPlanAsync(plan);
        for (int i = 0; i < 300 && tts.SynthesizeCount < plan.SegmentCount; i++) await Task.Delay(10);

        Assert.Equal(plan.SegmentCount, tts.SynthesizeCount);
        Assert.Equal(1, aligner.WarmUps);
        Assert.Equal(0, aligner.Calls);

        aligner.WarmUpHold.SetResult();
        for (int i = 0; i < 300 && aligner.Calls < plan.SegmentCount; i++) await Task.Delay(10);
        Assert.Equal(plan.SegmentCount, aligner.Calls);
        Assert.True(aligner.WarmedBeforeFirstCall, "An alignment was asked of an aligner that had not finished starting.");
    }

    [Fact]
    public async Task WithTheEnginePreferred_AndTimingGiven_ADeclinedPassageStartsTheAlignerThen()
    {
        // The engine times its words, except for one passage: the aligner is not started at load,
        // and is started, and waited for, by the passage that needs it.
        var plan = await Plan("The first sentence is read aloud here.", "A second sentence follows it closely.");
        string declined = plan.Segments[1].SpokenText;
        var tts = new FakeTtsProvider(new FakeTtsOptions(EmitTimings: true,
            TimingFactory: (text, frames) => text == declined ? [] : EngineWords(text, frames)));
        var aligner = new TestAligner();
        await using var coordinator = new NarrationCoordinator(tts, new FakeAudioTransport(),
            options: NarrationCoordinatorOptions.Default with { WordTiming = WordTimingSource.Engine }, aligner: aligner);

        await coordinator.LoadPlanAsync(plan);
        for (int i = 0; i < 300 && aligner.Calls == 0; i++) await Task.Delay(10);

        Assert.Equal(1, aligner.Calls);
        Assert.Equal(1, aligner.WarmUps);
        Assert.True(aligner.WarmedBeforeFirstCall);
    }

    [Fact]
    public void EngineWordCues_TakeTheScriptsWords_AndTwoNamesOfOneLetterBecomeOneCue()
    {
        // The engine was given "double you sub tee"; mapped back, both names fall in "w".
        const string script = "w sub t";
        TimedCue[] cues =
        [
            new(0, 900, new TextRange(0, 1), [], CueGranularity.Word, null, []),
            new(900, 2000, new TextRange(0, 1), [], CueGranularity.Word, null, []),
            new(2000, 3000, new TextRange(2, 3), [], CueGranularity.Word, null, []),
            new(3000, 4000, new TextRange(6, 1), [], CueGranularity.Word, null, []),
        ];

        var placed = NarrationCoordinator.ToScriptWords(cues, script);

        Assert.Equal([new TextRange(0, 1), new TextRange(2, 3), new TextRange(6, 1)], placed.Select(c => c.SpokenRange!.Value));
        Assert.Equal((0L, 2000L), (placed[0].StartSample, placed[0].EndSample));
    }

    [Fact]
    public void AnEngineCueThatFallsInNoWordOfTheScript_LeavesNoWordTiming()
    {
        TimedCue[] cues = [new(0, 900, new TextRange(1, 1), [], CueGranularity.Word, null, [])];   // the space in "a b"

        Assert.Empty(NarrationCoordinator.ToScriptWords(cues, "a b"));
    }

    private sealed class TestAligner : IForcedAligner
    {
        public string Revision { get; set; } = "test/1";
        public int Calls;
        public bool Fail;
        public bool FailProbe;
        public TaskCompletionSource? Hold;
        public int WarmUps;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => FailProbe ? throw new IOException("test unavailable") : Task.FromResult(true);
        /// <summary>Set to make the start-up last until it is released, as the real worker's does for half a minute.</summary>
        public TaskCompletionSource? WarmUpHold;
        public bool WarmedBeforeFirstCall = true;
        private volatile bool _warm;
        public async Task WarmUpAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref WarmUps);
            if (WarmUpHold is { } hold) await hold.Task.WaitAsync(ct);
            _warm = true;
        }
        public async Task<WordAlignment> AlignAsync(ReadOnlyMemory<byte> pcm, AudioFormat format, string script, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Calls) == 1) WarmedBeforeFirstCall = _warm;
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            if (Fail) throw new InvalidDataException("test mismatch");
            return new WordAlignment(SpokenWord.Tokenize(script).Select((w, i) => new AlignedWord(w.Range, i * 12000, i * 12000 + 3000)).ToArray(), []);
        }
    }
}
