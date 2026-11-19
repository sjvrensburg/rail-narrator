using RailReader.Core.Models;
using RailNarrator.Domain;
using SkiaSharp;

namespace RailNarrator.Pdf;

/// <summary>
/// The transform between displayed page points and on-screen pixels.
/// </summary>
/// <remarks>
/// <para>
/// Core's convention, taken from <c>Viewport.ResolvePoint</c>:
/// <c>screen = pagePoint × Zoom + Offset</c>. This class is its explicit inverse so the same
/// arithmetic is used in both directions and is unit-testable without a renderer.
/// </para>
/// <para>
/// <see cref="RenderScaling"/> is the HiDPI factor and is applied outside the camera transform.
/// Conflating it with zoom is a classic source of highlights that sit a few pixels off on a scaled
/// display but look correct at 100%, so it is kept separate and multiplied last.
/// </para>
/// <para>
/// <see cref="PageOffsetX"/>/<see cref="PageOffsetY"/> carry continuous-scroll page offsets. In
/// continuous mode Core's camera describes the <i>anchor</i> page and every other page's transform is
/// derived from the page layout; drawing a highlight on a neighbour page without that offset puts it
/// on the wrong page entirely.
/// </para>
/// </remarks>
public readonly record struct PageTransform(
    double Zoom,
    double OffsetX,
    double OffsetY,
    double RenderScaling = 1.0,
    double PageOffsetX = 0.0,
    double PageOffsetY = 0.0)
{
    public static PageTransform FromCamera(Camera camera, double renderScaling = 1.0, double pageOffsetX = 0, double pageOffsetY = 0)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return new PageTransform(camera.Zoom, camera.OffsetX, camera.OffsetY, renderScaling, pageOffsetX, pageOffsetY);
    }

    /// <summary>Page point → screen pixel.</summary>
    public (double X, double Y) ToScreen(double pageX, double pageY)
        => ((pageX * Zoom + OffsetX + PageOffsetX) * RenderScaling,
            (pageY * Zoom + OffsetY + PageOffsetY) * RenderScaling);

    /// <summary>Screen pixel → page point. The inverse of <see cref="ToScreen"/>.</summary>
    public (double PageX, double PageY) ToPage(double screenX, double screenY)
    {
        double scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
        double x = screenX / scale - OffsetX - PageOffsetX;
        double y = screenY / scale - OffsetY - PageOffsetY;
        return Zoom <= 0 ? (x, y) : (x / Zoom, y / Zoom);
    }

    public SKRect ToScreenRect(PageRect rect)
    {
        var (x0, y0) = ToScreen(rect.Left, rect.Top);
        var (x1, y1) = ToScreen(rect.Right, rect.Bottom);
        return new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
    }

    public PageRect ToPageRect(SKRect rect) => ToPageRect(rect.Left, rect.Top, rect.Right, rect.Bottom);

    /// <summary>Screen rectangle → page-point rectangle, from raw edges.</summary>
    public PageRect ToPageRect(double left, double top, double right, double bottom)
    {
        var (x0, y0) = ToPage(left, top);
        var (x1, y1) = ToPage(right, bottom);
        return new PageRect((float)x0, (float)y0, (float)x1, (float)y1);
    }

    /// <summary>
    /// Applies this transform to a canvas so subsequent drawing can use page-point coordinates.
    /// RailReaderCore's own overlay renderer expects exactly this setup.
    /// </summary>
    public void Apply(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (RenderScaling != 1.0) canvas.Scale((float)RenderScaling);
        canvas.Translate((float)(OffsetX + PageOffsetX), (float)(OffsetY + PageOffsetY));
        if (Zoom != 1.0) canvas.Scale((float)Zoom);
    }
}

/// <summary>
/// Resolves a click or tap on the page to the narration unit it landed in.
/// </summary>
/// <remarks>
/// Hit-testing uses the anchors' own disjoint rectangles, so a click in the gutter between two
/// columns selects neither rather than whichever column happens to be closer to the union's centre.
/// A click on blank space returns no unit: choosing an arbitrary one would start reading somewhere
/// the reader did not point at.
/// </remarks>
public static class SourceHitTester
{
    /// <summary>
    /// Finds the narration unit containing a page point.
    /// </summary>
    /// <param name="plan">The plan whose units are searched.</param>
    /// <param name="pageIndex">Zero-based page the point is on.</param>
    /// <param name="pageX">X in displayed page points.</param>
    /// <param name="pageY">Y in displayed page points.</param>
    /// <param name="tolerancePoints">
    /// Slack added to each rectangle. A small tolerance matters because glyph boxes are tight and a
    /// click between two lines of a dense paragraph would otherwise miss both.
    /// </param>
    public static (ReadingUnit Unit, SourceAnchor Anchor)? HitTest(
        NarrationPlan plan, int pageIndex, double pageX, double pageY, double tolerancePoints = 1.5)
    {
        ArgumentNullException.ThrowIfNull(plan);
        float tol = (float)Math.Max(0, tolerancePoints);

        ReadingUnit? bestUnit = null;
        SourceAnchor? bestAnchor = null;
        double bestArea = double.MaxValue;

        foreach (var unit in plan.Units)
        {
            foreach (var anchor in unit.Anchors)
            {
                if (anchor.PageIndex != pageIndex) continue;
                foreach (var rect in anchor.Rects)
                {
                    var padded = rect.Inflated(tol, tol);
                    if (!padded.Contains((float)pageX, (float)pageY)) continue;
                    // Several units can overlap (an inline expression inside a sentence). Prefer the
                    // tightest match, which is the one the reader most likely meant.
                    if (rect.Area < bestArea)
                    {
                        bestArea = rect.Area;
                        bestUnit = unit;
                        bestAnchor = anchor;
                    }
                }
            }
        }
        return bestUnit is null || bestAnchor is null ? null : (bestUnit, bestAnchor);
    }

    /// <summary>Finds the narration segment for a unit, so a click can start playback there.</summary>
    public static NarrationSegment? SegmentFor(NarrationPlan plan, ReadingUnit unit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Segments.FirstOrDefault(s => s.ReadingUnitIds.Contains(unit.Id));
    }
}

/// <summary>How narration highlights are drawn.</summary>
/// <param name="FillColour">Fill over the source region. Kept translucent so the text stays legible.</param>
/// <param name="FillAlpha">0–255. Highlight contrast is user-configurable because a fixed value is unreadable for some readers.</param>
/// <param name="OutlineColour">Outline drawn over the fill's edge.</param>
/// <param name="OutlineWidthPoints">Outline thickness in page points, so it scales with the document rather than the zoom.</param>
/// <param name="CornerRadiusPoints">Rounding applied to each rectangle.</param>
/// <param name="PaddingPoints">Growth applied to each rectangle so descenders and diacritics are covered.</param>
public sealed record NarrationHighlightStyle(
    SKColor FillColour,
    byte FillAlpha = 72,
    SKColor? OutlineColour = null,
    float OutlineWidthPoints = 0.75f,
    float CornerRadiusPoints = 1.5f,
    float PaddingPoints = 1.0f)
{
    /// <summary>Default: an amber wash with a readable outline.</summary>
    public static NarrationHighlightStyle Default { get; } = new(new SKColor(255, 176, 32), 72, new SKColor(255, 140, 0, 220));

    /// <summary>High-contrast variant for readers who need a stronger boundary.</summary>
    public static NarrationHighlightStyle HighContrast { get; } = new(new SKColor(255, 214, 0), 120, new SKColor(0, 0, 0, 255), 1.25f);

    /// <summary>
    /// A passage spoken in other words than the page's (a maths interpretation, or the local
    /// reading of its LaTeX): violet, so it is plain that what is heard is not what is printed.
    /// </summary>
    public static NarrationHighlightStyle Paraphrase { get; } = new(new SKColor(150, 100, 255), 72, new SKColor(110, 60, 230, 220));

    /// <summary>High-contrast variant of <see cref="Paraphrase"/>.</summary>
    public static NarrationHighlightStyle ParaphraseHighContrast { get; } = new(new SKColor(200, 120, 255), 120, new SKColor(0, 0, 0, 255), 1.25f);

    /// <summary>
    /// The paraphrase tint on a filtered page. RailReader's line-highlight colour for each filter
    /// (cyan, yellow, amber, green) is taken by the spoken highlight, so the paraphrase is a magenta
    /// or violet chosen to stand apart from both it and the filtered paper.
    /// </summary>
    public static SKColor ParaphraseTint(ColourEffect effect) => effect switch
    {
        ColourEffect.Amber => new SKColor(255, 90, 200),
        ColourEffect.None => Paraphrase.FillColour,
        _ => new SKColor(255, 64, 255),
    };

    public NarrationHighlightStyle WithAlpha(byte alpha) => this with { FillAlpha = alpha };
}

/// <summary>
/// Draws narration highlights above the page.
/// </summary>
/// <remarks>
/// <para>
/// These are transient overlays, never PDF annotations. Writing them into the document would modify
/// the reader's file and would collide with RailReader2's own annotation store.
/// </para>
/// <para>
/// Each rectangle is drawn separately. Drawing the union of a multi-line or multi-column unit would
/// cover content the narration is not reading, which is exactly the failure the disjoint-rect anchor
/// design exists to prevent.
/// </para>
/// </remarks>
public static class NarrationOverlayRenderer
{
    [ThreadStatic] private static SKPaint? s_fill;
    [ThreadStatic] private static SKPaint? s_outline;

    private static SKPaint Fill => s_fill ??= new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    private static SKPaint Outline => s_outline ??= new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };

    /// <summary>
    /// Draws highlight rectangles. The canvas must already be in page-point space — apply
    /// <see cref="PageTransform.Apply"/> first.
    /// </summary>
    public static void DrawHighlights(SKCanvas canvas, IReadOnlyList<PageRect> rects, NarrationHighlightStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (rects is null || rects.Count == 0) return;
        style ??= NarrationHighlightStyle.Default;

        var fill = Fill;
        fill.Color = style.FillColour.WithAlpha(style.FillAlpha);
        var outline = Outline;
        outline.StrokeWidth = style.OutlineWidthPoints;
        bool drawOutline = style.OutlineColour.HasValue;
        if (drawOutline) outline.Color = style.OutlineColour!.Value;

        foreach (var rect in rects)
        {
            if (rect.IsEmpty) continue;
            var padded = rect.Inflated(style.PaddingPoints, style.PaddingPoints);
            var sk = new SKRect(padded.Left, padded.Top, padded.Right, padded.Bottom);
            float radius = style.CornerRadiusPoints;
            if (radius > 0)
            {
                using var path = new SKPath();
                path.AddRoundRect(new SKRoundRect(sk, radius, radius));
                canvas.DrawPath(path, fill);
                if (drawOutline) canvas.DrawPath(path, outline);
            }
            else
            {
                canvas.DrawRect(sk, fill);
                if (drawOutline) canvas.DrawRect(sk, outline);
            }
        }
    }

    /// <summary>
    /// Draws a reading progress rail: a thin bar along the left edge of the active region. Optional,
    /// and drawn in the same page-point space so it scales with the document.
    /// </summary>
    public static void DrawProgressRail(SKCanvas canvas, PageRect region, SKColor colour, float widthPoints = 2f)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (region.IsEmpty) return;
        var paint = Outline;
        paint.Color = colour;
        paint.StrokeWidth = widthPoints;
        float x = region.Left - widthPoints;
        canvas.DrawLine(x, region.Top, x, region.Bottom, paint);
    }
}
