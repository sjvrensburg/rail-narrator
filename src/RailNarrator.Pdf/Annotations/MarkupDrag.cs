using RailNarrator.Domain;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailNarrator.Pdf.Annotations;

/// <summary>
/// A drag over a page's text with a markup tool (highlight, underline, strikeout, squiggly), in
/// page points: where it started, the range it covers now, and the markup it would make.
/// </summary>
/// <remarks>
/// <para>
/// Core's own tool state machine works through its <c>Viewport</c> and <c>DocumentModel</c>, which
/// this application does not build (ADR-0014). This is the part of it the markup tools need,
/// over the cached extraction's character boxes (<see cref="PageTextIndex"/>), with Core's own
/// <c>BuildHighlightRects</c> and <c>CreateTextMarkup</c> for the boxes and the annotation.
/// </para>
/// <para>
/// A drag starts only on a line of text: pressing on blank paper selects nothing (Core snaps to
/// the nearest character anywhere on the page, which turns a press in a margin into a mark on
/// the nearest word). Once started, the end follows the nearest character, so a drag may pass
/// between lines. A press and release without moving is not a drag: it is left to the caller (a
/// click selects an annotation).
/// </para>
/// </remarks>
public sealed class MarkupDrag
{
    /// <summary>How far above or below a line's glyphs a press still lands on the line, in points.</summary>
    public const float LineSlack = 3f;

    /// <summary>How far the pointer must move, in points, for a press to become a drag.</summary>
    public const float DragThreshold = 2f;

    private readonly PageTextIndex _page;
    private readonly int _anchor;
    private readonly float _startX, _startY;
    private int _current;
    private bool _moved;

    private MarkupDrag(PageTextIndex page, AnnotationTool tool, string colour, float opacity, int anchor, float x, float y)
    {
        _page = page;
        Tool = tool;
        Colour = colour;
        Opacity = opacity;
        _anchor = _current = anchor;
        _startX = x;
        _startY = y;
    }

    public AnnotationTool Tool { get; }

    public string Colour { get; }

    public float Opacity { get; }

    public int PageIndex => _page.PageIndex;

    /// <summary>The text covered now; null until the pointer has moved.</summary>
    public TextRange? Range => _moved
        ? new TextRange(Math.Min(_anchor, _current), Math.Abs(_current - _anchor) + 1)
        : null;

    /// <summary>The markup the drag would make now, to draw while dragging; null until it has moved.</summary>
    public TextMarkupAnnotation? Preview { get; private set; }

    /// <summary>
    /// Starts a drag where the pointer was pressed. Null, with the reason, when it cannot start:
    /// a page with no text, or a press off every line.
    /// </summary>
    public static (MarkupDrag? Drag, string? Refusal) Begin(PageTextIndex page, AnnotationTool tool, string colour, float opacity,
        float x, float y)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!AnnotationInteractionHandler.IsTextMarkupTool(tool)) throw new ArgumentException($"{tool} is not a text markup.", nameof(tool));
        if (!page.Text.CharBoxes.Any(HasInk)) return (null, "No selectable text on this page.");
        if (NearestOnLine(page, x, y) is not { } index) return (null, null);
        return (new MarkupDrag(page, tool, colour, opacity, index, x, y), null);
    }

    /// <summary>The pointer moved to (x, y).</summary>
    public void Move(float x, float y)
    {
        if (!_moved && Math.Abs(x - _startX) < DragThreshold && Math.Abs(y - _startY) < DragThreshold) return;
        _moved = true;
        _current = NearestOnLine(_page, x, y) ?? Nearest(_page, x, y) ?? _current;
        var rects = Range is { } range ? _page.Rects(range) : [];
        Preview = rects.Count == 0 ? null : AnnotationInteractionHandler.CreateTextMarkup(Tool, rects, Colour, Opacity);
    }

    /// <summary>
    /// The character under (x, y) or, on the line there, the one nearest it; null off every line.
    /// </summary>
    public static int? NearestOnLine(PageTextIndex page, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(page);
        int best = -1;
        float bestGap = float.MaxValue, bestCentre = float.MaxValue;
        var boxes = page.Text.CharBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (!HasInk(b) || y < b.Top - LineSlack || y > b.Bottom + LineSlack) continue;
            float gap = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
            float centre = Math.Abs((b.Left + b.Right) / 2 - x) + Math.Abs((b.Top + b.Bottom) / 2 - y);
            if (gap < bestGap || (gap == bestGap && centre < bestCentre))
            {
                best = i;
                bestGap = gap;
                bestCentre = centre;
            }
        }
        return best < 0 ? null : best;
    }

    /// <summary>The character whose centre is nearest (x, y), as Core finds it; null on a page with no glyphs.</summary>
    public static int? Nearest(PageTextIndex page, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(page);
        int best = -1;
        float bestDistance = float.MaxValue;
        var boxes = page.Text.CharBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (!HasInk(b)) continue;
            float dx = (b.Left + b.Right) / 2 - x, dy = (b.Top + b.Bottom) / 2 - y;
            float distance = dx * dx + dy * dy;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best < 0 ? null : best;
    }

    private static bool HasInk(CharBox b) => !(b.Left == 0 && b.Right == 0 && b.Top == 0 && b.Bottom == 0);
}

/// <summary>Draws a markup being dragged, with Core's preview renderer, on the render thread.</summary>
/// <remarks>The annotation is made anew at each move and never changed after, so the render thread may hold it.</remarks>
public static class MarkupPreview
{
    public static void Draw(SKCanvas canvas, PageTransform transform, Annotation preview)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(preview);
        canvas.Save();
        try
        {
            transform.Apply(canvas);
            AnnotationRenderer.DrawPreviewAnnotation(canvas, preview);
        }
        finally { canvas.Restore(); }
    }
}
