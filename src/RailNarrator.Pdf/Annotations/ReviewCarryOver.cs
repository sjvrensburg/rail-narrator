using RailNarrator.Domain;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailNarrator.Pdf.Annotations;

/// <summary>An annotation and the page it is on.</summary>
public sealed record PagedAnnotation(int Page, Annotation Annotation);

/// <summary>
/// What bringing an earlier version's notes over would do (<see cref="ReviewCarryOver.Match"/>):
/// the annotations placed on this version, with new boxes and links, and those that could not be.
/// Every annotation here is a copy; the earlier version is never changed.
/// </summary>
/// <param name="Placed">Placed on this version, by their page here.</param>
/// <param name="PlacedLinks">Their links to this version's text and narration.</param>
/// <param name="Unplaced">Not placed, by their page in the earlier version.</param>
/// <param name="UnplacedLinks">Their links, as they were.</param>
/// <param name="NotesPlaced">How many notes were placed (a mark across a page break is one).</param>
/// <param name="NotesUnplaced">How many were not.</param>
/// <param name="PageMap">
/// Where an earlier page's text is now, for each earlier page with a note placed by its text (and
/// only where every such note on it agrees): the page a note not placed is put on "as before".
/// </param>
public sealed record CarriedNotes(
    IReadOnlyList<PagedAnnotation> Placed,
    IReadOnlyList<ReviewLink> PlacedLinks,
    AnnotationFile Unplaced,
    IReadOnlyList<ReviewLink> UnplacedLinks,
    int NotesPlaced,
    int NotesUnplaced,
    IReadOnlyDictionary<int, int> PageMap);

/// <summary>
/// Brings the notes of an earlier version of a PDF to a revised one by the text they quote (ADR-0014).
/// </summary>
/// <remarks>
/// <para>
/// Only a text markup with a quote can be placed: the quote kept in its link is looked for in this
/// version's text, and the mark is put on the words found, with this version's character boxes and
/// the passage that reads them now. Nothing is placed by position or by a near match. A note whose
/// words are gone (rewritten, or cut), and a sticky note, text box, drawing or imported mark, which
/// is tied to no text, are kept unplaced, for the reader to place where it was or to discard.
/// </para>
/// <para>
/// Text is compared by its letters and digits only, lower-cased, with a ligature the document
/// identified written as its letters (<see cref="PageQuote"/>): spacing, punctuation, line breaks
/// and hyphens are left out on both sides, because a quote and the page's text layer disagree about
/// them (a word hyphenated at a line end, a ligature, a space the text layer put inside a word)
/// without the words being any different. When the words appear more than once, the occurrence that
/// starts and ends on word boundaries is taken over one inside longer words, then the one nearest
/// where the note was (the same page, then the same height on it).
/// </para>
/// </remarks>
public static class ReviewCarryOver
{
    /// <summary>
    /// Matches every note of <paramref name="earlier"/> (its annotations, and any it had itself not
    /// placed) against this version's extraction. Pure: may run on any thread, given an
    /// <paramref name="earlier"/> no other thread is changing.
    /// </summary>
    public static CarriedNotes Match(ReviewDocument earlier, NarrationPlan? plan, ExtractionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(snapshot);
        var pages = new PageTextCache(snapshot);
        var letters = DocumentLetters.Of(snapshot, pages.Ligatures);
        var placed = new List<PagedAnnotation>();
        var placedLinks = new List<ReviewLink>();
        var unplaced = new AnnotationFile();
        var unplacedLinks = new List<ReviewLink>();
        int notesPlaced = 0, notesUnplaced = 0;
        var moved = new Dictionary<int, HashSet<int>>();
        var ownGroups = Groups(earlier.File, earlier.Links).ToList();

        foreach (var group in ownGroups.Concat(Groups(earlier.Unplaced, earlier.UnplacedLinks)))
        {
            var head = group.Members[0];
            var link = group.Link;
            bool markup = group.Members.All(m => m.Annotation is TextMarkupAnnotation);
            string key = markup && link is not null ? DocumentLetters.KeyOf(link.QuotedText) : "";
            float oldTop = AnnotationGeometry.GetAnnotationBounds(head.Annotation)?.Top ?? 0;
            var found = key.Length > 0 ? letters.Find(key, head.Page, oldTop, pages) : null;
            var ranges = found is { } start ? letters.Ranges(start, start + key.Length) : [];
            var boxes = ranges.Select(r => (Range: r, Rects: pages.For(r.PageIndex)?.Rects(new TextRange(r.Start, r.Length)) ?? []))
                .Where(x => x.Rects.Count > 0).ToList();

            if (boxes.Count == 0 || link is null)
            {
                foreach (var member in group.Members)
                {
                    var copy = ReviewAnnotationJson.Clone(member.Annotation);
                    if (!unplaced.Pages.TryGetValue(member.Page, out var list)) unplaced.Pages[member.Page] = list = [];
                    list.Add(copy);
                    if (copy.NativeId is { } id && group.Links.TryGetValue(id, out var own)) unplacedLinks.Add(own);
                }
                notesUnplaced++;
                continue;
            }

            var first = boxes[0].Range;
            var (segmentId, unitId) = ReviewTarget.Locate(plan, first.PageIndex, first.Start, first.Start + first.Length);
            // A note on a whole passage stays on the whole passage; one on a sentence or a range, on the sentence there now.
            if (!link.ByHand && link.UnitId is null) unitId = null;
            string? groupId = null;
            for (int i = 0; i < boxes.Count; i++)
            {
                var basis = group.Members[Math.Min(i, group.Members.Count - 1)].Annotation;
                var copy = (TextMarkupAnnotation)ReviewAnnotationJson.Clone(basis);
                if (i >= group.Members.Count || copy.NativeId is null) copy.NativeId = Guid.NewGuid().ToString();
                if (i > 0) copy.Contents = null;   // the note is on the first
                copy.Source = AnnotationSource.RailReader;
                copy.Rects = [.. boxes[i].Rects];
                groupId ??= boxes.Count > 1 ? copy.NativeId : null;
                placed.Add(new PagedAnnotation(boxes[i].Range.PageIndex, copy));
                placedLinks.Add(new ReviewLink(copy.NativeId, segmentId, unitId, snapshot.Identity.ExtractionRevision,
                    [boxes[i].Range], link.QuotedText, link.CreatedUtc) { GroupId = groupId, ByHand = link.ByHand });
            }
            notesPlaced++;
            // Pages of the earlier version only: a note it had not placed itself is by a still earlier one's page.
            if (ownGroups.Contains(group))
            {
                if (!moved.TryGetValue(head.Page, out var to)) moved[head.Page] = to = [];
                to.Add(first.PageIndex);
            }
        }
        var pageMap = moved.Where(m => m.Value.Count == 1).ToDictionary(m => m.Key, m => m.Value.Single());
        return new CarriedNotes(placed, placedLinks, unplaced, unplacedLinks, notesPlaced, notesUnplaced, pageMap);
    }

    /// <summary>How many notes a document has: a mark across a page break is one, and notes not placed count too.</summary>
    public static int CountNotes(ReviewDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Groups(document.File, document.Links).Count() + Groups(document.Unplaced, document.UnplacedLinks).Count();
    }

    private sealed record Group(IReadOnlyList<PagedAnnotation> Members, ReviewLink? Link, IReadOnlyDictionary<string, ReviewLink> Links);

    /// <summary>The annotations made as one, in page order, each with its head's link.</summary>
    private static IEnumerable<Group> Groups(AnnotationFile file, IReadOnlyDictionary<string, ReviewLink> links)
    {
        var groups = new List<(string? Key, List<PagedAnnotation> Members)>();
        var byKey = new Dictionary<string, List<PagedAnnotation>>(StringComparer.Ordinal);
        foreach (var (page, list) in file.Pages.OrderBy(p => p.Key))
            foreach (var annotation in list)
            {
                var link = annotation.NativeId is { } id && links.TryGetValue(id, out var l) ? l : null;
                string? key = link?.GroupId;
                if (key is not null && byKey.TryGetValue(key, out var members)) { members.Add(new PagedAnnotation(page, annotation)); continue; }
                var fresh = new List<PagedAnnotation> { new(page, annotation) };
                if (key is not null) byKey[key] = fresh;
                groups.Add((key, fresh));
            }
        foreach (var (_, members) in groups)
        {
            // The head carries the note: the group's first by id, else the first page's.
            var head = members.FirstOrDefault(m => links.TryGetValue(m.Annotation.NativeId ?? "", out var l) && l.GroupId == l.NativeId) ?? members[0];
            var ordered = new List<PagedAnnotation> { head };
            ordered.AddRange(members.Where(m => !ReferenceEquals(m, head)));
            var link = head.Annotation.NativeId is { } id && links.TryGetValue(id, out var own) ? own : null;
            yield return new Group(ordered, link, links);
        }
    }

    /// <summary>A document's letters and digits in reading order, each with the page and offset it came from.</summary>
    private sealed class DocumentLetters
    {
        private readonly string _letters;
        private readonly int[] _page, _offset;
        private readonly bool[] _wordStart;

        private DocumentLetters(string letters, int[] page, int[] offset, bool[] wordStart)
        {
            _letters = letters;
            _page = page;
            _offset = offset;
            _wordStart = wordStart;
        }

        public static string KeyOf(string quote)
        {
            var sb = new System.Text.StringBuilder(quote.Length);
            foreach (char c in quote)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static DocumentLetters Of(ExtractionSnapshot snapshot, IReadOnlyDictionary<char, string> ligatures)
        {
            var sb = new System.Text.StringBuilder();
            var page = new List<int>();
            var offset = new List<int>();
            var wordStart = new List<bool>();
            foreach (var p in snapshot.Pages.OrderBy(p => p.PageIndex))
            {
                string text = p.Text;
                bool inWord = false;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.IsLetterOrDigit(c)) Emit(char.ToLowerInvariant(c).ToString(), i);
                    else if (PageQuote.IsUnmapped(c) && !PageQuote.IsLineEndHyphen(text, i)
                             && ligatures.TryGetValue(c, out var spelled) && PageQuote.InsideAWord(text, i))
                        Emit(KeyOf(spelled), i);
                    else if (PageQuote.IsLineEndHyphen(text, i) || c is '-' && i + 1 < text.Length && text[i + 1] is '\n' or '\r')
                    {
                        // A word broken at a line end goes on: the next line's letters are not a new word.
                        while (i + 1 < text.Length && text[i + 1] is '\n' or '\r') i++;
                    }
                    else inWord = false;

                    void Emit(string letters, int at)
                    {
                        for (int k = 0; k < letters.Length; k++)
                        {
                            sb.Append(letters[k]);
                            page.Add(p.PageIndex);
                            offset.Add(at);
                            wordStart.Add(!inWord);
                            inWord = true;
                        }
                    }
                }
            }
            return new DocumentLetters(sb.ToString(), [.. page], [.. offset], [.. wordStart]);
        }

        /// <summary>
        /// The occurrence of <paramref name="key"/> to place a note on: on word boundaries if any
        /// is, then the nearest to where it was. Null when the words are not in this version.
        /// </summary>
        public int? Find(string key, int oldPage, float oldTop, PageTextCache pages)
        {
            int? best = null;
            (int Aligned, int Pages, float Height) bestScore = (int.MaxValue, int.MaxValue, float.MaxValue);
            for (int at = _letters.IndexOf(key, StringComparison.Ordinal); at >= 0;
                 at = at + 1 < _letters.Length ? _letters.IndexOf(key, at + 1, StringComparison.Ordinal) : -1)
            {
                int end = at + key.Length;
                bool aligned = _wordStart[at] && (end == _letters.Length || _wordStart[end]);
                float top = pages.For(_page[at]) is { } index && _offset[at] < index.Text.CharBoxes.Count
                    ? index.Text.CharBoxes[_offset[at]].Top : 0;
                var score = (aligned ? 0 : 1, Math.Abs(_page[at] - oldPage), Math.Abs(top - oldTop));
                if (score.CompareTo(bestScore) < 0) { bestScore = score; best = at; }
            }
            return best;
        }

        /// <summary>The page text the letters from <paramref name="start"/> to <paramref name="end"/> came from, per page.</summary>
        public IReadOnlyList<LinkedRange> Ranges(int start, int end)
        {
            var ranges = new List<LinkedRange>();
            int k = start;
            while (k < end)
            {
                int page = _page[k], first = _offset[k], last = _offset[k];
                for (; k < end && _page[k] == page; k++)
                {
                    first = Math.Min(first, _offset[k]);
                    last = Math.Max(last, _offset[k]);
                }
                ranges.Add(new LinkedRange(page, first, last - first + 1));
            }
            return ranges;
        }
    }
}
