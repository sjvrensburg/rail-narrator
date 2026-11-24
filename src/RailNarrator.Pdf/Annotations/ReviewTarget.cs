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
/// <param name="SegmentId">The passage.</param>
/// <param name="UnitId">The sentence, or null when the target is the whole passage.</param>
/// <param name="Pages">Per page it touches, in reading order.</param>
/// <param name="QuotedText">The document's own text for it.</param>
/// <param name="ExtractionRevision">The extraction the ranges index into.</param>
public sealed record ReviewTarget(
    string SegmentId,
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

    public PageTextIndex? For(int pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var index)) return index;
        if (_snapshot.Page(pageIndex) is not { } page) return null;
        return _pages[pageIndex] = PageTextIndex.Build(page);
    }
}
