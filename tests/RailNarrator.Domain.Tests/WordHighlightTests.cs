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
            Assert.Equal(CueGranularity.Word, coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000).Granularity);
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
        var cue = coordinator.Scheduler!.Resolve(plan.Segments[0].Id, 2000);
        Assert.Equal(CueGranularity.Sentence, cue.Granularity);
        Assert.Contains(cue.Diagnostics, d => d.Contains("alignment unavailable", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TestAligner : IForcedAligner
    {
        public string Revision { get; set; } = "test/1";
        public int Calls;
        public bool Fail;
        public bool FailProbe;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => FailProbe ? throw new IOException("test unavailable") : Task.FromResult(true);
        public Task<WordAlignment> AlignAsync(ReadOnlyMemory<byte> pcm, AudioFormat format, string script, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new InvalidDataException("test mismatch");
            return Task.FromResult(new WordAlignment(SpokenWord.Tokenize(script).Select((w, i) => new AlignedWord(w.Range, i * 12000, i * 12000 + 3000)).ToArray(), []));
        }
    }
}
