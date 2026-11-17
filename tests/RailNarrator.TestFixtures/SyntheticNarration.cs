using RailNarrator.Domain;

namespace RailNarrator.TestFixtures;

/// <summary>
/// Builds extraction snapshots and narration plans in memory, with exact known geometry.
/// </summary>
/// <remarks>
/// These complement <see cref="SyntheticPdf"/>: the PDF fixtures prove the adapter against real
/// files, and these prove the domain against inputs whose every coordinate is chosen by the test.
/// Neither needs a GPU, a network, a model or a sound card.
/// </remarks>
public static class SyntheticNarration
{
    public const float PageWidth = 612f;
    public const float PageHeight = 792f;
    public const float LineHeight = 10f;
    public const float LeftMargin = 72f;

    /// <summary>One line of text with per-character boxes at a fixed advance.</summary>
    public sealed record LineSpec(string Text, float Y, float CharWidth = 5.5f, float X = LeftMargin, bool FromOcr = false);

    /// <summary>
    /// Builds a single-page snapshot from line specs. Character boxes are laid out at a fixed
    /// advance, and word gaps are made wider than letter gaps so the geometry matches real text.
    /// </summary>
    public static ExtractionSnapshot Snapshot(IReadOnlyList<LineSpec> lines, int pageCount = 1, BlockKind kind = BlockKind.Text)
    {
        var pages = new List<PageExtraction>();
        for (int p = 0; p < pageCount; p++)
            pages.Add(Page(p, p == 0 ? lines : [], kind));
        return new ExtractionSnapshot(
            new ExtractionIdentity("deadbeefcafebabe", 1),
            CoordinateFrameId.DisplayedPagePoints(),
            ExtractionOptions.ModelFree,
            pages);
    }

    /// <summary>Builds one page from line specs, generating text, chars, lines and a single block.</summary>
    public static PageExtraction Page(int pageIndex, IReadOnlyList<LineSpec> lines, BlockKind kind = BlockKind.Text)
    {
        var text = new System.Text.StringBuilder();
        var chars = new List<ExtractedChar>();
        var extractedLines = new List<ExtractedLine>();
        var lineIndices = new List<int>();

        foreach (var spec in lines)
        {
            int lineStart = text.Length;
            float x = spec.X;
            foreach (char c in spec.Text)
            {
                int index = text.Length;
                text.Append(c);
                if (char.IsWhiteSpace(c))
                {
                    // A real space keeps a zero-area box, as PDF text layers do.
                    chars.Add(new ExtractedChar(index, default, 0f));
                    x += spec.CharWidth * 1.6f;
                }
                else
                {
                    chars.Add(new ExtractedChar(index,
                        new PageRect(x, spec.Y, x + spec.CharWidth, spec.Y + LineHeight), 0f));
                    x += spec.CharWidth;
                }
            }
            lineIndices.Add(extractedLines.Count);
            extractedLines.Add(new ExtractedLine(
                $"p{pageIndex}-l{extractedLines.Count}", 0,
                TextRange.FromBounds(lineStart, text.Length),
                new PageRect(spec.X, spec.Y - 1f, x, spec.Y + LineHeight),
                spec.FromOcr));
            text.Append('\n');
            chars.Add(new ExtractedChar(text.Length - 1, default, 0f));
        }

        var block = new ExtractedBlock(
            $"p{pageIndex}-b0", kind, 0,
            new PageRect(LeftMargin, 40f, PageWidth - LeftMargin, 40f + lines.Count * (LineHeight + 6f)),
            lineIndices, 0.99f, 0);

        double glyphArea = chars.Sum(c => (double)c.Rect.Area);
        var coverage = new TextCoverage(
            TextLayerPresent: !lines.Any(l => l.FromOcr) && lines.Count > 0,
            OcrUsed: lines.Any(l => l.FromOcr),
            CharCount: text.ToString().Trim().Length,
            PageAreaPoints: PageWidth * PageHeight,
            GlyphAreaPoints: glyphArea,
            Warnings: lines.Count == 0 ? ["No text extracted."] : []);

        return new PageExtraction(pageIndex, PageWidth, PageHeight, text.ToString(), [block], extractedLines, chars, coverage);
    }

    /// <summary>A source anchor over one line rectangle, for hand-built plans.</summary>
    public static SourceAnchor Anchor(string id, int pageIndex, PageRect rect, TextRange? charRange = null,
        ExtractionProvenance provenance = ExtractionProvenance.NativeText)
        => new(id, new ExtractionIdentity("deadbeefcafebabe", 1), pageIndex, $"p{pageIndex}-b0",
               [$"p{pageIndex}-l0"], charRange, [rect], CoordinateFrameId.DisplayedPagePoints(), provenance);

    /// <summary>A prose reading unit with one anchor.</summary>
    public static ReadingUnit ProseUnit(string id, string text, SourceAnchor anchor)
        => new(id, ReadingUnitKind.Prose, [anchor], text, null, "", "", [], 0)
        {
            SourceFragments = [new UnitSourceFragment(TextRange.OfString(text), anchor.AnchorId, anchor.PageIndex)],
        };

    /// <summary>A display-maths reading unit with one anchor and a recognised expression.</summary>
    public static ReadingUnit MathsUnit(string id, string latex, SourceAnchor anchor, string contextBefore = "", string contextAfter = "")
        => new(id, ReadingUnitKind.DisplayMath, [anchor], latex, latex, contextBefore, contextAfter, [], 0);

    /// <summary>A minimal plan over the given units with one segment per unit.</summary>
    public static NarrationPlan Plan(IReadOnlyList<ReadingUnit> units, MathMode mode = MathMode.NaturalFaithful)
    {
        var segments = units.Select((u, i) => new NarrationSegment(
            $"s{i:D5}", [u.Id], u.Kind, u.OriginalText, "en-GB",
            [new SourceLink(TextRange.OfString(u.OriginalText), u.AnchorIds)],
            mode, u.IsMaths ? ScriptProvenance.ModelGenerated : ScriptProvenance.SourceVerbatim, [], 1)).ToList();

        return new NarrationPlan(
            new NarrationPlanHeader("plan-1", new ExtractionIdentity("deadbeefcafebabe", 1), "en-GB", mode,
                NarrationVersions.MathPrompt, null, NarrationFilterSettings.Default),
            units, segments);
    }

    /// <summary>A speech asset of an exact length, with no timings — the clip-boundary baseline.</summary>
    public static SpeechAsset Clip(string segmentId, long frames, int sampleRate = 24000, IReadOnlyList<TimedCue>? timings = null)
        => new(
            Id: $"asset-{segmentId}",
            SegmentId: segmentId,
            AudioLocation: $"mem://{segmentId}",
            Format: new AudioFormat(sampleRate, 1, SampleFormat.Float32LittleEndian),
            FrameCount: frames,
            ProviderId: "fake",
            ModelRevision: "fake/1",
            VoiceId: "fake-default",
            SynthesisOptions: "rate=24000",
            Timings: timings ?? [],
            TimingProvenance: timings is { Count: > 0 } ? TimingProvenance.NativeDuration : TimingProvenance.ClipBoundary,
            ScriptRevision: 1);

    /// <summary>Float32 mono PCM of an exact frame count, filled with a fixed tone.</summary>
    public static byte[] TonePcm(long frames, int sampleRate = 24000, float amplitude = 0.25f)
    {
        var buffer = new byte[frames * 4];
        for (long i = 0; i < frames; i++)
            BitConverter.TryWriteBytes(buffer.AsSpan((int)(i * 4), 4),
                (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / sampleRate)));
        return buffer;
    }
}

/// <summary>
/// A <see cref="IMathVerbaliser"/> that returns a fixed, inspectable result.
/// </summary>
/// <remarks>
/// Lets planner tests assert on the faithful/announcement behaviour without a model, and lets a test
/// supply a deliberately invalid response (unknown anchor ids, over-length output) to prove the
/// planner refuses it rather than trusting it.
/// </remarks>
public sealed class StubMathVerbaliser : IMathVerbaliser
{
    private readonly Func<MathVerbalisationRequest, MathVerbalisationResult> _respond;

    public StubMathVerbaliser(Func<MathVerbalisationRequest, MathVerbalisationResult> respond) => _respond = respond;

    /// <summary>Always available, always returns the given faithful reading.</summary>
    public static StubMathVerbaliser Returning(string faithful, string? explanation = null)
        => new(req => new MathVerbalisationResult(faithful, explanation, req.AnchorIds, [], false, [],
            NarrationVersions.MathPrompt, "stub/1", 1));

    /// <summary>Always available, always rejects — simulating malformed model output.</summary>
    public static StubMathVerbaliser Rejecting(params string[] reasons)
        => new(_ => MathVerbalisationResult.Reject(reasons, NarrationVersions.MathPrompt, "stub/1", 2));

    /// <summary>Cites an anchor id that does not exist, to prove the planner rejects invented sources.</summary>
    public static StubMathVerbaliser CitingInventedAnchor(string faithful)
        => new(_ => new MathVerbalisationResult(faithful, null, ["a-invented"], [], false, [],
            NarrationVersions.MathPrompt, "stub/1", 1));

    public bool Available { get; set; } = true;

    public List<MathVerbalisationRequest> Received { get; } = [];

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

    public Task<MathVerbalisationResult> VerbaliseAsync(MathVerbalisationRequest request, CancellationToken ct = default)
    {
        Received.Add(request);
        return Task.FromResult(_respond(request));
    }
}

/// <summary>An <see cref="IAudioTransport"/> whose clock is driven entirely by the test.</summary>
public sealed class FakeAudioTransport : IAudioTransport
{
    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public double PositionSeconds { get; set; }
    public double ClipDurationSeconds { get; set; }
    public double BufferedAheadSeconds { get; set; }
    public double PlaybackRate { get; private set; } = 1.0;

    public List<(string SegmentId, SpeechAsset Asset, int PcmLength)> Loaded { get; } = [];
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int StopCount { get; private set; }
    public int FlushOnLoadCount { get; private set; }
    public List<double> SeekedTo { get; } = [];
    public string? FailLoadWith { get; set; }

    public event Action<PlaybackState>? StateChanged;
    public event Action<string>? ClipCompleted;
    public event Action<string>? Underflow;
    public event Action<string>? DeviceFailed;

    public Task LoadAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
    {
        if (FailLoadWith is { } reason) throw new InvalidOperationException(reason);
        Loaded.Add((segmentId, asset, pcm.Length));
        ClipDurationSeconds = asset.DurationSeconds;
        PositionSeconds = 0;
        FlushOnLoadCount++;
        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken ct = default)
    {
        PlayCount++;
        SetState(PlaybackState.Playing);
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default) { PauseCount++; SetState(PlaybackState.Paused); return Task.CompletedTask; }

    public int ResumeCount { get; private set; }

    public Task ResumeAsync(CancellationToken ct = default) { ResumeCount++; SetState(PlaybackState.Playing); return Task.CompletedTask; }

    public Task StopAsync(CancellationToken ct = default) { StopCount++; SetState(PlaybackState.Idle); return Task.CompletedTask; }

    public Task SeekAsync(double seconds, CancellationToken ct = default)
    {
        SeekedTo.Add(seconds);
        PositionSeconds = seconds;
        return Task.CompletedTask;
    }

    public void SetPlaybackRate(double rate) => PlaybackRate = rate;

    /// <summary>Raises clip completion, as the real transport does once the last samples have drained.</summary>
    public void RaiseClipCompleted(string segmentId) => ClipCompleted?.Invoke(segmentId);

    public void RaiseUnderflow(string segmentId) { SetState(PlaybackState.Buffering); Underflow?.Invoke(segmentId); }

    public void RaiseDeviceFailed(string detail) { SetState(PlaybackState.Error); DeviceFailed?.Invoke(detail); }

    private void SetState(PlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}

/// <summary>An in-memory speech asset store, so caching and invalidation are testable.</summary>
public sealed class InMemorySpeechAssetStore : ISpeechAssetStore
{
    private readonly Dictionary<string, (SpeechAsset Asset, byte[] Pcm)> _items = new(StringComparer.Ordinal);

    public int StoreCount { get; private set; }
    public int InvalidateCount { get; private set; }

    public Task<SpeechAsset?> TryGetAsync(string assetId, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(assetId, out var v) ? v.Asset : null);

    public Task StoreAsync(SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
    {
        _items[asset.Id] = (asset, pcm.ToArray());
        StoreCount++;
        return Task.CompletedTask;
    }

    public Task<ReadOnlyMemory<byte>?> TryLoadPcmAsync(string assetId, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(assetId, out var v) ? (ReadOnlyMemory<byte>?)v.Pcm : null);

    public Task InvalidateAsync(string documentContentHash, CancellationToken ct = default)
    {
        InvalidateCount++;
        _items.Clear();
        return Task.CompletedTask;
    }
}
