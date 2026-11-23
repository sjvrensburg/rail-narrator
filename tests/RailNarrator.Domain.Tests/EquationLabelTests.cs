using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Which parenthesised numbers among an equation's characters are its label: the ones standing
/// apart at its edge.
/// </summary>
public sealed class EquationLabelTests
{
    /// <summary>Sets text from <paramref name="x"/>, 5 pt a character, on the line at <paramref name="y"/>.</summary>
    private static List<EquationLabel.Glyph> Line(string text, float x, float y = 100f, int page = 0)
    {
        var glyphs = new List<EquationLabel.Glyph>();
        foreach (char c in text)
        {
            glyphs.Add(new EquationLabel.Glyph(c, c == ' ' ? default : new PageRect(x, y, x + 4f, y + 8f), page));
            x += 5f;
        }
        return glyphs;
    }

    private static string Labels(List<EquationLabel.Glyph> glyphs)
        => new(EquationLabel.Find(glyphs).Order().Select(i => glyphs[i].Character).ToArray());

    [Theory]
    [InlineData("(1)")]
    [InlineData("(2.1)")]
    [InlineData("(A.3)")]
    [InlineData("(12a)")]
    [InlineData("(3.2.1)")]
    [InlineData("ð13Þ")]
    public void ALabelSetApartOnTheRight_IsFound(string label)
    {
        List<EquationLabel.Glyph> glyphs = [.. Line("x = y + 2,", 100), .. Line(label, 400)];

        Assert.Equal(label, Labels(glyphs));
    }

    [Fact]
    public void ALabelOnTheLeft_IsFound()
    {
        List<EquationLabel.Glyph> glyphs = [.. Line("(4)", 72), .. Line("x = y + 2", 200)];

        Assert.Equal("(4)", Labels(glyphs));
    }

    [Fact]
    public void ALabelInTheMiddleOfTheText_IsFoundByWhereItStands()
    {
        // The text layer's order puts the label after the line it is level with, not at the end.
        List<EquationLabel.Glyph> glyphs =
        [
            .. Line("a = b,", 100, 100), .. Line("c = d - e", 100, 115), .. Line("(2.2)", 400, 115), .. Line("- f + g,", 120, 130),
        ];

        Assert.Equal("(2.2)", Labels(glyphs));
    }

    [Fact]
    public void EveryNumberedLine_LosesItsLabel()
    {
        List<EquationLabel.Glyph> glyphs =
        [
            .. Line("a = b,", 100, 100), .. Line("(1)", 400, 100), .. Line("c = d.", 100, 115), .. Line("(2)", 400, 115),
        ];

        Assert.Equal("(1)(2)", Labels(glyphs));
    }

    [Fact]
    public void AnArgumentInParentheses_IsNotALabel()
    {
        // "g(2)" ends the equation and reaches its right edge, but touches its function.
        Assert.Equal("", Labels(Line("x = f(1) + g(2)", 100)));
    }

    [Fact]
    public void ANumberWithInkToItsRight_IsNotALabel()
    {
        List<EquationLabel.Glyph> glyphs = [.. Line("x =", 100), .. Line("(2)", 200), .. Line("+ y", 300)];

        Assert.Equal("", Labels(glyphs));
    }

    [Fact]
    public void ALabelAlone_IsLeftAlone()
    {
        Assert.Equal("", Labels(Line("(2.1)", 400)));
    }

    [Fact]
    public void ALabelOnAnotherPage_IsJudgedAgainstItsOwnPage()
    {
        // An equation continued overleaf: the label is level with nothing on the first page.
        List<EquationLabel.Glyph> glyphs =
        [
            .. Line("a = b + c + d + e + f + g + h + i + j + k + l + m + n + o + p + q + r + s + t + u + v + w + x + y + z", 100, 700, page: 0),
            .. Line("+ c.", 100, 80, page: 1), .. Line("(7)", 400, 80, page: 1),
        ];

        Assert.Equal("(7)", Labels(glyphs));
    }
}
