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
/// <remarks>
/// There used to be two following modes, a safe-zone "keep visible" and a "reading rail". Both
/// jumped: the view stood still and then leapt a screenful. One gliding mode replaced them (see
/// <see cref="CameraFollowPlanner"/>). Settings saved with the old names still load, as
/// <see cref="Follow"/>.
/// </remarks>
public enum FollowMode
{
    /// <summary>The camera never moves on its own.</summary>
    Off,

    /// <summary>
    /// The camera glides with the reading, in the manner of RailReader's auto-scroll: the line being
    /// read sits at a steady height, a line too wide for the view is panned along as its words are
    /// spoken, and a new line or passage is reached by an eased glide rather than a jump.
    /// </summary>
    Follow,
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
    /// <summary>
    /// The transport itself is playing, whatever <see cref="State"/> says. The state is the last
    /// thing reported, and a report about a passage ahead (its speech failed) replaces "Playing"
    /// while the current passage is still being read.
    /// </summary>
    public bool TransportPlaying { get; init; }

    /// <summary>
    /// Audio is being heard. What a play/pause control must go by: one that went by
    /// <see cref="State"/> offered Play during such a report, and Play is ignored while audio
    /// plays, so the reading could not be paused.
    /// </summary>
    public bool IsAudible => State is PlaybackState.Playing || TransportPlaying;

    public bool IsBusy => State is PlaybackState.Preparing or PlaybackState.Buffering or PlaybackState.Seeking;

    /// <summary>Progress through the whole plan, 0..1. Segment-based, so it advances even before a clip is timed.</summary>
    public double PlanProgress => SegmentCount <= 0
        ? 0
        : Math.Clamp((SegmentIndex + (ClipDurationSeconds <= 0 ? 0 : PositionSeconds / ClipDurationSeconds)) / SegmentCount, 0, 1);

    /// <summary>
    /// Set while a user-requested start (Play, a seek, a voice change, or the next passage not being
    /// ready in time) is waiting for a passage's audio: what the wait is for and how long it has
    /// lasted. Null whenever nothing is being waited for.
    /// </summary>
    public PreparationProgress? Preparation { get; init; }

    public static PlaybackSnapshot Idle { get; } = new(
        PlaybackState.Idle, null, 0, 0, 0, 0, null, [], CueGranularity.Sentence, 1.0,
        FollowMode.Follow, FollowSuspended: false, 0, null, null);
}

/// <summary>What a passage's preparation is doing at the moment.</summary>
public enum PreparationStage
{
    /// <summary>Waiting for maths further ahead to be interpreted before starting (pre-roll).</summary>
    InterpretingAhead,

    /// <summary>The passage itself is with the maths interpreter.</summary>
    Interpreting,

    /// <summary>Loading the passage's audio from the speech cache.</summary>
    LoadingCached,

    /// <summary>The speech provider is producing the passage's audio.</summary>
    Synthesising,

    /// <summary>The forced aligner is timing the passage's words.</summary>
    Aligning,
}

/// <summary>
/// A wait the reader asked for, made visible: which passage, what it is waiting on, and for how long.
/// </summary>
/// <param name="SegmentId">The passage that will play when the wait is over.</param>
/// <param name="SegmentIndex">Its zero-based index in the plan.</param>
/// <param name="Stage">What is being done for it right now.</param>
/// <param name="ElapsedSeconds">Wall time since the wait began.</param>
/// <param name="ReadySeconds">For <see cref="PreparationStage.InterpretingAhead"/>: narration already ready ahead.</param>
/// <param name="TargetSeconds">For <see cref="PreparationStage.InterpretingAhead"/>: how much is wanted before starting.</param>
public sealed record PreparationProgress(
    string SegmentId,
    int SegmentIndex,
    PreparationStage Stage,
    double ElapsedSeconds,
    double ReadySeconds = 0,
    double TargetSeconds = 0)
{
    /// <summary>One line for the reader: the stage in words and the time waited so far.</summary>
    public string Describe()
    {
        string what = Stage switch
        {
            PreparationStage.InterpretingAhead =>
                $"Interpreting the maths ahead ({ReadySeconds:F0} of {TargetSeconds:F0} s ready)",
            PreparationStage.Interpreting => "Interpreting the maths in this passage",
            PreparationStage.LoadingCached => "Loading prepared speech",
            PreparationStage.Synthesising => "Synthesising speech",
            PreparationStage.Aligning => "Timing the words",
            _ => "Preparing",
        };
        return ElapsedSeconds >= 1 ? $"{what} · {ElapsedSeconds:F0} s" : what;
    }
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
/// <param name="VerticalBias">
/// Where the line being read sits, as a fraction of the viewport height from the top. Slightly
/// above centre reads better for continuous text, and leaves the lines still to come in view.
/// </param>
/// <param name="LeadFraction">
/// On a line wider than the view, where the reading point sits, as a fraction of the viewport width
/// from the left: the rest of the line is ahead of it, in view.
/// </param>
/// <param name="LineStartFraction">
/// On a wide line, where the line's start sits when the camera arrives at it, as a fraction of the
/// viewport width from the left. Past the page margin, so the first word is not at the screen edge.
/// </param>
/// <param name="LineEndFraction">
/// On a wide line, the furthest the camera goes: the line's end sits this far across the viewport.
/// Near the middle, so the last words are read where the eye already is, not at the far edge.
/// </param>
/// <param name="MarginFraction">Margin kept around a line that fits in the view, per side, as a fraction of the viewport width.</param>
/// <param name="MinMovePoints">Moves smaller than this are dropped as invisible.</param>
/// <param name="GlideSeconds">
/// Time constant of the glide along a line: roughly how long the camera lags the reading point.
/// It smooths the pace changes from word to word into a steady pan.
/// </param>
/// <param name="ReturnGlideSeconds">
/// Time constant of the move to a new line or passage: RailReader's carriage return, which is
/// quicker than the pan so the start of the next line is in place when its first word is spoken.
/// </param>
/// <param name="ReduceMotion">Jump instead of gliding.</param>
public sealed record FollowOptions(
    double VerticalBias = 0.42,
    double LeadFraction = 0.3,
    double LineStartFraction = 0.2,
    double LineEndFraction = 0.55,
    double MarginFraction = 0.05,
    double MinMovePoints = 0.5,
    double GlideSeconds = 0.3,
    double ReturnGlideSeconds = 0.14,
    bool ReduceMotion = false)
{
    public static FollowOptions Default { get; } = new();

    public static FollowOptions NoMotion { get; } = new(ReduceMotion: true, GlideSeconds: 0, ReturnGlideSeconds: 0);
}

/// <summary>The part of a page currently on screen, in page points.</summary>
/// <param name="PageIndex">Zero-based page the viewport is anchored to.</param>
/// <param name="Visible">The visible region of that page, in displayed page points.</param>
public readonly record struct PageViewport(int PageIndex, PageRect Visible);

/// <summary>
/// What the camera is asked to follow: the active cue, and the passage it belongs to, on one page.
/// </summary>
/// <param name="PageIndex">The page the cue is on.</param>
/// <param name="CueRects">The active cue's rects on that page. Disjoint; for a word, its box.</param>
/// <param name="PassageRects">
/// The whole passage's rects on that page, one per line fragment. Used to find the extent of the
/// line a word is on, so the camera knows whether that line fits in the view.
/// </param>
/// <param name="Granularity">
/// What the cue honestly points at. Only a <see cref="CueGranularity.Word"/> cue carries measured
/// timing within a line; anything coarser is framed and held, never panned at a guessed rate.
/// </param>
public sealed record FollowTarget(
    int PageIndex,
    IReadOnlyList<PageRect> CueRects,
    IReadOnlyList<PageRect> PassageRects,
    CueGranularity Granularity)
{
    /// <summary>The next aligned word's rects on the same page, when there is one.</summary>
    public IReadOnlyList<PageRect> NextCueRects { get; init; } = [];

    /// <summary>
    /// How far the media clock is from this word's measured start to the next word's measured start,
    /// 0..1 (to this word's end when there is no next word). Lets the camera travel between two
    /// measured positions at the measured pace instead of stepping word by word. Camera only: the
    /// highlight still changes at the measured word boundary.
    /// </summary>
    public double Progress { get; init; }
}

/// <summary>
/// A camera movement decision.
/// </summary>
/// <param name="ShouldMove">False means the camera is already where it should be (or follow is off).</param>
/// <param name="TargetVisible">
/// The visible region to glide to, in the same page-point frame. Also set when the camera is already
/// within the minimum move of it, so a glide toward an older target can be redirected; default when
/// there is no target at all.
/// </param>
/// <param name="GlideSeconds">0 when the move should be instantaneous.</param>
/// <param name="Reason">Why the decision was made, for diagnostics. Never shown as an error.</param>
public sealed record FollowDecision(bool ShouldMove, PageViewport TargetVisible, double GlideSeconds, string Reason)
{
    public static FollowDecision Hold(string reason) => new(false, default, 0, reason);
}

/// <summary>
/// Decides where the camera should be for the reading position. The host glides toward that
/// target every frame (<see cref="CameraGlide"/>), so the view moves continuously instead of
/// standing still and then jumping.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on RailReader's auto-scroll: the line being read is the rail. Vertically, that line is
/// kept at a steady reading height, so the page rolls up by one line as each line is finished.
/// Horizontally, a line that fits in the view is simply kept in view (at fit-width zoom nothing
/// moves sideways); a line wider than the view is panned along, keeping the word being spoken a
/// little left of centre with the rest of the line ahead of it. At the end of a line the next
/// line's start is reached by the same glide — RailReader's carriage return.
/// </para>
/// <para>
/// The pan along a line is paced by measured word timings only. A sentence or equation cue has no
/// timing within it, so its first line is framed and held (ADR-0004): sweeping across it at a rate
/// derived from the clip length would be an invented speech rate presented as following.
/// </para>
/// <para>
/// Pure geometry over page points, so it is testable without a renderer and identical at every
/// zoom and DPI. A move never changes magnification.
/// </para>
/// </remarks>
public static class CameraFollowPlanner
{
    /// <summary>
    /// Plans where the camera should be.
    /// </summary>
    /// <param name="current">Where the camera is now. Its size is kept; only its position changes.</param>
    /// <param name="target">The cue and its passage.</param>
    /// <param name="mode">Follow policy.</param>
    /// <param name="options">Reading height, lead, margins and motion.</param>
    public static FollowDecision Plan(PageViewport current, FollowTarget target, FollowMode mode, FollowOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= FollowOptions.Default;

        if (mode == FollowMode.Off) return FollowDecision.Hold("follow is off");
        var cueRects = target.CueRects.Where(r => !r.IsEmpty).ToList();
        if (cueRects.Count == 0) return FollowDecision.Hold("cue has no geometry");

        var visible = current.Visible;
        double vw = visible.Width, vh = visible.Height;
        if (vw <= 0 || vh <= 0) return FollowDecision.Hold("viewport is degenerate");

        bool word = target.Granularity == CueGranularity.Word;
        // The rail: the line the reading is on. For a word, its own box; for a coarser cue, its first
        // line in reading order (top-most, then left-most).
        var anchor = word
            ? cueRects[0]
            : cueRects.OrderBy(r => r.Top).ThenBy(r => r.Left).First();
        var line = LineExtent(anchor, target.PassageRects);
        double margin = vw * options.MarginFraction;

        // The reading point along the line: between this word and the next, at the measured pace.
        // The last word of a line is crossed from its start to its end.
        double readingX = anchor.Left;
        if (word)
        {
            double p = Math.Clamp(target.Progress, 0, 1);
            var next = target.NextCueRects.FirstOrDefault(r => !r.IsEmpty);
            bool nextOnLine = !next.IsEmpty && VerticalOverlap(next, anchor) >= 0.5 * Math.Min(next.Height, anchor.Height)
                && next.Left >= anchor.Left;
            readingX = nextOnLine ? anchor.Left + (next.Left - anchor.Left) * p : anchor.Left + anchor.Width * p;
        }

        // Vertical. A coarse cue that fits (an equation, a short sentence) is framed whole around the
        // reading height; otherwise the anchor line goes to the reading height.
        double top;
        var cueBounds = PageRect.UnionAll(cueRects)!.Value;
        if (!word && cueBounds.Height + 2 * margin <= vh)
            top = Math.Clamp(cueBounds.MidY - vh * options.VerticalBias, cueBounds.Bottom + margin - vh, cueBounds.Top - margin);
        else
            // The line's middle, not the word's: a word's box is its ink, and following it would bob
            // the view up and down from "a" to "Typography".
            top = line.MidY - vh * options.VerticalBias;

        // Horizontal.
        double left = visible.Left;
        string how;
        if (line.Width + 2 * margin <= vw)
        {
            // The whole line fits: keep it in view, moving as little as possible.
            left = Math.Clamp(left, line.Right + margin - vw, line.Left - margin);
            how = "line fits";
        }
        else if (word)
        {
            // Wider than the view: pan along it with the reading point, the rest of the line ahead.
            // The camera starts with the line's start in from the left edge and stops with its end
            // near the middle of the view, past the page margins on either side.
            left = Math.Clamp(readingX - vw * options.LeadFraction,
                line.Left - vw * options.LineStartFraction, Math.Max(line.Left - vw * options.LineStartFraction, line.Right - vw * options.LineEndFraction));
            how = "panning along the line";
        }
        else
        {
            // No timing within the cue: frame the start of its line and hold.
            left = line.Left - vw * options.LineStartFraction;
            how = "holding at the line start";
        }

        bool newPage = target.PageIndex != current.PageIndex;
        double dx = left - visible.Left, dy = top - visible.Top;
        var to = new PageRect((float)left, (float)top, (float)(left + vw), (float)(top + vh));
        // A new page is a cut: the old page's geometry means nothing on the new one, so there is
        // nothing to glide from.
        // A new line (or a jump back to a line's start) is a carriage return: quicker than the pan.
        bool carriageReturn = Math.Abs(dy) > Math.Max(line.Height, 1) * 0.5 || dx < -vw * 0.25;
        double glide = newPage || options.ReduceMotion ? 0
            : Math.Clamp(carriageReturn ? options.ReturnGlideSeconds : options.GlideSeconds, 0, 2);

        // Already there. The target is still given, so a glide in progress toward an older target
        // can be redirected here instead of carrying on past the reading line.
        if (!newPage && Math.Abs(dx) < options.MinMovePoints && Math.Abs(dy) < options.MinMovePoints)
            return new FollowDecision(false, new PageViewport(target.PageIndex, to), glide, $"already on the reading line ({how})");

        return new FollowDecision(true, new PageViewport(target.PageIndex, to), glide,
            newPage ? $"page transition to {target.PageIndex + 1}; {how}" : $"{how}; glide {dx:F1}pt x, {dy:F1}pt y");
    }

    /// <summary>
    /// The extent of the line <paramref name="anchor"/> is on: the anchor plus every
    /// passage rect that shares most of its height. A rect in the other column of a two-column page
    /// shares the height too, so only rects joined to the anchor by small gaps are taken.
    /// </summary>
    private static PageRect LineExtent(PageRect anchor, IReadOnlyList<PageRect> passage)
    {
        var sameLine = passage
            .Where(r => !r.IsEmpty && VerticalOverlap(r, anchor) >= 0.5 * Math.Min(r.Height, anchor.Height))
            .OrderBy(r => r.Left)
            .ToList();
        var line = anchor;
        double gap = Math.Max(anchor.Height, 1) * 1.5;
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var r in sameLine)
            {
                if (r.Left >= line.Left && r.Right <= line.Right) continue;
                if (r.Left - line.Right <= gap && line.Left - r.Right <= gap)
                {
                    line = new PageRect(Math.Min(line.Left, r.Left), Math.Min(line.Top, r.Top), Math.Max(line.Right, r.Right), Math.Max(line.Bottom, r.Bottom));
                    grew = true;
                }
            }
        }
        return line;
    }

    private static double VerticalOverlap(PageRect a, PageRect b)
        => Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

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
}

/// <summary>
/// One axis of the camera's glide: a critically damped spring toward a target, stepped once per
/// frame with the frame's own interval.
/// </summary>
/// <remarks>
/// A critically damped spring eases in and out without overshoot, and a target that moves while the
/// camera is on its way (the next word) is followed without a restart or a jolt — the velocity
/// carries over. Stepped by the compositor's frame interval rather than a wall clock read on the UI
/// thread, for the reason RailReader gives: tick time wobbles within a frame, presentation time does
/// not, and the wobble shows as stutter at high magnification.
/// </remarks>
public static class CameraGlide
{
    /// <summary>Largest frame interval one step integrates; a stall must not fling the camera.</summary>
    public const double MaxStepSeconds = 0.1;

    /// <summary>
    /// Advances <paramref name="position"/> toward <paramref name="target"/> by
    /// <paramref name="dtSeconds"/>, updating <paramref name="velocity"/>. Returns the new position.
    /// </summary>
    /// <param name="glideSeconds">Time constant: about how long the camera takes to arrive. 0 jumps.</param>
    public static double Step(double position, double target, ref double velocity, double glideSeconds, double dtSeconds)
    {
        if (glideSeconds <= 0 || double.IsNaN(position))
        {
            velocity = 0;
            return target;
        }
        double dt = Math.Clamp(dtSeconds, 0, MaxStepSeconds);
        if (dt <= 0) return position;

        // Closed-form step of a critically damped spring (exact for a fixed target over dt), with
        // the smoothing time as its time constant. See Game Programming Gems 4, "Critically Damped
        // Ease-In/Ease-Out Smoothing".
        double omega = 2.0 / glideSeconds;
        double x = omega * dt;
        double decay = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
        double change = position - target;
        double temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * decay;
        double next = target + (change + temp) * decay;

        // Never overshoot the target.
        if ((target - position > 0) == (next > target))
        {
            next = target;
            velocity = 0;
        }
        return next;
    }

    /// <summary>True when the camera is close enough to the target, and slow enough, to stop stepping.</summary>
    public static bool HasArrived(double position, double target, double velocity, double tolerance = 0.05)
        => Math.Abs(position - target) <= tolerance && Math.Abs(velocity) <= tolerance * 10;
}

/// <summary>
/// Gives the highlight boxes of a line one height, the same for every line of a paragraph.
/// </summary>
/// <remarks>
/// A word's box is its glyphs' ink: "a" is short, "Typography" tall and deep, so a box sized to each
/// word jumps up and down as reading moves along a line. Each box is widened vertically to the line
/// it sits on (the union of its paragraph's boxes on that line), and every line takes the
/// paragraph's typical full line height from its top, so a line without descenders gets the same
/// depth as one with them. Only the drawing changes; anchors and hit-testing keep the real geometry.
/// </remarks>
public static class HighlightLines
{
    /// <summary>Returns <paramref name="rects"/> with uniform line heights, taken from <paramref name="context"/>.</summary>
    /// <param name="rects">The boxes to draw.</param>
    /// <param name="context">The paragraph's boxes on the same page, including <paramref name="rects"/>.</param>
    public static IReadOnlyList<PageRect> Uniform(IReadOnlyList<PageRect> rects, IReadOnlyList<PageRect> context)
    {
        ArgumentNullException.ThrowIfNull(rects);
        ArgumentNullException.ThrowIfNull(context);
        if (rects.Count == 0) return rects;

        var lines = Lines(context.Where(r => !r.IsEmpty).Concat(rects.Where(r => !r.IsEmpty)));
        if (lines.Count == 0) return rects;
        var heights = lines.Select(l => (double)l.Height).OrderBy(h => h).ToList();
        double median = heights[heights.Count / 2];
        // The fullest line is the one with both ascenders and descenders; one with a tall formula in
        // it is not typical, so the height is capped near the median.
        double height = Math.Min(heights[^1], median * 1.25);

        var result = new List<PageRect>(rects.Count);
        foreach (var r in rects)
        {
            if (r.IsEmpty) { result.Add(r); continue; }
            var line = lines.FirstOrDefault(l => Overlap(l, r) >= 0.5 * Math.Min(l.Height, r.Height));
            if (line.IsEmpty) { result.Add(r); continue; }
            float top = line.Top;
            float bottom = (float)Math.Max(line.Bottom, top + height);
            result.Add(new PageRect(r.Left, top, r.Right, bottom));
        }
        return result;
    }

    /// <summary>
    /// The boxes of a cue as one shape: <see cref="Uniform"/> boxes, the words on a line joined into
    /// one band across the spaces between them, and consecutive lines of a column joined across the
    /// leading between them. Drawn as a union, nothing is tinted twice where padded word boxes meet,
    /// which is what made a passage highlighted all at once look like a stack of overlapping boxes.
    /// </summary>
    /// <remarks>
    /// Only whitespace between the cue's own boxes is added. A gap wider than the line height (a
    /// column gutter) is never bridged, nor one holding a box of <paramref name="context"/> the cue
    /// does not cover: a word the narration is not reading stays uncovered.
    /// </remarks>
    /// <param name="rects">The boxes to draw.</param>
    /// <param name="context">The paragraph's boxes on the same page, including <paramref name="rects"/>.</param>
    public static IReadOnlyList<PageRect> Passage(IReadOnlyList<PageRect> rects, IReadOnlyList<PageRect> context)
    {
        ArgumentNullException.ThrowIfNull(rects);
        ArgumentNullException.ThrowIfNull(context);
        var uniform = Uniform(rects, context).Where(r => !r.IsEmpty).ToList();
        if (uniform.Count == 0) return [];
        var others = context.Where(r => !r.IsEmpty).ToList();

        // Words on one line, left to right, joined while the gap is a space rather than a gutter.
        var bands = new List<PageRect>();
        foreach (var line in LineGroups(uniform))
        {
            line.Sort((a, b) => a.Left.CompareTo(b.Left));
            var band = line[0];
            for (int i = 1; i < line.Count; i++)
            {
                var next = line[i];
                float gap = next.Left - band.Right;
                bool joins = gap <= 0 || (gap <= Math.Max(band.Height, next.Height)
                    && !others.Any(o => o.Left >= band.Right && o.Right <= next.Left
                                     && Overlap(o, band) >= 0.5 * Math.Min(o.Height, band.Height)));
                if (joins) band = band.Union(next);
                else { bands.Add(band); band = next; }
            }
            bands.Add(band);
        }

        // Each band meets the band below it (or above it) half-way across the leading, when the two
        // share some width and the leading is less than a line: the lines of one column.
        var result = bands.ToArray();
        for (int i = 0; i < bands.Count; i++)
        {
            float? below = null, above = null;
            for (int j = 0; j < bands.Count; j++)
            {
                if (i == j || Math.Min(bands[i].Right, bands[j].Right) <= Math.Max(bands[i].Left, bands[j].Left)) continue;
                float limit = 0.75f * Math.Min(bands[i].Height, bands[j].Height);
                float down = bands[j].Top - bands[i].Bottom;
                if (down > 0 && down <= limit && (below is null || down < below)) below = down;
                float up = bands[i].Top - bands[j].Bottom;
                if (up > 0 && up <= limit && (above is null || up < above)) above = up;
            }
            var r = result[i];
            result[i] = new PageRect(r.Left, r.Top - (above ?? 0) / 2, r.Right, r.Bottom + (below ?? 0) / 2);
        }
        return result;
    }

    /// <summary>Groups boxes into lines by vertical overlap, keeping the boxes of each line.</summary>
    private static List<List<PageRect>> LineGroups(IEnumerable<PageRect> boxes)
    {
        var groups = new List<(PageRect Extent, List<PageRect> Boxes)>();
        foreach (var r in boxes.OrderBy(b => b.MidY))
        {
            int i = groups.FindIndex(g => Overlap(g.Extent, r) >= 0.5 * Math.Min(g.Extent.Height, r.Height));
            if (i < 0) groups.Add((r, [r]));
            else { groups[i].Boxes.Add(r); groups[i] = (groups[i].Extent.Union(r), groups[i].Boxes); }
        }
        return groups.Select(g => g.Boxes).ToList();
    }

    /// <summary>Groups boxes into lines by vertical overlap; each line is the union of its boxes' heights.</summary>
    private static List<PageRect> Lines(IEnumerable<PageRect> boxes)
    {
        var lines = new List<PageRect>();
        foreach (var r in boxes.OrderBy(b => b.MidY))
        {
            int i = lines.FindIndex(l => Overlap(l, r) >= 0.5 * Math.Min(l.Height, r.Height));
            if (i < 0) lines.Add(r);
            else lines[i] = lines[i].Union(r);
        }
        return lines;
    }

    private static double Overlap(PageRect a, PageRect b)
        => Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}
