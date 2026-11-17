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
        var candidates = new List<(PageExtraction Page, ExtractedBlock Block, ExtractedLine? Line, string Normalised, bool InHeaderBand, bool InFooterBand)>();

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

                    candidates.Add((page, block, line, normalised, inHeaderBand, inFooterBand));
                    textCounts[normalised] = textCounts.GetValueOrDefault(normalised) + 1;
                }
            }
        }

        int pages = snapshot.Pages.Count;
        // A header printed on every page repeats once per page, but a cover page often differs, so the
        // threshold scales with document length rather than being an absolute count.
        int threshold = Math.Max(minPages, Math.Min(pages, (int)Math.Ceiling(pages * 0.5)));

        foreach (var c in candidates)
        {
            if (textCounts[c.Normalised] < threshold) continue;
            var kind = c.InHeaderBand ? RunningElementKind.RepeatedHeader : RunningElementKind.RepeatedFooter;
            found.Add(new RunningElement(c.Page.PageIndex, c.Block.BlockId, c.Line!.LineId, c.Normalised, kind));
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
/// Each unit's anchors hold one rectangle set per source line, never a union. A sentence spanning
/// two columns therefore highlights two rectangles, not one covering the gutter and both columns.
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

    private ReadingRun BuildRun(
        ExtractionSnapshot snapshot,
        HashSet<(int PageIndex, string BlockId)> runningBlockSet,
        HashSet<(int PageIndex, string LineId)> runningLineSet,
        List<string> diag)
    {
        var run = new ReadingRun();
        int droppedFiltered = 0, droppedDuplicateOcr = 0, droppedRunning = 0;

        foreach (var page in snapshot.Pages.OrderBy(p => p.PageIndex))
        {
            var charRects = BuildCharRectLookup(page);
            var nativeLines = page.Lines.Where(l => !l.FromOcr).ToList();

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

                    int lineStart = run.Text.Length;
                    float median = MedianGlyphHeight(page, charRects, line.Text);
                    float baseline = line.Rect.Bottom;

                    // Collect the line's *marking* glyphs, then decide where the word spaces go.
                    //
                    // The text layer cannot be used as a transcription. Measured on a real PDF, the
                    // producer had embedded " \n" pairs inside words, so a line whose text range read
                    // back as "Alp \nh \na o \nn..." stood for "Alpha one in the left column." Those
                    // phantom characters do carry CharBox entries — zero-area, exactly like a real
                    // space — so neither index filtering nor a whitespace test can tell them apart.
                    //
                    // Geometry can, but not from ink gaps: a glyph box is tight around the ink and
                    // excludes side bearings, so the gap between two letters of a word routinely
                    // exceeds a fifth of an em. Advances (left edge to left edge) are regular, so a
                    // space shows up as an advance well above the line's median advance.
                    var lineGlyphs = new List<(int Index, char Char, PageRect Rect)>();
                    for (int ci = line.Text.Start; ci < line.Text.End && ci < page.Text.Length; ci++)
                    {
                        if (!charRects.TryGetValue(ci, out var rect)) continue;
                        if (rect.IsEmpty) continue;          // whitespace and other non-marking glyphs
                        char ch = page.Text[ci];
                        if (char.IsWhiteSpace(ch) || char.IsControl(ch)) continue;
                        lineGlyphs.Add((ci, ch, rect));
                    }
                    if (lineGlyphs.Count == 0) continue;

                    float spaceGap = SpaceGapThreshold(lineGlyphs, median);
                    bool emittedAny = false;

                    for (int gi = 0; gi < lineGlyphs.Count; gi++)
                    {
                        var (ci, ch, rect) = lineGlyphs[gi];
                        // Ink-to-ink gap, not advance: advance includes the previous glyph's own
                        // width, which varies far more than the space it is being compared against.
                        if (gi > 0 && rect.Left - lineGlyphs[gi - 1].Rect.Right > spaceGap)
                        {
                            // A synthesised space: no box of its own, so it inherits the preceding
                            // glyph's source offset and contributes no highlight geometry.
                            run.Text.Append(' ');
                            run.Glyphs.Add(new RunGlyph(' ', default, lineGlyphs[gi - 1].Index, page.PageIndex,
                                block.BlockId, line.LineId, lineIndex, block.Kind, line.FromOcr, median, baseline));
                        }

                        run.Text.Append(ch);
                        run.Glyphs.Add(new RunGlyph(ch, rect, ci, page.PageIndex, block.BlockId, line.LineId,
                            lineIndex, block.Kind, line.FromOcr, median, baseline));
                        emittedAny = true;
                    }
                    if (!emittedAny) continue;

                    run.LineSpans.Add((lineStart, page.PageIndex, block.BlockId, line.LineId, block.Kind));
                    // Mark the break so dehyphenation only joins words across a real line boundary.
                    run.LineBreakOffsets.Add(run.Text.Length);
                    run.Text.Append('\n');
                    run.Glyphs.Add(default); // placeholder keeps Text and Glyphs index-aligned
                }
            }
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

    /// <summary>
    /// The ink-to-ink gap above which two consecutive marking glyphs are separated by a word space.
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
    private static float SpaceGapThreshold(IReadOnlyList<(int Index, char Char, PageRect Rect)> glyphs, float medianGlyphHeight)
    {
        if (glyphs.Count < 4 || medianGlyphHeight <= 0f)
            return Math.Max(1.0f, medianGlyphHeight * SpaceGapFallbackFraction);

        var gaps = new List<float>(glyphs.Count - 1);
        for (int i = 1; i < glyphs.Count; i++)
            gaps.Add(glyphs[i].Rect.Left - glyphs[i - 1].Rect.Right);
        gaps.Sort();
        float median = gaps[gaps.Count / 2];

        // A floor keeps a face whose glyphs touch (median gap near zero) from splitting on kerning.
        return Math.Max(median * SpaceGapMultiple, medianGlyphHeight * SpaceGapMinFractionOfHeight);
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

        int cursor = 0;
        foreach (var m in inline.OrderBy(r => r.Start))
        {
            if (forcedProse.Any(p => p.Overlaps(m))) continue;
            if (m.Start > cursor) yield return (new TextRange(from + cursor, m.Start - cursor), ReadingUnitKind.Prose);
            yield return (new TextRange(from + m.Start, m.Length), ReadingUnitKind.InlineMath);
            cursor = m.End;
        }
        foreach (var f in forcedMaths)
        {
            if (inline.Any(m => m.Overlaps(f))) continue;
            if (f.Start > cursor) yield return (new TextRange(from + cursor, f.Start - cursor), ReadingUnitKind.Prose);
            yield return (new TextRange(from + f.Start, f.Length), ReadingUnitKind.InlineMath);
            cursor = Math.Max(cursor, f.End);
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

        var anchors = BuildAnchors(glyphs, run, text, range, snapshot, seq);
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

        var anchorByPage = anchors.ToDictionary(a => a.PageIndex, a => a.AnchorId);
        var sb = new System.Text.StringBuilder(glyphs.Count);
        var fragments = new List<UnitSourceFragment>();
        var breaks = new List<int>();

        int fragmentStart = 0;
        string? currentAnchor = null;
        int? lastLine = null;

        foreach (var (g, _) in glyphs)
        {
            string anchor = anchorByPage.GetValueOrDefault(g.PageIndex) ?? "";
            bool lineChanged = lastLine is { } ll && ll != g.LineOrdinal;
            if (lineChanged)
            {
                breaks.Add(sb.Length);
                sb.Append(' ');
            }

            // Close the previous fragment when the anchor changes. The inserted space is attributed
            // to the fragment that follows it.
            if (currentAnchor is { } prev && prev != anchor && sb.Length > fragmentStart)
            {
                int end = lineChanged ? sb.Length - 1 : sb.Length;
                if (end > fragmentStart) fragments.Add(new UnitSourceFragment(TextRange.FromBounds(fragmentStart, end), prev, PageOf(anchors, prev)));
                fragmentStart = end;
            }
            else if (currentAnchor is null || lineChanged)
            {
                if (currentAnchor is not null && sb.Length > fragmentStart)
                    fragments.Add(new UnitSourceFragment(TextRange.FromBounds(fragmentStart, sb.Length), currentAnchor, PageOf(anchors, currentAnchor)));
                fragmentStart = lineChanged ? Math.Max(0, sb.Length - 1) : sb.Length;
            }

            sb.Append(g.Character);
            currentAnchor = anchor;
            lastLine = g.LineOrdinal;
        }

        if (currentAnchor is { } last && sb.Length > fragmentStart)
            fragments.Add(new UnitSourceFragment(TextRange.FromBounds(fragmentStart, sb.Length), last, PageOf(anchors, last)));

        return (sb.ToString().Trim(), fragments, breaks);
    }

    private static int PageOf(IReadOnlyList<SourceAnchor> anchors, string anchorId)
        => anchors.FirstOrDefault(a => a.AnchorId == anchorId)?.PageIndex ?? 0;

    private IReadOnlyList<SourceAnchor> BuildAnchors(
        List<(RunGlyph Glyph, int RunIndex)> glyphs, ReadingRun run, string text, TextRange range,
        ExtractionSnapshot snapshot, int seq)
    {
        if (glyphs.Count == 0) return [];

        // Group by page, then by line, so each line contributes its own tight rectangle. A unit that
        // spans lines, columns or pages therefore keeps every disjoint region.
        var anchors = new List<SourceAnchor>();
        int anchorSeq = 0;
        foreach (var pageGroup in glyphs.GroupBy(x => x.Glyph.PageIndex).OrderBy(x => x.Key))
        {
            var page = snapshot.Page(pageGroup.Key);
            var rects = new List<PageRect>();
            var lineIds = new List<string>();
            string? blockId = null;

            foreach (var lineGroup in pageGroup.GroupBy(x => (x.Glyph.LineOrdinal, x.Glyph.LineId)).OrderBy(x => x.Key.LineOrdinal))
            {
                var lineRects = lineGroup.Select(x => x.Glyph.Rect).Where(r => !r.IsEmpty).ToList();
                if (lineRects.Count > 0)
                {
                    // One tight rectangle per source line. Lines are unioned only within a line, so a
                    // unit spanning two columns keeps two rectangles and never covers the gutter.
                    var tight = lineRects[0];
                    for (int i = 1; i < lineRects.Count; i++) tight = tight.Union(lineRects[i]);
                    rects.Add(tight);
                }
                if (lineGroup.Key.LineId is { } lid && !lineIds.Contains(lid)) lineIds.Add(lid);
                blockId ??= lineGroup.First().Glyph.BlockId;
            }

            // Source character range within the page's own text, taken from the glyphs themselves.
            // Reconstructing it by counting positions would be wrong: a text layer carries
            // characters that have no glyph, so offsets in it are not contiguous.
            var pageChars = pageGroup.Select(x => x.Glyph.SourceIndex).ToList();
            TextRange? sourceRange = pageChars.Count > 0
                ? TextRange.FromBounds(pageChars.Min(), pageChars.Max() + 1)
                : null;

            if (rects.Count == 0 && sourceRange is null) continue;

            anchors.Add(new SourceAnchor(
                AnchorId: $"a{seq:D5}-{anchorSeq++}",
                Extraction: snapshot.Identity,
                PageIndex: pageGroup.Key,
                BlockId: blockId ?? "",
                LineIds: lineIds,
                SourceCharRange: sourceRange,
                Rects: rects,
                Frame: snapshot.Frame,
                Provenance: pageGroup.Any(x => x.Glyph.FromOcr)
                    ? ExtractionProvenance.Ocr
                    : ExtractionProvenance.NativeText));
        }
        return anchors;
    }

}
