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
/// The camera must move only when it has to, only as far as it has to, and never against the user.
/// </summary>
public sealed class CameraFollowTests
{
    private static readonly PageRect Viewport = new(0, 0, 400, 600);

    [Fact]
    public void ACueInsideTheSafeZone_DoesNotMoveTheCamera()
    {
        var cue = new PageRect(150, 300, 250, 312);
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), [cue], 0,
            FollowMode.KeepVisible, FollowOptions.Default);

        Assert.False(decision.ShouldMove);
        Assert.Contains("safe zone", decision.Reason);
    }

    [Fact]
    public void ACueBelowTheViewport_MovesByTheMinimumNeeded()
    {
        var cue = new PageRect(100, 700, 200, 712);   // below the 600pt viewport
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), [cue], 0,
            FollowMode.KeepVisible, FollowOptions.Default);

        Assert.True(decision.ShouldMove);
        // Only the vertical axis moves: the reader's horizontal position is theirs, not the app's.
        Assert.Equal(Viewport.Left, decision.TargetVisible.Visible.Left);
        Assert.True(decision.TargetVisible.Visible.Top > Viewport.Top);
        // The move is the minimum that satisfies the policy, not a re-centring.
        Assert.True(decision.TargetVisible.Visible.Top < cue.Bottom);
        Assert.True(cue.Bottom <= decision.TargetVisible.Visible.Bottom);
    }

    [Fact]
    public void FollowOff_NeverMoves()
    {
        var cue = new PageRect(100, 5000, 200, 5012);
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), [cue], 0,
            FollowMode.Off, FollowOptions.Default);
        Assert.False(decision.ShouldMove);
    }

    [Fact]
    public void APageTransition_ReAnchorsInsteadOfPanningAcrossTheMargin()
    {
        var cue = new PageRect(100, 100, 200, 112);
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), [cue], 3,
            FollowMode.KeepVisible, FollowOptions.Default);

        Assert.True(decision.ShouldMove);
        Assert.Equal(3, decision.TargetVisible.PageIndex);
        Assert.Contains("page transition", decision.Reason);
    }

    [Fact]
    public void ReducedMotion_JumpsRatherThanAnimating()
    {
        var cue = new PageRect(100, 700, 200, 712);
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), [cue], 0,
            FollowMode.KeepVisible, FollowOptions.NoMotion);

        Assert.True(decision.ShouldMove);
        Assert.Equal(0, decision.AnimationSeconds);
    }

    [Fact]
    public void ReadingRail_CentresTheCueVertically()
    {
        var cue = new PageRect(100, 700, 200, 712);
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), [cue], 0,
            FollowMode.ReadingRail, FollowOptions.Default with { VerticalBias = 0.5 });

        Assert.True(decision.ShouldMove);
        double centre = decision.TargetVisible.Visible.Top + decision.TargetVisible.Visible.Height * 0.5;
        Assert.True(Math.Abs(centre - cue.MidY) < 1.0,
            $"rail mode should centre the cue: centre {centre}, cue mid {cue.MidY}");
    }

    [Fact]
    public void DisjointRects_AreFramedByTheirUnion_ButOnlyForTheMoveDecision()
    {
        // A cue split across two columns: the decision uses the union, which is safe because it only
        // decides whether and how far to move. It must never be what gets drawn.
        var left = new PageRect(20, 700, 180, 712);
        var right = new PageRect(240, 700, 390, 712);
        var decision = CameraFollowPlanner.Plan(new PageViewport(0, Viewport), [left, right], 0,
            FollowMode.KeepVisible, FollowOptions.Default);
        Assert.True(decision.ShouldMove);
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
