using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// The planner's contract: prose is never rewritten by a model, and mathematics is never handed to the
/// speech engine as raw notation.
/// </summary>
public sealed class NarrationPlannerTests
{
    [Fact]
    public void Prose_IsNormalisedForSpeechNotRewritten()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 300, 112), new TextRange(0, 20));
        var unit = new ReadingUnit("u0", ReadingUnitKind.Prose, [anchor],
            "See \u00A73 and Fig. 4 for the of\uFB01ce report.", null, "", "", [], 0)
        {
            SourceFragments = [new UnitSourceFragment(new TextRange(0, 38), "a0", 0)],
        };

        var planner = new NarrationPlanner();
        var segment = planner.BuildProseSegment(unit, 0);

        Assert.Contains("section", segment.SpokenText);
        Assert.Contains("Figure", segment.SpokenText);
        Assert.Contains("office", segment.SpokenText);
        Assert.Equal(ScriptProvenance.SourceVerbatim, segment.Provenance);
        Assert.Equal("en-GB", segment.Language);
        // Links cover the spoken text and cite only real anchors.
        Assert.NotEmpty(segment.SourceLinks);
        Assert.All(segment.SourceLinks, l => Assert.Equal(["a0"], l.AnchorIds));
    }

    [Fact]
    public async Task Prose_LinksEachLineFragmentToItsOwnAnchor()
    {
        // Two lines of one sentence: each spoken fragment must point at its own line, or a highlight
        // would cover both lines while only one is being read.
        var a0 = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 300, 112));
        var a1 = SyntheticNarration.Anchor("a1", 0, new PageRect(72, 116, 260, 128));
        const string text = "First line here second line there";
        var unit = new ReadingUnit("u0", ReadingUnitKind.Prose, [a0, a1], text, null, "", "", [], 0)
        {
            SourceFragments =
            [
                new UnitSourceFragment(new TextRange(0, 16), "a0", 0),
                new UnitSourceFragment(new TextRange(16, 17), "a1", 0),
            ],
        };

        var segment = new NarrationPlanner().BuildProseSegment(unit, 0);

        Assert.Equal(2, segment.SourceLinks.Count);
        Assert.Equal(["a0"], segment.SourceLinks[0].AnchorIds);
        Assert.Equal(["a1"], segment.SourceLinks[1].AnchorIds);
        Assert.Equal(0, segment.SourceLinks[0].SpokenRange.Start);
        Assert.Equal(segment.SpokenText.Length, segment.SourceLinks[^1].SpokenRange.End);
    }

    [Fact]
    public async Task Maths_UsesTheVerbaliserAndMarksTheSegmentGenerated()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", @"P(A\mid B)", anchor,
            contextBefore: "We consider conditional probability.", contextAfter: "This is the definition.");

        var verbaliser = StubMathVerbaliser.Returning("The probability of A given B.");
        var planner = new NarrationPlanner(verbaliser: verbaliser);
        var segment = await planner.BuildMathsSegmentAsync(unit, 0, CancellationToken.None);

        Assert.Equal("The probability of A given B.", segment.SpokenText);
        Assert.Equal(ScriptProvenance.ModelGenerated, segment.Provenance);
        Assert.Equal(ReadingUnitKind.DisplayMath, segment.Kind);
        Assert.Equal(["a0"], segment.AnchorIds);

        // The verbaliser got the expression and bounded context, and only real anchor ids to cite.
        var request = Assert.Single(verbaliser.Received);
        Assert.Equal(@"P(A\mid B)", request.Representation);
        Assert.Equal("We consider conditional probability.", request.ContextBefore);
        Assert.Equal(["a0"], request.AnchorIds);
    }

    [Fact]
    public async Task Maths_WithoutAVerbaliser_AnnouncesInsteadOfReadingLaTeX()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", @"\bar{x} = \frac{1}{n}\sum_{i=1}^{n} x_i", anchor);

        var planner = new NarrationPlanner(verbaliser: null);
        var segment = await planner.BuildMathsSegmentAsync(unit, 0, CancellationToken.None);

        // The single most important negative guarantee in the maths path: raw LaTeX must never be
        // passed to a speech engine and presented as a natural reading.
        Assert.DoesNotContain("\\frac", segment.SpokenText);
        Assert.DoesNotContain("\\sum", segment.SpokenText);
        Assert.DoesNotContain("backslash", segment.SpokenText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScriptProvenance.FailureAnnouncement, segment.Provenance);
        Assert.Contains("could not be read reliably", segment.SpokenText);
        Assert.NotEmpty(segment.Warnings);
    }

    [Fact]
    public async Task Maths_RejectedVerbalisation_BecomesAnAnnouncementNotSilentFallback()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", @"\int_0^1 x^2 dx", anchor);

        var planner = new NarrationPlanner(verbaliser: StubMathVerbaliser.Rejecting("output cited unknown source id"));
        var segment = await planner.BuildMathsSegmentAsync(unit, 0, CancellationToken.None);

        Assert.Equal(ScriptProvenance.FailureAnnouncement, segment.Provenance);
        Assert.Contains(segment.Warnings, w => w.Contains("unknown source id", StringComparison.Ordinal));
        Assert.DoesNotContain("\\int", segment.SpokenText);
    }

    [Fact]
    public async Task Maths_AnInventedAnchorId_IsDiscarded()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", "x^2", anchor);

        var planner = new NarrationPlanner(verbaliser: StubMathVerbaliser.CitingInventedAnchor("X squared."));
        var segment = await planner.BuildMathsSegmentAsync(unit, 0, CancellationToken.None);

        Assert.Equal("X squared.", segment.SpokenText);
        // The invented id is dropped; the real anchor is used so the highlight still lands somewhere true.
        Assert.DoesNotContain("a-invented", segment.AnchorIds);
        Assert.Equal(["a0"], segment.AnchorIds);
    }

    [Fact]
    public async Task Maths_ExplainMode_KeepsTheInterpretationSeparate()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", @"\hat{\beta}", anchor);

        var verbaliser = StubMathVerbaliser.Returning("Beta hat.", explanation: "This is a generated interpretation.");
        var planner = new NarrationPlanner(verbaliser: verbaliser,
            options: new NarrationPlannerOptions(MathMode: MathMode.Explain));
        var segment = await planner.BuildMathsSegmentAsync(unit, 0, CancellationToken.None);

        Assert.StartsWith("Beta hat.", segment.SpokenText);
        Assert.Contains("generated interpretation", segment.SpokenText);
        Assert.Equal(MathMode.Explain, segment.MathMode);
    }

    [Fact]
    public async Task Maths_DefaultMode_DoesNotAppendTheExplanation()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", @"\hat{\beta}", anchor);

        var planner = new NarrationPlanner(verbaliser: StubMathVerbaliser.Returning("Beta hat.", "An OLS estimator."));
        var segment = await planner.BuildMathsSegmentAsync(unit, 0, CancellationToken.None);

        // Faithful-only is the default; an unsolicited interpretation is extra speech the reader did
        // not ask for, and may be an unsupported identification.
        Assert.Equal("Beta hat.", segment.SpokenText);
        Assert.DoesNotContain("OLS", segment.SpokenText);
    }

    [Fact]
    public async Task Maths_LocalRuleFallback_IsLabelledAsMechanical()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", "x^2", anchor);

        var planner = new NarrationPlanner(
            verbaliser: null,
            options: new NarrationPlannerOptions(MathFallback: MathFallbackPolicy.LocalRuleReader),
            localMathReader: (_, representation) => $"local reading of {representation}");
        var segment = await planner.BuildMathsSegmentAsync(unit, 0, CancellationToken.None);

        Assert.Equal(ScriptProvenance.LocalRule, segment.Provenance);
        Assert.Contains(segment.Warnings, w => w.Contains("rule-based", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildPlan_StampsTheExtractionIdentitySoAReExtractionInvalidatesIt()
    {
        var snapshot = SyntheticNarration.Snapshot([new SyntheticNarration.LineSpec("Hello world.", 100f)]);
        var builder = new ReadingUnitBuilder();
        var units = builder.Build(snapshot);
        var plan = await new NarrationPlanner().BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default);

        Assert.True(plan.Matches(snapshot.Identity));
        Assert.False(plan.Matches(snapshot.Identity with { ExtractionRevision = 2 }));
        Assert.False(plan.Matches(new ExtractionIdentity("other", 1)));
    }

    [Fact]
    public async Task BuildPlan_FingerprintChangesWhenTheInputsChange()
    {
        var snapshot = SyntheticNarration.Snapshot([new SyntheticNarration.LineSpec("Hello world.", 100f)]);
        var units = new ReadingUnitBuilder().Build(snapshot);
        var planner = new NarrationPlanner();
        var plan = await planner.BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default);

        Assert.NotEqual("", plan.Header.InputFingerprint);
        string current = NarrationPlanner.ComputeInputFingerprint(
            planner.Options, NarrationFilterSettings.Default, units);
        Assert.True(plan.Matches(snapshot.Identity, current));

        // Same file, different inputs in force: the cached plan must not be reused.
        var otherFilters = NarrationFilterSettings.Default with { IncludeReferences = true };
        Assert.False(plan.Matches(snapshot.Identity,
            NarrationPlanner.ComputeInputFingerprint(planner.Options, otherFilters, units)));
        Assert.False(plan.Matches(snapshot.Identity,
            NarrationPlanner.ComputeInputFingerprint(
                planner.Options with { MathMode = MathMode.DetailedSymbolic }, NarrationFilterSettings.Default, units)));

        // A region override changes the units themselves, and the fingerprint follows the units.
        var changedUnits = units.Select(u => u with { Kind = ReadingUnitKind.InlineMath }).ToList();
        Assert.False(plan.Matches(snapshot.Identity,
            NarrationPlanner.ComputeInputFingerprint(planner.Options, NarrationFilterSettings.Default, changedUnits)));

        // Plans persisted before the fingerprint existed never match a fingerprinted comparison.
        var legacy = plan with { Header = plan.Header with { InputFingerprint = "" } };
        Assert.False(legacy.Matches(snapshot.Identity, current));
        Assert.True(legacy.Matches(snapshot.Identity));
    }
}

/// <summary>
/// The camera glides with the reading: the line being read at a steady height, a line too wide for
/// the view panned along with its words, and never a pan the timing does not support.
/// </summary>
public sealed class CameraFollowTests
{
    private static readonly PageRect Viewport = new(0, 0, 400, 600);

    private static FollowTarget Words(PageRect word, params PageRect[] passage)
        => new(0, [word], passage.Length == 0 ? [word] : passage, CueGranularity.Word);

    private static FollowDecision Plan(FollowTarget target, FollowOptions? options = null, PageRect? viewport = null)
        => CameraFollowPlanner.Plan(new PageViewport(0, viewport ?? Viewport), target, FollowMode.Follow, options ?? FollowOptions.Default);

    /// <summary>
    /// The reading line goes to the reading height. At fit-width that is the whole motion: the page
    /// rolls up a line at a time, gliding, instead of standing still and then jumping a screenful.
    /// </summary>
    [Fact]
    public void TheLineBeingRead_GoesToTheReadingHeight()
    {
        var word = new PageRect(100, 700, 140, 712);
        var options = FollowOptions.Default;
        var decision = Plan(Words(word, new PageRect(20, 700, 380, 712)), options);

        Assert.True(decision.ShouldMove);
        var to = decision.TargetVisible.Visible;
        Assert.Equal(to.Top + Viewport.Height * options.VerticalBias, word.MidY, precision: 3);
        // The line fits across the view, so nothing moves sideways.
        Assert.Equal(Viewport.Left, to.Left);
        Assert.True(decision.GlideSeconds > 0, "a move on the same page glides");
    }

    [Fact]
    public void TheNextWordOnTheSameFittingLine_DoesNotMoveTheCamera()
    {
        var options = FollowOptions.Default;
        var line = new PageRect(20, 300, 380, 312);
        double top = 306 - Viewport.Height * options.VerticalBias;
        var viewport = new PageRect(0, (float)top, 400, (float)top + 600);
        var decision = Plan(Words(new PageRect(200, 300, 240, 312), line), options, viewport);

        Assert.False(decision.ShouldMove);
        Assert.Contains("already on the reading line", decision.Reason);
        // The target is still given, so a glide in flight can be redirected to it.
        Assert.False(decision.TargetVisible.Visible.IsEmpty);
    }

    /// <summary>
    /// At high magnification a line is wider than the view: the camera pans along it, keeping the
    /// spoken word a little left of centre so the rest of the line is ahead, in view.
    /// </summary>
    [Fact]
    public void AWideLine_IsPannedAlongWithTheSpokenWord()
    {
        var options = FollowOptions.Default;
        var narrow = new PageRect(0, 0, 100, 150);   // 400% of the 400x600 view
        var line = new PageRect(20, 300, 380, 312);
        var first = Plan(Words(new PageRect(150, 300, 180, 312), line), options, narrow).TargetVisible.Visible;
        var later = Plan(Words(new PageRect(250, 300, 280, 312), line), options, narrow).TargetVisible.Visible;

        Assert.Equal(150 - 100 * options.LeadFraction, first.Left, precision: 3);
        Assert.Equal(250 - 100 * options.LeadFraction, later.Left, precision: 3);
    }

    /// <summary>
    /// The camera goes past the page margins at both ends of a wide line: the line's start sits in
    /// from the left edge, and the camera stops with the line's end near the middle of the view, so
    /// the last words are read where the eye already is.
    /// </summary>
    [Fact]
    public void PanningStartsInFromTheLeftAndEndsNearTheMiddle()
    {
        var options = FollowOptions.Default;
        var narrow = new PageRect(0, 0, 100, 150);
        var line = new PageRect(20, 300, 380, 312);

        var start = Plan(Words(new PageRect(20, 300, 50, 312), line), options, narrow).TargetVisible.Visible;
        var end = Plan(Words(new PageRect(350, 300, 380, 312), line) with { Progress = 1 }, options, narrow).TargetVisible.Visible;

        Assert.Equal(20 - 100 * options.LineStartFraction, start.Left, precision: 3);
        Assert.Equal(380 - 100 * options.LineEndFraction, end.Left, precision: 3);
    }

    /// <summary>
    /// Between two aligned words the camera travels at the measured pace: halfway through the time
    /// from one word's start to the next, it is halfway between their positions. That is what makes
    /// the pan continuous rather than a step per word.
    /// </summary>
    [Fact]
    public void BetweenTwoWords_TheCameraTravelsAtTheMeasuredPace()
    {
        var options = FollowOptions.Default;
        var narrow = new PageRect(0, 0, 100, 150);
        var line = new PageRect(20, 300, 380, 312);
        var word = new PageRect(150, 300, 180, 312);
        var next = new PageRect(190, 300, 230, 312);

        var halfway = Plan(Words(word, line) with { NextCueRects = [next], Progress = 0.5 }, options, narrow).TargetVisible.Visible;

        Assert.Equal(170 - 100 * options.LeadFraction, halfway.Left, precision: 3);
    }

    [Fact]
    public void TheLastWordOfALine_IsCrossedFromItsStartToItsEnd()
    {
        var options = FollowOptions.Default;
        var narrow = new PageRect(0, 0, 100, 150);
        var line = new PageRect(20, 300, 380, 312);
        var last = new PageRect(200, 300, 240, 312);
        var nextLine = new PageRect(20, 314, 60, 326);

        var to = Plan(Words(last, line) with { NextCueRects = [nextLine], Progress = 0.5 }, options, narrow).TargetVisible.Visible;

        Assert.Equal(220 - 100 * options.LeadFraction, to.Left, precision: 3);
    }

    /// <summary>A move to a new line is the carriage return: quicker than the pan along a line.</summary>
    [Fact]
    public void ANewLine_GlidesFasterThanThePan()
    {
        var options = FollowOptions.Default;
        var narrow = new PageRect(0, 0, 100, 150);
        var line = new PageRect(20, 300, 380, 312);
        var along = Plan(Words(new PageRect(150, 300, 180, 312), line), options, new PageRect(60, 243, 160, 393));   // already on the line, a little behind the reading
        var nextLine = Plan(Words(new PageRect(20, 340, 50, 352), new PageRect(20, 340, 380, 352)), options,
            new PageRect(260, 300 - 63, 360, 300 - 63 + 150));

        Assert.Equal(options.GlideSeconds, along.GlideSeconds, precision: 6);
        Assert.Equal(options.ReturnGlideSeconds, nextLine.GlideSeconds, precision: 6);
    }

    /// <summary>The camera's height follows the line, not each word's ink, so it does not bob.</summary>
    [Fact]
    public void WordsOfDifferentHeights_KeepTheCameraAtOneHeight()
    {
        var line = new PageRect(20, 298, 380, 314);
        var a = Plan(Words(new PageRect(100, 303, 108, 310), line)).TargetVisible.Visible;
        var tall = Plan(Words(new PageRect(120, 298, 190, 314), line)).TargetVisible.Visible;
        Assert.Equal(a.Top, tall.Top, precision: 3);
    }

    /// <summary>
    /// A sentence or equation cue has no timing inside it. Sweeping across it at a rate taken from
    /// the clip length would be an invented speech rate (ADR-0004), so its first line is framed at
    /// its start and held.
    /// </summary>
    [Fact]
    public void ACueWithoutWordTiming_IsFramedAtItsStartAndHeld()
    {
        var options = FollowOptions.Default;
        var narrow = new PageRect(200, 0, 300, 150);
        var lines = new[] { new PageRect(20, 300, 380, 312), new PageRect(20, 314, 380, 326), new PageRect(20, 328, 200, 340) };
        var decision = Plan(new FollowTarget(0, lines, lines, CueGranularity.Sentence), options, narrow);

        Assert.True(decision.ShouldMove);
        Assert.Equal(20 - 100 * options.LineStartFraction, decision.TargetVisible.Visible.Left, precision: 3);
        Assert.Contains("holding at the line start", decision.Reason);
    }

    [Fact]
    public void AnEquationThatFits_IsFramedWhole()
    {
        var equation = new PageRect(120, 900, 280, 960);
        var decision = Plan(new FollowTarget(0, [equation], [equation], CueGranularity.Equation));

        var to = decision.TargetVisible.Visible;
        Assert.True(to.Top <= equation.Top && to.Bottom >= equation.Bottom, "the whole equation is in view");
    }

    /// <summary>
    /// The line's extent comes from the passage's rects that share the word's line and touch it, not
    /// from a rect in the other column at the same height.
    /// </summary>
    [Fact]
    public void TheOtherColumn_IsNotPartOfTheLine()
    {
        var options = FollowOptions.Default;
        var leftColumn = new PageRect(20, 300, 190, 312);
        var rightColumn = new PageRect(230, 300, 390, 312);
        // 200 points wide: the left-column line fits, the two together would not.
        var view = new PageRect(0, 0, 200, 300);
        var decision = Plan(Words(new PageRect(100, 300, 130, 312), leftColumn, rightColumn), options, view);

        Assert.Equal(0, decision.TargetVisible.Visible.Left, precision: 3);
    }

    [Fact]
    public void FollowOff_NeverMoves()
    {
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), Words(new PageRect(100, 5000, 200, 5012)),
            FollowMode.Off, FollowOptions.Default);
        Assert.False(decision.ShouldMove);
    }

    [Fact]
    public void APageTransition_CutsRatherThanGlides()
    {
        var target = new FollowTarget(3, [new PageRect(100, 100, 200, 112)], [new PageRect(100, 100, 200, 112)], CueGranularity.Word);
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), target, FollowMode.Follow, FollowOptions.Default);

        Assert.True(decision.ShouldMove);
        Assert.Equal(3, decision.TargetVisible.PageIndex);
        Assert.Equal(0, decision.GlideSeconds);
        Assert.Contains("page transition", decision.Reason);
    }

    [Fact]
    public void ReducedMotion_JumpsRatherThanGliding()
    {
        var decision = Plan(Words(new PageRect(100, 700, 200, 712)), FollowOptions.NoMotion);
        Assert.True(decision.ShouldMove);
        Assert.Equal(0, decision.GlideSeconds);
    }

    /// <summary>The glide eases toward its target without overshooting, at any frame rate.</summary>
    [Theory]
    [InlineData(1.0 / 144)]
    [InlineData(1.0 / 60)]
    [InlineData(1.0 / 20)]
    public void TheGlide_ArrivesWithoutOvershoot(double dt)
    {
        double position = 0, velocity = 0, target = 100;
        double previous = position;
        for (double t = 0; t < 3; t += dt)
        {
            position = CameraGlide.Step(position, target, ref velocity, 0.35, dt);
            Assert.True(position >= previous - 1e-9, "never moves backwards");
            Assert.True(position <= target + 1e-9, "never overshoots");
            previous = position;
        }
        Assert.True(CameraGlide.HasArrived(position, target, velocity));
    }

    /// <summary>A target that moves on while the camera is gliding is followed without a stop.</summary>
    [Fact]
    public void ARetargetedGlide_KeepsItsSpeed()
    {
        double position = 0, velocity = 0;
        for (int i = 0; i < 6; i++) position = CameraGlide.Step(position, 40, ref velocity, 0.35, 1.0 / 60);
        double before = velocity;
        position = CameraGlide.Step(position, 80, ref velocity, 0.35, 1.0 / 60);
        Assert.True(before > 0 && velocity >= before * 0.9, $"velocity carried over: {before} then {velocity}");
    }

    [Fact]
    public void AStalledFrame_DoesNotFlingTheCamera()
    {
        double velocity = 0;
        double position = CameraGlide.Step(0, 1000, ref velocity, 0.35, dtSeconds: 5.0);
        double bounded = 0, v2 = 0;
        bounded = CameraGlide.Step(bounded, 1000, ref v2, 0.35, CameraGlide.MaxStepSeconds);
        Assert.Equal(bounded, position, precision: 6);
    }

    /// <summary>
    /// Highlight boxes on a line share one height, and every line of the paragraph gets the same
    /// height from its top, so the box does not jump as reading moves from "a" to "Typography".
    /// </summary>
    [Fact]
    public void HighlightBoxes_HaveOneHeightPerLine_AcrossTheParagraph()
    {
        var shortWord = new PageRect(100, 304, 108, 311);     // "a": x-height only
        var tallWord = new PageRect(120, 300, 190, 314);      // "Typography": ascender and descender
        var nextLineNoDescender = new PageRect(20, 318, 80, 329);
        var context = new[] { shortWord, tallWord, new PageRect(200, 301, 300, 311), nextLineNoDescender, new PageRect(90, 317, 160, 332) };

        var a = HighlightLines.Uniform([shortWord], context)[0];
        var t = HighlightLines.Uniform([tallWord], context)[0];
        var n = HighlightLines.Uniform([nextLineNoDescender], context)[0];

        Assert.Equal(t.Top, a.Top);
        Assert.Equal(t.Bottom, a.Bottom);
        Assert.Equal(t.Height, n.Height, precision: 3);
        Assert.Equal(100, a.Left);
        Assert.Equal(108, a.Right);   // only the height changes
    }

    [Fact]
    public void FrameUnit_RefusesToZoomBelowTheUsersMinimum()
    {
        // A long line at 800% cannot fit. Returning null is the caller's signal to offer line-level
        // narration or a focus pane; silently zooming out would discard the reader's magnification.
        var longLine = new PageRect(0, 100, 600, 112);
        var result = CameraFollowPlanner.FrameUnit(longLine, new PageRect(0, 0, 200, 150), minZoom: 4.0);
        Assert.Null(result);
    }

    [Fact]
    public void FrameUnit_FitsAShortUnitAtHighMagnification()
    {
        var equation = new PageRect(200, 300, 300, 330);
        var result = CameraFollowPlanner.FrameUnit(equation, new PageRect(0, 0, 400, 600), minZoom: 1.0);
        Assert.NotNull(result);
        Assert.True(result!.Value.Zoom >= 1.0);
    }
}
