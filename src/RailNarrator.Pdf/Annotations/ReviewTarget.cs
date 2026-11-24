using RailNarrator.Domain;
using RailReader.Core.Models;

namespace RailNarrator.Pdf.Annotations;

/// <summary>Where on one page a <see cref="ReviewTarget"/> is.</summary>
/// <param name="Rects">The boxes to highlight, one per line fragment, in page points.</param>
/// <param name="Ranges">The page's text the boxes cover; empty for content with no text behind it.</param>
public sealed record ReviewTargetPage(int PageIndex, IReadOnlyList<HighlightRect> Rects, IReadOnlyList<LinkedRange> Ranges);

/// <summary>
/// What was just read, located on the page: the thing a note or highlight made while listening is
/// put on (ADR-0014).
/// </summary>
/// <param name="SegmentId">The passage; null for text marked by hand that no passage reads (a running head, say).</param>
/// <param name="UnitId">The sentence, or null when the target is the whole passage.</param>
/// <param name="Pages">Per page it touches, in reading order.</param>
/// <param name="QuotedText">The document's own text for it.</param>
/// <param name="ExtractionRevision">The extraction the ranges index into.</param>
public sealed record ReviewTarget(
    string? SegmentId,
    string? UnitId,
    IReadOnlyList<ReviewTargetPage> Pages,
    string QuotedText,
    int ExtractionRevision)
{
    /// <summary>
    /// A unit's text as a reader would quote it: a word hyphenated at the end of a line joined again,
    /// by the planner's own rule (<see cref="TextNormaliser.Dehyphenate"/>, at the unit's recorded
    /// line breaks only, so "state-of-the-art" keeps its hyphens).
    /// </summary>
    public static string QuoteOf(ReadingUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return TextNormaliser.Dehyphenate(unit.OriginalText, unit.LineBreakOffsets.ToHashSet()).Text.Trim();
    }

    /// <summary>
    /// A range of one page's text marked by hand (a drag in annotate mode): its boxes, its quote,
    /// and the passage that reads it, if one does, so the note can be read from.
    /// </summary>
    /// <returns>Null when the range has no glyphs.</returns>
    public static ReviewTarget? ForRange(NarrationPlan? plan, ExtractionSnapshot snapshot, PageTextIndex page, TextRange range,
        IReadOnlyDictionary<char, string>? ligatures = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(page);
        var rects = page.Rects(range);
        if (rects.Count == 0) return null;
        string text = page.Text.Text;
        int start = Math.Clamp(range.Start, 0, text.Length);
        int end = Math.Clamp(range.End, start, text.Length);
        string quote = PageQuote.Of(text, start, end, ligatures ?? new Dictionary<char, string>());

        string? segmentId = null, unitId = null;
        if (plan is not null)
        {
            var unit = plan.Units.FirstOrDefault(u => u.Anchors.Any(a => a.PageIndex == page.PageIndex
                && a.SourceCharRange is { } r && r.Start < end && start < r.End));
            if (unit is not null)
            {
                unitId = unit.Id;
                segmentId = plan.Segments.FirstOrDefault(s => s.ReadingUnitIds.Contains(unit.Id))?.Id;
            }
        }
        return new ReviewTarget(segmentId, unitId,
            [new ReviewTargetPage(page.PageIndex, rects, [new LinkedRange(page.PageIndex, start, end - start)])],
            quote, snapshot.Identity.ExtractionRevision) { ByHand = true };
    }

    /// <summary>Marked by hand over a range of text, not on what was just read: never the same mark as one made by N or H.</summary>
    public bool ByHand { get; init; }

    /// <summary>The page the target starts on.</summary>
    public int FirstPage => Pages[0].PageIndex;

    /// <summary>
    /// Finds the sentence (or, with <paramref name="wholePassage"/>, the passage) that a cue was
    /// reading.
    /// </summary>
    /// <remarks>
    /// The sentence is the passage's reading unit that holds most of the cue's anchors; a passage of
    /// one sentence is that sentence. With no cue to go by (nothing has been heard yet), a passage
    /// of several sentences is taken whole rather than guessing which one was meant.
    /// Boxes come from the extraction's own character boxes for the unit's source ranges
    /// (<see cref="PageTextIndex"/>). An anchor with no characters behind it (an equation recovered
    /// from pixels), or one from another extraction than <paramref name="snapshot"/>, is boxed by
    /// its own disjoint rectangles; never by their union, which covers text between them.
    /// </remarks>
    /// <returns>Null when the passage is not in the plan or has nothing on any page.</returns>
    public static ReviewTarget? Find(
        NarrationPlan plan,
        ExtractionSnapshot snapshot,
        PageTextCache pageText,
        string segmentId,
        IReadOnlyList<string>? cueAnchorIds,
        bool wholePassage)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pageText);
        if (plan.Segment(segmentId) is not { } segment) return null;
        var units = segment.ReadingUnitIds.Select(plan.Unit).OfType<ReadingUnit>().ToList();
        if (units.Count == 0) return null;

        ReadingUnit? sentence = null;
        if (!wholePassage)
        {
            if (units.Count == 1) sentence = units[0];
            else if (cueAnchorIds is { Count: > 0 })
            {
                var cue = cueAnchorIds.ToHashSet(StringComparer.Ordinal);
                var best = units.Select(u => (Unit: u, Hits: u.AnchorIds.Count(cue.Contains)))
                    .Where(x => x.Hits > 0)
                    .OrderByDescending(x => x.Hits)
                    .FirstOrDefault();
                sentence = best.Unit;
            }
        }
        var chosen = sentence is null ? units : [sentence];

        var byPage = new SortedDictionary<int, (List<HighlightRect> Rects, List<LinkedRange> Ranges)>();
        foreach (var anchor in chosen.SelectMany(u => u.Anchors))
        {
            if (!byPage.TryGetValue(anchor.PageIndex, out var page))
                byPage[anchor.PageIndex] = page = ([], []);

            List<HighlightRect>? boxes = null;
            if (anchor.SourceCharRange is { Length: > 0 } range && anchor.BelongsTo(snapshot.Identity)
                && pageText.For(anchor.PageIndex) is { } index)
            {
                boxes = index.Rects(range);
                if (boxes.Count > 0) page.Ranges.Add(new LinkedRange(anchor.PageIndex, range.Start, range.Length));
            }
            if (boxes is not { Count: > 0 })
                boxes = anchor.Rects.Where(r => !r.IsEmpty)
                    .Select(r => new HighlightRect(r.Left, r.Top, r.Width, r.Height)).ToList();
            page.Rects.AddRange(boxes);
        }

        var pages = byPage.Where(p => p.Value.Rects.Count > 0)
            .Select(p => new ReviewTargetPage(p.Key, p.Value.Rects, p.Value.Ranges)).ToList();
        if (pages.Count == 0) return null;
        string quoted = string.Join(" ", chosen.Select(QuoteOf).Where(t => t.Length > 0));
        return new ReviewTarget(segment.Id, sentence?.Id, pages, quoted, snapshot.Identity.ExtractionRevision);
    }
}

/// <summary>Each page's <see cref="PageTextIndex"/>, built when first asked for. Used on one thread.</summary>
public sealed class PageTextCache
{
    private readonly ExtractionSnapshot _snapshot;
    private readonly Dictionary<int, PageTextIndex> _pages = [];

    public PageTextCache(ExtractionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
    }

    /// <summary>The document's unmapped ligatures, worked out when first needed (<see cref="PageQuote.Ligatures"/>).</summary>
    public IReadOnlyDictionary<char, string> Ligatures => _ligatures ??= PageQuote.Ligatures(_snapshot);

    private IReadOnlyDictionary<char, string>? _ligatures;

    public PageTextIndex? For(int pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var index)) return index;
        if (_snapshot.Page(pageIndex) is not { } page) return null;
        return _pages[pageIndex] = PageTextIndex.Build(page);
    }
}

/// <summary>
/// A page's raw text as a reader would quote it, by the rules reading units are built with
/// (<c>ReadingUnitBuilder</c>): PDFium's line-end hyphen marker restored and the word joined across
/// the break, a ligature glyph the document has identified written as its letters, and a glyph no
/// one could identify left out, never guessed.
/// </summary>
public static class PageQuote
{
    /// <summary>PDFium reports a hyphen that ends a line as U+0002 (the same value is a ligature in some fonts; position decides).</summary>
    private const char LineEndHyphen = '\u0002';

    public static string Of(string text, int start, int end, IReadOnlyDictionary<char, string> ligatures)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ligatures);
        var sb = new System.Text.StringBuilder(end - start);
        var breaks = new HashSet<int>();
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (c == '\n') { breaks.Add(sb.Length); sb.Append('\n'); continue; }
            if (!IsUnmapped(c)) { sb.Append(c); continue; }
            if (IsLineEndHyphen(text, i)) sb.Append('-');
            else if (ligatures.TryGetValue(c, out var letters) && InsideAWord(text, i)) sb.Append(letters);
            else sb.Append(' ');
        }
        string joined = TextNormaliser.Dehyphenate(sb.ToString(), breaks).Text;
        return string.Join(' ', joined.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The words under a text markup's boxes (an annotation the PDF carries, which has no link to
    /// say what it is on): each box's characters, those whose horizontal centre is within the box
    /// and whose own box (taken 2 points lower, for an underline drawn as a strip at the baseline)
    /// holds the box's vertical centre, quoted as
    /// <see cref="Of"/> quotes; boxes in order, joined by a space. Empty when no glyph is under them.
    /// </summary>
    /// <summary>How far below a glyph's box an underline or squiggle drawn at the baseline may sit, in points.</summary>
    private const float BaselineStrip = 2f;

    public static string UnderRects(PageTextIndex page, IEnumerable<RailReader.Core.Models.HighlightRect> rects,
        IReadOnlyDictionary<char, string> ligatures)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(rects);
        var boxes = page.Text.CharBoxes;
        var parts = new List<string>();
        foreach (var r in rects)
        {
            int first = -1, last = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (b.Left == 0 && b.Right == 0 && b.Top == 0 && b.Bottom == 0) continue;
                float cx = (b.Left + b.Right) / 2, ry = r.Y + r.H / 2;
                if (cx < r.X || cx > r.X + r.W || ry < b.Top || ry > b.Bottom + BaselineStrip) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first >= 0) parts.Add(Of(page.Text.Text, first, last + 1, ligatures));
        }
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    /// <summary>
    /// The ligatures a document's text layer left unmapped, worked out from all its pages at once
    /// (<see cref="UnmappedGlyphResolver.Infer"/>), as reading units are.
    /// </summary>
    public static IReadOnlyDictionary<char, string> Ligatures(ExtractionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return UnmappedGlyphResolver.Infer(snapshot.Pages.SelectMany(p => Tokens(p.Text)));
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var token = new System.Text.StringBuilder();
        bool unmapped = false;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || char.IsWhiteSpace(text[i]))
            {
                if (unmapped) yield return token.ToString();
                token.Clear();
                unmapped = false;
                continue;
            }
            if (IsLineEndHyphen(text, i)) { token.Append('-'); continue; }
            token.Append(text[i]);
            unmapped |= IsUnmapped(text[i]);
        }
    }

    private static bool IsUnmapped(char c) => UnmappedGlyphResolver.IsUnmapped(c) && !char.IsWhiteSpace(c);

    private static bool IsLineEndHyphen(string text, int i)
        => text[i] == LineEndHyphen && i > 0 && char.IsLetter(text[i - 1])
           && (i + 1 == text.Length || text[i + 1] is '\n' or '\r');

    private static bool InsideAWord(string text, int i)
        => (i > 0 && char.IsLetter(text[i - 1])) || (i + 1 < text.Length && char.IsLetter(text[i + 1]));
}
