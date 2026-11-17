using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>Sentence boundaries around numbers and decimals.</summary>
public sealed class SentenceSplitterTests
{
    private static List<string> Sentences(string text)
        => SentenceSplitter.Split(text, minChars: 1).Select(r => text.Substring(r.Start, r.Length).Trim()).ToList();

    [Fact]
    public void SentenceEndingInANumber_SplitsBeforeTheNextSentence()
    {
        var sentences = Sentences("The measured value was 5. Next sentence follows here now.");
        Assert.Equal(["The measured value was 5.", "Next sentence follows here now."], sentences);
    }

    [Fact]
    public void SentenceEndingInAYear_SplitsBeforeTheNextSentence()
    {
        var sentences = Sentences("The study was published in 2019. It changed the field completely.");
        Assert.Equal(2, sentences.Count);
        Assert.Equal("The study was published in 2019.", sentences[0]);
    }

    [Fact]
    public void DecimalPoint_DoesNotSplit()
    {
        var sentences = Sentences("Add 0.5 ml of reagent to the sample. Then stir the mixture gently.");
        Assert.Equal(2, sentences.Count);
        Assert.Equal("Add 0.5 ml of reagent to the sample.", sentences[0]);
    }

    [Fact]
    public void DecimalFinalSentence_StillSplitsBeforeACapital()
    {
        var sentences = Sentences("The ratio came out at 0.5. That was the expected result overall.");
        Assert.Equal(["The ratio came out at 0.5.", "That was the expected result overall."], sentences);
    }
}
