using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// What a speech engine is given in place of the script: a letter standing alone as a symbol is
/// written as its name, so "a sub t" is not read as the article and "Y t plus h" is not slurred.
/// </summary>
public sealed class LetterNameTests
{
    private static string Speech(string script) => LetterNames.ForSpeech(script).Text;

    [Theory]
    [InlineData("y sub t plus h", "why sub tee plus aitch")]
    [InlineData("Y sub t equals beta times x sub t, plus an error.", "why sub tee equals beta times ex sub tee, plus an error.")]
    [InlineData("the sum over i of w sub i", "the sum over eye of double you sub eye")]
    [InlineData("the t-distribution with n minus k degrees of freedom", "the tee-distribution with en minus kay degrees of freedom")]
    public void ALoneLetter_IsWrittenAsItsName(string script, string speech) => Assert.Equal(speech, Speech(script));

    [Theory]
    [InlineData("a sub t is positive", "eh sub tee is positive")]
    [InlineData("x equals a plus b", "ex equals eh plus bee")]
    [InlineData("b minus a.", "bee minus eh.")]
    [InlineData("where a is the intercept and b the slope", "where eh is the intercept and bee the slope")]
    [InlineData("the function f of a, evaluated twice", "the function eff of eh, evaluated twice")]
    [InlineData("the matrix A times x", "the matrix eh times ex")]
    [InlineData("Let a be a constant.", "Let eh be a constant.")]
    [InlineData("a squared plus b squared", "eh squared plus bee squared")]
    [InlineData("x plus a times b", "ex plus eh times bee")]
    [InlineData("given a, the estimate is", "given eh, the estimate is")]
    [InlineData("the sum over a of x", "the sum over eh of ex")]
    [InlineData("integrated from a to b", "integrated from eh to bee")]
    [InlineData("Let a denote the intercept.", "Let eh denote the intercept.")]
    [InlineData("Let a satisfy the bound.", "Let eh satisfy the bound.")]
    [InlineData("the point a lies on the line", "the point eh lies on the line")]
    [InlineData("suppose a exceeds one", "suppose eh exceeds one")]
    [InlineData("Let a(t) be the drift", "Let eh(tee) be the drift")]
    [InlineData("x plus a(t) is bounded", "ex plus eh(tee) is bounded")]
    [InlineData("Let a′ be another point", "Let eh′ be another point")]
    [InlineData("then a + b is", "then eh + bee is")]
    [InlineData("the constant a controls the decay", "the constant eh controls the decay")]
    [InlineData("(A + B) is invertible", "(eh + bee) is invertible")]
    [InlineData("for the matrix (A) alone", "for the matrix (eh) alone")]
    public void TheSymbolA_IsToldFromTheArticle(string script, string speech) => Assert.Equal(speech, Speech(script));

    [Theory]
    [InlineData("This is a vector of returns.")]
    [InlineData("A model is a simplification of a process.")]
    [InlineData("It follows a t distribution", "It follows a tee distribution")]
    [InlineData("We take a sub-sample of the data.")]
    [InlineData("Therefore I conclude that a limit exists.")]
    [InlineData("See, e.g., Smith, i.e. the U.S. edition.")]
    [InlineData("It doesn't hold for the y's.")]
    [InlineData("returns averaged over a window of days")]
    [InlineData("given a sequence of observations")]
    [InlineData("plus a constant term")]
    [InlineData("Let a model be fitted.")]
    [InlineData("at some point a shock arrives")]
    [InlineData("(A similar bound holds.)")]
    [InlineData("That claim is a lie.")]
    [InlineData("This is a by now classic argument.")]
    [InlineData("It was a then unknown quantity.")]
    [InlineData("(see Fig. 2.) A similar bound holds.")]
    [InlineData("It is tight.” A counterexample follows.")]
    [InlineData("3.2 A Simple Model")]
    [InlineData("“A model is a simplification.”")]
    public void ProseIsLeftAsItIs(string script, string? speech = null) => Assert.Equal(speech ?? script, Speech(script));

    [Fact]
    public void ARangeOfTheEnginesText_MapsBackToTheScript()
    {
        // script: "w sub t is here"      speech: "double you sub tee is here"
        var form = LetterNames.ForSpeech("w sub t is here");
        Assert.Equal("double you sub tee is here", form.Text);

        Assert.Equal(new TextRange(0, 1), form.ToScript(new TextRange(0, 10)));    // "double you" -> "w"
        Assert.Equal(new TextRange(2, 3), form.ToScript(new TextRange(11, 3)));    // "sub"
        Assert.Equal(new TextRange(6, 1), form.ToScript(new TextRange(15, 3)));    // "tee" -> "t"
        Assert.Equal(new TextRange(8, 2), form.ToScript(new TextRange(19, 2)));    // "is"
        Assert.Equal(new TextRange(0, 15), form.ToScript(new TextRange(0, form.Text.Length)));
    }

    [Fact]
    public void ScriptWithNoLoneLetter_IsReturnedUnchanged()
    {
        var form = LetterNames.ForSpeech("The conditional variance rises with the shock.");

        Assert.Equal("The conditional variance rises with the shock.", form.Text);
        Assert.Empty(form.Changes);
        Assert.Equal(new TextRange(4, 11), form.ToScript(new TextRange(4, 11)));
    }

    // ---- notation set solid in prose ----------------------------------------------------

    private static string Printed(string text) => TextNormaliser.ExpandForSpeech(text).Text;

    [Theory]
    [InlineData("draws from the distribution Yt+h|Ft.", "draws from the distribution Y t plus h given F t.")]
    [InlineData("the variance (Var(Yt+h|Ft)) is", "the variance (Var(Y t plus h given F t)) is")]
    [InlineData("so that xt+1 depends on xt", "so that x t plus one depends on xt")]
    public void NotationSetSolid_IsReadPieceByPiece(string text, string spoken) => Assert.Equal(spoken, Printed(text));

    [Theory]
    [InlineData("written in C++ and/or Fortran")]
    [InlineData("posted on Google+ last year")]
    [InlineData("grade A+ work")]
    public void WordsWithASign_AreNotNotation(string text) => Assert.Equal(text, Printed(text));

    [Fact]
    public void EachPieceKeepsItsOwnSource()
    {
        var spoken = TextNormaliser.ExpandForSpeech("see Yt+h now");

        Assert.Equal("see Y t plus h now", spoken.Text);
        int plus = spoken.Text.IndexOf("plus", StringComparison.Ordinal);
        Assert.Equal([new TextRange(6, 1)], spoken.ToSourceRanges(new TextRange(plus, 4)));
        int h = spoken.Text.IndexOf(" h ", StringComparison.Ordinal) + 1;
        Assert.Equal([new TextRange(7, 1)], spoken.ToSourceRanges(new TextRange(h, 1)));
    }
}
