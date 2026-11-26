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
/// <param name="HasDisplayEquation">
/// The passage contains a display equation — itself, or the equation a lead-in sentence introduces.
/// An interpreter may spend more effort on these: they carry the most meaning per word.
/// </param>
/// <param name="Formulas">
/// The passage's display equations as a formula model transcribed them, in reading order; none when
/// no formula model is in use or it could not read them. A transcription can misread a symbol.
/// </param>
public sealed record MathInterpretationRequest(
    MathPassageKind Kind,
    string TextLayer,
    IReadOnlyList<ReadOnlyMemory<byte>> Images,
    string ContextBefore,
    string ContextAfter,
    string Language,
    bool HasDisplayEquation = false,
    IReadOnlyList<RecognisedFormula>? Formulas = null)
{
    public IReadOnlyList<RecognisedFormula> Formulas { get; init; } = Formulas ?? [];
}

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

    /// <summary>
    /// Set when the endpoint, not the answer, was the failure: a refused key, an exhausted quota,
    /// a server error, no connection. In words for the reader; never the key or the response body.
    /// </summary>
    public string? EndpointProblem { get; init; }

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

    /// <summary>Why <see cref="IsAvailableAsync"/> last answered false, in words for the reader; null when unknown.</summary>
    string? UnavailableReason => null;

    /// <summary>Never throws for a bad model response; returns a rejection.</summary>
    Task<MathInterpretationResult> InterpretAsync(MathInterpretationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Reads a passage's maths mechanically ("mathspeak": "h sub t equals alpha sub zero plus…") when it
/// could not be interpreted. Says the notation, not its meaning; better than notation read from a
/// garbled text layer, worse than an interpretation.
/// </summary>
public interface IMathReader
{
    /// <summary>Part of every cache key.</summary>
    string Revision { get; }

    /// <summary>The words, or null when the passage cannot be read reliably. Never throws for a bad response.</summary>
    Task<string?> ReadAsync(MathInterpretationRequest request, CancellationToken ct = default);
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

    /// <summary>
    /// Asks again for a segment that already has words, ignoring any cached answer — the reader's
    /// "Regenerate". If no better answer comes back, the current words are kept.
    /// </summary>
    Task<NarrationSegment> RefreshAsync(NarrationPlan plan, NarrationSegment segment, CancellationToken ct = default);
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
/// When the interpretation is refused, an <see cref="IMathReader"/> (if supplied) reads the
/// passage mechanically from a transcription, labelled <see cref="ScriptProvenance.LocalRule"/>.
/// Failing that, the fallback differs by kind. A sentence with maths keeps its verbatim reading —
/// the prose around the notation is still worth hearing. A display equation becomes the explicit
/// "could not be read" announcement: its text layer is not a reading of it.
/// </para>
/// <para>
/// With an <see cref="IFormulaRecogniser"/>, a display equation is also transcribed locally from a
/// tight crop of its own, and <see cref="MathEvidence"/> decides what the interpreter is given: the
/// page image, the transcription, or both. A transcription the interpreter did not turn into an
/// answer is read by rule (the formula reader) before the interpreter's own transcription is asked
/// for, and with no interpreter at all that reading is what a display equation gets.
/// </para>
/// </remarks>
public sealed class MathInterpretationResolver : ISegmentResolver
{
    private readonly IMathInterpreter? _interpreter;
    private readonly IMathReader? _reader;
    private readonly IFormulaRecogniser? _formulas;
    private readonly IMathReader? _formulaReader;
    private readonly MathEvidence _evidence;
    private readonly IExtractionSource? _crops;
    private readonly ITextCache? _cache;
    private readonly NarrationPlannerOptions _options;
    private readonly int _contextBeforeChars;
    private readonly int _contextAfterChars;
    private readonly int _cropPixelWidth;

    /// <summary>
    /// Pixels across an equation's crop for the formula model. It scales every crop into a 384 px
    /// square, so more than this adds nothing; less loses the sub- and superscripts of a wide equation.
    /// </summary>
    public const int FormulaCropPixelWidth = 1024;

    /// <param name="interpreter">The language model; null when none is configured and a formula model alone reads display equations.</param>
    /// <param name="formulas">A formula model for display equations, or null.</param>
    /// <param name="formulaReader">Reads a request's <see cref="MathInterpretationRequest.Formulas"/> by rule, with no model call.</param>
    /// <param name="evidence">What the interpreter is given when a formula model is in use; without one it is the image.</param>
    public MathInterpretationResolver(
        IMathInterpreter? interpreter,
        IExtractionSource? crops = null,
        ITextCache? cache = null,
        NarrationPlannerOptions? options = null,
        int contextBeforeChars = 1500,
        int contextAfterChars = 400,
        int cropPixelWidth = 1400,
        IMathReader? reader = null,
        IFormulaRecogniser? formulas = null,
        IMathReader? formulaReader = null,
        MathEvidence evidence = MathEvidence.Both)
    {
        if (interpreter is null && (formulas is null || formulaReader is null))
            throw new ArgumentException("Without an interpreter, a formula model and its reader are needed.", nameof(interpreter));
        _interpreter = interpreter;
        _reader = reader;
        _formulas = formulas;
        _formulaReader = formulaReader;
        _evidence = formulas is null ? MathEvidence.Image : evidence;
        _crops = crops;
        _cache = cache;
        _options = options ?? NarrationPlannerOptions.Default;
        _contextBeforeChars = contextBeforeChars;
        _contextAfterChars = contextAfterChars;
        _cropPixelWidth = cropPixelWidth;
    }

    public Task<NarrationSegment> ResolveAsync(NarrationPlan plan, NarrationSegment segment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Provenance == ScriptProvenance.PendingInterpretation
            ? InterpretAsync(plan, segment, refresh: false, ct)
            : Task.FromResult(segment);
    }

    public async Task<NarrationSegment> RefreshAsync(NarrationPlan plan, NarrationSegment segment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var result = await InterpretAsync(plan, segment, refresh: true, ct).ConfigureAwait(false);
        // A failed regeneration keeps what the reader already had rather than degrading it to a
        // fallback: the earlier interpretation passed the same checks. A mechanical reading still
        // beats a verbatim or announced fallback.
        bool better = result.Provenance == ScriptProvenance.ModelGenerated
            || (result.Provenance == ScriptProvenance.LocalRule && segment.Provenance is not (ScriptProvenance.ModelGenerated or ScriptProvenance.UserEdited));
        return better
            ? result
            : segment with { Warnings = [.. segment.Warnings, .. result.Warnings.Where(w => !segment.Warnings.Contains(w)),
                "Regenerating did not produce a usable answer; the previous wording is kept."] };
    }

    private async Task<NarrationSegment> InterpretAsync(NarrationPlan plan, NarrationSegment segment, bool refresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // A passage is one unit, or a lead-in sentence and the equation it introduces.
        var indices = segment.ReadingUnitIds.Select(id => IndexOf(plan, id)).Where(i => i >= 0).OrderBy(i => i).ToList();
        if (indices.Count == 0) return Fallback(segment, null, "The passage's source unit is not in the plan.");
        int unitIndex = indices[0], lastIndex = indices[^1];
        var parts = indices.Select(i => plan.Units[i]).ToList();
        var unit = parts.Count == 1 ? parts[0]
            : parts[0] with { Kind = ReadingUnitKind.Prose, Anchors = parts.SelectMany(p => p.Anchors).ToList(),
                OriginalText = string.Join(" ", parts.Select(p => p.OriginalText)), ContainsMaths = true };
        var kind = unit.Kind == ReadingUnitKind.DisplayMath ? MathPassageKind.DisplayEquation : MathPassageKind.SentenceWithMaths;

        bool hasEquation = parts.Any(p => p.Kind == ReadingUnitKind.DisplayMath);
        bool canInterpret = _interpreter is not null && await _interpreter.IsAvailableAsync(ct).ConfigureAwait(false);
        if (_interpreter is not null && !canInterpret)
            EndpointProblem?.Invoke(_interpreter.UnavailableReason ?? "The maths endpoint is not reachable.");
        string unavailable = _interpreter is null
            ? "No language model is configured for maths, so this passage was not interpreted."
            : "The maths interpreter is not reachable, so this passage was not interpreted.";
        // Without an interpreter only a display equation can still be read, from the formula model.
        if (!canInterpret && !(hasEquation && _formulas is not null && _formulaReader is not null))
            return Fallback(segment, unit, unavailable);

        var warnings = new List<string>();
        var formulas = hasEquation ? await FormulasAsync(parts, refresh, warnings, ct).ConfigureAwait(false) : [];
        // The image goes to the interpreter unless the reader chose the transcription alone.
        IReadOnlyList<ReadOnlyMemory<byte>> images = [];
        if (canInterpret && _evidence != MathEvidence.Formula)
        {
            (images, string? cropProblem) = await CropsAsync(unit, ct).ConfigureAwait(false);
            if (cropProblem is not null) warnings.Add(cropProblem);
        }
        if (warnings.Count > 0) segment = segment with { Warnings = segment.Warnings.Concat(warnings).Distinct().ToArray() };

        var request = new MathInterpretationRequest(kind, unit.OriginalText, images,
            ContextBefore(plan, unitIndex), ContextAfter(plan, lastIndex), segment.Language,
            HasDisplayEquation: hasEquation, Formulas: formulas);

        // A request without a formula is keyed exactly as it was before formula models existed.
        var keyParts = new List<string>
        {
            "interpret/1", _interpreter?.Revision ?? "no-interpreter", kind.ToString(),
            request.TextLayer, request.ContextBefore, request.ContextAfter, request.Language,
            string.Join(",", images.Select(i => CacheKey.Sha256Hex(i.Span))),
        };
        if (formulas.Count > 0)
        {
            keyParts.Add(RecognisedFormula.EvidenceVersion);
            keyParts.AddRange(formulas.Select(f => f.Latex + "␞" + string.Join("␞", f.Notes)));
        }
        string key = CacheKey.Sha256Hex(string.Join("␟", keyParts));

        if (!canInterpret)
            return await ReadFormulasAsync(segment, request, key, refresh, unavailable, ct).ConfigureAwait(false)
                   ?? Fallback(segment, unit, unavailable);

        string? spoken = _cache is null || refresh ? null : await _cache.TryGetAsync(key, ct).ConfigureAwait(false);
        if (spoken is null)
        {
            MathInterpretationResult result;
            try { result = await _interpreter!.InterpretAsync(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = MathInterpretationResult.Reject($"request failed: {ex.Message}") with { EndpointProblem = "The maths endpoint could not be asked." };
            }

            if (!result.Succeeded)
            {
                if (result.EndpointProblem is { } problem) EndpointProblem?.Invoke(problem);
                // Each reason once: a refused endpoint gives the same one for every retry.
                string reason = "The interpretation was rejected: " + string.Join("; ", result.Problems.Distinct());
                // An equation alone is read from the formula model's transcription, which is already
                // made. A sentence leading into one is better read whole from the interpreter's own
                // transcription, which also reads the maths inside the sentence; the formula model
                // can only supply the equation after the sentence as printed.
                return kind == MathPassageKind.DisplayEquation
                    ? await ReadFormulasAsync(segment, request, key, refresh, reason, ct).ConfigureAwait(false)
                      ?? await ReadMechanicallyAsync(segment, request, key, refresh, reason, ct).ConfigureAwait(false)
                      ?? Fallback(segment, unit, reason)
                    : await ReadMechanicallyAsync(segment, request, key, refresh, reason, ct).ConfigureAwait(false)
                      ?? await ReadFormulasAsync(segment, request, key, refresh, reason, ct).ConfigureAwait(false)
                      ?? Fallback(segment, unit, reason);
            }
            spoken = result.Spoken!.Trim();
            warnings.AddRange(result.Warnings);
            if (_cache is not null)
            {
                try { await _cache.PutAsync(key, spoken, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        warnings.Add(formulas.Count == 0
            ? "Interpreted by a language model from the page image and its context; check it against the original."
            : images.Count == 0
                ? "Interpreted by a language model from a formula model's transcription of the equation and its context; check it against the original."
                : "Interpreted by a language model from the page image, a formula model's transcription and the context; check it against the original.");
        // A paraphrase has no word-for-word correspondence with the glyphs, so the whole passage is
        // linked to the whole utterance (ADR-0004: no invented word timings).
        return segment with
        {
            SpokenText = spoken,
            Provenance = ScriptProvenance.ModelGenerated,
            ScriptRevision = segment.ScriptRevision + 1,
            SourceLinks = segment.AnchorIds.Count > 0 ? [new SourceLink(new TextRange(0, spoken.Length), segment.AnchorIds)] : [],
            // Distinct: a regenerated passage already carries the provenance note.
            Warnings = segment.Warnings.Concat(warnings).Distinct().ToArray(),
        };
    }

    /// <summary>The <see cref="IMathReader"/>'s reading, cached like an interpretation; null when there is none.</summary>
    private Task<NarrationSegment?> ReadMechanicallyAsync(NarrationSegment segment, MathInterpretationRequest request,
        string interpretationKey, bool refresh, string reason, CancellationToken ct)
        => ReadByRuleAsync(_reader, "mathspeak/1", segment, request, interpretationKey, refresh, reason,
            "Read mechanically from a transcription of the page image: it says the notation, not its meaning. Check it against the original.", ct);

    /// <summary>
    /// The formula model's transcription read by rule; null when there is none or it cannot be read
    /// reliably. For a sentence leading into its equation, the sentence keeps its printed reading
    /// and the equation's announcement becomes the reading.
    /// </summary>
    private async Task<NarrationSegment?> ReadFormulasAsync(NarrationSegment segment, MathInterpretationRequest request,
        string interpretationKey, bool refresh, string reason, CancellationToken ct)
    {
        if (_formulaReader is null || request.Formulas.Count == 0) return null;
        // The reader is asked for the equations alone, whatever passage they came in.
        var equations = request with
        {
            Kind = MathPassageKind.DisplayEquation, Images = [],
            TextLayer = string.Join(" ", request.Formulas.Select(f => f.TextLayer)),
        };
        const string label = "Read mechanically from a formula model's transcription of the equation: it says the notation, not its meaning. Check it against the original.";
        if (request.Kind == MathPassageKind.DisplayEquation)
            return await ReadByRuleAsync(_formulaReader, "formulaspeak/1", segment, equations, interpretationKey, refresh, reason, label, ct).ConfigureAwait(false);

        // The planner's lead-in: the sentence as printed, then the announcement linked to the equation.
        string announcement = _options.AnnouncementTemplate;
        int start = segment.SpokenText.Length - announcement.Length;
        if (start <= 0 || !segment.SpokenText.EndsWith(announcement, StringComparison.Ordinal)
            || segment.SourceLinks.LastOrDefault() is not { } equationLink || equationLink.SpokenRange.Start != start)
            return null;
        var read = await ReadByRuleAsync(_formulaReader, "formulaspeak/1", segment, equations, interpretationKey, refresh, reason, label, ct).ConfigureAwait(false);
        if (read is null) return null;
        string text = segment.SpokenText[..start] + read.SpokenText;
        return read with
        {
            SpokenText = text,
            SourceLinks = [.. segment.SourceLinks.Take(segment.SourceLinks.Count - 1), new SourceLink(new TextRange(start, read.SpokenText.Length), equationLink.AnchorIds)],
            Warnings = read.Warnings.Append("The sentence before the equation is read as printed.").Distinct().ToArray(),
        };
    }

    private async Task<NarrationSegment?> ReadByRuleAsync(IMathReader? reader, string keyPrefix, NarrationSegment segment,
        MathInterpretationRequest request, string interpretationKey, bool refresh, string reason, string label, CancellationToken ct)
    {
        if (reader is null) return null;
        string key = CacheKey.Sha256Hex(string.Join("␟", keyPrefix, reader.Revision, interpretationKey));
        string? spoken = _cache is null || refresh ? null : await _cache.TryGetAsync(key, ct).ConfigureAwait(false);
        if (spoken is null)
        {
            try { spoken = (await reader.ReadAsync(request, ct).ConfigureAwait(false))?.Trim(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { spoken = null; }
            if (string.IsNullOrEmpty(spoken)) return null;
            if (_cache is not null)
            {
                try { await _cache.PutAsync(key, spoken, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return segment with
        {
            SpokenText = spoken,
            Provenance = ScriptProvenance.LocalRule,
            ScriptRevision = segment.ScriptRevision + 1,
            SourceLinks = segment.AnchorIds.Count > 0 ? [new SourceLink(new TextRange(0, spoken.Length), segment.AnchorIds)] : [],
            Warnings = segment.Warnings.Concat([reason, label]).Distinct().ToArray(),
        };
    }

    /// <summary>
    /// The endpoint could not be asked for a passage, which was then read by rule or announced: the
    /// reason, in words for the reader. Raised for every such passage, on the thread that resolved
    /// it; a listener that shows it says it once.
    /// </summary>
    public event Action<string>? EndpointProblem;

    /// <summary>
    /// The passage's display equations as the formula model reads them, each from a tight crop of its
    /// own and reconciled with its text layer. An equation the model cannot read is left out, with a
    /// warning; a transcription is cached by its crop, so it is made once.
    /// </summary>
    private async Task<IReadOnlyList<RecognisedFormula>> FormulasAsync(IReadOnlyList<ReadingUnit> parts, bool refresh,
        List<string> warnings, CancellationToken ct)
    {
        if (_formulas is null || _crops is null || (_evidence == MathEvidence.Image && _interpreter is not null)) return [];
        var formulas = new List<RecognisedFormula>();
        foreach (var equation in parts.Where(p => p.Kind == ReadingUnitKind.DisplayMath))
        {
            var pieces = new List<string>();
            string? problem = null;
            foreach (var page in equation.Anchors.GroupBy(a => a.PageIndex))
            {
                if (PageRect.UnionAll(page.SelectMany(a => a.Rects).Where(r => !r.IsEmpty).ToList()) is not { } bounds) continue;
                try
                {
                    var png = await _crops.RenderCropPngAsync(page.First().Extraction, page.Key, FormulaCrop(bounds), FormulaCropPixelWidth, ct).ConfigureAwait(false);
                    if (png is not { IsEmpty: false } bytes) { problem = "its image could not be rendered"; break; }
                    string key = CacheKey.Sha256Hex(string.Join("␟", "formula/1", _formulas.Revision, CacheKey.Sha256Hex(bytes.Span)));
                    string? latex = _cache is null || refresh ? null : await _cache.TryGetAsync(key, ct).ConfigureAwait(false);
                    if (latex is null)
                    {
                        var read = await _formulas.RecogniseAsync(bytes, ct).ConfigureAwait(false);
                        if (!read.Succeeded) { problem = read.Problem ?? "it returned nothing"; break; }
                        latex = read.Latex!;
                        if (_cache is not null)
                        {
                            try { await _cache.PutAsync(key, latex, ct).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                    }
                    pieces.Add(latex);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { problem = $"{ex.GetType().Name}: {ex.Message}"; break; }
            }
            if (problem is not null || pieces.Count == 0)
            {
                warnings.Add($"The formula model could not read the equation ({problem ?? "it has no region on the page"}).");
                continue;
            }
            var formula = FormulaTextLayerCheck.Reconcile(string.Join(" ", pieces), equation.OriginalText);
            warnings.AddRange(formula.Notes);
            formulas.Add(formula);
        }
        return formulas;
    }

    /// <summary>
    /// The crop a formula model reads: the equation's own bounds, a little wider and no taller. The
    /// interpreter's crop is padded all round, which takes in the edge of the line above or below; a
    /// formula model transcribes that ink too (on the GARCH paper it ran one equation on to its
    /// 1,536-token limit).
    /// </summary>
    public static PageRect FormulaCrop(PageRect bounds) => new(bounds.Left - 2, bounds.Top, bounds.Right + 2, bounds.Bottom);

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

    private static int IndexOf(NarrationPlan plan, string id)
    {
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

    /// <summary>
    /// One crop per page the passage touches (at most two), slightly padded. A failed crop is not
    /// fatal — the text layer and context remain — but it is reported: every crop once failed
    /// silently, and interpretation quietly ran without the page image.
    /// </summary>
    private async Task<(IReadOnlyList<ReadOnlyMemory<byte>> Images, string? Problem)> CropsAsync(ReadingUnit unit, CancellationToken ct)
    {
        if (_crops is null) return ([], null);
        var images = new List<ReadOnlyMemory<byte>>();
        string? problem = null;
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
            catch (Exception ex)
            {
                problem = $"The page image could not be rendered ({ex.GetType().Name}: {ex.Message}); the passage was interpreted from its text layer and context only.";
            }
        }
        return (images, problem);
    }
}
