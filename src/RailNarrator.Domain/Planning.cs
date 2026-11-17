namespace RailNarrator.Domain;

/// <summary>
/// Supplies an inspectable representation (LaTeX or similar) for a mathematical unit, transcribing
/// the region when the extraction did not already carry one.
/// </summary>
/// <remarks>
/// Separate from <see cref="IMathVerbaliser"/> because the two stages fail differently and are
/// corrected differently: a transcription the user can see and edit, then a spoken rendering built
/// from what the user accepted. Coupling them would make a fluent but wrong reading uncorrectable.
/// </remarks>
public interface IMathScriptResolver
{
    Task<string?> ResolveRepresentationAsync(ReadingUnit unit, CancellationToken ct = default);
}

/// <summary>What to do with a mathematical unit when no verbalisation service is available.</summary>
public enum MathFallbackPolicy
{
    /// <summary>
    /// Announce that the equation could not be read reliably, and let the user retry, edit or skip.
    /// Default, and the only honest option: feeding LaTeX to a speech engine produces "backslash
    /// frac one n", which is neither the expression nor a reading of it.
    /// </summary>
    AnnounceUnreadable,

    /// <summary>
    /// Use the local rule-based reader. Clearly labelled as a mechanical reading in the segment's
    /// warnings and provenance, so the user is never led to believe a model rendered it.
    /// </summary>
    LocalRuleReader,
}

/// <summary>Versions folded into cache keys and persisted with every model-generated segment.</summary>
public static class NarrationVersions
{
    /// <summary>Bump when the verbalisation prompt changes meaning.</summary>
    public const string MathPrompt = "math-prompt/1";

    /// <summary>Bump when the local rule-based reader's output changes.</summary>
    public const string LocalMathReader = "local-math/1";

    /// <summary>Bump when the prose normalisation pipeline changes.</summary>
    public const string ProsePipeline = "prose/2-word-anchors";
}

/// <summary>Planner configuration.</summary>
/// <param name="Language">BCP-47 for the narration. English first; the field exists so a second language is data, not a code change.</param>
/// <param name="MathMode">Default maths mode.</param>
/// <param name="MathFallback">Behaviour when no verbaliser is available.</param>
/// <param name="PronunciationDictionary">User overrides applied while normalising prose for speech.</param>
/// <param name="AnnouncementTemplate">
/// Spoken text for an unreadable equation. Contains no document content, so it is safe to synthesise.
/// </param>
public sealed record NarrationPlannerOptions(
    string Language = "en-GB",
    MathMode MathMode = MathMode.NaturalFaithful,
    MathFallbackPolicy MathFallback = MathFallbackPolicy.AnnounceUnreadable,
    IReadOnlyDictionary<string, string>? PronunciationDictionary = null,
    string AnnouncementTemplate = "This equation could not be read reliably.")
{
    public static NarrationPlannerOptions Default { get; } = new();
}

/// <summary>Planning progress, so the UI can say what it is waiting for.</summary>
/// <param name="Stage">Human-readable stage, e.g. "Verbalising equations".</param>
public sealed record PlanningProgress(int UnitsDone, int UnitsTotal, string Stage);

/// <summary>
/// Turns reading units into a source-linked narration plan.
/// </summary>
/// <remarks>
/// <para>
/// Prose is normalised for speech through a mapping-preserving pipeline, so each spoken range still
/// points at the exact source characters it came from and therefore at real anchors. A model is
/// never asked to rewrite prose: that would put the document's own wording at the mercy of a
/// generator for no gain.
/// </para>
/// <para>
/// Mathematics goes through the verbaliser, and a segment that cannot be verbalised becomes an
/// explicit announcement rather than raw notation. Every generated segment records the prompt and
/// model revision it came from, so a later change invalidates exactly those segments.
/// </para>
/// </remarks>
public sealed class NarrationPlanner
{
    private readonly IMathVerbaliser? _verbaliser;
    private readonly IMathScriptResolver? _resolver;
    private readonly NarrationPlannerOptions _options;
    private readonly Func<ReadingUnit, string, string>? _localMathReader;

    /// <param name="verbaliser">Model-backed verbalisation, or null to fall back to <see cref="NarrationPlannerOptions.MathFallback"/>.</param>
    /// <param name="resolver">Supplies LaTeX for a maths unit when extraction did not. May be null.</param>
    /// <param name="localMathReader">
    /// Optional rule-based reader, <c>(unit, representation) → spoken text</c>. Used only under
    /// <see cref="MathFallbackPolicy.LocalRuleReader"/>, and always labelled as such.
    /// </param>
    public NarrationPlanner(
        IMathVerbaliser? verbaliser = null,
        IMathScriptResolver? resolver = null,
        NarrationPlannerOptions? options = null,
        Func<ReadingUnit, string, string>? localMathReader = null)
    {
        _verbaliser = verbaliser;
        _resolver = resolver;
        _options = options ?? NarrationPlannerOptions.Default;
        _localMathReader = localMathReader;
    }

    public NarrationPlannerOptions Options => _options;

    /// <summary>
    /// Fingerprints everything a plan is built from beyond the extraction identity: the planner
    /// options, the filter settings, the prompt version, and the reading units themselves (kind +
    /// original text + representation), which is where maths-region overrides and unit-builder
    /// options take effect. The planner stamps it into the header; a caller deciding whether a
    /// cached plan may be reused recomputes it over the units it just built and compares via
    /// <see cref="NarrationPlan.Matches(ExtractionIdentity, string?)"/>.
    /// </summary>
    public static string ComputeInputFingerprint(
        NarrationPlannerOptions options, NarrationFilterSettings filters, IReadOnlyList<ReadingUnit> units)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(units);
        var sb = new System.Text.StringBuilder();
        sb.Append("lang=").Append(options.Language)
          .Append(";mode=").Append((int)options.MathMode)
          .Append(";fallback=").Append((int)options.MathFallback)
          .Append(";announce=").Append(options.AnnouncementTemplate)
          .Append(";prompt=").Append(NarrationVersions.MathPrompt)
          .Append(";prose=").Append(NarrationVersions.ProsePipeline)
          .Append(";local=").Append(NarrationVersions.LocalMathReader)
          .Append(";filters=")
          .Append(filters.ExcludeRunningHeaders ? 'H' : '-').Append(filters.ExcludeFooters ? 'F' : '-')
          .Append(filters.ExcludePageNumbers ? 'N' : '-').Append(filters.IncludeCitations ? 'c' : '-')
          .Append(filters.IncludeFootnotes ? 'f' : '-').Append(filters.IncludeCaptions ? 'p' : '-')
          .Append(filters.IncludeReferences ? 'r' : '-').Append(filters.IncludeEquations ? 'e' : '-')
          .Append(filters.IncludeTables ? 't' : '-');
        if (options.PronunciationDictionary is { Count: > 0 } dict)
            foreach (var (k, v) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(";pron=").Append(k).Append('=').Append(v);
        foreach (var u in units)
            sb.Append('\n').Append((int)u.Kind).Append(':').Append(u.OriginalText).Append(':').Append(u.Latex);
        return CacheKey.Sha256Hex(sb.ToString());
    }

    /// <summary>
    /// Builds a plan over <paramref name="units"/>.
    /// </summary>
    /// <param name="snapshot">The extraction the units came from. Its identity is stamped into the plan header.</param>
    /// <param name="units">Reading units in reading order.</param>
    /// <param name="filters">The filter settings in force, recorded so a change rebuilds rather than edits.</param>
    /// <param name="modelRevision">Revision of the verbalisation model, or null when none was used.</param>
    public async Task<NarrationPlan> BuildPlanAsync(
        ExtractionSnapshot snapshot,
        IReadOnlyList<ReadingUnit> units,
        NarrationFilterSettings filters,
        string? modelRevision = null,
        IProgress<PlanningProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(units);

        var segments = new List<NarrationSegment>(units.Count);
        int mathsSeen = 0, seq = 0;

        for (int i = 0; i < units.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var unit = units[i];
            if (unit.IsMaths) mathsSeen++;

            NarrationSegment segment = unit.IsMaths
                ? await BuildMathsSegmentAsync(unit, seq++, ct).ConfigureAwait(false)
                : BuildProseSegment(unit, seq++);

            // A unit with nothing to say produces no segment; the text was empty or whitespace-only.
            if (segment.SpokenText.Trim().Length > 0) segments.Add(segment);

            progress?.Report(new PlanningProgress(i + 1, units.Count,
                mathsSeen > 0 ? $"Verbalising equations ({mathsSeen} so far)" : "Preparing narration"));
        }

        var header = new NarrationPlanHeader(
            PlanId: CacheKey.Sha256Hex($"plan:{snapshot.Identity}:{_options.Language}:{(int)_options.MathMode}:{NarrationVersions.ProsePipeline}"),
            Extraction: snapshot.Identity,
            Language: _options.Language,
            MathMode: _options.MathMode,
            PromptVersion: NarrationVersions.MathPrompt,
            ModelRevision: modelRevision,
            FilterSettings: filters,
            InputFingerprint: ComputeInputFingerprint(_options, filters, units));

        return new NarrationPlan(header, units, segments);
    }

    /// <summary>
    /// Builds the spoken text for a prose unit and links each spoken fragment back to the anchors it
    /// came from. Pure and synchronous: no model is involved.
    /// </summary>
    public NarrationSegment BuildProseSegment(ReadingUnit unit, int seq)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var breaks = unit.LineBreakOffsets.ToHashSet();

        // Order matters. Dehyphenation runs first because the recorded line-break offsets are offsets
        // into OriginalText; expanding ligatures before it would shift every later offset by one per
        // ligature and join or refuse the wrong hyphens. Composition keeps one mapping all the way
        // back to the original characters.
        var spoken = TransformedText.Identity(unit.OriginalText)
            .Then(t => TextNormaliser.Compose(t, TextNormaliser.Dehyphenate(t.Text, breaks)))
            .Then(t => TextNormaliser.Compose(t, TextNormaliser.ExpandLigatures(t.Text)))
            .Then(t => TextNormaliser.Compose(t, TextNormaliser.CollapseWhitespace(t.Text)))
            .Then(t => TextNormaliser.Compose(t, TextNormaliser.ExpandForSpeech(t.Text, _options.PronunciationDictionary)));

        string text = spoken.Text.Trim();
        int trim = LeadingTrim(spoken.Text);
        var links = BuildSourceLinks(unit, spoken, trim, text.Length);

        var warnings = new List<string>(unit.ExtractionWarnings);
        return new NarrationSegment(
            Id: $"s{seq:D5}",
            ReadingUnitIds: [unit.Id],
            Kind: unit.Kind,
            SpokenText: text,
            Language: _options.Language,
            SourceLinks: links,
            MathMode: _options.MathMode,
            Provenance: ScriptProvenance.SourceVerbatim,
            Warnings: warnings,
            ScriptRevision: 1);
    }

    /// <summary>
    /// How many leading whitespace characters <see cref="NarrationSegment.SpokenText"/> dropped by
    /// being trimmed, so source links computed before the trim still land on the right offsets.
    /// </summary>
    private static int LeadingTrim(string transformed)
    {
        int n = 0;
        while (n < transformed.Length && char.IsWhiteSpace(transformed[n])) n++;
        return n;
    }

    /// <summary>
    /// Maps each source fragment through the normalisation pipeline into the spoken string, then
    /// merges adjacent fragments that share an anchor.
    /// </summary>
    private static IReadOnlyList<SourceLink> BuildSourceLinks(ReadingUnit unit, TransformedText spoken, int trim, int spokenLength)
    {
        var allAnchors = unit.AnchorIds;
        if (unit.SourceFragments.Count == 0 || spokenLength <= 0)
            return allAnchors.Count > 0 ? [new SourceLink(new TextRange(0, spokenLength), allAnchors)] : [];

        var links = new List<SourceLink>();
        foreach (var fragment in unit.SourceFragments.OrderBy(f => f.OriginalRange.Start))
        {
            var output = spoken.ToOutputRange(fragment.OriginalRange);
            if (output is null) continue;
            int start = Math.Clamp(output.Value.Start - trim, 0, spokenLength);
            int end = Math.Clamp(output.Value.End - trim, start, spokenLength);
            if (end <= start) continue;

            var range = TextRange.FromBounds(start, end);
            // Merge into the previous link when it is the same anchor and contiguous, so a
            // multi-line paragraph on one page is one link rather than one per line.
            if (links.Count > 0 && links[^1].AnchorIds.Count == 1 && links[^1].AnchorIds[0] == fragment.AnchorId
                && links[^1].SpokenRange.End >= range.Start)
            {
                links[^1] = links[^1] with { SpokenRange = TextRange.FromBounds(links[^1].SpokenRange.Start, Math.Max(links[^1].SpokenRange.End, range.End)) };
                continue;
            }
            links.Add(new SourceLink(range, [fragment.AnchorId]));
        }

        // Any spoken text not covered by a fragment still needs an anchor, otherwise a highlight gap
        // appears. Attribute the remainder to the whole unit rather than dropping it.
        if (links.Count == 0 && allAnchors.Count > 0)
            links.Add(new SourceLink(new TextRange(0, spokenLength), allAnchors));
        return links;
    }

    /// <summary>
    /// Builds the spoken text for a mathematical unit. Never returns raw notation as if it were a
    /// natural reading.
    /// </summary>
    public async Task<NarrationSegment> BuildMathsSegmentAsync(ReadingUnit unit, int seq, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var warnings = new List<string>(unit.ExtractionWarnings);
        var anchors = unit.AnchorIds;

        string? representation = unit.Latex;
        if (representation is null && _resolver is not null)
            representation = await _resolver.ResolveRepresentationAsync(unit, ct).ConfigureAwait(false);

        // No representation at all: there is nothing to read and nothing to verify.
        if (string.IsNullOrWhiteSpace(representation))
        {
            warnings.Add("No recognised expression is available for this region, so it cannot be read aloud.");
            return Announcement(seq, unit, anchors, warnings, ScriptProvenance.FailureAnnouncement);
        }

        if (unit.IsUnverifiedText)
            warnings.Add("The expression was recovered from a scan or an image and has not been reviewed.");

        // No model configured. Fall back according to policy, and say which happened.
        if (_verbaliser is null || !await _verbaliser.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            if (_options.MathFallback == MathFallbackPolicy.LocalRuleReader && _localMathReader is not null)
            {
                string local = _localMathReader(unit, representation);
                warnings.Add("Read by the local rule-based reader, not a language model. Check it against the original.");
                return new NarrationSegment(
                    $"s{seq:D5}", [unit.Id], unit.Kind, local, _options.Language,
                    WholeUnitLinks(anchors, local.Length), _options.MathMode,
                    ScriptProvenance.LocalRule, warnings, 1);
            }
            warnings.Add("No maths narration service is configured, so the expression was not read.");
            return Announcement(seq, unit, anchors, warnings, ScriptProvenance.FailureAnnouncement);
        }

        var result = await _verbaliser.VerbaliseAsync(new MathVerbalisationRequest(
            representation, _options.MathMode, unit.ContextBefore, unit.ContextAfter, anchors, _options.Language), ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            foreach (var r in result.RejectionReasons) warnings.Add($"Verbalisation rejected: {r}");
            warnings.AddRange(result.Warnings);
            return Announcement(seq, unit, anchors, warnings, ScriptProvenance.FailureAnnouncement);
        }

        warnings.AddRange(result.Warnings);
        // An explanation is only appended in Explain mode, and is kept distinguishable from the
        // faithful reading so a listener is not told a guess as though it were the expression.
        string spoken = _options.MathMode == MathMode.Explain && result.Explanation is { Length: > 0 }
            ? $"{result.Faithful} {result.Explanation}"
            : result.Faithful!;

        // The model may only cite anchors that exist. Anything else was already rejected above, but
        // re-check here so a validator bug cannot leak an invented source into a highlight.
        var cited = result.CitedAnchorIds.Where(anchors.Contains).ToList();
        var linkAnchors = cited.Count > 0 ? cited : anchors;

        return new NarrationSegment(
            $"s{seq:D5}", [unit.Id], unit.Kind, spoken, _options.Language,
            WholeUnitLinks(linkAnchors, spoken.Length), _options.MathMode,
            ScriptProvenance.ModelGenerated, warnings, 1);
    }

    /// <summary>
    /// An equation's paraphrase has no word-for-word correspondence with the glyphs on the page, so
    /// the whole expression region is linked to the whole utterance. Inventing per-word links here
    /// would move the highlight onto text that was never read.
    /// </summary>
    private static IReadOnlyList<SourceLink> WholeUnitLinks(IReadOnlyList<string> anchors, int spokenLength)
        => anchors.Count > 0 ? [new SourceLink(new TextRange(0, spokenLength), anchors)] : [];

    private NarrationSegment Announcement(int seq, ReadingUnit unit, IReadOnlyList<string> anchors, List<string> warnings, ScriptProvenance provenance)
        => new(
            $"s{seq:D5}", [unit.Id], unit.Kind, _options.AnnouncementTemplate, _options.Language,
            WholeUnitLinks(anchors, _options.AnnouncementTemplate.Length), _options.MathMode,
            provenance, warnings, 1);
}
