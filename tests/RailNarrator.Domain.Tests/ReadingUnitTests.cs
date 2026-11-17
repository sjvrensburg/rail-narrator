using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Reading-unit construction against fully controlled in-memory snapshots: cross-page text and
/// manual maths/prose overrides.
/// </summary>
public sealed class ReadingUnitTests
{
    private static ExtractionSnapshot TwoPageSnapshot()
    {
        var page0 = SyntheticNarration.Page(0, [new SyntheticNarration.LineSpec("First part ends here", 100f)]);
        var page1 = SyntheticNarration.Page(1, [new SyntheticNarration.LineSpec("continues on next page.", 100f)]);
        return new ExtractionSnapshot(
            new ExtractionIdentity("deadbeefcafebabe", 1),
            CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree,
            [page0, page1]);
    }

    [Fact]
    public void SentenceCrossingAPageBreak_KeepsItsLineBreakSpace()
    {
        // Both pages have a single line, so both lines carry per-page LineOrdinal 0. Keying line
        // changes on the ordinal alone merged the words either side of the break.
        var units = new ReadingUnitBuilder().Build(TwoPageSnapshot());
        string joined = string.Join(" ", units.Select(u => u.OriginalText));

        Assert.Contains("here continues", joined);
        Assert.DoesNotContain("herecontinues", joined);
    }

    [Fact]
    public void TreatAsMathsOverride_EachGlyphIsEmittedOnceInReadingOrder()
    {
        const string line = "Alpha beta gamma where x + ∑ y holds here today.";
        var snapshot = SyntheticNarration.Snapshot([new SyntheticNarration.LineSpec(line, 100f)]);
        // "Alpha" occupies the first five glyphs from the left margin.
        var region = new PageRect(70f, 99f, 101f, 111f);
        var builder = new ReadingUnitBuilder(new ReadingUnitBuilderOptions(
            NarrationFilterSettings.Default,
            Overrides: [new MathsRegionOverride(0, region, TreatAsMaths: true)]));

        var units = builder.Build(snapshot);
        string joined = string.Join(" ", units.Select(u => u.OriginalText));

        // Every glyph exactly once: the concatenated units are the line, whitespace aside.
        Assert.Equal(
            new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray()),
            new string(joined.Where(c => !char.IsWhiteSpace(c)).ToArray()));
        // The forced region is maths, emitted whole — not as letters after the prose spoke them.
        Assert.Contains(units, u => u.Kind == ReadingUnitKind.InlineMath && u.OriginalText.Contains("Alpha"));
        Assert.DoesNotContain(units, u => u.OriginalText is "A" or "l" or "p" or "h");
        // Reading order: the forced region precedes the detected inline maths.
        Assert.True(joined.IndexOf("Alpha", StringComparison.Ordinal) < joined.IndexOf('∑'),
            $"forced maths was emitted out of order: {joined}");
    }
}
