using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// In-line citation markers are left out of a first reading, as the filter setting has always
/// promised; content in brackets is not.
/// </summary>
public sealed class CitationTests
{
    private static string Dropped(string text) => TextNormaliser.CollapseWhitespace(TextNormaliser.DropCitations(text).Text).Text;

    [Theory]
    [InlineData("The ARCH model by Engle (1982) and, later, the GARCH model by Bollerslev (1986) work well.",
                "The ARCH model by Engle and, later, the GARCH model by Bollerslev work well.")]
    [InlineData("a central role in asset allocation (Hamilton, 1989; Ang & Timmermann, 2012).",
                "a central role in asset allocation.")]
    [InlineData("the time-varying GARCH model of Amado and Teräsvirta (2008, 2013) in which",
                "the time-varying GARCH model of Amado and Teräsvirta in which")]
    [InlineData("As Mikosch and Starica (2004) demonstrated, this effect may be due to breaks.",
                "As Mikosch and Starica demonstrated, this effect may be due to breaks.")]
    [InlineData("the process of Glosten et al. (1993) is used", "the process of Glosten et al. is used")]
    [InlineData("see the survey (van Bellegem and von Sachs, 2004) for details", "see the survey for details")]
    [InlineData("as shown before [12] and in later work [3, 7] and [4–6].", "as shown before and in later work and.")]
    public void CitationMarkersAreLeftOut(string text, string expected) => Assert.Equal(expected, Dropped(text));

    [Theory]
    [InlineData("returns over the period (1990–1995) were calm.")]
    [InlineData("the GJR-GARCH(p, q) model is nested in (2) when g is one.")]
    [InlineData("a weight of (0.5) on the first lag.")]
    [InlineData("the vector v[2] holds the second weight.")]
    public void ContentInBracketsIsKept(string text) => Assert.Equal(text, Dropped(text));

    [Fact]
    public void TheWordsAroundACitationStillMapToTheirSource()
    {
        const string text = "Engle (1982) proposed ARCH.";
        var anchor = SyntheticNarration.Anchor("a0", 0, new PageRect(72, 100, 300, 112));
        var unit = SyntheticNarration.ProseUnit("u0", text, anchor);

        var segment = new NarrationPlanner().BuildProseSegment(unit, 0);

        Assert.Equal("Engle proposed ARCH.", segment.SpokenText);
        Assert.All(segment.SourceLinks, l => Assert.True(l.SpokenRange.End <= segment.SpokenText.Length));
        Assert.Equal("Engle (1982) proposed ARCH.",
            new NarrationPlanner().BuildProseSegment(unit, 0, NarrationFilterSettings.Default with { IncludeCitations = true }).SpokenText
                .Replace("nineteen eighty two", "1982", StringComparison.Ordinal));
    }
}
