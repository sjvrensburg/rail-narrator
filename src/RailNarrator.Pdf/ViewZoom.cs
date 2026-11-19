namespace RailNarrator.Pdf;

/// <summary>
/// The magnification of the page shown: either fitted to the view's width, or the value the reader
/// chose.
/// </summary>
/// <remarks>
/// <para>
/// Fit-width is a mode, not a number worked out once. It used to be computed from the page shown
/// when the document opened, so a publisher's cover sheet wider than the article (634 pt against
/// 504 pt on the paper it was found on) left every later page at 79 % of the window's width. In
/// the mode, the zoom is re-derived whenever the page shown or the view's width changes.
/// </para>
/// <para>
/// A magnification the reader chose is theirs: a page turn, a window resize or the script panel
/// opening must not replace it. A resize used to re-fit unconditionally and discarded a 400 %
/// zoom.
/// </para>
/// </remarks>
public sealed class ViewZoom
{
    public const double Minimum = 0.25;
    public const double Maximum = 8.0;

    public ViewZoom(bool fitsWidth = true, double zoom = 1.0)
    {
        FitsWidth = fitsWidth;
        Zoom = Clamp(zoom);
    }

    /// <summary>True while the zoom follows the width of the page shown.</summary>
    public bool FitsWidth { get; private set; }

    /// <summary>Screen units per page point.</summary>
    public double Zoom { get; private set; }

    public static double Clamp(double zoom)
        => double.IsFinite(zoom) && zoom > 0 ? Math.Clamp(zoom, Minimum, Maximum) : 1.0;

    /// <summary>
    /// The page shown or the view's width changed. Re-fits when fitting; leaves a zoom the reader
    /// chose alone. A view or page with no width yet (before first layout, a page that could not be
    /// measured) changes nothing.
    /// </summary>
    /// <returns>True when the zoom changed.</returns>
    public bool Layout(double viewWidth, double pageWidth)
    {
        if (!FitsWidth || !(viewWidth > 0) || !(pageWidth > 0)) return false;
        double fitted = Clamp(viewWidth / pageWidth);
        if (fitted == Zoom) return false;
        Zoom = fitted;
        return true;
    }

    /// <summary>The reader chose a magnification: it replaces fit-width until they ask to fit again.</summary>
    public void SetByReader(double zoom)
    {
        FitsWidth = false;
        Zoom = Clamp(zoom);
    }

    /// <summary>The reader asked to fit the width. The zoom follows at the next <see cref="Layout"/>.</summary>
    public void Fit() => FitsWidth = true;

    /// <summary>Stops fitting and keeps the present zoom as the reader's.</summary>
    public void Pin() => FitsWidth = false;
}
