namespace RailNarrator.Domain;

/// <summary>
/// Where a passage is on the page, and the passage at a saved place in whatever plan is loaded.
/// </summary>
/// <remarks>
/// A passage id is its place in one plan, so a position saved as an id alone pointed after a new
/// plan (a version of the planner that skips or joins differently, another layout model) at
/// whichever passage now had that number, or at the start when there were fewer. A place on the
/// page, a page and an offset into its text, is the same in every plan of the same document.
/// </remarks>
public static class ResumePoint
{
    /// <summary>
    /// What a passage id is relative to: the plan's id and the fingerprint of its inputs. The id
    /// alone stays the same when only the filters change (references or captions read or not),
    /// and the passages after the change then have other numbers.
    /// </summary>
    public static string PlanKey(NarrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"{plan.Header.PlanId}:{plan.Header.InputFingerprint}";
    }

    /// <summary>
    /// The page and the offset into its text where <paramref name="segmentId"/> starts: its first
    /// source anchor with text behind it, else the page of its first anchor with no offset. Null
    /// when the plan has no such passage, or the passage has no anchor.
    /// </summary>
    public static (int Page, int? Offset)? Locate(NarrationPlan plan, string segmentId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Segment(segmentId) is not { } segment) return null;
        var anchors = AnchorsOf(plan.Unit, segment);
        if (anchors.FirstOrDefault(a => a.SourceCharRange is not null) is { } text)
            return (text.PageIndex, text.SourceCharRange!.Value.Start);
        return anchors.Count > 0 ? (anchors[0].PageIndex, null) : null;
    }

    /// <summary>
    /// The passage of <paramref name="plan"/> to resume <paramref name="position"/> from, or null
    /// to start at the beginning.
    /// </summary>
    /// <remarks>
    /// The saved id when it was saved against this plan. Otherwise the place on the page decides:
    /// the passage whose text covers the saved offset, else the first passage that starts after it
    /// (what was there may now be skipped), else the last that starts before it (nothing is read
    /// after it now), else the first passage on the saved page. A position
    /// saved before the plan was recorded with it keeps its id, as before: there is nothing else
    /// to find it by.
    /// </remarks>
    public static string? Find(NarrationPlan plan, ReaderPosition position)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(position);
        if (position.SegmentId is not { } id) return null;
        if (position.PlanId is null || position.PlanId == PlanKey(plan))
            return plan.Segment(id) is not null ? id : null;

        int page = position.PageIndex;
        var units = UnitsOf(plan);
        if (position.SourceCharOffset is { } offset)
        {
            foreach (var segment in plan.Segments)
                if (AnchorsOf(units, segment).Any(a => a.PageIndex == page && a.SourceCharRange is { } r
                                                      && r.Start <= offset && offset < r.End))
                    return segment.Id;
            // Nothing covers it now: the passage that follows it, in page and text order, or, when
            // nothing is read after it (the references at the end, now skipped), the one before it.
            NarrationSegment? next = null, previous = null;
            (int Page, int Start) nearestAfter = (int.MaxValue, int.MaxValue), nearestBefore = (int.MinValue, int.MinValue);
            var target = (page, offset);
            foreach (var segment in plan.Segments)
                foreach (var a in AnchorsOf(units, segment))
                {
                    if (a.SourceCharRange is not { } r) continue;
                    var at = (a.PageIndex, r.Start);
                    if (Compare(at, target) > 0 && Compare(at, nearestAfter) < 0) { nearestAfter = at; next = segment; }
                    if (Compare(at, target) < 0 && Compare(at, nearestBefore) > 0) { nearestBefore = at; previous = segment; }
                }
            if ((next ?? previous) is { } nearest) return nearest.Id;
        }
        return plan.Segments.FirstOrDefault(s => AnchorsOf(units, s).Any(a => a.PageIndex == page))?.Id;
    }

    private static int Compare((int Page, int Start) x, (int Page, int Start) y)
        => x.Page != y.Page ? x.Page.CompareTo(y.Page) : x.Start.CompareTo(y.Start);

    private static Func<string, ReadingUnit?> UnitsOf(NarrationPlan plan)
    {
        var byId = plan.Units.DistinctBy(u => u.Id).ToDictionary(u => u.Id);
        return byId.GetValueOrDefault;
    }

    // The anchors a passage speaks for, in link order, resolved through its reading units.
    private static List<SourceAnchor> AnchorsOf(Func<string, ReadingUnit?> units, NarrationSegment segment)
    {
        var ids = segment.AnchorIds;
        var byId = segment.ReadingUnitIds.Select(units).OfType<ReadingUnit>()
            .SelectMany(u => u.Anchors).DistinctBy(a => a.AnchorId).ToDictionary(a => a.AnchorId);
        return ids.Select(byId.GetValueOrDefault).OfType<SourceAnchor>().ToList();
    }
}
