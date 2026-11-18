namespace RailNarrator.Domain;

/// <summary>
/// The narration engine's state. Explicit and total: every transition the UI can observe is a
/// value here, so "what is it doing right now" is answerable rather than inferred from a spinner.
/// </summary>
public enum PlaybackState
{
    /// <summary>Nothing loaded or stopped by the user.</summary>
    Idle,

    /// <summary>Extraction, planning or first synthesis is happening. No audio yet.</summary>
    Preparing,

    /// <summary>Audio is expected imminently but the buffer ran dry. Reading progress is frozen until it refills.</summary>
    Buffering,

    /// <summary>Audible samples are being presented.</summary>
    Playing,

    /// <summary>Paused by the user. Position is retained.</summary>
    Paused,

    /// <summary>A seek is in flight. Late audio from the previous position must not play.</summary>
    Seeking,

    /// <summary>The last audible sample has drained. Not reached while audio is still queued.</summary>
    Completed,

    /// <summary>A recoverable failure. Carries a message and the options retry / edit / skip.</summary>
    Error,
}

/// <summary>
/// How the camera relates to the reading position.
/// </summary>
public enum FollowMode
{
    /// <summary>The camera never moves on its own.</summary>
    Off,

    /// <summary>Move only when the active cue would leave the safe zone, and only as far as needed.</summary>
    KeepVisible,

    /// <summary>
    /// Track the reading position along its line, rail-style, for high-magnification reading.
    /// Still only moves when the cue leaves the safe zone: a rail that re-centres on every word is
    /// unreadable.
    /// </summary>
    ReadingRail,
}

/// <summary>
/// What the engine is doing, for the UI. A snapshot, not a live object: the UI polls or subscribes
/// and renders this, and never keeps its own competing idea of the reading position.
/// </summary>
/// <param name="State">Current state.</param>
/// <param name="SegmentId">The segment being spoken, or the one that will be next.</param>
/// <param name="PositionSeconds">Media position in the current clip's own timebase. Frozen during an underflow.</param>
/// <param name="ClipDurationSeconds">Length of the current clip.</param>
/// <param name="SegmentIndex">Zero-based index of <paramref name="SegmentId"/> in the plan.</param>
/// <param name="SegmentCount">Total segments, for progress display.</param>
/// <param name="ActiveCue">The cue that should be highlighted, or null when only the whole segment is known.</param>
/// <param name="ActiveAnchorIds">Source regions to highlight right now.</param>
/// <param name="CueGranularity">What the highlight may honestly claim to point at.</param>
/// <param name="PlaybackRate">Pitch-preserving speed multiplier, 1.0 for normal.</param>
/// <param name="FollowMode">Current camera policy.</param>
/// <param name="FollowSuspended">True when manual browsing has suspended following. Audio may still be playing.</param>
/// <param name="BufferedAheadSeconds">How much audio is ready beyond the current position.</param>
/// <param name="Message">User-facing explanation of <see cref="State"/>, e.g. "Loading voice model".</param>
/// <param name="Error">Set when <see cref="State"/> is <see cref="PlaybackState.Error"/>.</param>
public sealed record PlaybackSnapshot(
    PlaybackState State,
    string? SegmentId,
    double PositionSeconds,
    double ClipDurationSeconds,
    int SegmentIndex,
    int SegmentCount,
    TimedCue? ActiveCue,
    IReadOnlyList<string> ActiveAnchorIds,
    CueGranularity CueGranularity,
    double PlaybackRate,
    FollowMode FollowMode,
    bool FollowSuspended,
    double BufferedAheadSeconds,
    string? Message,
    NarrationError? Error)
{
    public long Generation { get; init; }
    public bool IsAudible => State is PlaybackState.Playing;

    public bool IsBusy => State is PlaybackState.Preparing or PlaybackState.Buffering or PlaybackState.Seeking;

    /// <summary>Progress through the whole plan, 0..1. Segment-based, so it advances even before a clip is timed.</summary>
    public double PlanProgress => SegmentCount <= 0
        ? 0
        : Math.Clamp((SegmentIndex + (ClipDurationSeconds <= 0 ? 0 : PositionSeconds / ClipDurationSeconds)) / SegmentCount, 0, 1);

    public static PlaybackSnapshot Idle { get; } = new(
        PlaybackState.Idle, null, 0, 0, 0, 0, null, [], CueGranularity.Sentence, 1.0,
        FollowMode.KeepVisible, FollowSuspended: false, 0, null, null);
}

/// <summary>
/// A recoverable failure, with the options the user should be given.
/// </summary>
/// <param name="Code">Machine-readable kind, so a caller can branch without parsing text.</param>
/// <param name="Message">User-facing, and safe to show verbatim. Must not contain document passages or credentials.</param>
/// <param name="SegmentId">The segment that failed, when one did.</param>
/// <param name="CanRetry">A retry could plausibly succeed (endpoint was busy, model still loading).</param>
/// <param name="CanEdit">The user can correct the input — a maths transcription, say — and continue.</param>
/// <param name="CanSkip">Skipping this segment yields a still-coherent narration.</param>
/// <remarks>
/// A maths region that could not be read reliably is reported here with an explicit announcement
/// offered, never silently fed to the speech provider as LaTeX and passed off as natural
/// narration.
/// </remarks>
public sealed record NarrationError(
    NarrationErrorCode Code,
    string Message,
    string? SegmentId,
    bool CanRetry,
    bool CanEdit,
    bool CanSkip)
{
    public static NarrationError MathsUnreadable(string segmentId, string detail) => new(
        NarrationErrorCode.MathsUnreadable,
        $"This equation could not be read reliably. {detail}",
        segmentId, CanRetry: true, CanEdit: true, CanSkip: true);

    public static NarrationError ProviderUnavailable(string detail) => new(
        NarrationErrorCode.SpeechProviderUnavailable,
        $"The speech service is not available. {detail}",
        null, CanRetry: true, CanEdit: false, CanSkip: true);

    public static NarrationError AudioDeviceFailed(string detail) => new(
        NarrationErrorCode.AudioDeviceFailure,
        $"Audio output failed. {detail}",
        null, CanRetry: true, CanEdit: false, CanSkip: false);

    public static NarrationError SpeechSynthesisFailed(string detail, string? segmentId = null) => new(
        NarrationErrorCode.SpeechSynthesisFailed,
        $"Speech could not be produced for this passage. {detail}",
        segmentId, CanRetry: true, CanEdit: true, CanSkip: true);
}

/// <summary>Failure kinds the UI and tests can branch on.</summary>
public enum NarrationErrorCode
{
    Unknown,
    ExtractionFailed,
    NoNarratableContent,
    MathsUnreadable,
    MathsTranscriptionFailed,
    VerbalisationRejected,
    SpeechProviderUnavailable,
    SpeechSynthesisFailed,
    AudioDeviceFailure,
    AlignmentUnavailable,
    Cancelled,
}

/// <summary>
/// Tuning for the camera-follow policy.
/// </summary>
/// <param name="SafeZone">
/// Fraction of the viewport, per edge, that the active cue is allowed to occupy before the camera
/// moves. A cue inside the inset region leaves the camera alone entirely; this is what stops the
/// view sliding on every word.
/// </param>
/// <param name="HysteresisPoints">
/// Extra slack in page points, applied after the safe zone. The camera does not move until the cue
/// is this far outside the zone, and having moved it will not move back for the same distance — so
/// a cue sitting exactly on a boundary cannot oscillate the view.
/// </param>
/// <param name="MinMovePoints">Moves smaller than this are dropped as invisible.</param>
/// <param name="MaxAnimationSeconds">Upper bound on a follow animation. Reduced-motion settings lower it to zero.</param>
/// <param name="ReduceMotion">Jump instead of animating.</param>
/// <param name="VerticalBias">
/// Where to place the cue vertically when a move is needed, as a fraction of the viewport height.
/// 0.5 centres it; slightly above centre reads better for continuous text.
/// </param>
public sealed record FollowOptions(
    double SafeZone = 0.12,
    double HysteresisPoints = 2.0,
    double MinMovePoints = 0.5,
    double MaxAnimationSeconds = 0.35,
    bool ReduceMotion = false,
    double VerticalBias = 0.42)
{
    public static FollowOptions Default { get; } = new();

    public static FollowOptions NoMotion { get; } = new(ReduceMotion: true, MaxAnimationSeconds: 0);

    public FollowOptions WithSafeZoneClamped()
        => this with { SafeZone = Math.Clamp(SafeZone, 0, 0.4) };
}

/// <summary>The part of a page currently on screen, in page points.</summary>
/// <param name="PageIndex">Zero-based page the viewport is anchored to.</param>
/// <param name="Visible">The visible region of that page, in displayed page points.</param>
public readonly record struct PageViewport(int PageIndex, PageRect Visible);

/// <summary>
/// A camera movement decision.
/// </summary>
/// <param name="ShouldMove">False means the camera is left exactly where the user put it.</param>
/// <param name="TargetVisible">The visible region to move to, in the same page-point frame.</param>
/// <param name="AnimationSeconds">0 when the move should be instantaneous.</param>
/// <param name="Reason">Why the decision was made, for diagnostics. Never shown as an error.</param>
public sealed record FollowDecision(bool ShouldMove, PageViewport TargetVisible, double AnimationSeconds, string Reason)
{
    public static FollowDecision Hold(string reason) => new(false, default, 0, reason);
}

/// <summary>
/// Decides when and where the camera follows the reading position.
/// </summary>
/// <remarks>
/// <para>
/// Pure geometry over page points, so it is fully testable without a renderer and identical
/// whatever the zoom or DPI. It follows <b>the active reliable cue</b>, not the midpoint of a whole
/// paragraph: a paragraph's midpoint can be a screen away from the word being spoken.
/// </para>
/// <para>
/// Two rules matter more than the rest. First, nothing moves while the cue is inside the safe zone,
/// which is what makes the view stable enough to read. Second, a move never changes magnification
/// (at 800% the camera pans along the line rather than zooming out to fit it), and it leads: the cue
/// goes to the zone's top and left edge, so the text still to be read is in view and the next move is
/// a screenful away. A minimum move kept the word pinned to the edge it had crossed.
/// </para>
/// </remarks>
public static class CameraFollowPlanner
{
    /// <summary>
    /// Plans one follow step.
    /// </summary>
    /// <param name="current">Where the camera is now.</param>
    /// <param name="cueRects">
    /// The active cue's source rects, on <paramref name="current"/>'s page. Disjoint; their union is
    /// used only to decide <i>whether</i> a move is needed, never as the target, so a cue split
    /// across two columns does not centre the gutter.
    /// </param>
    /// <param name="cuePage">Page the cue rects are on.</param>
    /// <param name="mode">Follow policy.</param>
    /// <param name="options">Safe zone, hysteresis and motion limits.</param>
    public static FollowDecision Plan(
        PageViewport current,
        IReadOnlyList<PageRect> cueRects,
        int cuePage,
        FollowMode mode,
        FollowOptions options)
    {
        ArgumentNullException.ThrowIfNull(cueRects);
        options ??= FollowOptions.Default;

        if (mode == FollowMode.Off) return FollowDecision.Hold("follow is off");
        if (cueRects.Count == 0) return FollowDecision.Hold("cue has no geometry");

        // A cue on another page is a page transition: follow reading order and re-anchor there
        // rather than panning across the intervening margin or the adjacent column.
        if (cuePage != current.PageIndex)
            return new FollowDecision(true, current with { PageIndex = cuePage }, Animate(options), $"page transition to {cuePage + 1}");

        var visible = current.Visible;
        if (visible.Width <= 0 || visible.Height <= 0) return FollowDecision.Hold("viewport is degenerate");

        var bounds = PageRect.UnionAll(cueRects)!.Value;
        var zone = Inset(visible, options.SafeZone, options.HysteresisPoints);

        // Inside the safe zone: the whole point of the zone. Do not move.
        if (Within(bounds, zone)) return FollowDecision.Hold("cue is inside the safe zone");

        double dx = 0, dy = 0;

        // Horizontal: the cue starts at the zone's left, so the rest of its line is ahead in view.
        // Moving only far enough to clear the right edge kept the word being read pinned there and
        // panned on every word. Preserves magnification.
        if (bounds.Right > zone.Right || bounds.Left < zone.Left) dx = bounds.Left - zone.Left;

        // Vertical: in rail mode place the cue at the reader's preferred height; in keep-visible
        // mode put its line at the top of the zone, so what follows is in view and the camera moves
        // about once a screenful rather than once a line.
        if (bounds.Bottom > zone.Bottom || bounds.Top < zone.Top)
        {
            dy = mode == FollowMode.ReadingRail
                ? bounds.MidY - (visible.Top + visible.Height * options.VerticalBias)
                : bounds.Top - zone.Top;
        }

        if (Math.Abs(dx) < options.MinMovePoints && Math.Abs(dy) < options.MinMovePoints)
            return FollowDecision.Hold("move is below the minimum");

        var target = visible.Translate((float)dx, (float)dy);
        return new FollowDecision(
            true,
            new PageViewport(current.PageIndex, target),
            Animate(options),
            $"cue left the safe zone; moved {dx:F1}pt x, {dy:F1}pt y");
    }

    /// <summary>
    /// Frames a whole unit — the "fit current line or equation" command, and the answer to a long
    /// sentence at high magnification. Returns the zoom that fits the unit and the visible region
    /// at that zoom, or null when fitting would need a magnification below <paramref name="minZoom"/>.
    /// </summary>
    /// <remarks>
    /// When a unit cannot fit without violating the user's minimum magnification the caller must
    /// offer line or phrase-level narration, or a focus pane. It must not pan according to an
    /// invented speech rate, and it must not silently drop the user's zoom.
    /// </remarks>
    public static (double Zoom, PageRect Visible)? FrameUnit(
        PageRect unitBounds, PageRect viewportSizeAtZoom1, double minZoom, double paddingPoints = 6)
    {
        if (unitBounds.IsEmpty) return null;
        var padded = unitBounds.Inflated((float)paddingPoints, (float)paddingPoints);
        double zx = viewportSizeAtZoom1.Width / padded.Width;
        double zy = viewportSizeAtZoom1.Height / padded.Height;
        double zoom = Math.Min(zx, zy);
        if (zoom < minZoom) return null;

        // Centre the unit in the viewport at the chosen zoom.
        double vw = viewportSizeAtZoom1.Width / zoom, vh = viewportSizeAtZoom1.Height / zoom;
        return (zoom, new PageRect(
            padded.MidX - (float)(vw / 2), padded.MidY - (float)(vh / 2),
            padded.MidX + (float)(vw / 2), padded.MidY + (float)(vh / 2)));
    }

    private static double Animate(FollowOptions o)
        => o.ReduceMotion ? 0 : Math.Clamp(o.MaxAnimationSeconds, 0, 1.5);

    private static PageRect Inset(PageRect r, double fraction, double extraPoints)
    {
        double ix = r.Width * fraction + extraPoints;
        double iy = r.Height * fraction + extraPoints;
        // Never invert: an inset larger than the viewport degenerates to the viewport itself.
        if (ix * 2 >= r.Width || iy * 2 >= r.Height) return r;
        return new PageRect(r.Left + (float)ix, r.Top + (float)iy, r.Right - (float)ix, r.Bottom - (float)iy);
    }

    private static bool Within(PageRect inner, PageRect outer)
        => inner.Left >= outer.Left && inner.Right <= outer.Right
        && inner.Top >= outer.Top && inner.Bottom <= outer.Bottom;
}
