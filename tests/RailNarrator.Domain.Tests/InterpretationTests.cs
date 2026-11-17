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
            SyntheticNarration.ProseUnit("u1", "The variance σ 2t is assumed to have a multiplicative decomposition", a1) with { ContainsMaths = true },
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

    [Fact]
    public async Task TheCoordinatorSpeaksTheInterpretation_AndPublishesIt()
    {
        var plan = await PlanAsync(Interpret);
        var tts = new FakeTtsProvider();
        var transport = new FakeAudioTransport();
        const string words = "The variance is assumed to split into two multiplied parts.";
        var resolver = new MathInterpretationResolver(FakeInterpreter.Saying(words), options: Interpret);
        await using var coordinator = new NarrationCoordinator(tts, transport,
            options: new NarrationCoordinatorOptions(LookaheadSegments: 3), resolver: resolver);
        var published = new List<NarrationSegment>();
        coordinator.SegmentResolved += s => { lock (published) published.Add(s); };

        await coordinator.LoadPlanAsync(plan);
        await coordinator.PlayAsync();
        await WaitUntil(() => tts.Requests.Any(r => r.Text == words), "the interpretation to be synthesised");

        Assert.DoesNotContain(tts.Requests, r => r.Text == plan.Segments[1].SpokenText);   // the placeholder is never spoken
        await WaitUntil(() => { lock (published) return published.Any(s => s.Id == plan.Segments[1].Id); }, "the resolution to be published");
        lock (published) Assert.Equal(words, published.First(s => s.Id == plan.Segments[1].Id).SpokenText);
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
