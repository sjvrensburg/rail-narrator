using System.Text;
using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// Where one word ends and the next begins, and which letters a glyph stands for.
/// </summary>
/// <remarks>
/// Each case reproduces geometry measured on a real document that the application read aloud wrongly:
/// a typeset journal article ("two hundred one seven" for 2017, "ofthe", "su ciently"), a page of
/// rotated text (no spaces at all) and an image-only scan ("B ac kground and m otivat ion"). The
/// numbers in the helpers are those documents' numbers, in points, not round ones.
/// </remarks>
public sealed class WordBoundaryTests
{
    // ---- a page laid out glyph by glyph ------------------------------------------------

    /// <summary>Ink box and side bearings of one glyph, in points.</summary>
    private readonly record struct Metrics(float LeftBearing, float Ink, float RightBearing);

    /// <summary>Body text at about 10 pt: letters 0.97 pt apart, x-height 5.2 pt.</summary>
    private static Metrics Body(char c) => c switch
    {
        // A tabular figure one: the same advance as the other digits, far less ink.
        '1' => new Metrics(1.485f, 1.8f, 1.485f),
        // An f whose terminal overhangs whatever follows it.
        'f' => new Metrics(0.485f, 3.6f, -1.6f),
        _ => new Metrics(0.485f, 4.0f, 0.485f),
    };

    private const float GlyphHeight = 5.2f;
    private const float WordSpace = 2.0f;

    private sealed class PageBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly List<ExtractedChar> _chars = [];
        private readonly List<ExtractedLine> _lines = [];
        private readonly int _pageIndex;

        public PageBuilder(int pageIndex = 0) => _pageIndex = pageIndex;

        /// <summary>
        /// Sets a line as a typesetter would: bearings between letters, a word space between words.
        /// </summary>
        /// <param name="textLayerSpaces">Whether the PDF's text layer carries a space character where the words part.</param>
        public PageBuilder Typeset(string text, float y, Func<char, Metrics>? metrics = null,
            bool textLayerSpaces = true, bool fromOcr = false)
        {
            metrics ??= Body;
            int start = _text.Length;
            float x = 72f, right = x;
            foreach (char c in text)
            {
                if (c == ' ')
                {
                    x += WordSpace;
                    if (textLayerSpaces) AppendNonMarking(' ');
                    continue;
                }
                var m = metrics(c);
                float left = x + m.LeftBearing;
                right = left + m.Ink;
                _chars.Add(new ExtractedChar(_text.Length, new PageRect(left, y, right, y + GlyphHeight), 0f));
                _text.Append(c);
                x = right + m.RightBearing;
            }
            EndLine(start, new PageRect(72f, y, right, y + GlyphHeight), fromOcr);
            return this;
        }

        /// <summary>Starts a line whose glyphs the test places itself.</summary>
        public LineWriter Line(bool fromOcr = false) => new(this, fromOcr);

        public sealed class LineWriter
        {
            private readonly PageBuilder _page;
            private readonly bool _fromOcr;
            private readonly int _start;
            private float _l = float.MaxValue, _t = float.MaxValue, _r = float.MinValue, _b = float.MinValue;

            public LineWriter(PageBuilder page, bool fromOcr) { _page = page; _fromOcr = fromOcr; _start = page._text.Length; }

            public LineWriter Glyph(char c, float left, float top, float right, float bottom)
            {
                _page._chars.Add(new ExtractedChar(_page._text.Length, new PageRect(left, top, right, bottom), 0f));
                _page._text.Append(c);
                _l = Math.Min(_l, left); _t = Math.Min(_t, top); _r = Math.Max(_r, right); _b = Math.Max(_b, bottom);
                return this;
            }

            /// <summary>A character the text layer holds that leaves no ink: a space, a line feed.</summary>
            public LineWriter NonMarking(char c) { _page.AppendNonMarking(c); return this; }

            public PageBuilder End() { _page.EndLine(_start, new PageRect(_l, _t, _r, _b), _fromOcr); return _page; }
        }

        private void AppendNonMarking(char c)
        {
            _chars.Add(new ExtractedChar(_text.Length, default, 0f));
            _text.Append(c);
        }

        private void EndLine(int start, PageRect rect, bool fromOcr)
        {
            _lines.Add(new ExtractedLine($"p{_pageIndex}-l{_lines.Count}", 0, TextRange.FromBounds(start, _text.Length), rect, fromOcr));
            AppendNonMarking('\n');
        }

        public PageExtraction Build()
        {
            var block = new ExtractedBlock($"p{_pageIndex}-b0", BlockKind.Text, 0, new PageRect(72f, 90f, 540f, 700f),
                Enumerable.Range(0, _lines.Count).ToList(), 0.99f, 0);
            var coverage = new TextCoverage(true, _lines.Any(l => l.FromOcr), _text.Length, 612 * 792,
                _chars.Sum(c => (double)c.Rect.Area), []);
            return new PageExtraction(_pageIndex, 612, 792, _text.ToString(), [block], _lines, _chars, coverage);
        }
    }

    private static ExtractionSnapshot Snapshot(params PageExtraction[] pages)
        => new(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree, pages);

    private static string Narrated(ExtractionSnapshot snapshot)
        => string.Join(" ", new ReadingUnitBuilder().Build(snapshot).Select(u => u.OriginalText));

    // ---- word spaces -------------------------------------------------------------------

    [Fact]
    public void TabularFigures_AreNotSplitAtTheWhiteAroundAOne()
    {
        // Measured: the gap beside a tabular 1 was 1.97 pt on a line whose letters sat 0.97 pt apart
        // and whose word spaces were 2.7 pt and up. Twice the usual gap reads as a space to any rule
        // that knows only gaps, and the year came out as "two hundred one seven".
        var page = new PageBuilder()
            .Typeset("To cite this article: Amado and Another (2017) in Reviews 36, pages 421 to 446.", 100f)
            .Typeset("ISSN: 0747-4938 (Print) 1532-4168 (Online) published online in November 2015.", 112f)
            .Build();

        string text = Narrated(Snapshot(page));

        Assert.Contains("(2017)", text);
        Assert.Contains("1532-4168", text);
        Assert.Contains("421 to 446", text);
        Assert.Contains("2015", text);
        Assert.DoesNotContain("201 ", text);
    }

    [Fact]
    public void ASpaceHiddenUnderAnOverhang_IsStillASpace()
    {
        // An f reaches over the space that follows it, leaving about 1 pt of white where other word
        // spaces leave 3 pt. Gaps alone joined "of the" into "ofthe" 38 times in one paper.
        var page = new PageBuilder()
            .Typeset("To allow an asymmetric response of volatility to positive shocks, the", 100f)
            .Typeset("variance of the returns is a function of time and of the lagged values.", 112f)
            .Build();

        string text = Narrated(Snapshot(page));

        Assert.Contains("response of volatility", text);
        Assert.Contains("variance of the returns", text);
        Assert.Contains("function of time and of the lagged", text);
    }

    [Fact]
    public void ATextLayerWithWhitespaceInsideWords_IsNotBelieved()
    {
        // ADR-0005: one producer wrote " \n" between the letters of a word. The spaces a text layer
        // offers are only taken where the glyphs actually part; here they do not, so the gaps decide.
        var writer = new PageBuilder().Line();
        float x = 72f;
        foreach (string word in "Alpha one in the left column of this page.".Split(' '))
        {
            for (int i = 0; i < word.Length; i++)
            {
                writer.Glyph(word[i], x + 0.485f, 100f, x + 4.485f, 100f + GlyphHeight);
                x += 4.97f;
                if (i < word.Length - 1) writer.NonMarking(' ').NonMarking('\n');   // inside the word
            }
            x += WordSpace;   // between words: more white, and no character for it at all
        }
        var page = writer.End().Build();

        Assert.Equal("Alpha one in the left column of this page.", Narrated(Snapshot(page)));
    }

    [Fact]
    public void RotatedText_KeepsItsWordSpaces_IncludingAcrossAWrap()
    {
        // A page set sideways with no Rotate attribute reads bottom to top, and the analyzer returns
        // its lines as one. Gaps measured along x are all negative there; the whole page came out
        // as "Thequickbrownfoxjumpsoverthelazydog".
        var writer = new PageBuilder().Line();
        float column = 36f;
        foreach (string line in new[] { "The quick brown fox jumps over", "the lazy dog and runs away." })
        {
            float y = 680f;
            foreach (char c in line)
            {
                if (c == ' ') { y -= WordSpace; writer.NonMarking(' '); continue; }
                // Glyph boxes of rotated text: narrow along the reading direction, x-height across it.
                float bottom = y - 0.485f, top = bottom - 4.0f;
                writer.Glyph(c, column, top, column + GlyphHeight, bottom);
                y = top - 0.485f;
            }
            writer.NonMarking('\r').NonMarking('\n');
            column += 14f;
        }
        var page = writer.End().Build();

        Assert.Equal("The quick brown fox jumps over the lazy dog and runs away.", Narrated(Snapshot(page)));
    }

    [Fact]
    public void RecognisedText_KeepsTheRecognisersSpaces()
    {
        // OCR character boxes are estimates: measured on a scan, "Introduction" had gaps of 4, 7 and
        // 9 pt between its own letters. The recogniser's text has the spaces; its boxes do not.
        var writer = new PageBuilder().Line(fromOcr: true);
        const string text = "1 Introduction and Background";
        float[] lefts = [95.6f, 0, 121.3f, 128.9f, 144.6f, 154.8f, 163.6f, 180.4f, 195.2f, 209.5f, 223.9f, 234.1f, 240.0f, 255.6f,
                         0, 270.0f, 281.0f, 295.5f, 0, 310.0f, 322.0f, 331.5f, 347.0f, 352.2f, 368.0f, 377.1f, 390.0f, 401.3f, 418.0f];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ') { writer.NonMarking(' '); continue; }
            writer.Glyph(text[i], lefts[i], 174f, lefts[i] + 2.8f, 191f);
        }
        var page = writer.End().Build();

        Assert.Equal("1 Introduction and Background", Narrated(Snapshot(page)));
    }

    // ---- glyphs without a Unicode value ------------------------------------------------

    private const char Fi = '\u0002', Ff = '\u0000', Ffi = '\u0008';

    [Fact]
    public void LigaturesTheTextLayerLeftUnmapped_AreRestoredFromTheDocumentItself()
    {
        // The measured paper's fonts encode fi, ff and ffi at 2, 0 and 8 under glyph names PDFium
        // does not decompose. Each used to be dropped, and the hole it left read as a space.
        var page = new PageBuilder()
            .Typeset($"The speci{Fi}cation is tested {Fi}rst, and the e{Ff}ect is signi{Fi}cant for co{Ff}ee.", 100f)
            .Typeset($"A di{Ff}erent model is {Fi}tted when the series are su{Ffi}ciently long, which is e{Ffi}cient.", 112f)
            .Typeset($"Tests of misspeci{Fi}cation use the robusti{Fi}ed statistic and its coe{Ffi}cients.", 124f)
            .Build();

        string text = Narrated(Snapshot(page));

        Assert.Contains("specification is tested first", text);
        Assert.Contains("effect is significant for coffee", text);
        Assert.Contains("different model is fitted", text);
        Assert.Contains("sufficiently long, which is efficient", text);
        // Words no list holds are still repaired, because the glyph was identified, not the word.
        Assert.Contains("misspecification use the robustified statistic", text);
        Assert.Contains("coefficients", text);
    }

    [Fact]
    public void AnUnidentifiedGlyph_IsNeverTurnedIntoLetters()
    {
        // Symbol fonts leak control characters the same way. One standing alone, or one whose
        // surroundings spell nothing, stays unspoken and still separates its neighbours.
        var page = new PageBuilder()
            .Typeset("The innovation \u0012 has mean zero and the level \u0013 is fixed for every value here.", 100f)
            .Typeset("We write alpha\u0014beta for the product over all of the pairs in the sample.", 112f)
            .Build();

        string text = Narrated(Snapshot(page));

        Assert.Contains("innovation has mean zero", text);
        Assert.Contains("alpha beta", text);
        Assert.DoesNotContain("fi", text.Replace("fixed", ""));
    }

    [Fact]
    public void ALineEndHyphenMarker_IsAHyphen_NotTheLigatureThatSharesItsCode()
    {
        // PDFium reports a line-ending hyphen as U+0002 — the very code this paper's fonts give to
        // "fi". In the middle of a word it is the ligature; at the end of a line it is the hyphen.
        var page = new PageBuilder()
            .Typeset($"The speci{Fi}cation is tested {Fi}rst and the vari{Fi}", 100f)
            .Typeset($"ance is signi{Fi}cant in each of the {Fi}ve cases we study here.", 112f)
            .Build();

        var units = new ReadingUnitBuilder().Build(Snapshot(page));
        string original = string.Join(" ", units.Select(u => u.OriginalText));
        var unit = units.Single(u => u.OriginalText.Contains("vari-"));
        string spoken = TextNormaliser.ForSpeech(unit.OriginalText, unit.LineBreakOffsets.ToHashSet()).Text;

        Assert.DoesNotContain("varifi", original);
        Assert.Contains("the variance is significant", spoken);
        Assert.Contains("five cases", spoken);
    }

    [Fact]
    public void AGlyphIsIdentifiedOnlyByOccurrencesThatFitOneLigature()
    {
        // "a?er" and "o?en" fit both ft ("after", "often") and ff ("affer", "offen": a big word list
        // has both). Counted as half a vote each they left ft short of a majority; "shi?s" and
        // "therea?er" fit nothing else.
        const char code = '\u000f';
        var tokens = Enumerable.Repeat($"a{code}er", 12).Concat(Enumerable.Repeat($"o{code}en", 6))
            .Concat([$"shi{code}s", $"therea{code}er", $"so{code}ware"]);

        Assert.Equal("ft", UnmappedGlyphResolver.Infer(tokens)[code]);
        // Ambiguous evidence alone settles nothing, however much of it there is.
        Assert.Empty(UnmappedGlyphResolver.Infer(Enumerable.Repeat($"a{code}er", 40)));
        // Nor does one occurrence.
        Assert.Empty(UnmappedGlyphResolver.Infer([$"shi{code}s"]));
    }

    // ---- running heads -----------------------------------------------------------------

    [Fact]
    public void AlternatingRunningHeadsAndOffsetPageNumbers_AreNotNarrated()
    {
        // A journal article: author on left-hand pages, journal on right-hand ones, numbered from
        // 421. Each head is on fewer than half the pages and no number equals its page index, so a
        // half-the-pages rule and a number-equals-index rule caught neither.
        var pages = new List<PageExtraction>();
        for (int p = 0; p < 10; p++)
        {
            pages.Add(SyntheticNarration.Page(p,
            [
                new SyntheticNarration.LineSpec(p % 2 == 0 ? "C. AMADO AND T. TERASVIRTA" : "ECONOMETRIC REVIEWS", 28f),
                new SyntheticNarration.LineSpec((421 + p).ToString(), 40f, X: 500f),
                new SyntheticNarration.LineSpec($"Body sentence number {(char)('A' + p)} continues the argument on this page.", 300f),
            ]));
        }

        var units = new ReadingUnitBuilder().Build(Snapshot([.. pages]), out var diagnostics);
        string text = string.Join(" ", units.Select(u => u.OriginalText));

        Assert.DoesNotContain("AMADO", text);
        Assert.DoesNotContain("ECONOMETRIC", text);
        Assert.DoesNotContain("42", text);
        for (int p = 0; p < 10; p++) Assert.Contains($"number {(char)('A' + p)} continues", text);
        Assert.Contains(diagnostics, d => d.StartsWith("Suppressed 20 ", StringComparison.Ordinal));
    }
}
