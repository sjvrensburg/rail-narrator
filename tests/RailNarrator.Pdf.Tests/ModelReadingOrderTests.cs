using RailNarrator.Domain;
using RailNarrator.Pdf;
using RailNarrator.TestFixtures;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// A layout model that supplies its own reading order returns its detections by confidence, with the
/// order in a field. The adapter must sort them before Core's post-processing, which expects reading
/// order and renumbers the blocks by their place in the list when it removes a duplicate.
/// </summary>
/// <remarks>
/// Found on real papers with PP-DocLayoutV3: 11 of 449 pages (every one where Core removed a
/// duplicate block) were read in order of confidence, so "Acknowledgments" and "Funding" were spoken
/// after the paragraphs under them. A stand-in analyzer gives the same shape here with no model.
/// </remarks>
public sealed class ModelReadingOrderTests
{
    private static readonly ExtractionOptions Options = new(
        "stand-in-model", OcrPolicy.Off, 1024, DeskewLines: false, ExtractorVersions.Current);

    private static string Page() => SyntheticPdf.Write(SyntheticPdf.TempPath("model-order"),
    [[
        new FixtureLine("Acknowledgments", 72, 100, 13),
        new FixtureLine("An earlier version of this paper went round under another title.", 72, 130),
        new FixtureLine("We thank two referees for the comments that reshaped section four.", 72, 146),
        new FixtureLine("Funding", 72, 200, 13),
        new FixtureLine("This work was supported by a research council grant to both authors.", 72, 230),
        new FixtureLine("The council had no part in the design or the writing of the study.", 72, 246),
    ]]);

    private static async Task<PageExtraction> ExtractAsync(ExtractionBackend backend)
    {
        using var source = new RailReaderExtractionSource();
        source.WithBackend(backend);
        var snapshot = await source.ExtractAsync(new ExtractionRequest(Page(), null, null, Options, NarrationFilterSettings.Default));
        Assert.Empty(snapshot.AllWarnings());
        return snapshot.Pages[0];
    }

    private static string Text(PageExtraction page, ExtractedBlock block)
        => string.Join(" ", block.LineIndices.Select(i => page.TextOf(page.Lines[i].Text)?.Trim()));

    /// <summary>The first word of each block, in the order the page is read.</summary>
    private static string[] Reading(PageExtraction page)
        => page.OrderedBlocks.Select(b => Text(page, b).Split(' ')[0]).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AModelsOwnOrderIsKept_WhenItsBlocksArriveByConfidence(bool withDuplicate)
    {
        // No resolver of its own: the adapter picks the model's order from the capabilities.
        var backend = new ExtractionBackend("stand-in-model", () => new ConfidenceOrderedModel(withDuplicate),
            ConfidenceOrderedModel.Shape);

        var page = await ExtractAsync(backend);

        // The duplicate is the case that failed: removing it is what renumbered the page.
        Assert.Equal(4, page.Blocks.Count);
        Assert.Equal(["Acknowledgments", "An", "Funding", "This"], Reading(page));
    }

    /// <summary>
    /// Four blocks cut from the page's characters, handed back the way PP-DocLayoutV3 hands back a
    /// page: most confident first (the paragraphs, then the headings), each carrying its place in
    /// the reading as a sparse number. Optionally with a second box over the last paragraph's
    /// second line, the kind of duplicate Core removes.
    /// </summary>
    private sealed class ConfidenceOrderedModel(bool withDuplicate) : ILayoutAnalyzer
    {
        public static LayoutModelCapabilities Shape { get; } = new(800, [], ProvidesReadingOrder: true);

        public LayoutModelCapabilities Capabilities => Shape;

        public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
            IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
        {
            var chars = charBoxes ?? [];
            LayoutBlock Block(float top, float bottom, BlockRole role, int order, float confidence, float grow = 0)
            {
                var inside = chars.Where(c => c.Right > c.Left && (c.Top + c.Bottom) / 2 is var mid && mid >= top && mid < bottom).ToList();
                Assert.NotEmpty(inside);
                float left = inside.Min(c => c.Left) - 2 - grow, right = inside.Max(c => c.Right) + 2 + grow;
                float y0 = inside.Min(c => c.Top) - 2, y1 = inside.Max(c => c.Bottom) + 2;
                return new LayoutBlock { BBox = new BBox(left, y0, right - left, y1 - y0), Role = role, Order = order, Confidence = confidence };
            }

            var blocks = new List<LayoutBlock>
            {
                Block(115, 160, BlockRole.Text, 40, 0.96f),
                Block(215, 260, BlockRole.Text, 77, 0.95f),
                Block(80, 110, BlockRole.Heading, 12, 0.86f),
                Block(180, 210, BlockRole.Heading, 41, 0.85f),
            };
            if (withDuplicate) blocks.Insert(2, Block(238, 260, BlockRole.Text, 78, 0.90f, grow: 3));
            return new PageAnalysis { Blocks = blocks, PageWidth = pageW, PageHeight = pageH };
        }

        public void Dispose()
        {
        }
    }
}
