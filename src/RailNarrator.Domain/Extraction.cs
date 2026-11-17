namespace RailNarrator.Domain;

/// <summary>
/// Where a piece of extracted text came from. Carried through to narration so that OCR'd and
/// visually-transcribed content can be presented as unverified rather than as fact.
/// </summary>
public enum ExtractionProvenance
{
    /// <summary>The document's own text layer.</summary>
    NativeText,

    /// <summary>Synthesised by optical character recognition. Unverified; quality depends on raster resolution.</summary>
    Ocr,

    /// <summary>Recovered from pixels by a vision model, e.g. an equation crop transcribed to LaTeX. Unverified.</summary>
    VisualMath,
}

/// <summary>
/// Rail Narrator's own semantic block classification. Mirrors RailReaderCore's <c>BlockRole</c>
/// one-for-one so the PDF adapter is a straight mapping, but lives here because the domain must
/// not depend on the PDF stack.
/// </summary>
/// <remarks>
/// A model-free analyser supplies no classes: every block arrives as <see cref="Text"/>. Role-keyed
/// behaviour (running-header suppression, display-maths detection) must therefore degrade to
/// geometry and glyph cues rather than assume the role is known — see <c>MathsRegionDetector</c>.
/// </remarks>
public enum BlockKind
{
    Unknown,
    Text,
    Heading,
    Title,
    Caption,
    Aside,
    DisplayMath,
    InlineMath,
    Algorithm,
    Table,
    Figure,
    Chart,
    Header,
    Footer,
    PageNumber,
    Footnote,
    Reference,
    Decoration,
}

/// <summary>
/// Names one extraction of one document. Everything derived from an extraction — anchors, reading
/// units, narration plans, speech assets, timings — carries this identity, so an upstream change
/// invalidates exactly the downstream artefacts that depended on it.
/// </summary>
/// <param name="DocumentContentHash">SHA-256 of the document's bytes, hex. Content, not path: a renamed or moved file keeps its caches.</param>
/// <param name="ExtractionRevision">Monotonic counter, bumped whenever the extraction inputs change (analyser, OCR mode, render DPI, options) or extraction is re-run.</param>
/// <remarks>
/// Indices into a mutable layout list are <b>not</b> stable identifiers and are never persisted on
/// their own. A cue pointing at block 7 means nothing once re-OCR renumbers the blocks; the
/// revision is what lets a plan be remapped or discarded instead of pointing at the wrong text.
/// </remarks>
public readonly record struct ExtractionIdentity(string DocumentContentHash, int ExtractionRevision)
{
    public static ExtractionIdentity None => new("", 0);
    public bool IsEmpty => string.IsNullOrEmpty(DocumentContentHash);
    public override string ToString() => $"{DocumentContentHash[..Math.Min(12, DocumentContentHash.Length)]}@r{ExtractionRevision}";
}

/// <summary>One character's box in displayed page points.</summary>
/// <param name="Index">UTF-16 offset into the owning page's <see cref="PageExtraction.Text"/>.</param>
/// <param name="Rect">Tight glyph box. May be empty for whitespace and other non-marking glyphs.</param>
/// <param name="Angle">Glyph rotation in the displayed frame, clockwise degrees normalised to {0,90,180,270}.</param>
public readonly record struct ExtractedChar(int Index, PageRect Rect, float Angle);

/// <summary>One detected text line.</summary>
/// <param name="LineId">Stable within an extraction snapshot.</param>
/// <param name="BlockIndex">Index into <see cref="PageExtraction.Blocks"/>.</param>
/// <param name="Text">UTF-16 range into <see cref="PageExtraction.Text"/>.</param>
/// <param name="Rect">The line's true horizontal and vertical extent — not the parent block's.</param>
/// <param name="FromOcr">True when the line's characters were recognised rather than extracted.</param>
public sealed record ExtractedLine(string LineId, int BlockIndex, TextRange Text, PageRect Rect, bool FromOcr);

/// <summary>One layout block, in reading order.</summary>
/// <param name="BlockId">Stable within an extraction snapshot.</param>
/// <param name="Order">Reading order, 0..N-1 across the page.</param>
/// <param name="LineIndices">Indices into <see cref="PageExtraction.Lines"/>, in reading order.</param>
/// <param name="UprightTurns">Clockwise quarter-turns that would make this block's text read upright; 0 for ordinary text.</param>
public sealed record ExtractedBlock(
    string BlockId,
    BlockKind Kind,
    int Order,
    PageRect BBox,
    IReadOnlyList<int> LineIndices,
    float Confidence,
    int UprightTurns)
{
    public bool IsRotatedText => UprightTurns != 0;
}

/// <summary>
/// How much of a page is actually readable, and whether that reading can be trusted.
/// </summary>
/// <remarks>
/// The failure this guards against is a page with a sparse or corrupt text layer: enough
/// characters to look like text, not enough to narrate. Treating "has a text layer" as "is
/// narratable" suppresses the OCR the page needs, so coverage is measured and reported instead.
/// </remarks>
/// <param name="TextLayerPresent">The document supplied any text for this page.</param>
/// <param name="OcrUsed">Recognition ran and contributed text.</param>
/// <param name="CharCount">Characters available after extraction, whichever source supplied them.</param>
/// <param name="PageAreaPoints">Page area in points², the denominator for glyph density.</param>
/// <param name="GlyphAreaPoints">Sum of non-empty character box areas.</param>
/// <param name="Warnings">Human-readable problems. Never empty-by-default: an empty transcript is a warning, not a success.</param>
public sealed record TextCoverage(
    bool TextLayerPresent,
    bool OcrUsed,
    int CharCount,
    double PageAreaPoints,
    double GlyphAreaPoints,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Fraction of the page covered by glyph boxes. A born-digital text page is typically 0.02–0.15.</summary>
    public double GlyphCoverage => PageAreaPoints <= 0 ? 0 : Math.Clamp(GlyphAreaPoints / PageAreaPoints, 0, 1);

    /// <summary>
    /// True when the page has less text than narration needs. Deliberately conservative: a figure
    /// page legitimately has almost none, so this is a signal for the caller to consider OCR or to
    /// announce the page as unreadable, never an automatic skip.
    /// </summary>
    public bool IsSparse => CharCount < 24;

    public static TextCoverage Empty { get; } = new(false, false, 0, 0, 0, ["No text extracted."]);
}

/// <summary>Everything extracted from one page, in reading order.</summary>
/// <param name="PageIndex">Zero-based.</param>
/// <param name="Text">The page's text. Every <see cref="ExtractedChar.Index"/> and <see cref="ExtractedLine.Text"/> is a UTF-16 offset into this string.</param>
public sealed record PageExtraction(
    int PageIndex,
    double PageWidth,
    double PageHeight,
    string Text,
    IReadOnlyList<ExtractedBlock> Blocks,
    IReadOnlyList<ExtractedLine> Lines,
    IReadOnlyList<ExtractedChar> Chars,
    TextCoverage Coverage)
{
    /// <summary>Blocks in reading order. A caller must never re-sort these by geometry.</summary>
    public IEnumerable<ExtractedBlock> OrderedBlocks => Blocks.OrderBy(b => b.Order);

    public ExtractedBlock? FindBlock(string blockId) => Blocks.FirstOrDefault(b => b.BlockId == blockId);

    public ExtractedLine? FindLine(string lineId) => Lines.FirstOrDefault(l => l.LineId == lineId);

    /// <summary>Text of a range, or null when the range does not fit this page.</summary>
    public string? TextOf(TextRange range)
    {
        if (range.Start < 0 || range.End > Text.Length) return null;
        return Text.Substring(range.Start, range.Length);
    }
}

/// <summary>
/// The inputs an extraction was produced under. Two extractions of the same bytes under different
/// options are different snapshots, and their anchors are not interchangeable.
/// </summary>
public sealed record ExtractionOptions(
    string AnalyzerId,
    OcrPolicy Ocr,
    int AnalysisRenderSize,
    bool DeskewLines,
    string ExtractorVersion)
{
    public static ExtractionOptions ModelFree { get; } = new("text-layout", OcrPolicy.Off, 0, false, ExtractorVersions.Current);
}

/// <summary>How much recognition work to do on a page with no usable text layer.</summary>
public enum OcrPolicy
{
    /// <summary>No recognition. Scanned pages stay unreadable and are reported as such.</summary>
    Off,

    /// <summary>Detect line geometry only. Gives framing without text; not enough to narrate.</summary>
    LinesOnly,

    /// <summary>Detect and transcribe. Required for narrating a scan.</summary>
    Full,
}

/// <summary>Extractor versions, folded into cache keys so an upgrade invalidates what it changes.</summary>
public static class ExtractorVersions
{
    /// <summary>Bump when extraction output changes shape or meaning for the same input.</summary>
    public const string Current = "extraction/1";
}

/// <summary>
/// A versioned, self-describing extraction of a whole document. The single source of geometry:
/// anchors reference it, and nothing downstream invents coordinates.
/// </summary>
public sealed record ExtractionSnapshot(
    ExtractionIdentity Identity,
    CoordinateFrameId Frame,
    ExtractionOptions Options,
    IReadOnlyList<PageExtraction> Pages)
{
    public int PageCount => Pages.Count;

    public PageExtraction? Page(int pageIndex)
        => pageIndex >= 0 && pageIndex < Pages.Count ? Pages[pageIndex] : null;

    /// <summary>Pages holding at least one character. Zero means the document is not narratable as extracted.</summary>
    public IReadOnlyList<int> PagesWithText => Pages.Where(p => p.Coverage.CharCount > 0).Select(p => p.PageIndex).ToList();

    /// <summary>Every coverage warning across the document, prefixed with its page for display.</summary>
    public IEnumerable<string> AllWarnings()
        => Pages.SelectMany(p => p.Coverage.Warnings.Select(w => $"Page {p.PageIndex + 1}: {w}"));
}

/// <summary>
/// A pointer from narration back to the exact place in the document it came from.
/// </summary>
/// <remarks>
/// <para>
/// An anchor is <b>single-page</b>. A reading unit that crosses a page boundary carries one anchor
/// per page rather than one anchor with a rectangle spanning the gap — a union across pages, or
/// across two columns, is a large rectangle over mostly unrelated content, and highlighting it
/// would darken text the narration is not reading. Keep the disjoint rects.
/// </para>
/// <para>
/// <see cref="SourceCharRange"/> is null for visual-only content (an equation recovered from
/// pixels with no text layer behind it): there are no source characters to point at, but the
/// region still has geometry and still needs highlighting.
/// </para>
/// </remarks>
public sealed record SourceAnchor(
    string AnchorId,
    ExtractionIdentity Extraction,
    int PageIndex,
    string BlockId,
    IReadOnlyList<string> LineIds,
    TextRange? SourceCharRange,
    IReadOnlyList<PageRect> Rects,
    CoordinateFrameId Frame,
    ExtractionProvenance Provenance)
{
    /// <summary>True when this anchor points at pixels only, with no source characters behind it.</summary>
    public bool IsVisualOnly => SourceCharRange is null;

    /// <summary>
    /// The bounding box of every fragment, for framing decisions only. Never use this to draw a
    /// highlight: it covers content between the fragments too.
    /// </summary>
    public PageRect? FramingBounds => PageRect.UnionAll(Rects);

    /// <summary>True when the anchor's geometry belongs to <paramref name="identity"/>'s snapshot.</summary>
    public bool BelongsTo(ExtractionIdentity identity) => Extraction == identity;
}
