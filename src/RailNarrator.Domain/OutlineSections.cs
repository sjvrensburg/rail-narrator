namespace RailNarrator.Domain;

/// <summary>A heading of the document's own outline (its PDF bookmarks): its title and the page it points to.</summary>
public sealed record OutlineHeading(string Title, int? Page);

/// <summary>A heading the narration reads: its index in the plan, the page it starts on and its text.</summary>
public sealed record ReadHeading(int SegmentIndex, int Page, string Text);

/// <summary>Which outline heading a reading position falls under, for the status bar's section name.</summary>
public static class OutlineSections
{
    /// <summary>
    /// The index in <paramref name="outline"/> of the heading the position falls under, or -1 for none.
    /// </summary>
    /// <param name="outline">Every outline heading in document order.</param>
    /// <param name="page">The page shown.</param>
    /// <param name="readingSegment">
    /// The passage being read when it is on <paramref name="page"/>; null while browsing elsewhere,
    /// when the page alone decides.
    /// </param>
    /// <param name="readHeadings">The plan's heading passages.</param>
    /// <remarks>
    /// An outline entry gives a page and no position on it, so by page alone a heading half way down
    /// is named while the end of the section before it is read above. On the page being read, a
    /// heading the narration has not reached yet is passed over, which needs the heading found in the
    /// plan: by its words (letters and digits, case ignored) on that page. One not found there is
    /// taken as reached, as before.
    /// </remarks>
    public static int Find(IReadOnlyList<OutlineHeading> outline, int page, int? readingSegment, IReadOnlyList<ReadHeading> readHeadings)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(readHeadings);
        int found = -1;
        for (int i = 0; i < outline.Count; i++)
            if (outline[i].Page is { } p && p <= page) found = i;
        if (readingSegment is not { } reading) return found;
        while (found >= 0 && outline[found].Page == page)
        {
            string title = Letters(outline[found].Title);
            var heading = title.Length == 0 ? null
                : readHeadings.FirstOrDefault(h => h.Page == page && Letters(h.Text) is { Length: > 0 } text
                                                   && (text.Contains(title, StringComparison.Ordinal) || title.Contains(text, StringComparison.Ordinal)));
            if (heading is null || heading.SegmentIndex <= reading) break;
            // Not reached: the heading before it, which may be on an earlier page.
            int previous = -1;
            for (int i = 0; i < found; i++)
                if (outline[i].Page is { } p && p <= page) previous = i;
            found = previous;
        }
        return found;
    }

    private static string Letters(string text)
        => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
