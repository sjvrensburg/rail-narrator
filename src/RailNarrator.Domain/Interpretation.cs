namespace RailNarrator.Domain;

/// <summary>What kind of mathematical passage is being interpreted.</summary>
public enum MathPassageKind
{
    /// <summary>A display equation standing on its own lines.</summary>
    DisplayEquation,

    /// <summary>A prose sentence with notation inside it; the result must read in place of the sentence.</summary>
    SentenceWithMaths,
}

/// <summary>A passage to interpret for a listener.</summary>
/// <param name="Kind">Display equation or sentence with maths.</param>
/// <param name="TextLayer">The passage as the PDF's text layer has it. Maths in it is usually garbled; a hint, not the source.</param>
/// <param name="Images">Crops of the passage, one per page region (PNG). The faithful source when present.</param>
/// <param name="ContextBefore">Preceding document text: where notation is defined. Untrusted data.</param>
/// <param name="ContextAfter">Following document text. Untrusted data.</param>
/// <param name="Language">BCP-47 language of the narration.</param>
public sealed record MathInterpretationRequest(
    MathPassageKind Kind,
    string TextLayer,
    IReadOnlyList<ReadOnlyMemory<byte>> Images,
    string ContextBefore,
    string ContextAfter,
    string Language);

/// <summary>An interpretation, or why there is none.</summary>
/// <param name="Spoken">Words to speak in place of the passage. Null when rejected.</param>
/// <param name="Warnings">Things the listener's reviewer should know (e.g. images were not accepted by the endpoint).</param>
/// <param name="Problems">Why the response was rejected.</param>
public sealed record MathInterpretationResult(
    string? Spoken,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems)
{
    public bool Succeeded => Spoken is { Length: > 0 } && Problems.Count == 0;

    public static MathInterpretationResult Reject(params string[] problems) => new(null, [], problems);
}

/// <summary>
/// Interprets mathematics for a listener: says what an expression or a maths-bearing sentence
/// means, in words, instead of reading its notation.
/// </summary>
/// <remarks>
/// The approach of the author's narrate skill: never voice notation, explain meaning in the
/// document's own terms, and reject any output that still carries symbols rather than speak it.
/// Implementations must never return their input as a fallback; a failed call is a rejection.
/// </remarks>
public interface IMathInterpreter
{
    /// <summary>Prompt and model revision; part of every cache key, so a change re-interprets.</summary>
    string Revision { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Never throws for a bad model response; returns a rejection.</summary>
    Task<MathInterpretationResult> InterpretAsync(MathInterpretationRequest request, CancellationToken ct = default);
}

/// <summary>A small persistent string cache. Misses and write failures are not errors.</summary>
public interface ITextCache
{
    Task<string?> TryGetAsync(string key, CancellationToken ct = default);
    Task PutAsync(string key, string value, CancellationToken ct = default);
}

/// <summary>
/// Turns a segment whose words are decided late (<see cref="ScriptProvenance.PendingInterpretation"/>)
/// into its final form, just before it is synthesised.
/// </summary>
public interface ISegmentResolver
{
    Task<NarrationSegment> ResolveAsync(NarrationPlan plan, NarrationSegment segment, CancellationToken ct = default);
}

/// <summary>
/// Resolves maths segments through an <see cref="IMathInterpreter"/>: crops the passage from the
/// page, gathers the text around it, asks once per distinct input (cached on disk), and falls
/// back honestly when there is no usable answer.
/// </summary>
/// <remarks>
/// <para>
/// Interpretation happens while the reader listens, a little ahead of the reading position, not
/// when the document is opened: a paper has hundreds of maths passages and a local model takes
/// seconds each, so interpreting them all up front would keep the document closed for tens of
/// minutes.
/// </para>
/// <para>
/// The fallback differs by kind. A sentence with maths keeps its verbatim reading — the prose
/// around the notation is still worth hearing. A display equation becomes the explicit "could not
/// be read" announcement: its text layer is not a reading of it.
/// </para>
/// </remarks>
public sealed class MathInterpretationResolver : ISegmentResolver
{
    private readonly IMathInterpreter _interpreter;
    private readonly IExtractionSource? _crops;
    private readonly ITextCache? _cache;
    private readonly NarrationPlannerOptions _options;
    private readonly int _contextBeforeChars;
    private readonly int _contextAfterChars;
    private readonly int _cropPixelWidth;

    public MathInterpretationResolver(
        IMathInterpreter interpreter,
        IExtractionSource? crops = null,
        ITextCache? cache = null,
        NarrationPlannerOptions? options = null,
        int contextBeforeChars = 1500,
        int contextAfterChars = 400,
        int cropPixelWidth = 1400)
    {
        _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _crops = crops;
        _cache = cache;
        _options = options ?? NarrationPlannerOptions.Default;
        _contextBeforeChars = contextBeforeChars;
        _contextAfterChars = contextAfterChars;
        _cropPixelWidth = cropPixelWidth;
    }

    public async Task<NarrationSegment> ResolveAsync(NarrationPlan plan, NarrationSegment segment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Provenance != ScriptProvenance.PendingInterpretation) return segment;

        int unitIndex = IndexOfUnit(plan, segment);
        if (unitIndex < 0) return Fallback(segment, null, "The passage's source unit is not in the plan.");
        var unit = plan.Units[unitIndex];
        var kind = unit.Kind == ReadingUnitKind.DisplayMath ? MathPassageKind.DisplayEquation : MathPassageKind.SentenceWithMaths;

        if (!await _interpreter.IsAvailableAsync(ct).ConfigureAwait(false))
            return Fallback(segment, unit, "The maths interpreter is not reachable, so this passage was not interpreted.");

        var images = await CropsAsync(unit, ct).ConfigureAwait(false);
        var request = new MathInterpretationRequest(kind, unit.OriginalText, images,
            ContextBefore(plan, unitIndex), ContextAfter(plan, unitIndex), segment.Language);

        string key = CacheKey.Sha256Hex(string.Join("␟", "interpret/1", _interpreter.Revision, kind.ToString(),
            request.TextLayer, request.ContextBefore, request.ContextAfter, request.Language,
            string.Join(",", images.Select(i => CacheKey.Sha256Hex(i.Span)))));

        string? spoken = _cache is null ? null : await _cache.TryGetAsync(key, ct).ConfigureAwait(false);
        var warnings = new List<string>();
        if (spoken is null)
        {
            MathInterpretationResult result;
            try { result = await _interpreter.InterpretAsync(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = MathInterpretationResult.Reject($"request failed: {ex.Message}"); }

            if (!result.Succeeded)
                return Fallback(segment, unit, "The interpretation was rejected: " + string.Join("; ", result.Problems));
            spoken = result.Spoken!.Trim();
            warnings.AddRange(result.Warnings);
            if (_cache is not null)
            {
                try { await _cache.PutAsync(key, spoken, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        warnings.Add("Interpreted by a language model from the page image and its context; check it against the original.");
        // A paraphrase has no word-for-word correspondence with the glyphs, so the whole passage is
        // linked to the whole utterance (ADR-0004: no invented word timings).
        return segment with
        {
            SpokenText = spoken,
            Provenance = ScriptProvenance.ModelGenerated,
            ScriptRevision = segment.ScriptRevision + 1,
            SourceLinks = segment.AnchorIds.Count > 0 ? [new SourceLink(new TextRange(0, spoken.Length), segment.AnchorIds)] : [],
            Warnings = [.. segment.Warnings, .. warnings],
        };
    }

    private NarrationSegment Fallback(NarrationSegment segment, ReadingUnit? unit, string reason)
    {
        if (unit is not { Kind: ReadingUnitKind.DisplayMath })
        {
            // The placeholder already is the verbatim reading of the sentence.
            return segment with { Provenance = ScriptProvenance.SourceVerbatim, Warnings = [.. segment.Warnings, reason] };
        }
        string text = _options.AnnouncementTemplate;
        return segment with
        {
            SpokenText = text,
            Provenance = ScriptProvenance.FailureAnnouncement,
            SourceLinks = segment.AnchorIds.Count > 0 ? [new SourceLink(new TextRange(0, text.Length), segment.AnchorIds)] : [],
            Warnings = [.. segment.Warnings, reason],
        };
    }

    private static int IndexOfUnit(NarrationPlan plan, NarrationSegment segment)
    {
        string? id = segment.ReadingUnitIds.FirstOrDefault();
        if (id is null) return -1;
        for (int i = 0; i < plan.Units.Count; i++) if (plan.Units[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// The text before the passage, up to the limit: where the document defines its notation
    /// ("where h describes the short-run dynamics…"). The unit's own 240-character context is too
    /// short to reach a definition a paragraph back.
    /// </summary>
    private string ContextBefore(NarrationPlan plan, int unitIndex)
    {
        var parts = new List<string>();
        int length = 0;
        for (int i = unitIndex - 1; i >= 0 && length < _contextBeforeChars; i--)
        {
            parts.Add(plan.Units[i].OriginalText);
            length += plan.Units[i].OriginalText.Length + 1;
        }
        parts.Reverse();
        string text = string.Join(" ", parts);
        return text.Length <= _contextBeforeChars ? text : text[^_contextBeforeChars..];
    }

    private string ContextAfter(NarrationPlan plan, int unitIndex)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = unitIndex + 1; i < plan.Units.Count && sb.Length < _contextAfterChars; i++)
            sb.Append(plan.Units[i].OriginalText).Append(' ');
        string text = sb.ToString().Trim();
        return text.Length <= _contextAfterChars ? text : text[.._contextAfterChars];
    }

    /// <summary>One crop per page the passage touches (at most two), slightly padded.</summary>
    private async Task<IReadOnlyList<ReadOnlyMemory<byte>>> CropsAsync(ReadingUnit unit, CancellationToken ct)
    {
        if (_crops is null) return [];
        var images = new List<ReadOnlyMemory<byte>>();
        foreach (var page in unit.Anchors.GroupBy(a => a.PageIndex).Take(2))
        {
            var rects = page.SelectMany(a => a.Rects).Where(r => !r.IsEmpty).ToList();
            // Framing bounds, not highlight geometry: a crop for the model to look at.
            if (PageRect.UnionAll(rects) is not { } bounds) continue;
            var padded = new PageRect(bounds.Left - 6, bounds.Top - 6, bounds.Right + 6, bounds.Bottom + 6);
            try
            {
                var png = await _crops.RenderCropPngAsync(page.First().Extraction, page.Key, padded, _cropPixelWidth, ct).ConfigureAwait(false);
                if (png is { IsEmpty: false } bytes) images.Add(bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* no image: the interpreter still has the text layer and context */ }
        }
        return images;
    }
}
