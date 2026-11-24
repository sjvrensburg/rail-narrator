using RailNarrator.Domain;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailNarrator.Pdf.Annotations;

/// <summary>
/// One page's annotations as they were at one <see cref="ReviewDocument.Version"/>: a private deep
/// copy, safe to draw on the render thread while the UI thread goes on changing the document.
/// </summary>
/// <remarks>
/// Core's undo actions change annotation objects in place (a move rewrites a note's position, an
/// edit its text), and Avalonia draws a recorded frame on its render thread. Drawing the live
/// objects would read them mid-change. Nothing changes a snapshot after it is made.
/// </remarks>
public sealed class AnnotationPageSnapshot
{
    private readonly List<Annotation> _annotations;
    private readonly Annotation? _selected;

    internal AnnotationPageSnapshot(int pageIndex, long version, List<Annotation> sorted, Annotation? selected)
    {
        PageIndex = pageIndex;
        Version = version;
        _annotations = sorted;
        _selected = selected;
    }

    public int PageIndex { get; }

    public long Version { get; }

    public int Count => _annotations.Count;

    /// <summary>The copies, in drawing order; for tests.</summary>
    internal IReadOnlyList<Annotation> Annotations => _annotations;

    /// <summary>
    /// Draws the annotations with Core's renderer, in page points under <paramref name="transform"/>.
    /// </summary>
    /// <param name="darkBackdrop">The page is shown under a dark colour filter; see <see cref="AnnotationRenderer.IsDarkBackdrop"/>.</param>
    /// <param name="expandAllNotes">Show every note's text, not only the open ones.</param>
    public void Draw(SKCanvas canvas, PageTransform transform, bool darkBackdrop, bool expandAllNotes)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (_annotations.Count == 0) return;
        canvas.Save();
        try
        {
            transform.Apply(canvas);
            AnnotationRenderer.DrawAnnotations(canvas, _annotations, _selected, expandAllNotes, darkBackdrop);
        }
        finally { canvas.Restore(); }
    }
}

/// <summary>
/// The reader's annotations, ready to draw: a snapshot per page, rebuilt only when the document
/// has changed or the selection has. Used on the UI thread.
/// </summary>
public sealed class AnnotationLayer
{
    private readonly ReviewDocument _document;
    private readonly Dictionary<int, AnnotationPageSnapshot> _pages = [];
    private Annotation? _selectedFor;

    public AnnotationLayer(ReviewDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
    }

    public ReviewDocument Document => _document;

    /// <summary>The page's snapshot, made now if the document or <paramref name="selected"/> changed since the last one.</summary>
    public AnnotationPageSnapshot ForPage(int pageIndex, Annotation? selected = null)
    {
        if (!ReferenceEquals(selected, _selectedFor))
        {
            _pages.Clear();
            _selectedFor = selected;
        }
        if (_pages.TryGetValue(pageIndex, out var cached) && cached.Version == _document.Version) return cached;

        var live = _document.PageAnnotations(pageIndex);
        var copies = new List<Annotation>(live.Count);
        Annotation? selectedCopy = null;
        foreach (var annotation in live)
        {
            var copy = ReviewAnnotationJson.Clone(annotation);
            if (ReferenceEquals(annotation, selected)) selectedCopy = copy;
            copies.Add(copy);
        }
        var snapshot = new AnnotationPageSnapshot(pageIndex, _document.Version,
            AnnotationRenderer.SortByZOrder(copies), selectedCopy);
        _pages[pageIndex] = snapshot;
        return snapshot;
    }
}

/// <summary>
/// A page's extracted text as Core's <see cref="PageText"/>, for building highlight boxes from a
/// range of the text with Core's own code.
/// </summary>
/// <remarks>
/// <c>AnnotationInteractionHandler.BuildHighlightRects</c> reads <see cref="PageText.CharBoxes"/>
/// by list position, as if position <c>i</c> held the box of <c>Text[i]</c>, and skips all-zero
/// boxes. The snapshot's characters are kept only where there is a glyph, each with its UTF-16
/// offset, so they are spread out here to one box per offset, with empty boxes between. The boxes
/// come from the cached extraction: building a highlight never calls PDFium.
/// </remarks>
public sealed class PageTextIndex
{
    private PageTextIndex(int pageIndex, PageText text)
    {
        PageIndex = pageIndex;
        Text = text;
    }

    public int PageIndex { get; }

    /// <summary>The page's text with one box per UTF-16 offset.</summary>
    public PageText Text { get; }

    public static PageTextIndex Build(PageExtraction page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var boxes = new List<CharBox>(page.Text.Length);
        for (int i = 0; i < page.Text.Length; i++) boxes.Add(new CharBox(i, 0, 0, 0, 0));
        foreach (var c in page.Chars)
        {
            if ((uint)c.Index >= (uint)boxes.Count || c.Rect.IsEmpty) continue;
            boxes[c.Index] = new CharBox(c.Index, c.Rect.Left, c.Rect.Top, c.Rect.Right, c.Rect.Bottom, c.Angle);
        }
        bool fromOcr = page.Lines.Count > 0 && page.Lines.All(l => l.FromOcr);
        return new PageTextIndex(page.PageIndex, new PageText(page.Text, boxes) { FromOcr = fromOcr });
    }

    /// <summary>
    /// One box per line of the range, as Core makes a highlight's quads. Empty when the range has
    /// no glyphs.
    /// </summary>
    public List<HighlightRect> Rects(TextRange range)
    {
        int start = Math.Clamp(range.Start, 0, Text.CharBoxes.Count);
        int length = Math.Clamp(range.Length, 0, Text.CharBoxes.Count - start);
        return AnnotationInteractionHandler.BuildHighlightRects(Text, start, length);
    }
}
