namespace RailNarrator.Domain;

/// <summary>
/// The two distinct mappings narration depends on, kept separate because conflating them is the
/// single most common way a reader highlights the wrong text:
/// <code>
///   audio sample position  ->  spoken script range
///   spoken script range    ->  original PDF source anchors
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// The first mapping is a property of the audio and the script that was synthesised. The second is
/// a property of the plan. Neither is derivable from the other, and neither passes through the PDF's
/// own text: for a paraphrased equation the spoken words bear no positional relationship to the
/// glyphs on the page at all.
/// </para>
/// <para>
/// <see cref="HighlightRects"/> returns the anchors' disjoint rectangles and never their union. A
/// union across lines, columns or pages is a large rectangle over mostly content the narration is
/// not reading, and drawing it would darken text the listener has not heard.
/// </para>
/// </remarks>
public sealed class SourceMap
{
    private readonly Dictionary<string, SourceAnchor> _anchors;
    private readonly Dictionary<string, NarrationSegment> _segments;

    public SourceMap(NarrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        _anchors = plan.Units.SelectMany(u => u.Anchors).DistinctBy(a => a.AnchorId).ToDictionary(a => a.AnchorId);
        _segments = plan.Segments.ToDictionary(s => s.Id);
    }

    public NarrationPlan Plan { get; }

    public SourceAnchor? Anchor(string anchorId) => _anchors.GetValueOrDefault(anchorId);

    public NarrationSegment? Segment(string segmentId) => _segments.GetValueOrDefault(segmentId);

    /// <summary>
    /// Every box of the reading units a segment speaks, on one page: the paragraph around a
    /// sentence, so highlight line heights can be made uniform across it.
    /// </summary>
    public IReadOnlyList<PageRect> UnitRects(string segmentId, int pageIndex)
    {
        if (Segment(segmentId) is not { } segment) return [];
        var rects = new List<PageRect>();
        foreach (var unitId in segment.ReadingUnitIds)
        {
            if (Plan.Unit(unitId) is not { } unit) continue;
            foreach (var a in unit.Anchors)
                if (a.PageIndex == pageIndex) rects.AddRange(a.Rects.Where(r => !r.IsEmpty));
        }
        return rects;
    }

    /// <summary>Every anchor id the plan knows. Used to reject a model response that cites invented sources.</summary>
    public IReadOnlySet<string> KnownAnchorIds => _anchors.Keys.ToHashSet();

    /// <summary>
    /// The disjoint rectangles to highlight for a set of anchors, in reading order. Anchors on other
    /// pages are excluded; the caller draws per page.
    /// </summary>
    public IReadOnlyList<PageRect> HighlightRects(IEnumerable<string> anchorIds, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(anchorIds);
        var rects = new List<PageRect>();
        foreach (var id in anchorIds)
        {
            if (_anchors.TryGetValue(id, out var a) && a.PageIndex == pageIndex)
                rects.AddRange(a.Rects.Where(r => !r.IsEmpty));
        }
        return rects;
    }

    /// <summary>All rectangles for a set of anchors, grouped by page, for an overlay that spans a page break.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<PageRect>> HighlightRectsByPage(IEnumerable<string> anchorIds)
    {
        ArgumentNullException.ThrowIfNull(anchorIds);
        var byPage = new Dictionary<int, List<PageRect>>();
        foreach (var id in anchorIds)
        {
            if (!_anchors.TryGetValue(id, out var a)) continue;
            if (!byPage.TryGetValue(a.PageIndex, out var list)) byPage[a.PageIndex] = list = [];
            list.AddRange(a.Rects.Where(r => !r.IsEmpty));
        }
        return byPage.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PageRect>)kv.Value);
    }

    /// <summary>Pages a set of anchors occupies, in order. More than one means the unit crosses a page break.</summary>
    public IReadOnlyList<int> PagesFor(IEnumerable<string> anchorIds)
    {
        ArgumentNullException.ThrowIfNull(anchorIds);
        return anchorIds
            .Select(id => _anchors.GetValueOrDefault(id)?.PageIndex)
            .Where(p => p.HasValue).Select(p => p!.Value)
            .Distinct().OrderBy(p => p).ToList();
    }

    /// <summary>
    /// The page the camera should show for a set of anchors, or null when none of them has geometry.
    /// </summary>
    /// <param name="anchorIds">The anchors of the cue or passage.</param>
    /// <param name="wholePassage">
    /// False for a cue that is read where it is printed (a word): the first page it has geometry on.
    /// True for a cue with no timing inside it (a sentence, an equation, an interpreted passage):
    /// the page that holds most of it, by highlighted area, and the earlier page on a tie.
    /// </param>
    /// <remarks>
    /// A passage can cross a page break: a lead-in at the foot of one page and its equation at the
    /// head of the next are one interpreted passage. With no timing inside it there is no telling
    /// when the reading crosses the break, and guessing the moment would be an invented timing
    /// (ADR-0004). Holding the first page left the view on the last few words of a page while the
    /// equation overleaf was explained for fifteen seconds. Which page holds most of the passage is
    /// a fact about the page, not a guess about the audio, so that page is framed and held. Pages
    /// whose anchors are all empty rectangles are never chosen: there is nothing on them to frame.
    /// </remarks>
    public int? FramingPage(IEnumerable<string> anchorIds, bool wholePassage)
    {
        ArgumentNullException.ThrowIfNull(anchorIds);
        var areaByPage = new SortedDictionary<int, double>();
        foreach (var id in anchorIds)
        {
            if (!_anchors.TryGetValue(id, out var a)) continue;
            double area = 0;
            bool any = false;
            foreach (var r in a.Rects)
            {
                if (r.IsEmpty) continue;
                any = true;
                area += (double)r.Width * r.Height;
            }
            if (!any) continue;
            areaByPage[a.PageIndex] = areaByPage.GetValueOrDefault(a.PageIndex) + area;
        }
        if (areaByPage.Count == 0) return null;
        if (!wholePassage) return areaByPage.Keys.First();
        int best = -1;
        double bestArea = double.NegativeInfinity;
        foreach (var (page, area) in areaByPage)   // ascending, so the earlier page wins a tie
            if (area > bestArea) { best = page; bestArea = area; }
        return best;
    }

    /// <summary>
    /// The anchors a spoken offset belongs to — the second mapping. Returns every link covering the
    /// offset, so an equation and the clause containing it both light up rather than one of them
    /// winning arbitrarily.
    /// </summary>
    public IReadOnlyList<string> AnchorsForSpokenOffset(string segmentId, int spokenUtf16Offset)
    {
        if (!_segments.TryGetValue(segmentId, out var segment)) return [];
        return segment.SourceLinks
            .Where(l => l.SpokenRange.Contains(spokenUtf16Offset))
            .SelectMany(l => l.AnchorIds)
            .Where(_anchors.ContainsKey)
            .Distinct().ToList();
    }

    /// <summary>
    /// The anchors covering a whole spoken range. Used for coarse cues, where the entire segment or
    /// equation region is highlighted for the duration of its utterance.
    /// </summary>
    public IReadOnlyList<string> AnchorsForSpokenRange(string segmentId, TextRange spokenRange)
    {
        if (!_segments.TryGetValue(segmentId, out var segment)) return [];
        return segment.SourceLinks
            .Where(l => l.SpokenRange.Overlaps(spokenRange))
            .SelectMany(l => l.AnchorIds)
            .Where(_anchors.ContainsKey)
            .Distinct().ToList();
    }

    /// <summary>All anchors of a segment, in link order. The fallback highlight when nothing finer is known.</summary>
    public IReadOnlyList<string> AnchorsForSegment(string segmentId)
        => _segments.TryGetValue(segmentId, out var s)
            ? s.SourceLinks.SelectMany(l => l.AnchorIds).Where(_anchors.ContainsKey).Distinct().ToList()
            : [];

    /// <summary>
    /// The tightest rectangle covering a set of anchors on one page, for camera framing only. Null
    /// when there is no geometry. Never pass this to <see cref="HighlightRects"/>'s callers as a
    /// highlight.
    /// </summary>
    public PageRect? FramingBounds(IEnumerable<string> anchorIds, int pageIndex)
        => PageRect.UnionAll(HighlightRects(anchorIds, pageIndex));
}

/// <summary>
/// What should be highlighted at one instant.
/// </summary>
/// <param name="SegmentId">The segment being spoken.</param>
/// <param name="SampleInClip">Position within that segment's clip, in the clip's own timebase.</param>
/// <param name="Cue">The matched cue, or null when only the whole clip is known.</param>
/// <param name="AnchorIds">Source regions to highlight.</param>
/// <param name="Granularity">What the highlight may honestly claim to point at.</param>
/// <param name="SpokenRange">Range of the script being spoken, when known.</param>
/// <param name="Provenance">Where the timing came from.</param>
/// <param name="Diagnostics">Notes for a diagnostics pane, e.g. why a finer granularity was not used.</param>
public sealed record ActiveCue(
    string SegmentId,
    long SampleInClip,
    TimedCue? Cue,
    IReadOnlyList<string> AnchorIds,
    CueGranularity Granularity,
    TextRange? SpokenRange,
    TimingProvenance Provenance,
    IReadOnlyList<string> Diagnostics)
{
    public long Generation { get; init; }
}

/// <summary>
/// Resolves a clip position to the cue that should be highlighted.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is a pure function of the media position. Nothing here reads a wall clock, counts
/// delivered buffers, or advances on its own, which is what makes "no cue advances while media time
/// is stationary" a structural property rather than a behaviour someone remembered to implement.
/// A time-based auto-scroll running alongside this would violate it; follow mode is owned by the
/// audio clock alone.
/// </para>
/// <para>
/// When a clip has no validated timings the answer is the whole-clip cue at sentence or equation
/// granularity. It never invents word boundaries — not uniformly, not proportionally by character
/// count. An interpolated word position is indistinguishable from a measured one in the UI and
/// wrong in a way the listener cannot see.
/// </para>
/// </remarks>
public sealed class CueScheduler
{
    private readonly SourceMap _sourceMap;
    private readonly Dictionary<string, ClipTiming> _clips = new();
    // Clips are attached from the preparation loop and the playback path concurrently, and
    // resolved from the host's frame loop, so every access is serialised.
    private readonly object _gate = new();

    public CueScheduler(NarrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        _sourceMap = new SourceMap(plan);
    }

    public NarrationPlan Plan { get; }

    public SourceMap SourceMap => _sourceMap;

    /// <summary>
    /// Records the timings for a clip. Only cues that survived <see cref="TimedCueValidator"/> are
    /// accepted; passing unvalidated timings here is a programming error and they are dropped with a
    /// diagnostic rather than trusted.
    /// </summary>
    /// <param name="asset">The synthesised clip.</param>
    /// <param name="validated">Cues already checked against the clip's real frame count and the plan's anchor ids.</param>
    /// <param name="diagnostics">Why a finer granularity is unavailable, when it is.</param>
    public void AttachClip(SpeechAsset asset, IReadOnlyList<TimedCue> validated, IReadOnlyList<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        validated ??= [];
        var segment = _sourceMap.Segment(asset.SegmentId);
        var checkedCues = TimedCueValidator.Validate(validated, asset.FrameCount, segment?.SpokenText ?? "",
            _sourceMap.KnownAnchorIds, provenance: asset.TimingProvenance);
        var notes = new List<string>(diagnostics ?? []);
        if (validated.Count > 0 && !checkedCues.IsUsable)
            notes.Add($"Alignment rejected: script coverage {checkedCues.WordCoverageFraction:P0}, rejected cues {checkedCues.Rejected.Count}.");
        lock (_gate) _clips[asset.SegmentId] = new ClipTiming(asset,
            checkedCues.IsUsable ? checkedCues.Accepted : [], notes);
    }

    /// <summary>Forgets a clip, e.g. after its script was edited.</summary>
    public void DetachClip(string segmentId) { lock (_gate) _clips.Remove(segmentId); }

    /// <summary>True when timings finer than the whole clip are available for a segment.</summary>
    public bool HasSubClipTiming(string segmentId)
    {
        lock (_gate) return _clips.TryGetValue(segmentId, out var c) && c.Cues.Count > 0;
    }

    /// <summary>The finest granularity this scheduler can honestly offer for a segment.</summary>
    public CueGranularity BestGranularity(string segmentId)
    {
        var segment = _sourceMap.Segment(segmentId);
        if (segment is null) return CueGranularity.Region;
        lock (_gate)
            if (_clips.TryGetValue(segmentId, out var clip) && clip.Cues.Count > 0)
                return clip.Cues.Max(c => c.Granularity);
        return segment.IsMaths ? CueGranularity.Equation : CueGranularity.Sentence;
    }

    /// <summary>
    /// Resolves a position within a clip. Returns the whole-segment fallback when the clip is not
    /// yet attached, so a highlight exists from the first audible sample.
    /// </summary>
    public ActiveCue Resolve(string segmentId, long sampleInClip)
    {
        var segment = _sourceMap.Segment(segmentId);
        var fallbackAnchors = _sourceMap.AnchorsForSegment(segmentId);
        var fallbackGranularity = segment is { IsMaths: true } ? CueGranularity.Equation : CueGranularity.Sentence;

        ClipTiming? clip;
        lock (_gate) _clips.TryGetValue(segmentId, out clip);
        if (clip is null)
        {
            return new ActiveCue(segmentId, sampleInClip, null, fallbackAnchors, fallbackGranularity,
                SpokenRange: null, TimingProvenance.None,
                Diagnostics: segment is null
                    ? [$"segment {segmentId} is not in the plan"]
                    : ["no clip attached yet; highlighting the whole unit"]);
        }

        // Timings finer than the clip: find the cue containing this sample.
        if (clip.Cues.Count > 0)
        {
            if (sampleInClip < clip.Cues[0].StartSample)
                return new ActiveCue(segmentId, sampleInClip, null, [], clip.Cues[0].Granularity,
                    null, clip.Asset.TimingProvenance, ["Waiting for the first aligned word."]);
            var cue = clip.Cues.FirstOrDefault(c => c.Contains(sampleInClip));
            if (cue is not null)
            {
                var anchors = cue.AnchorIds.Where(id => _sourceMap.Anchor(id) is not null).ToList();
                // A cue citing anchors the plan no longer has is not trustworthy for highlighting.
                if (anchors.Count > 0)
                {
                    return new ActiveCue(segmentId, sampleInClip, cue, anchors, cue.Granularity,
                        cue.SpokenRange, clip.Asset.TimingProvenance, cue.Warnings);
                }
            }

            // Between cues (a pause, or a gap the aligner did not cover). Hold the last one that
            // ended rather than dropping the highlight: the listener is still hearing that content.
            var previous = clip.Cues.LastOrDefault(c => c.EndSample <= sampleInClip);
            if (previous is not null)
            {
                var anchors = previous.AnchorIds.Where(id => _sourceMap.Anchor(id) is not null).ToList();
                if (anchors.Count > 0)
                {
                    return new ActiveCue(segmentId, sampleInClip, previous, anchors, previous.Granularity,
                        previous.SpokenRange, clip.Asset.TimingProvenance,
                        [.. previous.Warnings, "position falls in a gap between cues; holding the previous cue"]);
                }
            }
        }

        return new ActiveCue(segmentId, sampleInClip,
            TimedCue.ForWholeClip(clip.Asset.FrameCount, fallbackAnchors, fallbackGranularity),
            fallbackAnchors, fallbackGranularity, SpokenRange: null,
            clip.Asset.TimingProvenance, [.. clip.Diagnostics, "no sub-clip timing; highlighting the whole unit"]);
    }

    /// <summary>
    /// For a word cue: the next aligned word's anchors and how far the clock is from this word's
    /// measured start to the next word's measured start (to this word's end when it is the last).
    /// Null when the clip has no word timings. Both ends of the interval are measured; this is for
    /// moving the camera between them, never for placing a highlight.
    /// </summary>
    public (IReadOnlyList<string> NextAnchorIds, double Progress)? WordProgress(string segmentId, TimedCue cue, long sampleInClip)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ClipTiming? clip;
        lock (_gate) _clips.TryGetValue(segmentId, out clip);
        if (clip is null || clip.Cues.Count == 0) return null;
        var next = clip.Cues.FirstOrDefault(c => c.StartSample > cue.StartSample);
        long end = next?.StartSample ?? cue.EndSample;
        long span = end - cue.StartSample;
        double progress = span <= 0 ? 0 : Math.Clamp((double)(sampleInClip - cue.StartSample) / span, 0, 1);
        IReadOnlyList<string> anchors = next is null ? [] : next.AnchorIds.Where(id => _sourceMap.Anchor(id) is not null).ToList();
        return (anchors, progress);
    }

    private sealed record ClipTiming(SpeechAsset Asset, IReadOnlyList<TimedCue> Cues, IReadOnlyList<string> Diagnostics);
}

/// <summary>
/// The concatenated clip timeline: a global sample offset across a whole narration, and the mapping
/// back to (segment, offset within that segment).
/// </summary>
/// <remarks>
/// Expressed in one declared timebase. Clips from a single provider and voice share a sample rate in
/// practice, but a rate change mid-document is handled by converting each clip's length into the
/// timeline's timebase rather than by pretending the rates match — the same discipline that keeps
/// playback-rate changes from detaching highlights.
/// </remarks>
public sealed class NarrationTimeline
{
    private readonly List<ClipSpan> _spans = [];

    public NarrationTimeline(AudioTimebase timebase) => Timebase = timebase;

    public AudioTimebase Timebase { get; }

    public long TotalSamples => _spans.Count == 0 ? 0 : _spans[^1].TimelineEnd;

    public double TotalSeconds => Timebase.ToSeconds(TotalSamples);

    public IReadOnlyList<ClipSpan> Spans => _spans;

    /// <summary>Appends a clip. Clips must be appended in narration order.</summary>
    public void Append(SpeechAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        // Convert the clip's own frames into the timeline's timebase when the rates differ.
        long timelineFrames = asset.Format.SampleRate == Timebase.SampleRate
            ? asset.FrameCount
            : (long)Math.Round(asset.DurationSeconds * Timebase.SampleRate, MidpointRounding.AwayFromZero);
        long start = TotalSamples;
        _spans.Add(new ClipSpan(asset.SegmentId, asset.Id, start, start + timelineFrames, asset.Format.SampleRate));
    }

    /// <summary>Removes everything from <paramref name="segmentId"/> onwards, e.g. after its script was edited.</summary>
    public void TruncateFrom(string segmentId)
    {
        int i = _spans.FindIndex(s => s.SegmentId == segmentId);
        if (i >= 0) _spans.RemoveRange(i, _spans.Count - i);
    }

    public void Clear() => _spans.Clear();

    /// <summary>
    /// Resolves a global position to a clip and an offset within it.
    /// </summary>
    /// <returns>Null when the timeline is empty. Positions past the end resolve to the last clip's end.</returns>
    public (ClipSpan Span, long SampleInTimeline, long SampleInClip)? Resolve(long timelineSample)
    {
        if (_spans.Count == 0) return null;
        var span = _spans.FirstOrDefault(s => timelineSample >= s.TimelineStart && timelineSample < s.TimelineEnd);
        if (span is null)
        {
            span = timelineSample < 0 ? _spans[0] : _spans[^1];
            timelineSample = Math.Clamp(timelineSample, span.TimelineStart, Math.Max(span.TimelineStart, span.TimelineEnd - 1));
        }
        long offsetInTimeline = timelineSample - span.TimelineStart;
        // Map back into the clip's own rate when it differs from the timeline's.
        long sampleInClip = span.ClipSampleRate == Timebase.SampleRate
            ? offsetInTimeline
            : (long)Math.Round(offsetInTimeline * ((double)span.ClipSampleRate / Timebase.SampleRate), MidpointRounding.AwayFromZero);
        return (span, timelineSample, sampleInClip);
    }

    /// <summary>Global position of the start of a segment, or null when it is not on the timeline.</summary>
    public long? StartOf(string segmentId) => _spans.FirstOrDefault(s => s.SegmentId == segmentId)?.TimelineStart;
}

/// <summary>One clip's place on the concatenated timeline.</summary>
/// <param name="TimelineStart">Inclusive, in timeline frames.</param>
/// <param name="TimelineEnd">Exclusive, in timeline frames.</param>
/// <param name="ClipSampleRate">The clip's own rate, which may differ from the timeline's.</param>
public sealed record ClipSpan(string SegmentId, string AssetId, long TimelineStart, long TimelineEnd, int ClipSampleRate)
{
    public long DurationInTimeline => TimelineEnd - TimelineStart;
}
