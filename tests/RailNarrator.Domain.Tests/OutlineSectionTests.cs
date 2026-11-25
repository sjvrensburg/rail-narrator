using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>The section the status bar names: by page while browsing, by the reading on the page being read.</summary>
public sealed class OutlineSectionTests
{
    private static readonly OutlineHeading[] Outline =
    [
        new("1 Introduction", 0),
        new("2 Method", 2),     // half way down page 2
        new("2.1 Data", 2),     // lower still
        new("3 Results", 4),
    ];

    // Passages 10 and 14 are the headings of sections 2 and 2.1, both on page 2.
    private static readonly ReadHeading[] Read =
    [
        new(0, 0, "1. Introduction"),
        new(10, 2, "2 METHOD"),
        new(14, 2, "2.1 Data"),
        new(30, 4, "3 Results"),
    ];

    [Fact]
    public void ByPage_WhileBrowsing()
    {
        Assert.Equal(0, OutlineSections.Find(Outline, 1, null, Read));
        Assert.Equal(2, OutlineSections.Find(Outline, 2, null, Read));
        Assert.Equal(3, OutlineSections.Find(Outline, 7, null, Read));
    }

    [Fact]
    public void ReadingTheEndOfTheSectionBefore_NamesThatSection()
        => Assert.Equal(0, OutlineSections.Find(Outline, 2, 8, Read));

    [Fact]
    public void ReadingBetweenTwoHeadingsOnOnePage_NamesTheFirst()
        => Assert.Equal(1, OutlineSections.Find(Outline, 2, 12, Read));

    [Fact]
    public void ReadingTheHeadingItself_NamesIt()
    {
        Assert.Equal(1, OutlineSections.Find(Outline, 2, 10, Read));
        Assert.Equal(2, OutlineSections.Find(Outline, 2, 14, Read));
    }

    [Fact]
    public void HeadingNotFoundInThePlan_IsTakenAsReached()
        => Assert.Equal(3, OutlineSections.Find(Outline, 4, 25, [new(0, 0, "Introduction")]));

    [Fact]
    public void BeforeTheFirstHeading_NamesNone()
        => Assert.Equal(-1, OutlineSections.Find([new("Intro", 1)], 1, 0, [new(3, 1, "Intro")]));
}
