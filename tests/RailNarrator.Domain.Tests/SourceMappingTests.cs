using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Offset-unit discipline. Offsets are not interchangeable between UTF-16, code points and UTF-8
/// bytes, and treating them as such silently moves every highlight after the first astral character.
/// </summary>
public sealed class TextOffsetTests
{
    // "a" + U+1D400 (MATHEMATICAL BOLD CAPITAL A, a surrogate pair) + "b"
    private const string WithSurrogate = "a\U0001D400b";

    [Fact]
    public void Utf16Length_CountsSurrogatePairAsTwo()
    {
        Assert.Equal(4, WithSurrogate.Length);
        Assert.Equal(3, TextOffsets.CodePointCount(WithSurrogate));
    }

    [Theory]
    [InlineData(0, 0)]   // before 'a'
    [InlineData(1, 1)]   // before the surrogate pair
    [InlineData(3, 2)]   // before 'b' — the pair counted once
    [InlineData(4, 3)]   // end
    public void CodePointOffset_ConvertsBothWays(int utf16, int codePoint)
    {
        Assert.Equal(codePoint, TextOffsets.ToCodePointOffset(WithSurrogate, utf16));
        Assert.Equal(utf16, TextOffsets.FromCodePointOffset(WithSurrogate, codePoint));
    }

    [Fact]
    public void CodePointOffset_RefusesToSplitASurrogatePair()
    {
        // Offset 2 lands between the high and low surrogate. An LLM returning code points and a
        // caller applying them as UTF-16 offsets would produce exactly this.
        Assert.True(TextOffsets.SplitsSurrogatePair(WithSurrogate, 2));
        Assert.Throws<ArgumentException>(() => TextOffsets.ToCodePointOffset(WithSurrogate, 2));
        Assert.Equal(3, TextOffsets.AlignForward(WithSurrogate, 2));
    }

    [Fact]
    public void Utf8ByteOffset_RoundTrips()
    {
        Assert.Equal(0, TextOffsets.ToUtf8ByteOffset(WithSurrogate, 0));
        Assert.Equal(1, TextOffsets.ToUtf8ByteOffset(WithSurrogate, 1));
        Assert.Equal(5, TextOffsets.ToUtf8ByteOffset(WithSurrogate, 3));   // 1 + 4 bytes for the pair
        Assert.Equal(3, TextOffsets.FromUtf8ByteOffset(WithSurrogate, 5));
    }

    [Fact]
    public void Utf8ByteOffset_RejectsAnOffsetInsideAnEncodedCharacter()
        => Assert.Throws<ArgumentException>(() => TextOffsets.FromUtf8ByteOffset(WithSurrogate, 2));

    [Fact]
    public void ToUtf16Range_ConvertsAnExternalCodePointRange()
    {
        var range = TextOffsets.ToUtf16Range(WithSurrogate, start: 1, length: 1, TextOffsetUnit.CodePoint);
        Assert.Equal(1, range.Start);
        Assert.Equal(2, range.Length);
        Assert.Equal("\U0001D400", range.Substring(WithSurrogate));
    }

    [Fact]
    public void TextRange_RejectsARangeBeyondTheString()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TextRange(2, 5).ValidateAgainst("abc"));
}

/// <summary>
/// Normalisation must keep the mapping back to the source. A transform that loses it forces the
/// highlight to be guessed, and a guessed highlight is wrong in a way nobody can see.
/// </summary>
public sealed class NormalisationTests
{
    [Fact]
    public void Ligatures_ExpandAndKeepTheirMapping()
    {
        var result = TextNormaliser.ExpandLigatures("of\uFB01ce");   // "of" + fi ligature + "ce"
        Assert.Equal("office", result.Text);

        // The expanded "fi" maps back to the single source ligature glyph.
        var source = result.ToSourceRange(new TextRange(2, 2));
        Assert.NotNull(source);
        Assert.Equal(new TextRange(2, 1), source);
    }

    [Fact]
    public void WhitespaceCollapses_MapsEverySurvivingCharacter()
    {
        var result = TextNormaliser.CollapseWhitespace("a   b\t\nc");
        Assert.Equal("a b c", result.Text);
        Assert.Equal(new TextRange(0, 1), result.ToSourceRange(new TextRange(0, 1)));   // 'a'
        Assert.Equal(new TextRange(4, 1), result.ToSourceRange(new TextRange(2, 1)));   // 'b'
        Assert.Equal(new TextRange(7, 1), result.ToSourceRange(new TextRange(4, 1)));   // 'c'
    }

    [Fact]
    public void Dehyphenation_JoinsASoftHyphenAtALineBreak()
    {
        // "infor-" + break + "mation": the hyphen is soft, so the word joins.
        var result = TextNormaliser.Dehyphenate("infor- mation", new HashSet<int> { 6 });
        Assert.Equal("information", result.Text);

        // Every output character still traces to a real source character.
        var source = result.ToSourceRange(new TextRange(4, 3));   // "rma"
        Assert.NotNull(source);
        Assert.Equal("rma", "infor- mation".Substring(source!.Value.Start, source.Value.Length).Replace("- ", ""));
    }

    [Fact]
    public void Dehyphenation_KeepsACompoundHyphen()
    {
        // "state-" + break + "of": both sides are words, so this is a compound, not a soft hyphen.
        var result = TextNormaliser.Dehyphenate("state- of-the-art", new HashSet<int> { 6 });
        Assert.Contains("state-", result.Text);
    }

    [Fact]
    public void Dehyphenation_KeepsAStandalonePrefix()
    {
        var result = TextNormaliser.Dehyphenate("re- evaluation", new HashSet<int> { 3 });
        Assert.Contains("re-", result.Text);
    }

    [Fact]
    public void Dehyphenation_DoesNothingWithoutALineBreak()
    {
        var result = TextNormaliser.Dehyphenate("infor- mation", new HashSet<int>());
        Assert.Equal("infor- mation", result.Text);
    }

    [Fact]
    public void SpokenExpansions_ReplaceSymbolsAndKeepTheSourceLink()
    {
        var result = TextNormaliser.ExpandForSpeech("see \u00A73 and Fig. 4");
        Assert.Contains("section", result.Text);
        Assert.Contains("Figure", result.Text);
        // The expanded word still points at the single source glyph it came from.
        int sectionIndex = result.Text.IndexOf("section", StringComparison.Ordinal);
        var source = result.ToSourceRange(new TextRange(sectionIndex, 1));
        Assert.NotNull(source);
        Assert.Equal('\u00A7', "see \u00A73 and Fig. 4"[source!.Value.Start]);
    }

    [Fact]
    public void PronunciationDictionary_OverridesTheBuiltInTable()
    {
        var result = TextNormaliser.ExpandForSpeech("the % sign",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["%"] = " per centum" });
        Assert.Contains("per centum", result.Text);
    }

    [Fact]
    public void Composition_TracesAllTheWayBackToTheOriginal()
    {
        const string original = "of\uFB01ce   infor- mation";
        // Break offsets are offsets into the *original*, so dehyphenation must run before any
        // transform that changes string length - a ligature expansion shifts everything after it.
        var breaks = new HashSet<int> { original.IndexOf("- ", StringComparison.Ordinal) + 1 };

        var spoken = TransformedText.Identity(original)
            .Then(t => TextNormaliser.Compose(t, TextNormaliser.Dehyphenate(t.Text, breaks)))
            .Then(t => TextNormaliser.Compose(t, TextNormaliser.ExpandLigatures(t.Text)))
            .Then(t => TextNormaliser.Compose(t, TextNormaliser.CollapseWhitespace(t.Text)));

        Assert.Equal("office information", spoken.Text);
        Assert.Equal(original, spoken.SourceText);
        // The final "m" of "information" maps back into the original string.
        var source = spoken.ToSourceRange(new TextRange(spoken.Text.Length - 1, 1));
        Assert.NotNull(source);
        Assert.Equal('n', original[source!.Value.Start]);
    }
}

/// <summary>
/// Timing evidence is checked, never trusted. The failure mode is an aligner that drops one word and
/// thereby shifts every later anchor: the audio still sounds right while the highlight reads the
/// wrong line.
/// </summary>
public sealed class CueValidationTests
{
    private static readonly HashSet<string> Known = ["a0", "a1", "a2"];

    private static TimedCue Cue(long start, long end, TextRange? spoken = null, string anchor = "a0",
        CueGranularity g = CueGranularity.Phrase, double? confidence = 0.9)
        => new(start, end, spoken, [anchor], g, confidence, []);

    [Fact]
    public void AcceptsAWellFormedMonotonicSet()
    {
        var result = TimedCueValidator.Validate([Cue(0, 100), Cue(100, 250, anchor: "a1")], 250, "some text", Known);
        Assert.Equal(2, result.Accepted.Count);
        Assert.Empty(result.Rejected);
        Assert.Equal(1.0, result.CoverageFraction, 3);
    }

    [Fact]
    public void RejectsACueThatRunsPastTheClip()
    {
        var result = TimedCueValidator.Validate([Cue(0, 999)], 250, "text", Known);
        Assert.Empty(result.Accepted);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("exceeds clip length", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsOverlappingCues()
    {
        var result = TimedCueValidator.Validate([Cue(0, 150), Cue(100, 250)], 250, "text", Known);
        Assert.Single(result.Accepted);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("before the previous cue", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsACueCitingAnUnknownAnchor()
    {
        // A model or aligner that invents a source id must not be able to move a highlight.
        var result = TimedCueValidator.Validate([Cue(0, 100, anchor: "a-does-not-exist")], 100, "text", Known);
        Assert.Empty(result.Accepted);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("unknown anchor", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsLowConfidenceAndWordCuesWithoutConfidence()
    {
        var low = TimedCueValidator.Validate([Cue(0, 100, confidence: 0.1)], 100, "text", Known);
        Assert.Empty(low.Accepted);

        var wordWithoutConfidence = TimedCueValidator.Validate(
            [Cue(0, 100, g: CueGranularity.Word, confidence: null)], 100, "text", Known);
        Assert.Empty(wordWithoutConfidence.Accepted);
    }

    [Fact]
    public void RejectsDegenerateCues()
    {
        var result = TimedCueValidator.Validate([Cue(50, 52), Cue(-5, 10)], 100, "text", Known);
        Assert.Empty(result.Accepted);
        Assert.Equal(2, result.Rejected.Count);
    }

    [Fact]
    public void ReportsCoverageAndFlagsAFallBack()
    {
        // Half the clip is covered: enough to reject the set as a basis for word-level highlighting.
        var result = TimedCueValidator.Validate([Cue(0, 50)], 100, "text", Known);
        Assert.Equal(0.5, result.CoverageFraction, 3);
        Assert.True(result.ShouldFallBackToCoarse);
    }

    [Fact]
    public void RejectsASpokenRangeBeyondTheScript()
    {
        var result = TimedCueValidator.Validate([Cue(0, 100, spoken: new TextRange(0, 500))], 100, "short", Known);
        Assert.Empty(result.Accepted);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("exceeds script length", StringComparison.Ordinal));
    }
}

/// <summary>
/// The two mappings, kept separate: audio position to script range, script range to source anchors.
/// </summary>
public sealed class SourceMapTests
{
    private static (NarrationPlan Plan, SourceMap Map) BuildPlan()
    {
        // A sentence occupying two disjoint lines, and an equation region.
        var lineA = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 300, 112), new TextRange(0, 20));
        var lineB = SyntheticNarration.Anchor("a1", 0, new PageRect(72, 116, 260, 128), new TextRange(21, 40));
        var equation = SyntheticNarration.Anchor("a2", 0, new PageRect(200, 200, 400, 240));

        var prose = new ReadingUnit("u0", ReadingUnitKind.Prose, [lineA, lineB],
            "A sentence that spans two lines of the page.", null, "", "", [], 0)
        {
            SourceFragments =
            [
                new UnitSourceFragment(new TextRange(0, 22), "a0", 0),
                new UnitSourceFragment(new TextRange(22, 21), "a1", 0),
            ],
        };
        var maths = SyntheticNarration.MathsUnit("u1", "\\frac{1}{n}", equation);

        var segments = new List<NarrationSegment>
        {
            new("s0", ["u0"], ReadingUnitKind.Prose, "A sentence that spans two lines of the page.", "en-GB",
                [new SourceLink(new TextRange(0, 22), ["a0"]), new SourceLink(new TextRange(22, 21), ["a1"])],
                MathMode.NaturalFaithful, ScriptProvenance.SourceVerbatim, [], 1),
            new("s1", ["u1"], ReadingUnitKind.DisplayMath, "One over n.", "en-GB",
                [new SourceLink(new TextRange(0, 11), ["a2"])],
                MathMode.NaturalFaithful, ScriptProvenance.ModelGenerated, [], 1),
        };

        var plan = new NarrationPlan(
            new NarrationPlanHeader("p", new ExtractionIdentity("h", 1), "en-GB", MathMode.NaturalFaithful,
                NarrationVersions.MathPrompt, null, NarrationFilterSettings.Default),
            [prose, maths], segments);
        return (plan, new SourceMap(plan));
    }

    [Fact]
    public void HighlightRects_ReturnsDisjointRectsAndNeverTheirUnion()
    {
        var (_, map) = BuildPlan();
        var rects = map.HighlightRects(["a0", "a1"], 0);

        Assert.Equal(2, rects.Count);
        // The union would cover y=112..116, the gap between the two lines. Neither rect may.
        Assert.DoesNotContain(rects, r => r.Top < 112 && r.Bottom > 116);
        Assert.True(rects[0].Bottom <= rects[1].Top);
    }

    [Fact]
    public void HighlightRects_ExcludesOtherPages()
    {
        var (_, map) = BuildPlan();
        Assert.Empty(map.HighlightRects(["a0"], 1));
        Assert.Single(map.HighlightRects(["a0"], 0));
    }

    [Fact]
    public void SpokenOffset_ResolvesToTheRightAnchorOnly()
    {
        var (_, map) = BuildPlan();
        Assert.Equal(["a0"], map.AnchorsForSpokenOffset("s0", 5));
        Assert.Equal(["a1"], map.AnchorsForSpokenOffset("s0", 30));
    }

    [Fact]
    public void AnEquationSegment_LinksItsWholeUtteranceToTheWholeRegion()
    {
        var (_, map) = BuildPlan();
        // "One over n." is a paraphrase: no spoken word corresponds to a glyph, so the whole
        // expression region is linked to the whole utterance.
        Assert.Equal(["a2"], map.AnchorsForSpokenRange("s1", new TextRange(0, 11)));
        Assert.Equal(["a2"], map.AnchorsForSpokenOffset("s1", 0));
        Assert.Equal(["a2"], map.AnchorsForSpokenOffset("s1", 10));
    }

    [Fact]
    public void FramingBounds_MayUnion_ButOnlyForCameraFraming()
    {
        var (_, map) = BuildPlan();
        var bounds = map.FramingBounds(["a0", "a1"], 0);
        Assert.NotNull(bounds);
        // The framing box is allowed to span the gap; the highlight rects above are not.
        Assert.True(bounds!.Value.Top <= 100 && bounds.Value.Bottom >= 128);
    }
}

/// <summary>
/// Cue resolution must be a pure function of media position. If it advances on its own, the highlight
/// drifts ahead of the audio and there is no way to notice from the UI.
/// </summary>
public sealed class CueSchedulerTests
{
    private static (CueScheduler Scheduler, SpeechAsset Asset) BuildScheduler(bool withTimings)
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 300, 112));
        var unit = SyntheticNarration.ProseUnit("u0", "A short sentence.", anchor);
        var plan = SyntheticNarration.Plan([unit]);

        IReadOnlyList<TimedCue>? timings = withTimings
            ? [new TimedCue(0, 4000, new TextRange(0, 1), ["a0"], CueGranularity.Word, 0.95, []),
               new TimedCue(4000, 12000, new TextRange(2, 5), ["a0"], CueGranularity.Word, 0.95, []),
               new TimedCue(12000, 24000, new TextRange(8, 8), ["a0"], CueGranularity.Word, 0.95, [])]
            : null;
        var asset = SyntheticNarration.Clip("s00000", 24000, timings: timings);

        var scheduler = new CueScheduler(plan);
        scheduler.AttachClip(asset, timings ?? [], []);
        return (scheduler, asset);
    }

    [Fact]
    public void WithoutTimings_FallsBackToTheWholeUnitAtSentenceGranularity()
    {
        var (scheduler, _) = BuildScheduler(withTimings: false);
        var cue = scheduler.Resolve("s00000", 5000);

        Assert.Equal(CueGranularity.Sentence, cue.Granularity);
        Assert.Equal(["a0"], cue.AnchorIds);
        Assert.Equal(TimingProvenance.ClipBoundary, cue.Provenance);
        Assert.NotEmpty(cue.Diagnostics);   // the fallback is reported, not silently applied
    }

    [Fact]
    public void AMathsSegment_FallsBackToEquationGranularity()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(200, 200, 400, 240));
        var unit = SyntheticNarration.MathsUnit("u0", "\\frac{1}{n}", anchor);
        var plan = SyntheticNarration.Plan([unit]);
        var scheduler = new CueScheduler(plan);

        var cue = scheduler.Resolve("s00000", 0);
        Assert.Equal(CueGranularity.Equation, cue.Granularity);
    }

    [Fact]
    public void ResolutionIsPure_PositionAloneDecidesTheCue()
    {
        var (scheduler, _) = BuildScheduler(withTimings: true);

        var first = scheduler.Resolve("s00000", 5000);
        // Repeated resolution at the same media position must not advance. This is the invariant a
        // wall-clock or buffer-count driven scheduler would break.
        for (int i = 0; i < 25; i++)
        {
            var again = scheduler.Resolve("s00000", 5000);
            Assert.Equal(first.Cue?.StartSample, again.Cue?.StartSample);
            Assert.Equal(first.AnchorIds, again.AnchorIds);
        }

        var later = scheduler.Resolve("s00000", 20000);
        Assert.NotEqual(first.Cue?.StartSample, later.Cue?.StartSample);
    }

    [Fact]
    public void APositionInAGapBetweenCues_HoldsThePreviousCue()
    {
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 300, 112));
        var plan = SyntheticNarration.Plan([SyntheticNarration.ProseUnit("u0", "two words", anchor)]);
        var scheduler = new CueScheduler(plan);
        // Two cues with a gap between them, as a real aligner leaves around a pause.
        var timings = new List<TimedCue>
        {
            new(0, 1000, new TextRange(0, 3), ["a0"], CueGranularity.Word, 0.9, []),
            new(5000, 8000, new TextRange(4, 5), ["a0"], CueGranularity.Word, 0.9, []),
        };
        scheduler.AttachClip(SyntheticNarration.Clip("s00000", 9000, timings: timings), timings, []);

        var cue = scheduler.Resolve("s00000", 3000);
        Assert.Equal(0, cue.Cue!.StartSample);
        Assert.Contains(cue.Diagnostics, d => d.Contains("gap between cues", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownSegment_StillYieldsAnEmptyButValidCue()
    {
        var (scheduler, _) = BuildScheduler(withTimings: false);
        var cue = scheduler.Resolve("not-in-plan", 0);
        Assert.Empty(cue.AnchorIds);
        Assert.NotEmpty(cue.Diagnostics);
    }
}
