namespace RailNarrator.Domain;

/// <summary>Sample encoding of a PCM buffer.</summary>
public enum SampleFormat
{
    /// <summary>32-bit IEEE float, little-endian, nominally in [-1, 1].</summary>
    Float32LittleEndian,

    /// <summary>16-bit signed integer, little-endian.</summary>
    Int16LittleEndian,

    /// <summary>32-bit signed integer, little-endian.</summary>
    Int32LittleEndian,
}

/// <summary>
/// The exact shape of a PCM buffer. A provider must report this truthfully: guessing a sample rate
/// shifts every later cue in time, and guessing a format turns speech into noise.
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, SampleFormat SampleFormat)
{
    /// <summary>Bytes per single frame (one sample per channel).</summary>
    public int BytesPerFrame => Channels * SampleFormat switch
    {
        SampleFormat.Float32LittleEndian => 4,
        SampleFormat.Int16LittleEndian => 2,
        SampleFormat.Int32LittleEndian => 4,
        _ => throw new InvalidOperationException($"Unknown sample format {SampleFormat}."),
    };

    public static AudioFormat Mono44100Float32 { get; } = new(44100, 1, SampleFormat.Float32LittleEndian);
    public static AudioFormat Mono24000Float32 { get; } = new(24000, 1, SampleFormat.Float32LittleEndian);
    public static AudioFormat Mono44100Int16 { get; } = new(44100, 1, SampleFormat.Int16LittleEndian);

    public bool IsValid => SampleRate > 0 && Channels > 0;

    /// <summary>Frames → seconds in this format's own timebase.</summary>
    public double ToSeconds(long frames) => SampleRate <= 0 ? 0 : (double)frames / SampleRate;

    /// <summary>Seconds → frames, rounded to the nearest whole frame.</summary>
    public long ToFrames(double seconds) => (long)Math.Round(seconds * SampleRate, MidpointRounding.AwayFromZero);

    /// <summary>Duration of a byte buffer of this format.</summary>
    public long FramesFromBytes(long byteCount) => BytesPerFrame <= 0 ? 0 : byteCount / BytesPerFrame;

    public override string ToString() => $"{SampleRate}Hz/{Channels}ch/{SampleFormat}";
}

/// <summary>
/// Declares the timebase a <see cref="TimedCue"/>'s sample offsets are counted in.
/// </summary>
/// <remarks>
/// Cues are counted in <b>source audio</b> frames — the synthesised clip at its own sample rate —
/// and never in output-device frames. The two differ whenever the device rate differs from the
/// source rate (Kokoro synthesises at 24 kHz while the devices on the reference machine run at
/// 44.1 kHz) and whenever playback rate is not 1.0. Converting once, in the audio engine, and
/// keeping that mapping explicit is what lets a speed change move highlights correctly instead of
/// silently detaching them from the audio.
/// </remarks>
public readonly record struct AudioTimebase(int SampleRate, string AssetId)
{
    public double ToSeconds(long sample) => SampleRate <= 0 ? 0 : (double)sample / SampleRate;
    public long ToSample(double seconds) => (long)Math.Round(seconds * SampleRate, MidpointRounding.AwayFromZero);

    public static AudioTimebase Of(SpeechAsset asset) => new(asset.Format.SampleRate, asset.Id);
}

/// <summary>How a cue's timing was established. Determines how much the UI may trust it.</summary>
public enum TimingProvenance
{
    /// <summary>No timing at all. The asset plays as one undivided clip.</summary>
    None,

    /// <summary>
    /// Derived from the clip's own boundaries: one independently synthesised segment has a known
    /// sample interval and exact source anchors. The reliable baseline, and the reason sentence and
    /// equation highlighting needs no aligner.
    /// </summary>
    ClipBoundary,

    /// <summary>Reported by the synthesis model itself, e.g. per-phoneme durations.</summary>
    NativeDuration,

    /// <summary>Produced by forced-aligning the audio against the script that was synthesised.</summary>
    ForcedAlignment,
}

/// <summary>How large a region a cue can honestly highlight.</summary>
public enum CueGranularity
{
    /// <summary>A single word. Only offered when timing evidence supports it.</summary>
    Word,

    /// <summary>A clause or phrase.</summary>
    Phrase,

    /// <summary>A whole sentence.</summary>
    Sentence,

    /// <summary>A whole equation, highlighted for the duration of its paraphrase.</summary>
    Equation,

    /// <summary>An arbitrary source region, e.g. a figure or table being announced.</summary>
    Region,
}

/// <summary>
/// One highlight interval on the audio timeline.
/// </summary>
/// <param name="StartSample">Inclusive start, in the declared source-audio timebase.</param>
/// <param name="EndSample">Exclusive end, in the same timebase. Half-open so consecutive cues tile without overlap.</param>
/// <param name="SpokenRange">UTF-16 range into the segment's <c>SpokenText</c>, or null when the cue covers the whole segment.</param>
/// <param name="AnchorIds">Source regions to highlight while this cue is active.</param>
/// <param name="Granularity">What the cue may honestly claim to point at.</param>
/// <param name="Confidence">0..1 from an aligner, or null when the provenance does not produce one.</param>
/// <param name="Warnings">Problems with this cue. A cue with warnings is still usable; a cue that fails validation is dropped.</param>
/// <remarks>
/// <para>
/// For a paraphrased equation the cue covers the <b>entire</b> expression for the whole utterance.
/// Word cues may highlight the generated transcript, but there is no one-to-one match between a
/// spoken word and a PDF glyph, and inventing one moves the highlight onto text that was never
/// read.
/// </para>
/// <para>
/// When alignment is unreliable the correct behaviour is to fall back to a coarser
/// <see cref="Granularity"/> and say so in diagnostics — not to interpolate word boundaries
/// uniformly or proportionally by character count and call the result accurate.
/// </para>
/// </remarks>
public sealed record TimedCue(
    long StartSample,
    long EndSample,
    TextRange? SpokenRange,
    IReadOnlyList<string> AnchorIds,
    CueGranularity Granularity,
    double? Confidence,
    IReadOnlyList<string> Warnings)
{
    public long DurationSamples => EndSample - StartSample;

    public bool Contains(long sample) => sample >= StartSample && sample < EndSample;

    public bool IsEmpty => EndSample <= StartSample;

    /// <summary>The whole-clip cue for a segment: the baseline that needs no aligner.</summary>
    public static TimedCue ForWholeClip(long frameCount, IReadOnlyList<string> anchorIds, CueGranularity granularity)
        => new(0, frameCount, null, anchorIds, granularity, Confidence: null, Warnings: []);
}

/// <summary>
/// The result of validating a set of cues against the audio and script they claim to describe.
/// </summary>
/// <param name="Accepted">Cues that passed every check, in time order.</param>
/// <param name="Rejected">Cues that failed, each with the reason. Kept so diagnostics can explain a fallback instead of silently degrading.</param>
/// <param name="CoverageFraction">Fraction of the clip's duration covered by accepted cues.</param>
public sealed record CueValidationResult(
    IReadOnlyList<TimedCue> Accepted,
    IReadOnlyList<(TimedCue Cue, string Reason)> Rejected,
    double CoverageFraction)
{
    public bool IsUsable => Accepted.Count > 0;

    /// <summary>True when rejection lost enough of the clip that coarse highlighting is the honest answer.</summary>
    public bool ShouldFallBackToCoarse => CoverageFraction < 0.9;
}

/// <summary>
/// Range, monotonicity, coverage and confidence checks on a candidate cue set.
/// </summary>
/// <remarks>
/// Native timings and forced alignment are both <i>evidence</i>, not truth. The failure mode this
/// guards against is an aligner that drops or repeats one word and thereby shifts every later
/// anchor by a word: the audio still sounds right while the highlight reads the wrong line. A set
/// that does not survive these checks is rejected wholesale and the caller falls back to the
/// clip-boundary cue, rather than partially applying it.
/// </remarks>
public static class TimedCueValidator
{
    /// <summary>Cues shorter than this are treated as alignment noise, not as speech.</summary>
    public const long MinimumCueSamples = 8;

    /// <summary>Below this confidence a cue is not trusted to move a highlight.</summary>
    public const double MinimumConfidence = 0.35;

    /// <summary>
    /// Validates <paramref name="cues"/> against the clip they describe.
    /// </summary>
    /// <param name="cues">Candidate cues, in any order.</param>
    /// <param name="frameCount">The clip's real length in source frames. The authoritative bound.</param>
    /// <param name="spokenText">The script that was synthesised, for range checks on <see cref="TimedCue.SpokenRange"/>.</param>
    /// <param name="knownAnchorIds">Anchor ids that exist. A cue citing an unknown id is rejected.</param>
    /// <param name="requireMonotonic">Reject overlapping or backwards cues. Alignment output should satisfy this; interpolated output often does not.</param>
    public static CueValidationResult Validate(
        IReadOnlyList<TimedCue> cues,
        long frameCount,
        string spokenText,
        IReadOnlySet<string> knownAnchorIds,
        bool requireMonotonic = true)
    {
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(spokenText);
        knownAnchorIds ??= new HashSet<string>();

        var accepted = new List<TimedCue>();
        var rejected = new List<(TimedCue, string)>();
        long previousEnd = 0;

        foreach (var cue in cues.OrderBy(c => c.StartSample))
        {
            string? reason = Reject(cue);
            if (reason is null) accepted.Add(cue);
            else rejected.Add((cue, reason));
        }

        return new CueValidationResult(accepted, rejected, Coverage(accepted, frameCount));

        string? Reject(TimedCue cue)
        {
            if (cue.StartSample < 0) return $"start sample {cue.StartSample} is negative";
            if (cue.EndSample > frameCount) return $"end sample {cue.EndSample} exceeds clip length {frameCount}";
            if (cue.DurationSamples < MinimumCueSamples) return $"duration {cue.DurationSamples} samples is below the {MinimumCueSamples}-sample floor";
            if (cue.AnchorIds.Count == 0) return "cue cites no anchor, so it would highlight nothing";
            var unknown = cue.AnchorIds.Where(id => !knownAnchorIds.Contains(id)).ToList();
            if (unknown.Count > 0) return $"cue cites unknown anchor id(s) {string.Join(", ", unknown)}";
            if (cue.SpokenRange is { } r)
            {
                if (r.Start < 0 || r.End > spokenText.Length) return $"spoken range {r} exceeds script length {spokenText.Length}";
            }
            if (cue.Confidence is { } c && c < MinimumConfidence) return $"confidence {c:F2} is below {MinimumConfidence}";
            if (cue.Granularity == CueGranularity.Word && cue.Confidence is null)
                return "word granularity claimed with no confidence value";
            if (requireMonotonic && cue.StartSample < previousEnd) return $"starts at {cue.StartSample}, before the previous cue's end {previousEnd}";
            return null;
        }
    }

    private static double Coverage(IReadOnlyList<TimedCue> accepted, long frameCount)
    {
        if (frameCount <= 0 || accepted.Count == 0) return 0;
        long covered = accepted.Sum(c => c.DurationSamples);
        return Math.Clamp((double)covered / frameCount, 0, 1);
    }
}

/// <summary>
/// A synthesised clip, its format, and any timings that came with it.
/// </summary>
/// <param name="Id">Content-addressed cache identity. See <c>CacheKey.ForSpeechAsset</c>.</param>
/// <param name="SegmentId">The narration segment this is the speech for.</param>
/// <param name="AudioLocation">Where the PCM lives — a cache path or an in-memory reference. Never a remote URL: audio is local-first.</param>
/// <param name="Format">Sample rate, channels and encoding. Reported by the provider, verified on decode.</param>
/// <param name="FrameCount">Real length in frames, counted from the decoded audio rather than trusted from a header.</param>
/// <param name="ProviderId">Which provider synthesised it.</param>
/// <param name="ModelRevision">Provider model revision, so a model update invalidates cached speech.</param>
/// <param name="VoiceId">The voice. Changing it invalidates the asset.</param>
/// <param name="SynthesisOptions">Normalised rendering of every option that affects the audio, so two assets differing in any of them do not share a cache entry.</param>
/// <param name="Timings">Optional cues in this asset's own timebase.</param>
/// <param name="TimingProvenance">How <paramref name="Timings"/> were established. <see cref="Domain.TimingProvenance.None"/> when the list is empty.</param>
/// <param name="ScriptRevision">The segment revision this was synthesised from. A stale revision means the script was edited after this clip was made.</param>
public sealed record SpeechAsset(
    string Id,
    string SegmentId,
    string AudioLocation,
    AudioFormat Format,
    long FrameCount,
    string ProviderId,
    string ModelRevision,
    string VoiceId,
    string SynthesisOptions,
    IReadOnlyList<TimedCue> Timings,
    TimingProvenance TimingProvenance,
    int ScriptRevision)
{
    public double DurationSeconds => Format.ToSeconds(FrameCount);

    public AudioTimebase Timebase => new(Format.SampleRate, Id);

    public bool HasTimings => Timings.Count > 0 && TimingProvenance != TimingProvenance.None;

    /// <summary>
    /// True when this asset was made from the segment revision that is current. A false result
    /// means the script changed under it: the clip must be re-synthesised, not re-timed.
    /// </summary>
    public bool IsCurrentFor(NarrationSegment segment) => segment.Id == SegmentId && segment.ScriptRevision == ScriptRevision;

    /// <summary>The whole-clip cue, always available and always correct for this asset.</summary>
    public TimedCue ClipCue(CueGranularity granularity)
        => TimedCue.ForWholeClip(FrameCount, AnchorIdsFromTimings, granularity);

    private IReadOnlyList<string> AnchorIdsFromTimings => Timings.SelectMany(t => t.AnchorIds).Distinct().ToList();
}
