using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Brackets, "=" and "+" restored on a page whose maths font hides them as "ð", "Þ", "¼" and "þ",
/// and the same characters left alone where they are letters and a fraction.
/// </summary>
public sealed class LegacyMathsEncodingTests
{
    // As extracted from two journal articles (Oxford and Elsevier fonts).
    private const string Page =
        "we define time-varying parameters fi;j;t, with ri;j;t ¼ ɛ tanhðfi;j;tÞ for some constant. " +
        "JD ¼ INTð365:25 ðY þ 4716ÞÞ þ INTð30:6001 ðM þ 1ÞÞ þ D þ B; ð4Þ";

    [Fact]
    public void APageInTheEncoding_GetsItsSignsBack_CharacterForCharacter()
    {
        string restored = LegacyMathsEncoding.Restore(Page);

        Assert.Equal(Page.Length, restored.Length);
        Assert.Contains("ri;j;t = ɛ tanh(fi;j;t)", restored, StringComparison.Ordinal);
        Assert.Contains("JD = INT(365:25 (Y + 4716)) + INT(30:6001 (M + 1)) + D + B; (4)", restored, StringComparison.Ordinal);
        Assert.DoesNotContain(restored, c => c is 'ð' or 'Þ' or '¼' or 'þ');
    }

    [Theory]
    [InlineData("Það er ekki satt að Þór hafi sagt það við þig. Þetta er það sem við þurfum, sagði Þóra við bróður sinn.")]
    [InlineData("Add ¼ cup of sugar and ¼ teaspoon of salt, then another ¼ cup of milk.")]
    [InlineData("The rune þ and the letter ð are mentioned once each, and Þ once.")]
    [InlineData("Plain prose with (ordinary) brackets = and + signs.")]
    public void LettersAndFractions_AreLeftAlone(string text)
        => Assert.Same(text, LegacyMathsEncoding.Restore(text));

    [Fact]
    public void ABracketCutOffByTheEdgeOfThePage_DoesNotHideTheRest()
    {
        // One opening bracket whose close is overleaf, among many that match.
        string text = "aðbÞ cðdÞ eðfÞ gðhÞ iðjÞ and then kðl";
        Assert.Equal("a(b) c(d) e(f) g(h) i(j) and then k(l", LegacyMathsEncoding.Restore(text));
    }

    [Fact]
    public void APageWithNoEvidenceOfItsOwn_IsRestoredOnAnothersEvidence()
    {
        const string page = "with a common value of ν ¼ 7, where we can investigate the sensitivity";

        Assert.Same(page, LegacyMathsEncoding.Restore(page));
        Assert.Equal("with a common value of ν = 7, where we can investigate the sensitivity", LegacyMathsEncoding.RestoreAll(page));
    }
}
