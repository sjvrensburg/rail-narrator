using System.Text;
using RailNarrator.Domain;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// What the reading units look like once a layout model has labelled the blocks: formulas, headings
/// and tables taken from the model's roles, and sentences with maths in them kept whole.
/// </summary>
/// <remarks>
/// Each case reproduces what the 27-page GARCH article produced with PP-DocLayoutV3 before the fix:
/// 232 "display equations" (prose lines promoted by the geometric fallback), summation limits read
/// as fragments, a heading running into its paragraph, inline maths cut out of sentences ("where",
/// "ε ∗", "t") and table cells read as sentences of numbers.
/// </remarks>
public sealed class LayoutRoleTests
{
    private sealed class LayoutPage
    {
        private readonly StringBuilder _text = new();
        private readonly List<ExtractedChar> _chars = [];
        private readonly List<ExtractedLine> _lines = [];
        private readonly List<ExtractedBlock> _blocks = [];
        private float _y = 100f;
        private readonly int _pageIndex;

        public LayoutPage(int pageIndex = 0) => _pageIndex = pageIndex;

        /// <summary>Adds a block of <paramref name="kind"/> whose lines are set one under another.</summary>
        public LayoutPage Block(BlockKind kind, params string[] lines)
        {
            int blockIndex = _blocks.Count;
            var indices = new List<int>();
            float top = _y;
            foreach (string line in lines)
            {
                int start = _text.Length;
                float x = 72f;
                foreach (char c in line)
                {
                    if (c == ' ') { Append(' ', default); x += 2.5f; continue; }
                    Append(c, new PageRect(x, _y, x + 4f, _y + 5.2f));
                    x += 4.97f;
                }
                indices.Add(_lines.Count);
                _lines.Add(new ExtractedLine($"p{_pageIndex}l{_lines.Count}", blockIndex, TextRange.FromBounds(start, _text.Length),
                    new PageRect(72f, _y, x, _y + 5.2f), false));
                Append('\n', default);
                _y += 12f;
            }
            _blocks.Add(new ExtractedBlock($"p{_pageIndex}b{blockIndex}", kind, blockIndex, new PageRect(72f, top, 540f, _y), indices, 0.9f, 0));
            _y += 6f;
            return this;
        }

        /// <summary>Sets the next block's top, in page points (the page is 792 high).</summary>
        public LayoutPage At(float y)
        {
            _y = y;
            return this;
        }

        private void Append(char c, PageRect rect)
        {
            _chars.Add(new ExtractedChar(_text.Length, rect, 0f));
            _text.Append(c);
        }

        public PageExtraction Page()
        {
            var coverage = new TextCoverage(true, false, _text.Length, 612 * 792, _chars.Sum(c => (double)c.Rect.Area), []);
            return new PageExtraction(_pageIndex, 612, 792, _text.ToString(), _blocks, _lines, _chars, coverage);
        }

        public ExtractionSnapshot Snapshot(params LayoutPage[] more)
            => new(new ExtractionIdentity("feedface", 1), CoordinateFrameId.DisplayedPagePoints(),
                new ExtractionOptions("pp-doclayout-v3", OcrPolicy.Off, 800, false, ExtractorVersions.Current),
                [Page(), .. more.Select(p => p.Page())]);
    }

    private static IReadOnlyList<ReadingUnit> Units(ExtractionSnapshot snapshot) => new ReadingUnitBuilder().Build(snapshot);

    // ---- reference lists under a heading (models without a reference class: Docling Heron) ----

    /// <summary>
    /// Heron labels a bibliography as text and list items. Under a "References" heading, up to the
    /// next heading, it is the reference list, which narration skips by default; the appendix after
    /// it is read. Across a page break, in reading order.
    /// </summary>
    [Fact]
    public void WithoutAReferenceClass_TheListUnderAReferencesHeadingIsNotRead()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Heading, "1. Introduction")
            .Block(BlockKind.Text, "The introduction is read aloud as usual.")
            .Block(BlockKind.Heading, "References")
            .Block(BlockKind.Text, "[1] A. Author. A first title. Journal of Things, 2020.")
            .Block(BlockKind.Text, "[2] B. Author. A second title. Journal of Things, 2021.")
            .Snapshot(new LayoutPage(1)
                .Block(BlockKind.Text, "[3] C. Author. A third title. Journal of Things, 2022.")
                .Block(BlockKind.Heading, "Appendix A. Proofs")
                .Block(BlockKind.Text, "The appendix is read aloud again."));

        var text = string.Join(" | ", Units(snapshot).Select(u => u.OriginalText));

        Assert.Contains("The introduction is read aloud as usual.", text);
        Assert.Contains("The appendix is read aloud again.", text);
        Assert.DoesNotContain("Author", text);
        // Its heading goes with it: "References" announced a section of which nothing followed.
        Assert.DoesNotContain("References", text);
        Assert.Contains("Appendix A. Proofs", text);
    }

    private static string Read(ExtractionSnapshot snapshot, NarrationFilterSettings? filters = null)
        => string.Join(" | ", new ReadingUnitBuilder(new ReadingUnitBuilderOptions(filters ?? NarrationFilterSettings.Default))
            .Build(snapshot).Select(u => u.OriginalText));

    /// <summary>
    /// With a model that labels references itself, the heading is judged by what its section holds,
    /// not by its words, so a heading in another language goes too. Footnotes and page furniture in
    /// the section are not read either and do not keep it.
    /// </summary>
    [Theory]
    [InlineData("References")]
    [InlineData("Literatur")]
    public void TheHeadingOfAReferenceListThatIsNotRead_IsNotReadEither(string heading)
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Heading, "5. Conclusion")
            .Block(BlockKind.Text, "The conclusion is read aloud as usual.")
            .Block(BlockKind.Heading, heading)
            .Block(BlockKind.Reference, "[1] A. Author. A first title. Journal of Things, 2020.")
            .Block(BlockKind.Footnote, "1 A footnote that floated onto the page.")
            .Snapshot(new LayoutPage(1)
                .Block(BlockKind.Reference, "[2] B. Author. A second title. Journal of Things, 2021."));

        string text = Read(snapshot);

        Assert.Contains("The conclusion is read aloud as usual.", text);
        Assert.Contains("5. Conclusion", text);
        Assert.DoesNotContain(heading, text);
        Assert.DoesNotContain("Author", text);
    }

    /// <summary>A reader who asks for the bibliography hears its heading as well.</summary>
    [Fact]
    public void WhenReferencesAreRead_SoIsTheirHeading()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Heading, "References")
            .Block(BlockKind.Reference, "[1] A. Author. A first title. Journal of Things, 2020.")
            .Snapshot();

        string text = Read(snapshot, NarrationFilterSettings.Default with { IncludeReferences = true });

        Assert.Contains("References", text);
        Assert.Contains("A first title", text);
    }

    /// <summary>
    /// Only the heading of a reference list goes. A heading straight before another, one above a
    /// figure, and one above prose are read; so is the heading after the list.
    /// </summary>
    [Fact]
    public void OtherHeadings_AreStillRead()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Heading, "2. Methods")
            .Block(BlockKind.Heading, "2.1 Data")
            .Block(BlockKind.Text, "The data section is read aloud as usual.")
            .Block(BlockKind.Heading, "3. Figures")
            .Block(BlockKind.Figure, "a figure")
            .Block(BlockKind.Heading, "References")
            .Block(BlockKind.Reference, "[1] A. Author. A first title. Journal of Things, 2020.")
            .Block(BlockKind.Heading, "Appendix")
            .Block(BlockKind.Text, "The appendix is read aloud as usual.")
            .Snapshot();

        string text = Read(snapshot);

        Assert.Contains("2. Methods", text);
        Assert.Contains("2.1 Data", text);
        Assert.Contains("3. Figures", text);
        Assert.Contains("Appendix", text);
        Assert.DoesNotContain("References", text);
        Assert.DoesNotContain("Author", text);
    }

    /// <summary>
    /// The page this was found on: PP-DocLayoutV3 gave the acknowledgement, the reference list and
    /// the funding note in order and then their three headings, so by reading order "References"
    /// has nothing under it and "Funding" is followed by the next page's references. On the page
    /// each heading sits above its own block, and that decides.
    /// </summary>
    [Fact]
    public void AReferenceHeading_IsFoundByWhatLiesBelowIt_NotByTheModelsReadingOrder()
    {
        var laidOut = new LayoutPage()
            .Block(BlockKind.Heading, "Acknowledgments")
            .Block(BlockKind.Text, "An earlier version was presented at a workshop.")
            .Block(BlockKind.Heading, "Funding")
            .Block(BlockKind.Text, "This work has been supported by a foundation.")
            .Block(BlockKind.Heading, "References")
            .Block(BlockKind.Reference, "Author, A. (2011). A first title. Journal of Things.")
            .Snapshot(new LayoutPage(1)
                .Block(BlockKind.Reference, "Author, B. (2012). A second title. Journal of Things."));
        // The model's order: the three bodies first, then the three headings.
        int[] order = [3, 0, 5, 1, 4, 2];
        var first = laidOut.Pages[0];
        var snapshot = laidOut with
        {
            Pages = [first with { Blocks = first.Blocks.Select((b, i) => b with { Order = order[i] }).ToArray() }, laidOut.Pages[1]],
        };

        string text = Read(snapshot);

        Assert.Contains("Acknowledgments", text);
        Assert.Contains("Funding", text);
        Assert.Contains("This work has been supported by a foundation.", text);
        Assert.DoesNotContain("References", text);
        Assert.DoesNotContain("Author", text);
    }

    /// <summary>A heading that ends its page is judged by the block that follows in reading order.</summary>
    [Fact]
    public void AReferenceHeadingAtTheFootOfAPage_IsNotReadEither()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Text, "The conclusion is read aloud as usual.")
            .Block(BlockKind.Heading, "References")
            .Block(BlockKind.PageNumber, "12")
            .Snapshot(new LayoutPage(1)
                .Block(BlockKind.Header, "A RUNNING HEAD")
                .Block(BlockKind.Reference, "Author, A. (2011). A first title. Journal of Things."));

        string text = Read(snapshot);

        Assert.Contains("The conclusion is read aloud as usual.", text);
        Assert.DoesNotContain("References", text);
    }

    /// <summary>A model that labels references itself is trusted, as with every other role.</summary>
    [Fact]
    public void AModelThatLabelsReferences_IsTrusted()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Heading, "References")
            .Block(BlockKind.Text, "Text the model chose to call prose stays prose.")
            .Block(BlockKind.Reference, "[1] A. Author. A title. 2020.")
            .Snapshot();

        Assert.Same(snapshot, ReferenceSections.Apply(snapshot, out int relabelled));
        Assert.Equal(0, relabelled);
    }

    [Fact]
    public void WithoutModelRoles_NothingIsRelabelled()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Text, "References")
            .Block(BlockKind.Text, "[1] A. Author. A title. 2020.")
            .Snapshot();

        Assert.Same(snapshot, ReferenceSections.Apply(snapshot, out _));
    }

    [Theory]
    [InlineData("References", true)]
    [InlineData("REFERENCES", true)]
    [InlineData("7. References", true)]
    [InlineData("VII References", true)]
    [InlineData("Bibliography:", true)]
    [InlineData("Works Cited", true)]
    [InlineData("References and Notes", true)]
    [InlineData("References to earlier work", false)]
    [InlineData("Related work", false)]
    public void AReferencesHeadingIsRecognisedByItsWholeText(string heading, bool expected)
        => Assert.Equal(expected, ReferenceSections.IsReferencesHeading(heading));

    [Fact]
    public void WithAModel_AProseLineCarryingSymbolsIsNotPromotedToAnEquation()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Heading, "2. The time-varying GARCH model")
            .Block(BlockKind.Text,
                "The GJR-GARCH(p, q) model is nested in (2) when g = 1. The unconditional",
                "variance component g is smooth and time-varying, introducing nonstationarity.")
            .Snapshot();

        var units = Units(snapshot);

        Assert.DoesNotContain(units, u => u.Kind == ReadingUnitKind.DisplayMath);
        Assert.Contains(units, u => u.OriginalText.StartsWith("The GJR-GARCH", StringComparison.Ordinal) && u.ContainsMaths);
    }

    /// <summary>
    /// Heron has no class for an equation number, so the number arrives inside the equation's
    /// block. It stands apart at the block's edge and is not part of the equation: the unit's text
    /// and its highlighted region (which a formula model's crop is cut from) leave it out.
    /// </summary>
    [Fact]
    public void AnEquationsNumberInsideItsBlock_IsNotPartOfTheEquation()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Text, "The first is written as")
            .Block(BlockKind.DisplayMath, "g = d + G(t; y, c),", "h = f(2) + k,                    (2.1)", "m = n.")
            .Block(BlockKind.Text, "where the parameters are restricted as below.")
            .Snapshot();

        var equation = Assert.Single(Units(snapshot), u => u.Kind == ReadingUnitKind.DisplayMath);

        Assert.DoesNotContain("2.1", equation.OriginalText);
        Assert.Contains("f(2)", equation.OriginalText);
        Assert.EndsWith("m=n.", equation.OriginalText);
        // The label began 156 pt further right than the rest of its line ends.
        float right = equation.Anchors.SelectMany(a => a.Rects).Max(r => r.Right);
        Assert.InRange(right, 150f, 175f);
    }

    [Fact]
    public void TheLinesOfOneDisplayBlockAreOneEquation()
    {
        // A summation's limits sit on lines of their own above and below it.
        var snapshot = new LayoutPage()
            .Block(BlockKind.Text, "Its unconditional variance is a combination of bounded transition functions defined as")
            .Block(BlockKind.DisplayMath, "r", "g = d + X d G(t; y, c),", "l=1")
            .Block(BlockKind.Text, "where the parameters are restricted as below.")
            .Snapshot();

        var maths = Units(snapshot).Where(u => u.Kind == ReadingUnitKind.DisplayMath).ToList();

        var equation = Assert.Single(maths);
        Assert.Contains("r", equation.OriginalText);
        Assert.Contains("l=1", equation.OriginalText);
    }

    [Fact]
    public void AHeadingDoesNotRunIntoTheParagraphAfterIt()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Heading, "2. The time-varying GARCH model")
            .Block(BlockKind.Text, "In this article the tool for modelling daily return series is the model of two authors.")
            .Snapshot();

        var units = Units(snapshot);

        Assert.Equal(ReadingUnitKind.Heading, units[0].Kind);
        Assert.Equal("2. The time-varying GARCH model", units[0].OriginalText);
        Assert.Equal(ReadingUnitKind.Prose, units[1].Kind);
        Assert.StartsWith("In this article", units[1].OriginalText, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineMathsStaysInsideItsSentence()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Text,
                "where e = s/g and I(A) is the indicator variable, defined as I(A) = 1 when A is true,",
                "and zero otherwise. The innovation sequence has a conditional mean of zero.")
            .Snapshot();

        var units = Units(snapshot);

        Assert.Equal(2, units.Count);
        Assert.All(units, u => Assert.Equal(ReadingUnitKind.Prose, u.Kind));
        Assert.True(units[0].ContainsMaths);
        Assert.EndsWith("and zero otherwise.", units[0].OriginalText, StringComparison.Ordinal);
        Assert.False(units[1].ContainsMaths);
    }

    /// <summary>
    /// pdfTeX sets maths italics as Mathematical Alphanumeric Symbols (U+1D400–U+1D7FF, two UTF-16
    /// units each) and h as the letterlike U+210E. A sentence holding them is maths, and goes to the
    /// interpreter, like one holding a plain α.
    /// </summary>
    [Theory]
    [InlineData("Several procedures keep the FWER at or below \U0001D6FC.")]
    [InlineData("The hyperparameters \U0001D436 and \U0001D70E must not be chosen with the test data.")]
    [InlineData("Here \U0001D707\u210E is the average clear-sky index at hour of day \u210E.")]
    public void MathsItalicLettersMarkTheirSentenceAsMaths(string escaped)
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Text, System.Text.RegularExpressions.Regex.Unescape(escaped), "The next sentence is ordinary prose.")
            .Snapshot();

        var units = Units(snapshot);

        Assert.True(units[0].ContainsMaths);
        Assert.False(units[^1].ContainsMaths);
    }

    [Fact]
    public void ASpacedEllipsisDoesNotEndASentence()
    {
        var units = SentenceSplitter.Split("This implies a > 0, i = 1, . . . , q-1, and b > 0. Next sentence here.");
        Assert.Equal(2, units.Count);
    }

    [Fact]
    public async Task ATableIsAnnouncedWithTheWholeTableHighlighted_NotReadCellByCell()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Caption, "Table 2. Rejection frequencies of the tests.")
            .Block(BlockKind.Table, "T = 1000 T = 2500", "LM 0.118 0.339 0.517", "LMR 0.007 0.042 0.103")
            .Block(BlockKind.Text, "The size of the tests is close to the nominal level.")
            .Snapshot();

        var units = Units(snapshot);
        var table = Assert.Single(units, u => u.Kind == ReadingUnitKind.Table);
        var plan = await new NarrationPlanner().BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default);
        var segment = Assert.Single(plan.Segments, s => s.Kind == ReadingUnitKind.Table);

        Assert.Equal("Table not read aloud.", segment.SpokenText);
        Assert.Equal(ScriptProvenance.StructureAnnouncement, segment.Provenance);
        Assert.Equal(table.AnchorIds.OrderBy(a => a), segment.SourceLinks.SelectMany(l => l.AnchorIds).Distinct().OrderBy(a => a));
        Assert.Contains(plan.Segments, s => s.SpokenText.StartsWith("Table two", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Segments, s => s.SpokenText.Contains("zero point one one eight", StringComparison.Ordinal));
    }

    [Fact]
    public void APublishersDownloadCoverSheetIsSkipped()
    {
        var cover = new LayoutPage(0)
            .Block(BlockKind.Title, "Specification and testing of time-varying models")
            .Block(BlockKind.Text, "To cite this article: A. Author (2017) Specification and testing, Reviews, 36:4.",
                "To link to this article: https://doi.org/10.1080/07474938.2014.977064",
                "Submit your article to this journal Article views: 976 View related articles");
        var article = new LayoutPage(1)
            .Block(BlockKind.Title, "Specification and testing of time-varying models")
            .Block(BlockKind.Text, "In this article we develop a specification technique for these models.");

        var units = new ReadingUnitBuilder().Build(cover.Snapshot(article), out var diagnostics);

        Assert.All(units, u => Assert.Equal(1, u.PageIndex));
        Assert.Contains(diagnostics, d => d.Contains("cover sheet", StringComparison.Ordinal));
    }

    [Fact]
    public void ATableOfContentsIsNotRead()
    {
        var title = new LayoutPage(0).Block(BlockKind.Title, "Clustering daily patterns");
        var contents = new LayoutPage(1)
            .Block(BlockKind.Heading, "Table of contents")
            .Block(BlockKind.Text,
                "Abstract .......................................................... i",
                "1 Introduction ..................................................... 1",
                "2 Literature review ................................................ 2",
                "2.1 Support vector machines, incremental learning and",
                "learning curves .................................................... 6",
                "3 Methodology . . . . . . . . . . . . . . . . . . . . . . . . . . . 12");
        var body = new LayoutPage(2)
            .Block(BlockKind.Heading, "1 Introduction")
            .Block(BlockKind.Text, "Suppose a new customer in a store buys a smartwatch.");

        var units = new ReadingUnitBuilder().Build(title.Snapshot(contents, body), out var diagnostics);

        Assert.DoesNotContain(units, u => u.PageIndex == 1);
        Assert.Contains(units, u => u.OriginalText.Contains("smartwatch", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("contents", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentsWithoutLeaders_AreNotReadEither()
    {
        var title = new LayoutPage(0).Block(BlockKind.Title, "Building type classification");
        var contents = new LayoutPage(1)
            .Block(BlockKind.Heading, "TABLE OF CONTENTS")
            .Block(BlockKind.Text, "Abstract 2", "Acknowledgements 2", "Chapter 1: Introduction 8",
                "2.1 The urban energy metadata gap 9", "2.5. Summary of literature, identified gaps,",
                "and research positioning 11", "Chapter 3: Methodology 13");
        var body = new LayoutPage(2)
            .Block(BlockKind.Text, "The sample has 1 578 buildings, of which the training set took 80",
                "per cent, and each of the ten repeated splits was stratified by type.",
                "Accuracy was 0.71 for the first model and the second reached 74",
                "per cent once the weather features were added to the 12",
                "that the first model had.");

        var units = new ReadingUnitBuilder().Build(title.Snapshot(contents, body));

        Assert.DoesNotContain(units, u => u.PageIndex == 1);
        Assert.Contains(units, u => u.PageIndex == 2);
    }

    [Fact]
    public void AListOfFiguresIsSkippedUnderItsHeading_AndTheListAfterItIsRead()
    {
        var title = new LayoutPage(0).Block(BlockKind.Title, "Clustering daily solar irradiance patterns");
        var figures = new LayoutPage(1)
            .Block(BlockKind.Heading, "List of Figures")
            .Block(BlockKind.Text, "Figure 1: Locations of the five extraction points used in the study 14 Figure 2: Distributions of hourly",
                "estimated and clear-sky irradiation at the solar park, excluding observations with zero irradiance.");
        var more = new LayoutPage(2)
            .Block(BlockKind.Text, "Figure 7d: Mean hourly irradiation profiles obtained using three methods",
                "at the fourth location 59")
            .Block(BlockKind.Text, "Distributions of bootstrap values across the stages at the fifth location 66 Figure 9b: Bootstrap",
                "distribution for the full procedure at the second location 66 Figure 9d: Bootstrap distribution for",
                "the full procedure at the fourth location, which runs over the line")
            .Block(BlockKind.Heading, "List of Abbreviations")
            .Block(BlockKind.Text, "ARI Adjusted Rand index");

        var units = new ReadingUnitBuilder().Build(title.Snapshot(figures, more));

        Assert.DoesNotContain(units, u => u.OriginalText.Contains("Figure", StringComparison.Ordinal));
        Assert.Contains(units, u => u.OriginalText.Contains("Adjusted Rand", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentsEntriesThatRunOnToAnotherPage_AreSkippedByLine()
    {
        var page = new LayoutPage(0)
            .Block(BlockKind.Text, "A.5 Statistical tests ......................................... 42",
                "A.6 Required calibration amount ............................... 44")
            .Block(BlockKind.Text, "The declaration follows on this page and is read.");

        var units = new ReadingUnitBuilder().Build(page.Snapshot());

        var unit = Assert.Single(units);
        Assert.Contains("declaration", unit.OriginalText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("The results are shown in Section 4.", false)]
    [InlineData("and so on... 12", false)]
    [InlineData("Wait.... what", false)]
    [InlineData("4.1.3 Class distribution and imbalance ........ 21", true)]
    [InlineData("References . . . . . . . . . . xii", true)]
    public void ALeaderLineEndsInDotsAndAPageNumber(string line, bool expected) =>
        Assert.Equal(expected, ContentsPages.IsLeaderLine(line));

    [Fact]
    public void AnArticlePageMentioningOneCoverPhraseIsKept()
    {
        var first = new LayoutPage(0).Block(BlockKind.Text, "Published online: 30 Nov 2015. The model is introduced here in full.");
        var second = new LayoutPage(1).Block(BlockKind.Text, "The second page continues the argument.");

        var units = new ReadingUnitBuilder().Build(first.Snapshot(second));

        Assert.Contains(units, u => u.PageIndex == 0);
    }

    [Fact]
    public void ArticleMetadataIsSkipped_ButNotABodySentenceThatStartsTheSameWay()
    {
        var snapshot = new LayoutPage(0)
            .Block(BlockKind.Title, "Specification and testing of time-varying models")
            .Block(BlockKind.Text, "In this article we develop a specification technique for these models.")
            .Block(BlockKind.Text, "JEL CLASSIFICATION C12; C22; C51; C52")
            .Block(BlockKind.Text, "Keywords: conditional heteroskedasticity; nonlinear time series")
            .Block(BlockKind.Text, "Received wisdom holds that the variance is constant over long samples.")
            .Snapshot();

        var units = new ReadingUnitBuilder().Build(snapshot, out var diagnostics);

        Assert.DoesNotContain(units, u => u.OriginalText.StartsWith("JEL", StringComparison.Ordinal));
        Assert.DoesNotContain(units, u => u.OriginalText.StartsWith("Keywords", StringComparison.Ordinal));
        Assert.Contains(units, u => u.OriginalText.StartsWith("Received wisdom", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Contains("metadata", StringComparison.Ordinal));
    }

    // ---- author affiliations (skipped: the reader's choice, 2026-11-19) ----

    private static LayoutPage FirstPageLikeNbag025() => new LayoutPage(0)
        .Block(BlockKind.Text, "Journal of Financial Econometrics, 2026, 24(5), nbag025", "Article")
        .Block(BlockKind.Text, "Sparse Time-Variation in Conditional Correlations", "Etienne Wijler1, and Andre Lucas1,2,")
        .Block(BlockKind.Text, "1", "Vrije Universiteit Amsterdam", "2", "Tinbergen Institute",
            "Address correspondence to Etienne Wijler, Vrije Universiteit Amsterdam, SBE/EDS, De Boelelaan 1105,",
            "Amsterdam, The Netherlands, or e-mail: e.j.j.wijler@vu.nl.")
        .Block(BlockKind.Text, "Abstract", "We develop a data-driven procedure to identify partial correlations.");

    private static string Spoken(IReadOnlyList<ReadingUnit> units) => string.Join(" | ", units.Select(u => u.OriginalText));

    [Fact]
    public void AffiliationsAndTheCorrespondenceAddress_AreSkipped_TheTitleAndAuthorsRead()
    {
        var units = new ReadingUnitBuilder().Build(FirstPageLikeNbag025().Snapshot(), out var diagnostics);
        string spoken = Spoken(units);

        Assert.DoesNotContain("Universiteit", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Tinbergen", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Netherlands", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("wijler@", spoken, StringComparison.Ordinal);
        Assert.Contains("Sparse Time-Variation", spoken, StringComparison.Ordinal);
        Assert.Contains("Andre Lucas", spoken, StringComparison.Ordinal);
        Assert.Contains("data-driven procedure", spoken, StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Contains("affiliations", StringComparison.Ordinal));
    }

    [Fact]
    public void AffiliationsAreRead_WhenTheReaderAsksForThem()
    {
        var units = new ReadingUnitBuilder(ReadingUnitBuilderOptions.Default with { SkipAffiliations = false })
            .Build(FirstPageLikeNbag025().Snapshot());

        Assert.Contains("Tinbergen Institute", Spoken(units), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAffiliationFootnote_IsSkipped_ButABodySentenceAtTheFootIsRead_AndLaterPagesAreLeftAlone()
    {
        var first = new LayoutPage(0)
            .Block(BlockKind.Title, "College Admissions and Matching Markets")
            .Block(BlockKind.Text, "Abstract", "We study admissions.")
            .Block(BlockKind.Text, "The data were provided by the University of Cape Town library.")
            .At(700).Block(BlockKind.Text, "* Department of Statistics, Nelson Mandela University, Gqeberha, South Africa.")
            .Block(BlockKind.Text, "Our survey was answered by staff at the University of Cape Town.");
        var second = new LayoutPage(1)
            .Block(BlockKind.Text, "Department of Economics, University of Oxford, kindly shared its records.");

        string spoken = Spoken(new ReadingUnitBuilder().Build(first.Snapshot(second)));

        Assert.DoesNotContain("Nelson Mandela", spoken, StringComparison.Ordinal);
        Assert.Contains("College Admissions", spoken, StringComparison.Ordinal);       // a title, though it names a college
        Assert.Contains("provided by the University of Cape Town", spoken, StringComparison.Ordinal);
        Assert.Contains("answered by staff", spoken, StringComparison.Ordinal);        // no marker: a body sentence
        Assert.Contains("University of Oxford", spoken, StringComparison.Ordinal);     // not the first page
    }

    [Fact]
    public void TablesCanBeSkippedEntirely()
    {
        var snapshot = new LayoutPage()
            .Block(BlockKind.Table, "LM 0.118 0.339 0.517")
            .Block(BlockKind.Text, "The size of the tests is close to the nominal level.")
            .Snapshot();

        var units = new ReadingUnitBuilder(new ReadingUnitBuilderOptions(NarrationFilterSettings.Default with { IncludeTables = false }))
            .Build(snapshot);

        Assert.DoesNotContain(units, u => u.Kind == ReadingUnitKind.Table);
    }
}
