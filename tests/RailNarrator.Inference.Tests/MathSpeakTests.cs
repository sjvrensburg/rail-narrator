using RailNarrator.Inference;
using Xunit;

namespace RailNarrator.Inference.Tests;

/// <summary>
/// The rule-based reader on the constructs of a real paper (the GARCH paper's equations as a model
/// transcribes them): grouping must survive, and the phrasing follows ClearSpeak.
/// </summary>
public sealed class MathSpeakTests
{
    [Theory]
    [InlineData(@"\frac{1}{2}", "one half")]
    [InlineData(@"\frac{2}{3}", "two thirds")]
    [InlineData(@"\frac{3}{4}", "three quarters")]
    [InlineData(@"z^{n}", "z to the n-th power")]
    [InlineData(@"x^{4}", "x to the fourth power")]
    [InlineData(@"2 \times 10^{-4}", "two times ten to the negative fourth power")]
    [InlineData(@"g_t^{1/2}", "g sub t to the one half power")]
    [InlineData(@"s_{11}", "s sub one one")]
    [InlineData(@"\varepsilon_{t-i}^{*2}", "epsilon sub t minus i, star squared")]
    [InlineData(@"\hat{x}_{jt}", "x hat sub j t")]
    [InlineData(@"\vec{v}", "vector v")]
    [InlineData(@"g_t \equiv 1", "g sub t is identically equal to one")]
    [InlineData(@"\sqrt{x + 1}", "the square root of x plus one, end root")]
    [InlineData(@"\sqrt[3]{x}", "the cube root of x")]
    [InlineData(@"|x - y|", "the absolute value of x minus y")]
    [InlineData(@"\frac{a + b}{c}", "the fraction with numerator a plus b, and denominator c")]
    [InlineData(@"-x", "negative x")]
    [InlineData(@"[0, 1)", "the interval from zero to one")]
    [InlineData(@"\theta_j \text{ be the true value of } x", "theta sub j be the true value of x")]
    [InlineData(@"\pmb{\theta}", "theta")]
    public void ReadsLikeClearSpeak(string latex, string expected)
        => Assert.Equal(expected, LocalLatexSpeaker.Speak(latex));

    [Fact]
    public void ASumKeepsItsRangeAndTerms()
        => Assert.Equal(
            "h sub t equals alpha sub zero plus the sum from i equals one to q of alpha sub i epsilon sub t minus i, star squared " +
            "plus the sum from j equals one to p of beta sub j h sub t minus j",
            LocalLatexSpeaker.Speak(@"h_t = \alpha_0 + \sum_{i=1}^{q} \alpha_i \varepsilon_{t-i}^{*2} + \sum_{j=1}^{p} \beta_j h_{t-j}"));

    [Fact]
    public void ABracketedQuantityRaisedToAPowerIsMarked()
        => Assert.Equal("epsilon sub t equals zeta sub t the quantity h sub t g sub t, end quantity, to the one half power",
            LocalLatexSpeaker.Speak(@"\varepsilon_t = \zeta_t (h_t g_t)^{1/2}"));

    [Fact]
    public void ABracketedSumMultipliedIsAQuantity()
        => Assert.Contains("the square root of T the quantity theta hat minus theta sub zero, end quantity, converges in distribution to N of zero, capital Sigma",
            LocalLatexSpeaker.Speak(@"\sqrt{T}(\hat\theta - \theta_0) \xrightarrow{d} N(0, \Sigma)"), StringComparison.Ordinal);

    [Theory]
    [InlineData(@"\frac{\partial \ell(\theta, \varepsilon_t)}{\partial \theta_1}", "the partial derivative of ell of theta, epsilon sub t with respect to theta sub one")]
    [InlineData(@"\frac{dy}{dx}", "the derivative of y with respect to x")]
    [InlineData(@"\frac{\partial^2 f}{\partial x^2}", "the second partial derivative of f with respect to x")]
    [InlineData(@"\frac{\partial}{\partial x} f", "the partial derivative with respect to x of f")]
    public void DerivativesAreNamed(string latex, string expected)
        => Assert.Equal(expected, LocalLatexSpeaker.Speak(latex));

    [Fact]
    public void AnEvaluationBarIsSpoken()
        => Assert.EndsWith("the partial derivative of h sub t with respect to theta sub j, evaluated at H sub zero",
            LocalLatexSpeaker.Speak(@"\hat{x}_{jt} = \hat{h}_t^{-1} \left. \frac{\partial h_t}{\partial \theta_j} \right|_{H_0}"), StringComparison.Ordinal);

    [Fact]
    public void FunctionsTakeTheirArgumentWithoutBrackets()
    {
        Assert.Equal("G of s sub t; gamma, c", LocalLatexSpeaker.Speak(@"G(s_t; \gamma, c)"));
        Assert.Equal("the exponential of negative x", LocalLatexSpeaker.Speak(@"\exp(-x)"));
        Assert.Equal("the variance of X", LocalLatexSpeaker.Speak(@"\operatorname{Var}(X)"));
        Assert.Equal("the expectation of X given Y", LocalLatexSpeaker.Speak(@"\mathbb{E}[X \mid Y]"));
    }

    [Fact]
    public void CasesAreReadRowByRow()
        => Assert.Equal("I sub t equals cases: one, if epsilon sub t is less than zero; zero, otherwise",
            LocalLatexSpeaker.Speak(@"I_t = \begin{cases} 1 & \varepsilon_t < 0 \\ 0 & \text{otherwise} \end{cases}"));

    [Fact]
    public void UnicodeFromAModelIsReadToo()
        => Assert.Equal("alpha plus beta is less than one", LocalLatexSpeaker.Speak("α + β < 1"));

    [Theory]
    [InlineData("The weight $\\alpha_1$ is positive.", "The weight alpha sub one is positive.")]
    [InlineData("Sample $2^m$ tokens, where $m \\ge 1$.", "Sample two to the m-th power tokens, where m is greater than or equal to one.")]
    [InlineData("No maths here.", "No maths here.")]
    [InlineData("and $f_t = 0$ if $\\theta_3 = 0$.\\end{equation}", "and f sub t equals zero if theta sub three equals zero.")]
    public void TextWithMathsIsReadWithTheMathsInWords(string text, string expected)
        => Assert.Equal(expected, LocalLatexSpeaker.SpeakMixed(text));

    [Fact]
    public void AnUnclosedDelimiterIsNotHalfRead()
        => Assert.Null(LocalLatexSpeaker.SpeakMixed("The weight $\\alpha_1 is positive."));

    [Theory]
    [InlineData(@"\frac{1}{")]
    [InlineData(@"x^")]
    [InlineData(@"\left( x")]
    [InlineData(@"\begin{cases} 1 & x")]
    public void MalformedInputStillGivesWords(string latex)
    {
        string spoken = LocalLatexSpeaker.Speak(latex);
        Assert.DoesNotContain("\\", spoken, StringComparison.Ordinal);
    }
}
