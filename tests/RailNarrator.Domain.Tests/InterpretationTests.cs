using RailNarrator.Domain;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Maths interpreted during playback: the planner leaves maths passages pending with an honest
/// fallback, the resolver asks once per passage with the definitions around it, and the
/// coordinator speaks the interpretation and never word-aligns it.
/// </summary>
public sealed class InterpretationTests
{
    private sealed class FakeInterpreter(Func<MathInterpretationRequest, MathInterpretationResult> answer) : IMathInterpreter
    {
        private readonly List<MathInterpretationRequest> _requests = [];
        public IReadOnlyList<MathInterpretationRequest> Requests { get { lock (_requests) return _requests.ToList(); } }
        public string Revision => "fake/1";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<MathInterpretationResult> InterpretAsync(MathInterpretationRequest request, CancellationToken ct = default)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(answer(request));
        }

        public static FakeInterpreter Saying(string words) => new(_ => new MathInterpretationResult(words, [], []));
    }

    private sealed class MemoryCache : ITextCache
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> TryGetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task PutAsync(string key, string value, CancellationToken ct = default) { _values[key] = value; return Task.CompletedTask; }
    }

    private static readonly NarrationPlannerOptions Interpret = new(MathMode: MathMode.Interpret, InterpretMaths: true);

    private static IReadOnlyList<ReadingUnit> Units()
    {
        var a0 = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 500, 112));
        var a1 = SyntheticNarration.Anchor("a1", 0, new PageRect(72, 116, 500, 128));
        var a2 = SyntheticNarration.Anchor("a2", 0, new PageRect(200, 140, 400, 160));
        var a3 = SyntheticNarration.Anchor("a3", 0, new PageRect(72, 166, 500, 178));
        return
        [
            SyntheticNarration.ProseUnit("u0", "Here h describes the short-run dynamics of the variance of the returns.", a0),
            SyntheticNarration.ProseUnit("u1", "The variance σ 2t is assumed to have a multiplicative decomposition.", a1) with { ContainsMaths = true },
            new ReadingUnit("u2", ReadingUnitKind.DisplayMath, [a2], "σ2t = htgt,", null, "", "", [], 0),
            SyntheticNarration.ProseUnit("u3", "where g is a positive-valued deterministic component.", a3),
        ];
    }

    private static Task<NarrationPlan> PlanAsync(NarrationPlannerOptions options)
    {
        var units = Units();
        var snapshot = new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree, []);
        return new NarrationPlanner(options: options).BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default);
    }

    [Fact]
    public async Task ThePlannerLeavesMathsPendingWithAnHonestFallback()
    {
        var plan = await PlanAsync(Interpret);

        Assert.Equal(ScriptProvenance.SourceVerbatim, plan.Segments[0].Provenance);
        Assert.Equal(ScriptProvenance.PendingInterpretation, plan.Segments[1].Provenance);
        Assert.StartsWith("The variance", plan.Segments[1].SpokenText, StringComparison.Ordinal);   // read as printed if it must be
        Assert.Equal(ScriptProvenance.PendingInterpretation, plan.Segments[2].Provenance);
        Assert.Equal(Interpret.AnnouncementTemplate, plan.Segments[2].SpokenText);                  // never the garbled text layer
        Assert.Equal(ScriptProvenance.SourceVerbatim, plan.Segments[3].Provenance);
    }

    [Fact]
    public async Task WithoutAnInterpreterNothingIsLeftPending()
    {
        var plan = await PlanAsync(Interpret with { InterpretMaths = false });
        Assert.DoesNotContain(plan.Segments, s => s.Provenance == ScriptProvenance.PendingInterpretation);
    }

    [Fact]
    public async Task TheResolverSendsTheDefinitionsAroundThePassage_AndLinksTheWholePassage()
    {
        var plan = await PlanAsync(Interpret);
        var interpreter = FakeInterpreter.Saying("The conditional variance is the product of its short-run and long-run components.");
        var resolver = new MathInterpretationResolver(interpreter, options: Interpret);

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[2]);

        var request = Assert.Single(interpreter.Requests);
        Assert.Equal(MathPassageKind.DisplayEquation, request.Kind);
        Assert.Contains("short-run dynamics", request.ContextBefore, StringComparison.Ordinal);
        Assert.Contains("deterministic component", request.ContextAfter, StringComparison.Ordinal);
        Assert.Equal(ScriptProvenance.ModelGenerated, resolved.Provenance);
        Assert.Equal(plan.Segments[2].ScriptRevision + 1, resolved.ScriptRevision);
        var link = Assert.Single(resolved.SourceLinks);
        Assert.Equal(resolved.SpokenText.Length, link.SpokenRange.Length);
        Assert.Equal(plan.Segments[2].AnchorIds, link.AnchorIds);
    }

    private sealed class UnreachableInterpreter : IMathInterpreter
    {
        public string Revision => "fake/1";
        public string? UnavailableReason => "maths.example refused the API key (401).";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<MathInterpretationResult> InterpretAsync(MathInterpretationRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("an unavailable interpreter must not be asked");
    }

    [Fact]
    public async Task AnInterpreterThatCannotBeAsked_IsReportedWithItsReason()
    {
        // A refused key degraded every equation to its fallback with nothing said but a note on each passage.
        var plan = await PlanAsync(Interpret);
        var resolver = new MathInterpretationResolver(new UnreachableInterpreter(), options: Interpret);
        var reported = new List<string>();
        resolver.EndpointProblem += reported.Add;

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[2]);

        Assert.NotEqual(ScriptProvenance.ModelGenerated, resolved.Provenance);
        Assert.Equal(["maths.example refused the API key (401)."], reported);
    }

    [Fact]
    public async Task ARequestTheEndpointRefused_IsReported_AndARejectedAnswerIsNot()
    {
        var plan = await PlanAsync(Interpret);
        var reported = new List<string>();

        var refused = new MathInterpretationResolver(
            new FakeInterpreter(_ => MathInterpretationResult.Reject("HTTP 429") with { EndpointProblem = "maths.example is out of quota or limiting requests (429)." }),
            options: Interpret);
        refused.EndpointProblem += reported.Add;
        await refused.ResolveAsync(plan, plan.Segments[2]);

        var rejected = new MathInterpretationResolver(new FakeInterpreter(_ => MathInterpretationResult.Reject("it still has symbols")), options: Interpret);
        rejected.EndpointProblem += reported.Add;
        await rejected.ResolveAsync(plan, plan.Segments[2]);

        Assert.Equal(["maths.example is out of quota or limiting requests (429)."], reported);
    }

    [Fact]
    public async Task APassageIsInterpretedOnce_ThenServedFromTheCache()
    {
        var plan = await PlanAsync(Interpret);
        var interpreter = FakeInterpreter.Saying("The variance is assumed to split into two multiplied parts.");
        var cache = new MemoryCache();

        await new MathInterpretationResolver(interpreter, cache: cache, options: Interpret).ResolveAsync(plan, plan.Segments[1]);
        var again = await new MathInterpretationResolver(interpreter, cache: cache, options: Interpret).ResolveAsync(plan, plan.Segments[1]);

        Assert.Single(interpreter.Requests);
        Assert.Equal("The variance is assumed to split into two multiplied parts.", again.SpokenText);
        Assert.Equal(MathPassageKind.SentenceWithMaths, interpreter.Requests[0].Kind);
    }

    [Fact]
    public async Task ARejectionFallsBack_AnnouncementForAnEquation_VerbatimForASentence()
    {
        var plan = await PlanAsync(Interpret);
        var interpreter = new FakeInterpreter(_ => MathInterpretationResult.Reject("It still contains notation."));
        var resolver = new MathInterpretationResolver(interpreter, options: Interpret);

        var sentence = await resolver.ResolveAsync(plan, plan.Segments[1]);
        var equation = await resolver.ResolveAsync(plan, plan.Segments[2]);

        Assert.Equal(ScriptProvenance.SourceVerbatim, sentence.Provenance);
        Assert.Equal(plan.Segments[1].SpokenText, sentence.SpokenText);
        Assert.Contains(sentence.Warnings, w => w.Contains("rejected", StringComparison.Ordinal));
        Assert.Equal(ScriptProvenance.FailureAnnouncement, equation.Provenance);
        Assert.Equal(Interpret.AnnouncementTemplate, equation.SpokenText);
    }

    private sealed class FakeReader(string? words) : IMathReader
    {
        public int Calls;
        public string Revision => "reader/1";
        public Task<string?> ReadAsync(MathInterpretationRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(words);
        }
    }

    [Fact]
    public async Task ARefusedInterpretationIsReadMechanically_LabelledAndCached()
    {
        var plan = await PlanAsync(Interpret);
        var interpreter = new FakeInterpreter(_ => MathInterpretationResult.Reject("It still contains notation."));
        var reader = new FakeReader("sigma sub t squared equals h sub t g sub t");
        var cache = new MemoryCache();

        var equation = await new MathInterpretationResolver(interpreter, cache: cache, options: Interpret, reader: reader).ResolveAsync(plan, plan.Segments[2]);
        var again = await new MathInterpretationResolver(interpreter, cache: cache, options: Interpret, reader: reader).ResolveAsync(plan, plan.Segments[2]);

        Assert.Equal(ScriptProvenance.LocalRule, equation.Provenance);
        Assert.Equal("sigma sub t squared equals h sub t g sub t", equation.SpokenText);
        Assert.Contains(equation.Warnings, w => w.Contains("rejected", StringComparison.Ordinal));
        Assert.Contains(equation.Warnings, w => w.Contains("not its meaning", StringComparison.Ordinal));
        Assert.Equal(equation.SpokenText.Length, Assert.Single(equation.SourceLinks).SpokenRange.Length);
        Assert.Equal(1, reader.Calls);   // the second resolve came from the cache
        Assert.Equal(equation.SpokenText, again.SpokenText);
    }

    [Fact]
    public async Task WhenTheReaderCannotReadItEither_TheOldFallbackStands()
    {
        var plan = await PlanAsync(Interpret);
        var interpreter = new FakeInterpreter(_ => MathInterpretationResult.Reject("It still contains notation."));
        var resolver = new MathInterpretationResolver(interpreter, options: Interpret, reader: new FakeReader(null));

        Assert.Equal(ScriptProvenance.SourceVerbatim, (await resolver.ResolveAsync(plan, plan.Segments[1])).Provenance);
        Assert.Equal(ScriptProvenance.FailureAnnouncement, (await resolver.ResolveAsync(plan, plan.Segments[2])).Provenance);
    }

    [Fact]
    public async Task ASuccessfulInterpretationNeverAsksTheReader()
    {
        var plan = await PlanAsync(Interpret);
        var reader = new FakeReader("x sub t");
        await new MathInterpretationResolver(FakeInterpreter.Saying("The variance splits in two."), options: Interpret, reader: reader)
            .ResolveAsync(plan, plan.Segments[2]);
        Assert.Equal(0, reader.Calls);
    }

    private sealed class BrokenCrops : IExtractionSource
    {
        public Task<ExtractionSnapshot> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default)
            => throw new ArgumentNullException("image");
    }

    [Fact]
    public async Task AFailedCropIsReported_NotSilentlyDropped()
    {
        var plan = await PlanAsync(Interpret);
        var interpreter = FakeInterpreter.Saying("The variance splits in two.");
        var resolved = await new MathInterpretationResolver(interpreter, crops: new BrokenCrops(), options: Interpret).ResolveAsync(plan, plan.Segments[2]);

        Assert.Equal(ScriptProvenance.ModelGenerated, resolved.Provenance);
        Assert.Empty(interpreter.Requests[0].Images);
        Assert.Contains(resolved.Warnings, w => w.Contains("page image could not be rendered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRequestSaysWhetherThePassageHoldsADisplayEquation()
    {
        var plan = await PlanAsync(Interpret);
        var interpreter = FakeInterpreter.Saying("The variance splits in two.");
        var resolver = new MathInterpretationResolver(interpreter, options: Interpret);
        await resolver.ResolveAsync(plan, plan.Segments[1]);
        await resolver.ResolveAsync(plan, plan.Segments[2]);
        Assert.False(interpreter.Requests[0].HasDisplayEquation);
        Assert.True(interpreter.Requests[1].HasDisplayEquation);
    }

    /// <summary>A resolver whose interpretations wait for the test to release them.</summary>
    private sealed class GatedResolver(string words) : ISegmentResolver
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<NarrationSegment> ResolveAsync(NarrationPlan plan, NarrationSegment segment, CancellationToken ct = default)
        {
            if (segment.Provenance != ScriptProvenance.PendingInterpretation) return segment;
            await Gate.Task.WaitAsync(ct);
            return segment with { SpokenText = words, Provenance = ScriptProvenance.ModelGenerated, ScriptRevision = segment.ScriptRevision + 1,
                SourceLinks = [new SourceLink(new TextRange(0, words.Length), segment.AnchorIds)] };
        }

        public Task<NarrationSegment> RefreshAsync(NarrationPlan plan, NarrationSegment segment, CancellationToken ct = default)
            => ResolveAsync(plan, segment, ct);
    }

    [Fact]
    public async Task PlayWaitsForTheMathsAhead_ThenReads()
    {
        var plan = await PlanAsync(Interpret);
        var transport = new FakeAudioTransport();
        var resolver = new GatedResolver("The variance splits in two.");
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3, LookaheadSeconds: 0, PreRollSeconds: 30, PreRollTimeoutSeconds: 30), resolver: resolver);
        await coordinator.LoadPlanAsync(plan);

        var play = coordinator.PlayAsync();
        await WaitUntil(() => coordinator.Snapshot.State == PlaybackState.Buffering, "the coordinator to buffer");
        await Task.Delay(300);
        Assert.Equal(0, transport.PlayCount);   // the first sentence (five seconds) is not enough lead
        Assert.Contains("Interpreting the maths ahead", coordinator.Snapshot.Message ?? "", StringComparison.Ordinal);
        Assert.Equal(PreparationStage.InterpretingAhead, coordinator.Snapshot.Preparation?.Stage);
        Assert.Equal(30, coordinator.Snapshot.Preparation!.TargetSeconds);

        resolver.Gate.SetResult();
        await play;
        Assert.Equal(1, transport.PlayCount);
    }

    [Fact]
    public async Task PlayStartsAnywayWhenTheMathsTakesTooLong()
    {
        var plan = await PlanAsync(Interpret);
        var transport = new FakeAudioTransport();
        var resolver = new GatedResolver("never");
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 1, LookaheadSeconds: 0, PreRollSeconds: 30, PreRollTimeoutSeconds: 0.4), resolver: resolver);
        await coordinator.LoadPlanAsync(plan);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.PlayAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, transport.PlayCount);   // the first, ready, sentence plays
        Assert.InRange(started.Elapsed.TotalSeconds, 0.35, 5);
    }

    [Fact]
    public async Task WithNothingPendingPlayDoesNotBuffer()
    {
        var plan = await PlanAsync(Interpret with { InterpretMaths = false });
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), transport,
            options: new NarrationCoordinatorOptions(PreRollSeconds: 60), resolver: new GatedResolver("unused"));
        var states = new List<PlaybackState>();
        coordinator.SnapshotChanged += s => { lock (states) states.Add(s.State); };
        await coordinator.LoadPlanAsync(plan);

        await coordinator.PlayAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, transport.PlayCount);
        lock (states) Assert.DoesNotContain(PlaybackState.Buffering, states);
    }

    [Fact]
    public async Task TheCoordinatorSpeaksTheInterpretation_AndPublishesIt()
    {
        var plan = await PlanAsync(Interpret);
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        const string words = "The variance is assumed to split into two multiplied parts.";
        var resolver = new MathInterpretationResolver(FakeInterpreter.Saying(words), options: Interpret);
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3, LookaheadSeconds: 0), resolver: resolver);
        var published = new List<NarrationSegment>();
        coordinator.SegmentResolved += s => { lock (published) published.Add(s); };

        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => tts.Requests.Any(r => r.Text == words), "the interpretation to be synthesised");

        Assert.DoesNotContain(tts.Requests, r => r.Text == plan.Segments[1].SpokenText);   // the placeholder is never spoken
        await WaitUntil(() => { lock (published) return published.Any(s => s.Id == plan.Segments[1].Id); }, "the resolution to be published");
        lock (published) Assert.Equal(words, published.First(s => s.Id == plan.Segments[1].Id).SpokenText);
    }

    [Fact]
    public async Task RegenerateAsksAgainDespiteTheCache_AndKeepsTheWordingIfTheAnswerIsUnusable()
    {
        var plan = await PlanAsync(Interpret);
        int call = 0;
        var interpreter = new FakeInterpreter(_ => ++call switch
        {
            1 => new MathInterpretationResult("The variance splits into two multiplied parts.", [], []),
            2 => new MathInterpretationResult("The variance is a product of a short-run and a long-run part.", [], []),
            _ => MathInterpretationResult.Reject("It still contains notation."),
        });
        var resolver = new MathInterpretationResolver(interpreter, cache: new MemoryCache(), options: Interpret);

        var first = await resolver.ResolveAsync(plan, plan.Segments[1]);
        var second = await resolver.RefreshAsync(plan, first);
        var third = await resolver.RefreshAsync(plan, second);

        Assert.Equal(3, interpreter.Requests.Count);   // each refresh went to the model, not the cache
        Assert.Equal("The variance is a product of a short-run and a long-run part.", second.SpokenText);
        Assert.True(second.ScriptRevision > first.ScriptRevision);
        Assert.Equal(second.SpokenText, third.SpokenText);
        Assert.Contains(third.Warnings, w => w.Contains("previous wording is kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCoordinatorSpeaksARegeneratedPassage()
    {
        var plan = await PlanAsync(Interpret);
        int call = 0;
        var interpreter = new FakeInterpreter(_ => new MathInterpretationResult(++call == 1 ? "First wording of the passage." : "Second wording of the passage.", [], []));
        var tts = new FakeTtsProvider();
        await using var coordinator = new NarrationCoordinator(tts, new FakeAudioTransport(),
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3, LookaheadSeconds: 0),
            resolver: new MathInterpretationResolver(interpreter, options: Interpret));
        await coordinator.LoadPlanAsync(plan);
        await WaitUntil(() => coordinator.Plan?.Segments[1].Provenance == ScriptProvenance.ModelGenerated, "the first interpretation");

        var fresh = await coordinator.RegenerateSegmentAsync(plan.Segments[1].Id);
        await coordinator.PlayAsync();

        Assert.Equal("Second wording of the passage.", fresh?.SpokenText);
        Assert.Equal("Second wording of the passage.", coordinator.Plan!.Segments[1].SpokenText);
        await WaitUntil(() => tts.Requests.Any(r => r.Text == "Second wording of the passage."), "the regenerated wording to be synthesised");
    }

    [Fact]
    public async Task AnEditMadeWhileTheModelIsAskedAgainStands()
    {
        var plan = await PlanAsync(Interpret);
        bool regenerating = false;
        using var asked = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        var interpreter = new FakeInterpreter(_ =>
        {
            if (!Volatile.Read(ref regenerating)) return new MathInterpretationResult("First wording of the passage.", [], []);
            asked.Set();
            answer.Wait(TimeSpan.FromSeconds(10));
            return new MathInterpretationResult("Second wording of the passage.", [], []);
        });
        await using var coordinator = new NarrationCoordinator(new FakeTtsProvider(), new FakeAudioTransport(),
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3, LookaheadSeconds: 0),
            resolver: new MathInterpretationResolver(interpreter, options: Interpret));
        await coordinator.LoadPlanAsync(plan);
        // Every passage interpreted first: the model is asked next by the regeneration and nothing else.
        await WaitUntil(() => coordinator.Plan?.Segments.All(s => s.Provenance != ScriptProvenance.PendingInterpretation) == true,
            "every interpretation");

        string id = plan.Segments[1].Id;
        Volatile.Write(ref regenerating, true);
        var regeneration = Task.Run(() => coordinator.RegenerateSegmentAsync(id));
        Assert.True(asked.Wait(TimeSpan.FromSeconds(10)), "the model was not asked again");
        await coordinator.ApplyScriptEditAsync(id, "The reader's own wording.");
        answer.Set();
        var result = await regeneration;

        Assert.Equal("The reader's own wording.", coordinator.Plan!.Segment(id)!.SpokenText);
        Assert.Equal(ScriptProvenance.UserEdited, result?.Provenance);
    }

    private static IReadOnlyList<ReadingUnit> LeadInUnits(string leadIn)
    {
        var a0 = SyntheticNarration.Anchor("b0", 0, new PageRect(72, 100, 500, 112));
        var a1 = SyntheticNarration.Anchor("b1", 0, new PageRect(200, 120, 400, 140));
        var a2 = SyntheticNarration.Anchor("b2", 0, new PageRect(72, 146, 500, 158));
        return
        [
            SyntheticNarration.ProseUnit("v0", leadIn, a0),
            new ReadingUnit("v1", ReadingUnitKind.DisplayMath, [a1], "yt = E(yt |Ft−1) + εt , (1)", null, "", "", [], 0),
            SyntheticNarration.ProseUnit("v2", "where F contains the historical information available at the previous time.", a2) with { ContainsMaths = true },
        ];
    }

    private static Task<NarrationPlan> PlanOf(IReadOnlyList<ReadingUnit> units, NarrationPlannerOptions options)
        => new NarrationPlanner(options: options).BuildPlanAsync(
            new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(), ExtractionOptions.ModelFree, []),
            units, NarrationFilterSettings.Default);

    [Fact]
    public async Task ALeadInAndTheEquationItIntroducesAreOnePassage()
    {
        var plan = await PlanOf(LeadInUnits("Let the return series be given as"), Interpret);

        Assert.Equal(2, plan.Segments.Count);
        var passage = plan.Segments[0];
        Assert.Equal(["v0", "v1"], passage.ReadingUnitIds);
        Assert.Equal(ScriptProvenance.PendingInterpretation, passage.Provenance);
        // The fallback reads the lead-in as printed, then announces the equation, each linked to its own region.
        Assert.Equal("Let the return series be given as " + Interpret.AnnouncementTemplate, passage.SpokenText);
        Assert.Contains(passage.SourceLinks, l => l.AnchorIds.Contains("b1")
            && passage.SpokenText.Substring(l.SpokenRange.Start, l.SpokenRange.Length) == Interpret.AnnouncementTemplate);
        Assert.Contains(passage.SourceLinks, l => l.AnchorIds.Contains("b0"));
    }

    [Theory]
    [InlineData("The model is defined below.")]
    [InlineData("Is this the right model?")]
    public async Task AFinishedSentenceIsNotMergedWithTheEquationAfterIt(string sentence)
    {
        var plan = await PlanOf(LeadInUnits(sentence), Interpret);
        Assert.Equal(3, plan.Segments.Count);
    }

    [Fact]
    public async Task WithoutInterpretationALeadInStaysApart()
    {
        var plan = await PlanOf(LeadInUnits("Let the return series be given as"), Interpret with { InterpretMaths = false });
        Assert.Equal(3, plan.Segments.Count);
    }

    [Fact]
    public async Task TheMergedPassageIsInterpretedAsOneSentence_WithContextAfterTheEquation()
    {
        var plan = await PlanOf(LeadInUnits("Let the return series be given as"), Interpret);
        var interpreter = FakeInterpreter.Saying("Let the return be its expected value given the past, plus a surprise.");

        var resolved = await new MathInterpretationResolver(interpreter, options: Interpret).ResolveAsync(plan, plan.Segments[0]);

        var request = Assert.Single(interpreter.Requests);
        Assert.Equal(MathPassageKind.SentenceWithMaths, request.Kind);
        Assert.Contains("Let the return series be given as", request.TextLayer, StringComparison.Ordinal);
        Assert.Contains("E(yt", request.TextLayer, StringComparison.Ordinal);
        Assert.StartsWith("where F contains", request.ContextAfter, StringComparison.Ordinal);
        Assert.Equal(ScriptProvenance.ModelGenerated, resolved.Provenance);
        Assert.Equal(["b0", "b1"], resolved.AnchorIds.OrderBy(a => a));
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }
}
