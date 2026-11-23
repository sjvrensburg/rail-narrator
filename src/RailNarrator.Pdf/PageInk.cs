using RailReader.Core.Models;

namespace RailNarrator.Pdf;

/// <summary>
/// Measures the ink on a rendered page that its text layer does not account for.
/// </summary>
/// <remarks>
/// A page with a handful of characters is either nearly empty, in which case recognition has
/// nothing to find and costs about 20 s, or a scan carrying a stray text layer (a stamp, a page
/// number), in which case its words are ink that no character box covers. The raster tells the two
/// apart for the price of one pass over pixels that are rendered for layout anyway.
/// </remarks>
public static class PageInk
{
    /// <summary>
    /// How far a pixel's brightness must be from the paper's to count as ink, of 255. Wide enough
    /// to pass over a scan's paper grain and the faint edge of anti-aliased text.
    /// </summary>
    public const int InkContrast = 64;

    /// <summary>
    /// The share of the page that must be ink outside the text for recognition to be worth
    /// running. One line of sixty 10 pt characters on a Letter page is about this much; a hairline
    /// rule across the page is about half of it.
    /// </summary>
    public const double WorthRecognising = 0.001;

    /// <summary>
    /// The fraction of the raster's pixels that are ink and lie outside every character box.
    /// </summary>
    /// <param name="rgb">3 bytes per pixel, row-major, no padding.</param>
    /// <param name="chars">Character boxes in page points, top-left origin, as the raster is oriented.</param>
    /// <param name="pageWidth">Page width in points, which the raster's width spans.</param>
    /// <param name="pageHeight">Page height in points, which the raster's height spans.</param>
    public static double FractionOutsideText(
        ReadOnlySpan<byte> rgb, int width, int height,
        IReadOnlyList<CharBox> chars, double pageWidth, double pageHeight)
    {
        ArgumentNullException.ThrowIfNull(chars);
        if (width <= 0 || height <= 0 || pageWidth <= 0 || pageHeight <= 0) return 0;
        int pixels = width * height;
        if (rgb.Length < pixels * 3) return 0;

        // Brightness per pixel, and the paper's brightness as the commonest one: a scan's paper is
        // seldom pure white, and a page printed white on black is still text on a background.
        var luma = new byte[pixels];
        Span<int> histogram = stackalloc int[256];
        for (int i = 0, p = 0; i < pixels; i++, p += 3)
        {
            byte y = (byte)((rgb[p] * 77 + rgb[p + 1] * 150 + rgb[p + 2] * 29) >> 8);
            luma[i] = y;
            histogram[y]++;
        }
        int paper = 0;
        for (int v = 1; v < 256; v++)
            if (histogram[v] > histogram[paper]) paper = v;

        // The text's own ink: every character box, grown a little because a glyph's box is its
        // outline's bounds and anti-aliasing, italics and accents reach past it.
        var covered = new bool[pixels];
        double sx = width / pageWidth, sy = height / pageHeight;
        foreach (var c in chars)
        {
            double w = c.Right - c.Left, h = c.Bottom - c.Top;
            if (w <= 0 || h <= 0) continue;
            double pad = Math.Max(2, 0.3 * h * sy);
            int x0 = Math.Max(0, (int)Math.Floor(c.Left * sx - pad));
            int x1 = Math.Min(width, (int)Math.Ceiling(c.Right * sx + pad));
            int y0 = Math.Max(0, (int)Math.Floor(c.Top * sy - pad));
            int y1 = Math.Min(height, (int)Math.Ceiling(c.Bottom * sy + pad));
            for (int y = y0; y < y1; y++)
                covered.AsSpan(y * width + x0, Math.Max(0, x1 - x0)).Fill(true);
        }

        int outside = 0;
        for (int i = 0; i < pixels; i++)
            if (!covered[i] && Math.Abs(luma[i] - paper) > InkContrast) outside++;
        return (double)outside / pixels;
    }
}
