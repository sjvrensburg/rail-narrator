namespace RailNarrator.Domain;

/// <summary>
/// An axis-aligned rectangle in page points (1/72 inch), in a <b>displayed</b> frame: origin at
/// the top-left of the page as the user sees it, Y increasing downward.
/// </summary>
/// <remarks>
/// This deliberately matches RailReaderCore's <c>RectF</c> and its analysis geometry
/// (<c>CharBox</c>, <c>LayoutBlock.BBox</c>, <c>LineInfo</c>), which are already expressed in
/// displayed page points. It is <b>not</b> raw PDF user space: PDF's own origin is bottom-left
/// with Y up, and a page's <c>/Rotate</c>, <c>/CropBox</c> and any view rotation all compose
/// before Core's frame is reached. Adapters must transform once, at the boundary, and stamp the
/// result with the <see cref="CoordinateFrameId"/> it is in — see ADR-0005.
/// </remarks>
public readonly record struct PageRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public float MidX => (Left + Right) / 2f;
    public float MidY => (Top + Bottom) / 2f;
    public float Area => Math.Max(0f, Width) * Math.Max(0f, Height);

    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public static PageRect FromXYWH(float x, float y, float w, float h) => new(x, y, x + w, y + h);

    public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public bool Intersects(PageRect o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;

    /// <summary>Inflates (or deflates, for a negative delta) on both axes. Never inverts the rect.</summary>
    public PageRect Inflated(float dx, float dy)
    {
        float l = Left - dx, t = Top - dy, r = Right + dx, b = Bottom + dy;
        if (r < l) { float m = (l + r) / 2f; l = r = m; }
        if (b < t) { float m = (t + b) / 2f; t = b = m; }
        return new PageRect(l, t, r, b);
    }

    /// <summary>The smallest rect containing both. Disjoint inputs yield a rect covering the gap.</summary>
    public PageRect Union(PageRect o) => new(
        Math.Min(Left, o.Left), Math.Min(Top, o.Top),
        Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    /// <summary>
    /// The union of a set of rects, or null when the set is empty. Used only for framing decisions
    /// (what the camera must cover) — never stored as an anchor's geometry, because a union over
    /// unrelated lines or columns is a large rectangle over mostly blank page.
    /// </summary>
    public static PageRect? UnionAll(IReadOnlyList<PageRect> rects)
    {
        if (rects is null || rects.Count == 0) return null;
        var u = rects[0];
        for (int i = 1; i < rects.Count; i++) u = u.Union(rects[i]);
        return u;
    }

    public PageRect Translate(float dx, float dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
}

/// <summary>
/// Identifies the coordinate frame a piece of geometry is expressed in, so a rect is never
/// silently reinterpreted after a rotation, a crop-box change or a re-extraction.
/// </summary>
/// <param name="Units">Measurement unit. Always <c>"page-point"</c> today; carried so a future raster-space frame is a new value rather than a reinterpretation.</param>
/// <param name="Origin">Corner the Y axis grows from. Core's displayed frame is <c>"top-left"</c>; raw PDF user space would be <c>"bottom-left"</c>.</param>
/// <param name="PageRotationTurns">The page's own <c>/Rotate</c>, in clockwise quarter-turns.</param>
/// <param name="ViewRotationTurns">Extra user-requested rotation composed on top of <c>/Rotate</c>.</param>
/// <param name="CropApplied">True when the page crop box has already been applied to the coordinates.</param>
public readonly record struct CoordinateFrameId(
    string Units,
    string Origin,
    int PageRotationTurns,
    int ViewRotationTurns,
    bool CropApplied)
{
    /// <summary>The frame RailReaderCore's analysis and display geometry uses.</summary>
    public static CoordinateFrameId DisplayedPagePoints(int pageRotationTurns = 0, int viewRotationTurns = 0)
        => new("page-point", "top-left", Normalise(pageRotationTurns), Normalise(viewRotationTurns), CropApplied: true);

    private static int Normalise(int turns) => ((turns % 4) + 4) % 4;

    /// <summary>
    /// True when two frames describe the same space. Geometry may only be compared, unioned or
    /// hit-tested when this holds; otherwise it must be transformed first.
    /// </summary>
    public bool IsCompatibleWith(CoordinateFrameId other) => this == other;

    public override string ToString()
        => $"{Units}:{Origin}:rot{PageRotationTurns}+{ViewRotationTurns}:{(CropApplied ? "crop" : "nocrop")}";
}
