using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Years and identifiers are read the way a person reads them aloud, and only where the context
/// says that is what they are.
/// </summary>
public sealed class NumberReadingTests
{
    private static string Spoken(string text) => TextNormaliser.ExpandForSpeech(text).Text;

    [Theory]
    [InlineData("(Hamilton, 1989; Ang & Timmermann, 2012).", "(Hamilton, nineteen eighty nine; Ang & Timmermann, two thousand twelve).")]
    [InlineData("as shown by Engle (1982).", "as shown by Engle (nineteen eighty two).")]
    [InlineData("In 1987 the market fell.", "In nineteen eighty seven the market fell.")]
    [InlineData("since 1905.", "since nineteen oh five.")]
    [InlineData("from 1900.", "from nineteen hundred.")]
    [InlineData("October 1929", "October nineteen twenty nine")]
    [InlineData("from May 1, 1997 until", "from May one, nineteen ninety seven until")]
    [InlineData("the period 1990-1995 was calm", "the period nineteen ninety-nineteen ninety five was calm")]
    public void YearsInYearContexts_AreReadAsYears(string text, string expected)
        => Assert.Equal(expected, Spoken(text));

    [Fact]
    public void AYearAfterEtAl_IsReadAsAYear()
        => Assert.Contains("nineteen ninety four extended it.", Spoken("Bollerslev et al. 1994 extended it."));

    [Theory]
    [InlineData("a sample of 1500 firms", "a sample of one thousand five hundred firms")]
    [InlineData("in 1500 patients", "in one thousand five hundred patients")]
    [InlineData("we draw 1989 values", "we draw one thousand nine hundred eighty nine values")]
    [InlineData("Journal of Finance 48:1749-1779.", "Journal of Finance forty eight:one thousand seven hundred forty nine-one thousand seven hundred seventy nine.")]
    [InlineData("pp. 1187-1211", "pages  one thousand one hundred eighty seven-one thousand two hundred eleven")]
    [InlineData("on 3, 1997 occasions", "on three, one thousand nine hundred ninety seven occasions")]
    [InlineData("between 1500 and 1600 rows", "between one thousand five hundred and one thousand six hundred rows")]
    public void CountsStayCardinals(string text, string expected)
        => Assert.Equal(expected, Spoken(text));

    /// <summary>
    /// Four characters after a year cue are not always four digits: a decimal or a signed number
    /// must be read as a number, not handed to the year reading (which used to throw and fail the
    /// whole plan).
    /// </summary>
    [Theory]
    [InlineData("rose from 0.20 to 0.22", "rose from zero point two zero to zero point two two")]
    [InlineData("reduced by 0.75 overall", "reduced by zero point seven five overall")]
    [InlineData("in 1.25 hours", "in one point two five hours")]
    [InlineData("(Smith, 1.5)", "(Smith, one point five)")]
    [InlineData("from -100 to +150", "from minus one hundred to plus one hundred fifty")]
    [InlineData("since +1.5 degrees", "since plus one point five degrees")]
    public void DecimalsAndSignedNumbersInYearContexts_AreReadAsNumbers(string text, string expected)
        => Assert.Equal(expected, Spoken(text));

    [Theory]
    [InlineData("ISSN 1532-4168", "ISSN one five three two four one six eight")]
    [InlineData("ISSN: 0378-426X.", "ISSN: zero three seven eight four two six ex.")]
    [InlineData("ISBN 978-0-19-850", "ISBN nine seven eight zero one nine eight five zero")]
    [InlineData("ISSN: 0747-4938 (Print) 1532-4168 (Online)", "ISSN: zero seven four seven four nine three eight (Print) one five three two four one six eight (Online)")]
    public void IdentifiersAreReadDigitByDigit(string text, string expected)
        => Assert.Equal(expected, Spoken(text));

    [Fact]
    public void AYearMapsBackToItsFourDigits()
    {
        const string text = "(Hamilton, 1989)";
        var spoken = TextNormaliser.ExpandForSpeech(text);
        int start = spoken.Text.IndexOf("nineteen", StringComparison.Ordinal);
        var source = spoken.ToSourceRanges(new TextRange(start, "nineteen eighty nine".Length));
        Assert.Equal("1989", text.Substring(source.Min(r => r.Start), source.Max(r => r.End) - source.Min(r => r.Start)));
    }

    // ---- DOIs: "DOI link" (the reader's choice, 2026-11-19) ----

    [Theory]
    [InlineData("nbag025 https://doi.org/10.1093/jjfinec/nbag025 Article", "nbag025 DOI link Article")]
    [InlineData("See doi: 10.1016/j.jeconom.2020.01.003.", "See DOI link.")]
    [InlineData("DOI 10.1080/07350015.2014.983601, published", "DOI link, published")]
    [InlineData("(available at dx.doi.org/10.1002/jae.2389)", "(available at DOI link)")]
    [InlineData("(10.1016/S0304-4076(02)00043-8)", "(DOI link)")]
    [InlineData("Econometrica 10.2307/1912773 shows", "Econometrica DOI link shows")]   // JSTOR: an all-digit suffix
    public void Dois_AreReadAsADoiLink(string text, string expected)
        => Assert.Equal(expected, Spoken(text));

    [Theory]
    [InlineData("version 10.5 of the software", "version ten point five of the software")]
    [InlineData("a ratio of 10.25/2", "a ratio of ten point two five/two")]   // a DOI's registrant code has 4-9 digits
    public void NumbersThatAreNotDois_AreStillRead(string text, string expected)
        => Assert.Equal(expected, Spoken(text));

    [Fact]
    public void ADoiLink_MapsBackToTheWholePrintedDoi()
    {
        const string text = "See https://doi.org/10.1093/jjfinec/nbag025 here.";
        var spoken = TextNormaliser.ExpandForSpeech(text);
        int at = spoken.Text.IndexOf("DOI link", StringComparison.Ordinal);
        var mapping = Assert.Single(spoken.Mappings, m => m.Output.Start == at);
        Assert.Equal("https://doi.org/10.1093/jjfinec/nbag025", text.Substring(mapping.Input.Start, mapping.Input.Length));
    }
}
