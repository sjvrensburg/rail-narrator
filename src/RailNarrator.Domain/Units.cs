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

/// <summary>One block identified as a running header, footer or page number.</summary>
public sealed record RunningElement(int PageIndex, string BlockId, string NormalisedText, RunningElementKind Kind);

/// <summary>Why a block was classified as running content.</summary>
public enum RunningElementKind { RepeatedHeader, RepeatedFooter, PageNumber }

/// <summary>
/// Finds the repetitive margin content that should not be narrated.
/// </summary>
/// <remarks>
/// <para>
/// Two inspectable rules, not a learned classifier, so a reader can see why something was dropped
/// and turn it back on. A block counts as running when either its normalised text repeats across
/// enough pages in a margin band, or its text is nothing but a page number.
/// </para>
/// <para>
/// Only <see cref="BlockKind.Header"/>, <see cref="BlockKind.Footer"/> and
/// <see cref="BlockKind.PageNumber"/> blocks are candidates when the layout model supplied roles.
/// With a model-free extraction every block is plain text, so the geometric band is the only
/// evidence — and the band is deliberately narrow to avoid eating a real first or last paragraph.
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
        var candidates = new List<(PageExtraction Page, ExtractedBlock Block, string Normalised, bool InHeaderBand, bool InFooterBand)>();

        foreach (var page in snapshot.Pages)
        {
            if (page.PageHeight <= 0) continue;
            double headerLimit = page.PageHeight * HeaderBand;
            double footerLimit = page.PageHeight * (1 - FooterBand);

            foreach (var block in page.Blocks)
            {
                bool roleSaysRunning = block.Kind is BlockKind.Header or BlockKind.Footer or BlockKind.PageNumber;
                bool inHeaderBand = block.BBox.Top >= 0 && block.BBox.Bottom <= headerLimit;
                bool inFooterBand = block.BBox.Top >= footerLimit && block.BBox.Bottom <= page.PageHeight;
                if (!roleSaysRunning && !(inHeaderBand || inFooterBand)) continue;

                string text = page.TextOf(BlockTextRange(page, block)) ?? "";
                string normalised = NormaliseForComparison(text);
                if (normalised.Length == 0) continue;

                // A page number is a number, possibly with decoration. Never narrated by default.
                if (IsPageNumberLike(normalised, page.PageIndex, snapshot.PageCount))
                {
                    found.Add(new RunningElement(page.PageIndex, block.BlockId, normalised, RunningElementKind.PageNumber));
                    continue;
                }

                candidates.Add((page, block, normalised, inHeaderBand || block.Kind == BlockKind.Header, inFooterBand || block.Kind == BlockKind.Footer));
                textCounts[normalised] = textCounts.GetValueOrDefault(normalised) + 1;
            }
        }

        int pages = snapshot.Pages.Count;
        // A header printed on every page repeats (pages - 1) times at most if page 1 differs, so the
        // threshold scales with document length rather than being an absolute count.
        int threshold = Math.Max(minPages, Math.Min(pages, (int)Math.Ceiling(pages * 0.5)));

        foreach (var c in candidates)
        {
            if (textCounts[c.Normalised] < threshold) continue;
            var kind = c.InHeaderBand ? RunningElementKind.RepeatedHeader
                     : c.InFooterBand ? RunningElementKind.RepeatedFooter
                     : RunningElementKind.RepeatedHeader;
            found.Add(new RunningElement(c.Page.PageIndex, c.Block.BlockId, c.Normalised, kind));
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
        var runningSet = running.Select(r => (r.PageIndex, r.BlockId)).ToHashSet();
        if (running.Count > 0)
            diag.Add($"Suppressed {running.Count} running header/footer/page-number block(s) across {running.Select(r => r.PageIndex).Distinct().Count()} page(s).");

        var run = BuildRun(snapshot, runningSet, diag);
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
        public RunGlyph(char character, PageRect rect, int pageIndex, string blockId, string lineId, int lineOrdinal,
                        BlockKind kind, bool fromOcr, float lineMedianHeight, float lineBaselineY)
        {
            Character = character; Rect = rect; PageIndex = pageIndex; BlockId = blockId; LineId = lineId;
            LineOrdinal = lineOrdinal; Kind = kind; FromOcr = fromOcr;
            LineMedianHeight = lineMedianHeight; LineBaselineY = lineBaselineY;
            IsReal = true;
        }
        public char Character { get; }
        public PageRect Rect { get; }
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

    private ReadingRun BuildRun(ExtractionSnapshot snapshot, HashSet<(int PageIndex, string BlockId)> runningSet, List<string> diag)
    {
        var run = new ReadingRun();
        int droppedFiltered = 0, droppedDuplicateOcr = 0;

        foreach (var page in snapshot.Pages.OrderBy(p => p.PageIndex))
        {
            var charRects = BuildCharRectLookup(page);
            var nativeLines = page.Lines.Where(l => !l.FromOcr).ToList();

            foreach (var block in page.OrderedBlocks)
            {
                if (!_options.Filters.Allows(block.Kind)) { droppedFiltered++; continue; }
                if (runningSet.Contains((page.PageIndex, block.BlockId))) continue;
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

                    // Mixed-content guard: an OCR line that sits on top of native text would be
                    // narrated twice. The native layer wins because it is exact.
                    if (line.FromOcr && nativeLines.Any(n => Overlaps(n.Rect, line.Rect, 0.5)))
                    {
                        droppedDuplicateOcr++;
                        continue;
                    }

                    int lineStart = run.Text.Length;
                    bool emittedAny = false;
                    float median = MedianGlyphHeight(page, charRects, line.Text);
                    float baseline = line.Rect.Bottom;

                    for (int ci = line.Text.Start; ci < line.Text.End && ci < page.Text.Length; ci++)
                    {
                        char c = page.Text[ci];
                        var rect = charRects.TryGetValue(ci, out var r) ? r : default;
                        run.Text.Append(c);
                        run.Glyphs.Add(new RunGlyph(c, rect, page.PageIndex, block.BlockId, line.LineId, lineIndex,
                            block.Kind, line.FromOcr, median, baseline));
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

            // Source character range within the page's own text.
            var pageChars = pageGroup
                .Select(x => CharIndexInPage(page, x.Glyph, x.RunIndex, run))
                .Where(i => i >= 0).ToList();
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

    /// <summary>
    /// Recovers a glyph's offset in its page's extracted text. The run interleaves newline
    /// placeholders, so the offset is counted from the line span the glyph belongs to rather than
    /// from the run offset.
    /// </summary>
    private static int CharIndexInPage(PageExtraction? page, RunGlyph glyph, int runIndex, ReadingRun run)
    {
        if (page is null) return -1;
        // Find the line span containing runIndex; the character's page offset is that line's start
        // plus the glyph's position within the span.
        (int Start, int PageIndex, string BlockId, string LineId, BlockKind Kind) best = default;
        bool found = false;
        foreach (var span in run.LineSpans)
        {
            if (span.PageIndex != glyph.PageIndex || span.LineId != glyph.LineId) continue;
            best = span; found = true; break;
        }
        if (!found) return -1;
        if (glyph.LineId is not { } lineId) return -1;

        var line = page.FindLine(lineId);
        if (line is null) return -1;

        // Count non-placeholder glyphs from the span start to this one.
        int offsetWithinLine = 0;
        for (int i = best.Start; i < runIndex && i < run.Glyphs.Count; i++)
        {
            var g = run.Glyphs[i];
            if (g.LineId == glyph.LineId && g.PageIndex == glyph.PageIndex && g.BlockId == glyph.BlockId) offsetWithinLine++;
        }
        int index = line.Text.Start + offsetWithinLine;
        return index < line.Text.End ? index : -1;
    }
}
