namespace RailNarrator.Domain;

/// <summary>
/// A user's override of the automatic maths classification for one region.
/// </summary>
/// <param name="PageIndex">Zero-based page.</param>
/// <param name="Region">The region in displayed page points.</param>
/// <param name="TreatAsMaths">True for "treat as maths", false for "restore prose".</param>
/// <remarks>
/// Inline maths detection is heuristic and a model-free extraction has no semantic classes at all,
/// so the reader must be able to correct it. Overrides are keyed by geometry rather than by block
/// index because indices are not stable across a re-extraction.
/// </remarks>
public sealed record MathsRegionOverride(int PageIndex, PageRect Region, bool TreatAsMaths)
{
    public bool Contains(int pageIndex, PageRect rect)
        => pageIndex == PageIndex && rect.MidX >= Region.Left && rect.MidX <= Region.Right
           && rect.MidY >= Region.Top && rect.MidY <= Region.Bottom;
}

/// <summary>Tuning for <see cref="ReadingUnitBuilder"/>.</summary>
/// <param name="Filters">Which block kinds are narrated at all.</param>
/// <param name="Maths">Inline maths detection tuning.</param>
/// <param name="MaxUnitChars">
/// Longest unit before it is split at a clause boundary. A unit longer than one synthesis request
/// is also longer than one highlight can usefully cover at high magnification.
/// </param>
/// <param name="ContextChars">Characters of surrounding text kept for a verbaliser. Bounded: context is a hint, not the document.</param>
/// <param name="PronunciationDictionary">User word → spoken form overrides.</param>
/// <param name="Overrides">Manual maths/prose region overrides.</param>
/// <param name="DetectRunningElements">Suppress repeated headers, footers and page numbers.</param>
/// <param name="RunningElementMinPages">A block must repeat on at least this many pages to count as running.</param>
public sealed record ReadingUnitBuilderOptions(
    NarrationFilterSettings Filters,
    MathsDetectionOptions? Maths = null,
    int MaxUnitChars = 420,
    int ContextChars = 240,
    IReadOnlyDictionary<string, string>? PronunciationDictionary = null,
    IReadOnlyList<MathsRegionOverride>? Overrides = null,
    bool DetectRunningElements = true,
    int RunningElementMinPages = 3)
{
    public static ReadingUnitBuilderOptions Default { get; } = new(NarrationFilterSettings.Default);

    public MathsDetectionOptions MathsOptions => Maths ?? new MathsDetectionOptions();
}

/// <summary>One line or block identified as a running header, footer or page number.</summary>
/// <param name="LineId">The suppressed line, or null when a whole block was suppressed.</param>
public sealed record RunningElement(int PageIndex, string BlockId, string? LineId, string NormalisedText, RunningElementKind Kind)
{
    /// <summary>True when this element suppresses a single line rather than a whole block.</summary>
    public bool IsLineLevel => LineId is not null;
}

/// <summary>Why a block was classified as running content.</summary>
public enum RunningElementKind { RepeatedHeader, RepeatedFooter, PageNumber }

/// <summary>
/// Finds the repetitive margin content that should not be narrated.
/// </summary>
/// <remarks>
/// <para>
/// Detection works on <b>lines</b>, not blocks, and that is a measured decision. On a sparse page a
/// model-free analyzer groups the running header, the body and the footer into a single block, so a
/// block whose bounding box straddles the whole page never falls inside a margin band and is never
/// seen. Lines keep their own geometry whatever the block grouping did, so the same rule works for a
/// one-line page and a dense one.
/// </para>
/// <para>
/// Two inspectable rules, not a learned classifier, so a reader can see why something was dropped and
/// turn it back on. A line counts as running when either its normalised text repeats across enough
/// pages inside a margin band, or its text is nothing but a page number.
/// </para>
/// <para>
/// When a layout model did supply roles, a block tagged header/footer/page-number is suppressed whole,
/// which catches running elements that sit outside the geometric bands.
/// </para>
/// </remarks>
public static class RunningElementDetector
{
    /// <summary>Fraction of page height from the top edge that can hold a running header.</summary>
    public const double HeaderBand = 0.11;

    /// <summary>Fraction of page height from the bottom edge that can hold a running footer.</summary>
    public const double FooterBand = 0.11;

    public static IReadOnlyList<RunningElement> Detect(ExtractionSnapshot snapshot, int minPages = 3)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Pages.Count == 0) return [];

        var found = new List<RunningElement>();
        var textCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<(PageExtraction Page, ExtractedBlock Block, ExtractedLine? Line, string Normalised, string Key, bool InHeaderBand, bool InFooterBand)>();
        var printedNumbers = new List<(PageExtraction Page, ExtractedBlock Block, ExtractedLine Line, string Normalised, int Number)>();

        foreach (var page in snapshot.Pages)
        {
            if (page.PageHeight <= 0) continue;
            double headerLimit = page.PageHeight * HeaderBand;
            double footerLimit = page.PageHeight * (1 - FooterBand);

            foreach (var block in page.Blocks)
            {
                bool roleSaysRunning = block.Kind is BlockKind.Header or BlockKind.Footer or BlockKind.PageNumber;

                // A block the layout model explicitly tagged is suppressed whole, whatever its geometry.
                if (roleSaysRunning)
                {
                    string blockText = NormaliseForComparison(page.TextOf(RunningElementDetector.BlockTextRange(page, block)) ?? "");
                    if (blockText.Length > 0)
                        found.Add(new RunningElement(page.PageIndex, block.BlockId, null, blockText,
                            block.Kind == BlockKind.PageNumber ? RunningElementKind.PageNumber
                            : block.Kind == BlockKind.Footer ? RunningElementKind.RepeatedFooter
                            : RunningElementKind.RepeatedHeader));
                    continue;
                }

                foreach (int li in block.LineIndices)
                {
                    if (li < 0 || li >= page.Lines.Count) continue;
                    var line = page.Lines[li];
                    bool inHeaderBand = line.Rect.Top >= 0 && line.Rect.Bottom <= headerLimit;
                    bool inFooterBand = line.Rect.Top >= footerLimit && line.Rect.Bottom <= page.PageHeight;
                    if (!inHeaderBand && !inFooterBand) continue;

                    string normalised = NormaliseForComparison(page.TextOf(line.Text) ?? "");
                    if (normalised.Length == 0) continue;

                    // A page number is a number, possibly with decoration. Never narrated by default.
                    if (IsPageNumberLike(normalised, page.PageIndex, snapshot.PageCount))
                    {
                        found.Add(new RunningElement(page.PageIndex, block.BlockId, line.LineId, normalised, RunningElementKind.PageNumber));
                        continue;
                    }

                    // A bare number that is not this page's index may still be a printed page number:
                    // a journal article starts at 421, a chapter at 37. Decided below, across pages.
                    if (TryParseBareNumber(normalised, out int printed))
                    {
                        printedNumbers.Add((page, block, line, normalised, printed));
                        continue;
                    }

                    string key = RepetitionKey(normalised);
                    if (key.Length == 0) continue;
                    candidates.Add((page, block, line, normalised, key, inHeaderBand, inFooterBand));
                    textCounts[key] = textCounts.GetValueOrDefault(key) + 1;
                }
            }
        }

        int pages = snapshot.Pages.Count;
        // Books and journals alternate their running heads — author on the left-hand page, title on
        // the right — and a cover page or two carries neither, so each head appears on rather fewer
        // than half the pages. Measured on a 27-page article: 13 and 13. A half-the-pages rule
        // missed both, and they were read aloud in the middle of sentences that cross a page.
        int threshold = Math.Max(minPages, Math.Min(pages, (int)Math.Ceiling(pages * RunningShareOfPages)));

        foreach (var c in candidates)
        {
            if (textCounts[c.Key] < threshold) continue;
            var kind = c.InHeaderBand ? RunningElementKind.RepeatedHeader : RunningElementKind.RepeatedFooter;
            found.Add(new RunningElement(c.Page.PageIndex, c.Block.BlockId, c.Line!.LineId, c.Normalised, kind));
        }

        // Printed page numbers: the same offset from the page index, page after page.
        foreach (var run in printedNumbers.GroupBy(n => n.Number - n.Page.PageIndex))
        {
            if (run.Select(n => n.Page.PageIndex).Distinct().Count() < threshold) continue;
            foreach (var n in run)
                found.Add(new RunningElement(n.Page.PageIndex, n.Block.BlockId, n.Line.LineId, n.Normalised, RunningElementKind.PageNumber));
        }
        return found;
    }

    /// <summary>The character range of a block's text, derived from its lines.</summary>
    public static TextRange BlockTextRange(PageExtraction page, ExtractedBlock block)
    {
        if (block.LineIndices.Count == 0) return TextRange.Empty;
        int start = int.MaxValue, end = int.MinValue;
        foreach (var li in block.LineIndices)
        {
            if (li < 0 || li >= page.Lines.Count) continue;
            var r = page.Lines[li].Text;
            start = Math.Min(start, r.Start);
            end = Math.Max(end, r.End);
        }
        return end > start ? TextRange.FromBounds(start, end) : TextRange.Empty;
    }

    private static string NormaliseForComparison(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>Share of a document's pages a margin line must recur on to count as a running head or foot.</summary>
    public const double RunningShareOfPages = 0.3;

    /// <summary>
    /// What two margin lines must share to be the same running element: their letters. Digits are
    /// left out because a running head often carries the page number or the year with it.
    /// </summary>
    private static string RepetitionKey(string normalised)
    {
        var sb = new System.Text.StringBuilder(normalised.Length);
        foreach (char c in normalised)
        {
            if (char.IsLetter(c)) sb.Append(c);
            else if (c == ' ' && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        string key = sb.ToString().Trim();
        return key.Count(char.IsLetter) >= 3 ? key : "";
    }

    private static bool TryParseBareNumber(string normalised, out int number)
    {
        number = 0;
        string compact = normalised.Replace(" ", "");
        return compact.Length is > 0 and <= 5 && compact.All(char.IsAsciiDigit) && int.TryParse(compact, out number);
    }

    private static bool IsPageNumberLike(string normalised, int pageIndex, int pageCount)
    {
        if (normalised.Length > 12) return false;
        var digits = new string(normalised.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return false;
        if (!int.TryParse(digits, out int n)) return false;
        // Accept the page's own number in either 0- or 1-based form, or "x of y".
        if (n == pageIndex || n == pageIndex + 1) return true;
        if (n >= 1 && n <= pageCount) return normalised.Replace(" ", "").Contains("of") || digits.Length <= 4;
        return false;
    }
}

/// <summary>
/// Turns an extraction snapshot into ordered narration units with source anchors.
/// </summary>
/// <remarks>
/// <para>
/// The whole document's eligible text is walked once, in reading order, as a single run. Sentences
/// are then cut from that run, which is what lets a sentence crossing a line, a column or a page
/// boundary keep every one of its disjoint source regions instead of being truncated at the break.
/// </para>
/// <para>
/// Prose anchors describe word fragments within a line; equation anchors retain disjoint line
/// rectangles. A sentence spanning columns never highlights a union covering the gutter.
/// </para>
/// </remarks>
public sealed class ReadingUnitBuilder
{
    /// <summary>
    /// Multiple of the line's median ink gap that marks a word space. Measured on real 9–11 pt body
    /// text: the widest intra-word gap was 1.48× the median and the narrowest word space was 2.08×,
    /// so 1.6 sits inside that separation with margin on both sides. See
    /// <see cref="SpaceGapThreshold"/>.
    /// </summary>
    public const float SpaceGapMultiple = 1.6f;

    /// <summary>Fallback space gap as a fraction of glyph height, for lines too short to measure.</summary>
    public const float SpaceGapFallbackFraction = 0.55f;

    /// <summary>
    /// Floor on the space gap as a fraction of glyph height, so a face whose glyphs touch cannot have
    /// a near-zero median that splits ordinary kerning into words.
    /// </summary>
    public const float SpaceGapMinFractionOfHeight = 0.30f;

    private readonly ReadingUnitBuilderOptions _options;

    public ReadingUnitBuilder(ReadingUnitBuilderOptions? options = null)
        => _options = options ?? ReadingUnitBuilderOptions.Default;

    /// <summary>Builds units for every page in <paramref name="snapshot"/>, in reading order.</summary>
    public IReadOnlyList<ReadingUnit> Build(ExtractionSnapshot snapshot)
        => Build(snapshot, out _);

    /// <summary>Builds units, also reporting what was excluded and why.</summary>
    public IReadOnlyList<ReadingUnit> Build(ExtractionSnapshot snapshot, out IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var diag = new List<string>();

        var running = _options.DetectRunningElements
            ? RunningElementDetector.Detect(snapshot, _options.RunningElementMinPages)
            : [];

        // Detection decides what IS running content; the filters decide whether the reader wants it
        // suppressed. Conflating the two would make the toggles in settings inert, and a reader who
        // wants the running headers read back would have no way to ask for them.
        running = running.Where(r => r.Kind switch
        {
            RunningElementKind.RepeatedHeader => _options.Filters.ExcludeRunningHeaders,
            RunningElementKind.RepeatedFooter => _options.Filters.ExcludeFooters,
            RunningElementKind.PageNumber => _options.Filters.ExcludePageNumbers,
            _ => true,
        }).ToList();

        var runningBlockSet = running.Where(r => !r.IsLineLevel).Select(r => (r.PageIndex, r.BlockId)).ToHashSet();
        var runningLineSet = running.Where(r => r.IsLineLevel).Select(r => (r.PageIndex, r.LineId!)).ToHashSet();
        if (running.Count > 0)
            diag.Add($"Suppressed {running.Count} running header/footer/page-number line(s) across {running.Select(r => r.PageIndex).Distinct().Count()} page(s).");

        var run = BuildRun(snapshot, runningBlockSet, runningLineSet, diag);
        if (run.Glyphs.Count == 0)
        {
            diag.Add("No narratable text found. Check the extraction and the content filters before concluding the document is empty.");
            diagnostics = diag;
            return [];
        }

        var units = CutUnits(run, snapshot);
        diag.Add($"Built {units.Count} reading unit(s) from {run.Glyphs.Count} source character(s) " +
                 $"({units.Count(u => u.IsMaths)} mathematical).");
        diagnostics = diag;
        return units;
    }

    // ---- run construction -------------------------------------------------------------

    /// <summary>One source character pulled into the reading run, with everything needed to anchor it.</summary>
    internal readonly struct RunGlyph
    {
        public RunGlyph(char character, PageRect rect, int sourceIndex, int pageIndex, string blockId, string lineId,
                        int lineOrdinal, BlockKind kind, bool fromOcr, float lineMedianHeight, float lineBaselineY)
        {
            Character = character; Rect = rect; SourceIndex = sourceIndex; PageIndex = pageIndex;
            BlockId = blockId; LineId = lineId; LineOrdinal = lineOrdinal; Kind = kind; FromOcr = fromOcr;
            LineMedianHeight = lineMedianHeight; LineBaselineY = lineBaselineY;
            IsReal = true;
        }
        public char Character { get; }
        public PageRect Rect { get; }
        /// <summary>This character's offset in its own page's extracted text. Carried per glyph because a text layer is not contiguous: see <c>BuildRun</c>.</summary>
        public int SourceIndex { get; }
        public int PageIndex { get; }
        public string? BlockId { get; }
        public string? LineId { get; }
        public int LineOrdinal { get; }
        public BlockKind Kind { get; }
        public bool FromOcr { get; }
        public float LineMedianHeight { get; }
        public float LineBaselineY { get; }
        /// <summary>False for the newline placeholder that keeps run text and glyph lists index-aligned.</summary>
        public bool IsReal { get; }
    }

    internal sealed class ReadingRun
    {
        public System.Text.StringBuilder Text { get; } = new();
        public List<RunGlyph> Glyphs { get; } = [];
        public HashSet<int> LineBreakOffsets { get; } = [];
        /// <summary>Offset in <see cref="Text"/> where each source line's glyphs start.</summary>
        public List<(int Start, int PageIndex, string BlockId, string LineId, BlockKind Kind)> LineSpans { get; } = [];
        public string AsString() => Text.ToString();
    }

    /// <summary>A glyph that leaves ink on the page, and whether the text layer put whitespace before it.</summary>
    private readonly record struct LineGlyph(int Index, char Char, PageRect Rect, bool WhitespaceBefore);

    /// <summary>One source line with its word boundaries decided, ready to be written into the run.</summary>
    private sealed record PreparedLine(
        PageExtraction Page, ExtractedBlock Block, ExtractedLine Line, int LineIndex,
        float MedianHeight, float Baseline, List<LineGlyph> Glyphs, bool[] SpaceBefore);

    private ReadingRun BuildRun(
        ExtractionSnapshot snapshot,
        HashSet<(int PageIndex, string BlockId)> runningBlockSet,
        HashSet<(int PageIndex, string LineId)> runningLineSet,
        List<string> diag)
    {
        int droppedFiltered = 0, droppedDuplicateOcr = 0, droppedRunning = 0;
        var prepared = new List<PreparedLine>();

        // Pass one: for every narratable line, collect the glyphs and decide where the words end.
        foreach (var page in snapshot.Pages.OrderBy(p => p.PageIndex))
        {
            var charRects = BuildCharRectLookup(page);
            var nativeLines = page.Lines.Where(l => !l.FromOcr).ToList();
            bool trustTextLayer = TextLayerSpacesAgreeWithGeometry(page, charRects);

            foreach (var block in page.OrderedBlocks)
            {
                if (!_options.Filters.Allows(block.Kind)) { droppedFiltered++; continue; }
                if (runningBlockSet.Contains((page.PageIndex, block.BlockId))) { droppedRunning++; continue; }
                if (block.Kind is BlockKind.Figure or BlockKind.Chart or BlockKind.Decoration)
                {
                    // A figure has no text of its own. Its caption is a separate block and is
                    // filtered on its own merits; inventing a description here would not be faithful.
                    continue;
                }

                foreach (int lineIndex in block.LineIndices.OrderBy(i => i))
                {
                    if (lineIndex < 0 || lineIndex >= page.Lines.Count) continue;
                    var line = page.Lines[lineIndex];

                    // Running headers and footers are suppressed per line, because on a sparse page a
                    // model-free analyzer merges them into the body block and block-level suppression
                    // would either miss them or throw the body away with them.
                    if (runningLineSet.Contains((page.PageIndex, line.LineId))) { droppedRunning++; continue; }

                    // Mixed-content guard: an OCR line that sits on top of native text would be
                    // narrated twice. The native layer wins because it is exact.
                    if (line.FromOcr && nativeLines.Any(n => Overlaps(n.Rect, line.Rect, 0.5)))
                    {
                        droppedDuplicateOcr++;
                        continue;
                    }

                    var glyphs = MarkingGlyphs(page, charRects, line.Text);
                    if (glyphs.Count == 0) continue;
                    float median = MedianGlyphHeight(page, charRects, line.Text);
                    prepared.Add(new PreparedLine(page, block, line, lineIndex, median, line.Rect.Bottom, glyphs,
                        DecideSpaces(glyphs, median, line.FromOcr, trustTextLayer)));
                }
            }
        }

        // Between the passes: glyphs the text layer returned without a Unicode value are identified
        // from the whole document at once, because one occurrence proves nothing and fifty do.
        var ligatures = UnmappedGlyphResolver.Infer(prepared.SelectMany(TokensWithUnmappedGlyphs));
        if (ligatures.Count > 0)
            diag.Add($"Identified {ligatures.Count} ligature glyph(s) the PDF text layer left unmapped: " +
                     string.Join(", ", ligatures.Values.OrderBy(v => v, StringComparer.Ordinal)) + ".");

        // Pass two: write the run.
        var run = new ReadingRun();
        foreach (var p in prepared)
        {
            int lineStart = run.Text.Length;
            bool emittedAny = false, pendingSpace = false;
            int lastIndex = p.Glyphs[0].Index;

            for (int gi = 0; gi < p.Glyphs.Count; gi++)
            {
                var g = p.Glyphs[gi];
                if (gi > 0 && p.SpaceBefore[gi]) pendingSpace = true;

                string? expansion = null;
                if (IsLineEndHyphen(p, gi))
                {
                    // Restored as a real hyphen so dehyphenation can rejoin the word across the break.
                    expansion = "-";
                }
                else if (UnmappedGlyphResolver.IsUnmapped(g.Char))
                {
                    if (!(ligatures.TryGetValue(g.Char, out expansion) && SitsInsideAWord(p, gi)))
                    {
                        // Unidentified: usually a symbol from a mathematical font. It is not spoken,
                        // and it separates what stands on either side of it, as it does on the page.
                        pendingSpace = true;
                        continue;
                    }
                }

                if (pendingSpace && emittedAny)
                {
                    // A synthesised space: no box of its own, so it inherits the preceding glyph's
                    // source offset and contributes no highlight geometry.
                    run.Text.Append(' ');
                    run.Glyphs.Add(new RunGlyph(' ', default, lastIndex, p.Page.PageIndex,
                        p.Block.BlockId, p.Line.LineId, p.LineIndex, p.Block.Kind, p.Line.FromOcr, p.MedianHeight, p.Baseline));
                }
                pendingSpace = false;

                // A ligature becomes its letters. They share the glyph's box and source offset, so the
                // word still highlights as one piece and still maps back to one place in the PDF.
                foreach (char ch in expansion ?? g.Char.ToString())
                {
                    run.Text.Append(ch);
                    run.Glyphs.Add(new RunGlyph(ch, g.Rect, g.Index, p.Page.PageIndex, p.Block.BlockId, p.Line.LineId,
                        p.LineIndex, p.Block.Kind, p.Line.FromOcr, p.MedianHeight, p.Baseline));
                }
                emittedAny = true;
                lastIndex = g.Index;
            }
            if (!emittedAny) continue;

            run.LineSpans.Add((lineStart, p.Page.PageIndex, p.Block.BlockId, p.Line.LineId, p.Block.Kind));
            // Mark the break so dehyphenation only joins words across a real line boundary.
            run.LineBreakOffsets.Add(run.Text.Length);
            run.Text.Append('\n');
            run.Glyphs.Add(default); // placeholder keeps Text and Glyphs index-aligned
        }

        if (droppedFiltered > 0) diag.Add($"Skipped {droppedFiltered} block(s) excluded by the content filters.");
        if (droppedRunning > 0) diag.Add($"Skipped {droppedRunning} running header/footer/page-number element(s).");
        if (droppedDuplicateOcr > 0) diag.Add($"Dropped {droppedDuplicateOcr} OCR line(s) that overlapped native text, so nothing is narrated twice.");
        return run;
    }

    private static Dictionary<int, PageRect> BuildCharRectLookup(PageExtraction page)
    {
        var map = new Dictionary<int, PageRect>(page.Chars.Count);
        foreach (var c in page.Chars)
            if (!map.ContainsKey(c.Index)) map[c.Index] = c.Rect;
        return map;
    }

    // ---- word boundaries ---------------------------------------------------------------

    /// <summary>
    /// Share of a line's glyph pairs that may carry text-layer whitespace before that whitespace
    /// stops being believable. A layer with a space between most letters is the phantom-character
    /// case, not a line of one-letter words.
    /// </summary>
    public const float MaxWhitespacePairShare = 0.45f;

    /// <summary>
    /// Share of a page's text-layer spaces that must coincide with a visible gap for the layer's
    /// spacing to be trusted on that page.
    /// </summary>
    public const float MinWhitespaceAgreement = 0.75f;

    /// <summary>
    /// Without text-layer whitespace, how many times the geometric threshold a gap must reach to be
    /// taken as a word space on a page whose text layer is otherwise trusted.
    /// </summary>
    public const float UnmarkedGapMultiple = 2.5f;

    /// <summary>A backwards jump of this many glyph heights is a wrapped line, not kerning.</summary>
    public const float WrapJumpHeights = 2.0f;

    /// <summary>The line's glyphs that leave ink, in text order, noting text-layer whitespace between them.</summary>
    private static List<LineGlyph> MarkingGlyphs(PageExtraction page, Dictionary<int, PageRect> charRects, TextRange lineRange)
    {
        var glyphs = new List<LineGlyph>();
        bool whitespaceSeen = false;
        for (int ci = lineRange.Start; ci < lineRange.End && ci < page.Text.Length; ci++)
        {
            char ch = page.Text[ci];
            if (char.IsWhiteSpace(ch)) { whitespaceSeen = true; continue; }
            if (!charRects.TryGetValue(ci, out var rect) || rect.IsEmpty) continue;   // non-marking
            glyphs.Add(new LineGlyph(ci, ch, rect, glyphs.Count > 0 && whitespaceSeen));
            whitespaceSeen = false;
        }
        return glyphs;
    }

    /// <summary>The direction a line's glyphs advance in. Text is not always set left to right.</summary>
    private enum RunAxis { Right, Left, Down, Up }

    private static RunAxis DominantAxis(IReadOnlyList<LineGlyph> glyphs)
    {
        if (glyphs.Count < 3) return RunAxis.Right;
        var dx = new List<float>(glyphs.Count - 1);
        var dy = new List<float>(glyphs.Count - 1);
        for (int i = 1; i < glyphs.Count; i++)
        {
            dx.Add(glyphs[i].Rect.MidX - glyphs[i - 1].Rect.MidX);
            dy.Add(glyphs[i].Rect.MidY - glyphs[i - 1].Rect.MidY);
        }
        dx.Sort(); dy.Sort();
        // The median step, so the one large jump at a wrap does not outvote the whole line.
        float mx = dx[dx.Count / 2], my = dy[dy.Count / 2];
        if (Math.Abs(my) > Math.Abs(mx)) return my > 0 ? RunAxis.Down : RunAxis.Up;
        return mx < 0 ? RunAxis.Left : RunAxis.Right;
    }

    /// <summary>Where a glyph starts along the reading direction.</summary>
    private static float Leading(PageRect r, RunAxis axis) => axis switch
    {
        RunAxis.Left => -r.Right, RunAxis.Down => r.Top, RunAxis.Up => -r.Bottom, _ => r.Left,
    };

    /// <summary>Where a glyph ends along the reading direction.</summary>
    private static float Trailing(PageRect r, RunAxis axis) => axis switch
    {
        RunAxis.Left => -r.Left, RunAxis.Down => r.Bottom, RunAxis.Up => -r.Top, _ => r.Right,
    };

    /// <summary>Ink-to-ink gap before each glyph along the reading direction. Index 0 is unused.</summary>
    private static float[] GapsAlong(IReadOnlyList<LineGlyph> glyphs, RunAxis axis)
    {
        var gaps = new float[glyphs.Count];
        for (int i = 1; i < glyphs.Count; i++)
            gaps[i] = Leading(glyphs[i].Rect, axis) - Trailing(glyphs[i - 1].Rect, axis);
        return gaps;
    }

    /// <summary>Glyph size across the reading direction: height for horizontal text, width for rotated text.</summary>
    private static float CrossSize(IReadOnlyList<LineGlyph> glyphs, RunAxis axis, float medianHeight)
    {
        if (axis is RunAxis.Right or RunAxis.Left) return medianHeight;
        var widths = glyphs.Select(g => g.Rect.Width).Where(w => w > 0).OrderBy(w => w).ToList();
        return widths.Count == 0 ? medianHeight : widths[widths.Count / 2];
    }

    /// <summary>
    /// Decides, for each glyph, whether a word space precedes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources of evidence, neither sufficient alone. The text layer's own whitespace is usually
    /// right, because the PDF library places it from font metrics this code cannot see — but not
    /// always: one real producer embedded " \n" inside words, so a line read back as "Alp \nh \na
    /// o \nn..." (ADR-0005). Ink gaps cannot be faked, but a glyph box excludes side bearings, so
    /// gaps mislead in both directions: a tabular "1" has enough white around it to look like a
    /// space, and an "f" overhangs far enough to hide one.
    /// </para>
    /// <para>
    /// So geometry is used to check the text layer rather than to replace it. Where a page's
    /// text-layer spaces coincide with visible gaps, they are taken as the word boundaries and the
    /// gaps only confirm them. Where they do not, the page falls back to gaps alone. Measured on a
    /// 27-page typeset paper against an independent extractor's tokens: gaps alone scored F1 0.80
    /// (0.46 on tokens containing digits), this rule 0.85 (0.66); "2017" stopped being read as
    /// "201 7" and "of the" stopped being read as "ofthe".
    /// </para>
    /// <para>
    /// OCR text carries the recogniser's own spaces and character boxes that are estimates, not ink,
    /// so there the text is believed outright.
    /// </para>
    /// </remarks>
    private static bool[] DecideSpaces(List<LineGlyph> glyphs, float medianHeight, bool fromOcr, bool trustTextLayer)
    {
        var space = new bool[glyphs.Count];
        if (glyphs.Count < 2) return space;

        int marked = 0;
        for (int i = 1; i < glyphs.Count; i++) if (glyphs[i].WhitespaceBefore) marked++;

        if (fromOcr && marked > 0)
        {
            for (int i = 1; i < glyphs.Count; i++) space[i] = glyphs[i].WhitespaceBefore;
            return space;
        }

        var axis = DominantAxis(glyphs);
        float size = CrossSize(glyphs, axis, medianHeight);
        var gaps = GapsAlong(glyphs, axis);
        float threshold = SpaceGapThreshold(gaps, size);
        bool believeLayer = trustTextLayer && !fromOcr && marked < MaxWhitespacePairShare * (glyphs.Count - 1);

        for (int i = 1; i < glyphs.Count; i++)
        {
            float gap = gaps[i];
            // A merged line that wraps: the next glyph starts a whole line back from the last.
            bool wrapped = gap < -WrapJumpHeights * size;
            space[i] = wrapped || (believeLayer
                ? (glyphs[i].WhitespaceBefore && gap > 0) || gap > UnmarkedGapMultiple * threshold
                : gap > threshold);
        }
        return space;
    }

    /// <summary>
    /// Whether this page's text-layer whitespace falls where the glyphs actually part.
    /// </summary>
    private static bool TextLayerSpacesAgreeWithGeometry(PageExtraction page, Dictionary<int, PageRect> charRects)
    {
        int marked = 0, markedWithGap = 0, unmarked = 0;
        foreach (var line in page.Lines)
        {
            if (line.FromOcr) continue;
            var glyphs = MarkingGlyphs(page, charRects, line.Text);
            if (glyphs.Count < 2) continue;
            var axis = DominantAxis(glyphs);
            float size = CrossSize(glyphs, axis, MedianGlyphHeight(page, charRects, line.Text));
            var gaps = GapsAlong(glyphs, axis);
            float threshold = SpaceGapThreshold(gaps, size);
            for (int i = 1; i < glyphs.Count; i++)
            {
                if (!glyphs[i].WhitespaceBefore) { unmarked++; continue; }
                marked++;
                if (gaps[i] > 0.5f * threshold || gaps[i] < -WrapJumpHeights * size) markedWithGap++;
            }
        }
        return marked >= 5
            && markedWithGap >= MinWhitespaceAgreement * marked
            && marked < MaxWhitespacePairShare * (marked + unmarked);
    }

    /// <summary>The document's tokens that still contain an unmapped glyph, for <see cref="UnmappedGlyphResolver"/>.</summary>
    private static IEnumerable<string> TokensWithUnmappedGlyphs(PreparedLine line)
    {
        var token = new System.Text.StringBuilder();
        bool unmapped = false;
        for (int i = 0; i < line.Glyphs.Count; i++)
        {
            if (i > 0 && line.SpaceBefore[i])
            {
                if (unmapped) yield return token.ToString();
                token.Clear();
                unmapped = false;
            }
            if (IsLineEndHyphen(line, i)) { token.Append('-'); continue; }
            token.Append(line.Glyphs[i].Char);
            unmapped |= UnmappedGlyphResolver.IsUnmapped(line.Glyphs[i].Char);
        }
        if (unmapped) yield return token.ToString();
    }

    /// <summary>
    /// PDFium reports a hyphen that ends a line as U+0002 rather than as a hyphen.
    /// </summary>
    /// <remarks>
    /// The same value is, in some fonts, the character code of a ligature (the paper this was
    /// measured on encodes "fi" at 2), so position decides: last on its line and attached to a
    /// letter, it is the hyphen marker. Reading it as the ligature turned "time-" at a line end into
    /// "timefi"; dropping it, as before, left "vari ance" where the page has "variance".
    /// </remarks>
    private const char PdfiumLineEndHyphen = '\u0002';

    private static bool IsLineEndHyphen(PreparedLine line, int index)
        => line.Glyphs[index].Char == PdfiumLineEndHyphen
           && index == line.Glyphs.Count - 1
           && index > 0 && !line.SpaceBefore[index] && char.IsLetter(line.Glyphs[index - 1].Char);

    /// <summary>True when the glyph touches a letter of the same word on either side.</summary>
    private static bool SitsInsideAWord(PreparedLine line, int index)
    {
        bool before = index > 0 && !line.SpaceBefore[index] && char.IsLetter(line.Glyphs[index - 1].Char);
        bool after = index + 1 < line.Glyphs.Count && !line.SpaceBefore[index + 1] && char.IsLetter(line.Glyphs[index + 1].Char);
        return before || after;
    }

    /// <summary>
    /// The ink-to-ink gap above which two consecutive glyphs are taken to be separated by a word
    /// space when geometry is the only evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, not assumed. On a real 11 pt body line the gaps between letters inside a word topped
    /// out at 2.07 pt while every one of the five word boundaries was at least 3.88 pt — a clean
    /// 1.85× separation with nothing in between. A multiple of the line's own median gap sits inside
    /// that separation and adapts to face, size and tracking.
    /// </para>
    /// <para>
    /// Two things this must not be. Not the <i>advance</i> (left edge to left edge): that is
    /// confounded by glyph width, and on the same line a genuine space measured 7.4 pt while the
    /// non-space pair "mn" measured 10.0 pt, so no threshold separates them. Not a fraction of the
    /// type size either: a glyph box is tight around the ink and excludes side bearings, so the
    /// letter-to-letter gap inside a word routinely exceeds a fifth of an em.
    /// </para>
    /// <para>
    /// Missing a space joins two words, which costs a stumble in the audio. Inventing one splits a
    /// word, which puts a word in the narration that is not in the document. The multiplier is set
    /// towards the joining error for that reason.
    /// </para>
    /// </remarks>
    /// <param name="gaps">Gap before each glyph; index 0 is unused.</param>
    private static float SpaceGapThreshold(float[] gaps, float glyphSize)
    {
        if (gaps.Length < 4 || glyphSize <= 0f)
            return Math.Max(1.0f, glyphSize * SpaceGapFallbackFraction);

        var sorted = new float[gaps.Length - 1];
        Array.Copy(gaps, 1, sorted, 0, sorted.Length);
        Array.Sort(sorted);
        float median = sorted[sorted.Length / 2];

        // A floor keeps a face whose glyphs touch (median gap near zero) from splitting on kerning.
        return Math.Max(median * SpaceGapMultiple, glyphSize * SpaceGapMinFractionOfHeight);
    }

    private static float MedianGlyphHeight(PageExtraction page, Dictionary<int, PageRect> charRects, TextRange lineRange)
    {
        var heights = new List<float>();
        for (int i = lineRange.Start; i < lineRange.End; i++)
            if (charRects.TryGetValue(i, out var r) && r.Height > 0) heights.Add(r.Height);
        // A line with no glyph geometry at all still needs a scale for script-size cues; fall back to
        // the line's own height so the ratio test is meaningful rather than dividing by zero.
        if (heights.Count == 0)
        {
            var line = page.Lines.FirstOrDefault(l => l.Text == lineRange);
            return line is { } l && l.Rect.Height > 0 ? l.Rect.Height : 10f;
        }
        heights.Sort();
        return heights[heights.Count / 2];
    }

    private static bool Overlaps(PageRect a, PageRect b, double minFraction)
    {
        if (a.IsEmpty || b.IsEmpty) return false;
        float ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        float iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double inter = ix * iy;
        double smaller = Math.Min(a.Area, b.Area);
        return smaller > 0 && inter / smaller >= minFraction;
    }

    // ---- unit cutting ------------------------------------------------------------------

    private IReadOnlyList<ReadingUnit> CutUnits(ReadingRun run, ExtractionSnapshot snapshot)
    {
        string text = run.AsString();
        var units = new List<ReadingUnit>();
        int unitSeq = 0;

        // Classify the run into contiguous regions: display maths, inline maths, or prose.
        foreach (var region in ClassifyRegions(run, text, snapshot))
        {
            if (region.Kind is ReadingUnitKind.DisplayMath or ReadingUnitKind.InlineMath)
            {
                units.Add(MakeUnit(run, text, region.Range, region.Kind, ref unitSeq, snapshot));
                continue;
            }

            string prose = text.Substring(region.Range.Start, region.Range.Length);
            foreach (var sentence in SplitProse(prose, region.Range.Start, text))
            {
                var kind = ClassifyProse(run, sentence);
                foreach (var piece in EnforceMaxLength(sentence, text))
                    units.Add(MakeUnit(run, text, piece, kind, ref unitSeq, snapshot));
            }
        }
        return units;
    }

    private IReadOnlyList<TextRange> SplitProse(string prose, int offsetInRun, string runText)
    {
        var local = SentenceSplitter.Split(prose);
        return local.Select(r => new TextRange(r.Start + offsetInRun, r.Length)).ToList();
    }

    private IEnumerable<TextRange> EnforceMaxLength(TextRange range, string text)
    {
        if (range.Length <= _options.MaxUnitChars) { yield return range; yield break; }

        // Split at clause boundaries so a long sentence still narrates naturally, and so a highlight
        // at high magnification covers something the reader can take in.
        int cursor = range.Start;
        while (cursor < range.End)
        {
            int take = Math.Min(_options.MaxUnitChars, range.End - cursor);
            int cut = cursor + take;
            if (cut < range.End)
            {
                int clause = LastClauseBoundary(text, cursor, cut);
                if (clause > cursor) cut = clause + 1;
            }
            yield return TextRange.FromBounds(cursor, Math.Min(cut, range.End));
            cursor = Math.Min(cut, range.End);
        }
    }

    private static int LastClauseBoundary(string text, int from, int to)
    {
        for (int i = to - 1; i > from + (to - from) / 2; i--)
        {
            char c = text[i];
            if ((c is ',' or ';' or ':' or '—' || c == '-') && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                return i;
        }
        for (int i = to - 1; i > from; i--) if (char.IsWhiteSpace(text[i])) return i;
        return -1;
    }

    private static ReadingUnitKind ClassifyProse(ReadingRun run, TextRange range)
    {
        // The block role of the unit's first real glyph decides its kind. A unit never spans block
        // kinds, so one lookup is enough and a loop would only suggest otherwise.
        for (int i = range.Start; i < Math.Min(range.End, run.Glyphs.Count); i++)
        {
            var g = run.Glyphs[i];
            if (!g.IsReal) continue;
            return g.Kind switch
            {
                BlockKind.Title => ReadingUnitKind.Title,
                BlockKind.Heading => ReadingUnitKind.Heading,
                BlockKind.Caption => ReadingUnitKind.Caption,
                BlockKind.Footnote => ReadingUnitKind.Footnote,
                BlockKind.Reference => ReadingUnitKind.Reference,
                BlockKind.Algorithm => ReadingUnitKind.Algorithm,
                BlockKind.Table => ReadingUnitKind.Table,
                _ => ReadingUnitKind.Prose,
            };
        }
        return ReadingUnitKind.Prose;
    }

    private IEnumerable<(TextRange Range, ReadingUnitKind Kind)> ClassifyRegions(ReadingRun run, string text, ExtractionSnapshot snapshot)
    {
        var overrides = _options.Overrides ?? [];
        int cursor = 0;

        // Display maths blocks are whole regions: never sentence-split an equation.
        var displayRanges = new List<TextRange>();
        foreach (var span in run.LineSpans)
        {
            int end = NextSpanStart(run, span.Start);
            bool isDisplay = span.Kind == BlockKind.DisplayMath;
            if (!isDisplay && span.Kind is BlockKind.Unknown or BlockKind.Text)
            {
                // A model-free extraction gives every block the same role, so geometry decides: a
                // short block carrying operators and sub/superscripts is a display equation.
                var real = RealGlyphs(run, span.Start, end);
                var rects = real.Where(g => !g.Rect.IsEmpty).Select(g => g.Rect).ToList();
                var bbox = PageRect.UnionAll(rects);
                double pageHeight = snapshot.Page(span.PageIndex)?.PageHeight ?? 792;
                isDisplay = bbox is { } b && MathsRegionDetector.IsDisplayMathsBlock(
                    span.Kind, b, pageHeight, ToCues(real), _options.MathsOptions);
            }
            if (isDisplay) displayRanges.Add(TextRange.FromBounds(span.Start, end));
        }

        // Manual overrides win over both detection paths, and "restore prose" removes a detection.
        foreach (var o in overrides)
        {
            foreach (var span in run.LineSpans)
            {
                int end = NextSpanStart(run, span.Start);
                var r = TextRange.FromBounds(span.Start, end);
                var rects = RealGlyphs(run, span.Start, end)
                    .Where(g => g.PageIndex == o.PageIndex && !g.Rect.IsEmpty).Select(g => g.Rect).ToList();
                if (rects.Count == 0) continue;
                if (PageRect.UnionAll(rects) is not { } bbox) continue;
                if (!o.Contains(o.PageIndex, bbox)) continue;
                if (o.TreatAsMaths) displayRanges.Add(r);
                else displayRanges.RemoveAll(d => d == r);
            }
        }

        displayRanges = displayRanges.Distinct().OrderBy(r => r.Start).ToList();
        // Remove overlaps, keeping the earlier classification.
        var cleaned = new List<TextRange>();
        foreach (var r in displayRanges)
        {
            if (cleaned.Count > 0 && r.Start < cleaned[^1].End) continue;
            cleaned.Add(r);
        }

        foreach (var d in cleaned)
        {
            if (d.Start > cursor) foreach (var p in ProseAndInline(cursor, d.Start, run, text)) yield return p;
            yield return (d, ReadingUnitKind.DisplayMath);
            cursor = Math.Max(cursor, d.End);
        }
        if (cursor < text.Length)
            foreach (var p in ProseAndInline(cursor, text.Length, run, text)) yield return p;
    }

    /// <summary>The real glyphs (placeholders excluded) of a run range.</summary>
    private static List<RunGlyph> RealGlyphs(ReadingRun run, int from, int to)
    {
        var list = new List<RunGlyph>();
        for (int i = from; i < to && i < run.Glyphs.Count; i++)
            if (run.Glyphs[i].IsReal) list.Add(run.Glyphs[i]);
        return list;
    }

    private static List<GlyphCue> ToCues(List<RunGlyph> glyphs)
        => glyphs.Select(g => new GlyphCue(g.Character, g.Rect, g.LineMedianHeight, g.LineBaselineY)).ToList();

    private IEnumerable<(TextRange, ReadingUnitKind)> ProseAndInline(int from, int to, ReadingRun run, string text)
    {
        if (to <= from) yield break;
        var slice = text.Substring(from, to - from);
        var glyphs = GlyphsFor(text, run, from, to);
        IReadOnlyList<TextRange> inline = glyphs.Count == slice.Length
            ? MathsRegionDetector.DetectInline(slice, glyphs, _options.MathsOptions)
            : MathsRegionDetector.DetectInline(slice, null, _options.MathsOptions);

        // Apply manual overrides to inline detection too.
        var forcedMaths = new List<TextRange>();
        var forcedProse = new List<TextRange>();
        foreach (var o in _options.Overrides ?? [])
        {
            for (int i = from; i < to; i++)
            {
                if (i >= run.Glyphs.Count) break;
                var g = run.Glyphs[i];
                if (g.PageIndex != o.PageIndex || g.Rect.IsEmpty) continue;
                if (!o.Contains(o.PageIndex, g.Rect)) continue;
                (o.TreatAsMaths ? forcedMaths : forcedProse).Add(new TextRange(i - from, 1));
            }
        }

        // One sorted pass over the union of detected and forced ranges. Forced ranges arrive one
        // per glyph and may sit left of (or inside) a detected range; emitting them in a second
        // pass after the detected ones duplicated their text as out-of-order 1-char units.
        // Merging overlapping/adjacent ranges coalesces the per-glyph forced ranges, and the
        // cursor clip guarantees every character is emitted exactly once, in reading order.
        var mathsRanges = new List<TextRange>();
        foreach (var m in inline)
        {
            if (forcedProse.Any(p => p.Overlaps(m))) continue;
            mathsRanges.Add(m);
        }
        mathsRanges.AddRange(forcedMaths);
        mathsRanges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<TextRange>();
        foreach (var r in mathsRanges)
        {
            if (r.IsEmpty) continue;
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = TextRange.FromBounds(last.Start, Math.Max(last.End, r.End));
            }
            else merged.Add(r);
        }

        int cursor = 0;
        foreach (var m in merged)
        {
            int start = Math.Max(m.Start, cursor);
            if (m.End <= start) continue;
            if (start > cursor) yield return (new TextRange(from + cursor, start - cursor), ReadingUnitKind.Prose);
            yield return (new TextRange(from + start, m.End - start), ReadingUnitKind.InlineMath);
            cursor = m.End;
        }
        if (cursor < to - from) yield return (new TextRange(from + cursor, to - from - cursor), ReadingUnitKind.Prose);
    }

    private static int NextSpanStart(ReadingRun run, int spanStart)
    {
        for (int i = 0; i < run.LineSpans.Count; i++)
            if (run.LineSpans[i].Start == spanStart && i + 1 < run.LineSpans.Count) return run.LineSpans[i + 1].Start;
        return run.Text.Length;
    }

    private static List<GlyphCue> GlyphsFor(string text, ReadingRun run, int from, int to)
    {
        var list = new List<GlyphCue>(Math.Max(0, to - from));
        for (int i = from; i < to && i < run.Glyphs.Count; i++)
        {
            var g = run.Glyphs[i];
            char c = i < text.Length ? text[i] : ' ';
            list.Add(new GlyphCue(c, g.Rect, g.LineMedianHeight, g.LineBaselineY));
        }
        return list;
    }

    private ReadingUnit MakeUnit(ReadingRun run, string text, TextRange range, ReadingUnitKind kind, ref int seq, ExtractionSnapshot snapshot)
    {
        // Gather the source glyphs, skipping the newline placeholders that keep the lists aligned.
        var glyphs = new List<(RunGlyph Glyph, int RunIndex)>();
        for (int i = range.Start; i < range.End && i < run.Glyphs.Count; i++)
        {
            var g = run.Glyphs[i];
            if (!g.IsReal) continue;
            glyphs.Add((g, i));
        }

        var anchors = BuildAnchors(glyphs, snapshot, seq, kind);
        var (original, fragments, lineBreakOffsets) = ExtractOriginal(glyphs, anchors);

        var warnings = new List<string>();
        if (glyphs.Count > 0 && glyphs.All(x => x.Glyph.FromOcr))
            warnings.Add("Text came from OCR and is unverified.");
        else if (glyphs.Any(x => x.Glyph.FromOcr))
            warnings.Add("Part of this unit came from OCR and is unverified.");
        if (glyphs.Count > 0)
        {
            int firstPage = glyphs[0].Glyph.PageIndex;
            foreach (var w in snapshot.Page(firstPage)?.Coverage.Warnings ?? [])
                if (!warnings.Contains(w)) warnings.Add(w);
        }

        int ctxStart = Math.Max(0, range.Start - _options.ContextChars);
        int ctxEnd = Math.Min(text.Length, range.End + _options.ContextChars);

        seq++;
        return new ReadingUnit(
            Id: $"u{seq:D5}",
            Kind: kind,
            Anchors: anchors,
            OriginalText: original,
            Latex: null,
            ContextBefore: CleanContext(text[ctxStart..range.Start]),
            ContextAfter: CleanContext(text[range.End..ctxEnd]),
            ExtractionWarnings: warnings,
            PageIndex: glyphs.Count > 0 ? glyphs[0].Glyph.PageIndex : 0)
        {
            SourceFragments = fragments,
            LineBreakOffsets = lineBreakOffsets,
        };
    }

    private static string CleanContext(string s) => s.Replace('\n', ' ').Trim();

    /// <summary>
    /// Rebuilds the unit's text from its source glyphs and records, exactly, which anchor each
    /// character came from and where a space stands in for a line break. A line-break space belongs
    /// to the fragment that follows it, so no fragment is left pointing only at whitespace.
    /// </summary>
    private static (string Text, IReadOnlyList<UnitSourceFragment> Fragments, IReadOnlyList<int> LineBreakOffsets) ExtractOriginal(
        List<(RunGlyph Glyph, int RunIndex)> glyphs, IReadOnlyList<SourceAnchor> anchors)
    {
        if (glyphs.Count == 0) return ("", [], []);

        var sb = new System.Text.StringBuilder(glyphs.Count);
        var fragments = new List<UnitSourceFragment>();
        var breaks = new List<int>();
        (int PageIndex, int LineOrdinal, string? LineId)? lastLine = null;
        foreach (var (g, _) in glyphs)
        {
            var line = (g.PageIndex, g.LineOrdinal, g.LineId);
            if (lastLine is { } previous && previous != line)
            {
                breaks.Add(sb.Length);
                sb.Append(' ');
            }
            int offset = sb.Length;
            sb.Append(g.Character);
            // SourceIndex comes from extraction, not a guessed character count. Spaces and
            // line breaks remain in the text but need no highlight geometry of their own.
            var anchor = anchors.FirstOrDefault(a => a.PageIndex == g.PageIndex
                && a.SourceCharRange is { } r && r.Contains(g.SourceIndex));
            if (anchor is not null)
                fragments.Add(new UnitSourceFragment(new TextRange(offset, 1), anchor.AnchorId, g.PageIndex));
            lastLine = line;
        }
        string raw = sb.ToString();
        int trim = raw.Length - raw.TrimStart().Length;
        string text = raw.Trim();
        return (text,
            fragments.Where(f => f.OriginalRange.Start >= trim && f.OriginalRange.End <= trim + text.Length)
                .Select(f => f with { OriginalRange = new TextRange(f.OriginalRange.Start - trim, f.OriginalRange.Length) }).ToArray(),
            breaks.Where(i => i >= trim && i < trim + text.Length).Select(i => i - trim).ToArray());
    }

    private static IReadOnlyList<SourceAnchor> BuildAnchors(
        List<(RunGlyph Glyph, int RunIndex)> glyphs, ExtractionSnapshot snapshot, int seq, ReadingUnitKind kind)
    {
        var groups = new List<List<RunGlyph>>();
        bool maths = kind is ReadingUnitKind.InlineMath or ReadingUnitKind.DisplayMath;
        if (maths)
        {
            groups.AddRange(glyphs.GroupBy(x => x.Glyph.PageIndex)
                .Select(page => page.Select(x => x.Glyph).ToList()));
        }
        else
        {
            List<RunGlyph>? word = null;
            foreach (var (g, _) in glyphs)
            {
                if (char.IsWhiteSpace(g.Character)) { word = null; continue; }
                if (word is null || word[^1].PageIndex != g.PageIndex || word[^1].LineOrdinal != g.LineOrdinal
                    || word[^1].LineId != g.LineId)
                {
                    word = [];
                    groups.Add(word);
                }
                word.Add(g);
            }
        }
        var anchors = new List<SourceAnchor>();
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            var rects = group.GroupBy(g => (g.LineOrdinal, g.LineId))
                .Select(line => PageRect.UnionAll(line.Select(g => g.Rect).Where(r => !r.IsEmpty).ToArray()))
                .Where(r => r.HasValue).Select(r => r!.Value).ToArray();
            anchors.Add(new SourceAnchor(
                AnchorId: $"a{seq:D5}-{anchors.Count}",
                Extraction: snapshot.Identity,
                PageIndex: group[0].PageIndex,
                BlockId: group[0].BlockId ?? "",
                LineIds: group.Select(g => g.LineId).OfType<string>().Distinct().ToArray(),
                SourceCharRange: TextRange.FromBounds(group.Min(g => g.SourceIndex), group.Max(g => g.SourceIndex) + 1),
                Rects: rects,
                Frame: snapshot.Frame,
                Provenance: group.Any(g => g.FromOcr) ? ExtractionProvenance.Ocr : ExtractionProvenance.NativeText));
        }
        return anchors;
    }

}
