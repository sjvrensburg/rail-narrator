namespace RailNarrator.Domain;

/// <summary>What kind of content a reading unit holds, which decides how it is narrated and highlighted.</summary>
public enum ReadingUnitKind
{
    Prose,
    Heading,
    Title,
    Caption,
    ListItem,
    InlineMath,
    DisplayMath,
    Table,
    Figure,
    Footnote,
    Reference,
    Algorithm,
}

/// <summary>
/// One contiguous piece of a unit's <c>OriginalText</c> that maps to exactly one source anchor.
/// </summary>
/// <remarks>
/// Line membership is known exactly from the extraction, so these fragments are measured, not
/// inferred. They are what lets a narration segment carry per-line (and therefore per-column and
/// per-page) source links instead of one link covering the whole unit — which in turn is what stops
/// a two-column sentence from highlighting both columns at once.
/// </remarks>
public sealed record UnitSourceFragment(TextRange OriginalRange, string AnchorId, int PageIndex);

/// <summary>
/// One narratable piece of the document: a sentence, a heading, a caption, or a single
/// mathematical expression. The unit of narration planning, synthesis, highlighting and
/// previous/next navigation.
/// </summary>
/// <remarks>
/// <para>
/// A unit is built over <i>ordered source spans</i>, so it survives the normalisations reading
/// aloud requires — whitespace collapsing, line-break removal, dehyphenation, ligature expansion,
/// spoken number forms — by keeping the mapping from spoken text back to source characters rather
/// than by rewriting the source.
/// </para>
/// <para>
/// <see cref="Anchors"/> is plural because a sentence that runs across lines, columns or a page
/// break has several disjoint source regions. Highlighting draws all of them; it never draws their
/// union.
/// </para>
/// </remarks>
/// <param name="Id">Stable within a narration plan.</param>
/// <param name="Kind">Drives narration mode, filtering and highlight shape.</param>
/// <param name="Anchors">Source regions, in reading order, one or more per page the unit touches.</param>
/// <param name="OriginalText">The document's own text for this unit, whitespace-normalised but otherwise unedited.</param>
/// <param name="Latex">Recognised LaTeX for a mathematical unit; null for prose. Unverified unless a human has reviewed it.</param>
/// <param name="ContextBefore">Preceding source text, bounded. Supplied to a verbaliser so an expression can be read in context; never narrated itself.</param>
/// <param name="ContextAfter">Following source text, bounded. As <paramref name="ContextBefore"/>.</param>
/// <param name="ExtractionWarnings">Problems inherited from extraction, e.g. low-confidence OCR. Surfaced, not swallowed.</param>
/// <param name="PageIndex">Zero-based page the unit starts on.</param>
public sealed record ReadingUnit(
    string Id,
    ReadingUnitKind Kind,
    IReadOnlyList<SourceAnchor> Anchors,
    string OriginalText,
    string? Latex,
    string ContextBefore,
    string ContextAfter,
    IReadOnlyList<string> ExtractionWarnings,
    int PageIndex)
{
    /// <summary>
    /// Exact per-anchor decomposition of <see cref="OriginalText"/>. Empty for a unit whose geometry
    /// could not be resolved, in which case the whole unit links to all of its anchors.
    /// </summary>
    public IReadOnlyList<UnitSourceFragment> SourceFragments { get; init; } = [];

    /// <summary>
    /// Offsets in <see cref="OriginalText"/> where a space stands in for a source line break.
    /// Dehyphenation is only legitimate at a real line break, so the positions are recorded rather
    /// than guessed from where spaces happen to follow a hyphen.
    /// </summary>
    public IReadOnlyList<int> LineBreakOffsets { get; init; } = [];

    public bool IsMaths => Kind is ReadingUnitKind.InlineMath or ReadingUnitKind.DisplayMath;

    /// <summary>
    /// A prose sentence with mathematics inside it. The sentence stays one unit — cutting the maths
    /// out left fragments such as "where", "ε ∗", "t" — and is interpreted whole when a maths
    /// interpreter is configured, read verbatim otherwise.
    /// </summary>
    public bool ContainsMaths { get; init; }

    /// <summary>Every anchor id, in order. Used to validate that a verbaliser only cites real sources.</summary>
    public IReadOnlyList<string> AnchorIds => Anchors.Select(a => a.AnchorId).ToList();

    /// <summary>Pages this unit occupies. More than one means it crosses a page boundary.</summary>
    public IReadOnlyList<int> Pages => Anchors.Select(a => a.PageIndex).Distinct().OrderBy(p => p).ToList();

    /// <summary>True when any anchor came from recognition rather than the text layer.</summary>
    public bool IsUnverifiedText => Anchors.Any(a => a.Provenance is ExtractionProvenance.Ocr or ExtractionProvenance.VisualMath);

    /// <summary>All source character ranges, for hit-testing a click back to this unit.</summary>
    public IEnumerable<(int PageIndex, TextRange Range)> SourceRanges()
        => Anchors.Where(a => a.SourceCharRange is { } r).Select(a => (a.PageIndex, a.SourceCharRange!.Value));
}

/// <summary>
/// How a mathematical expression is turned into speech. The default is faithful, not summary.
/// </summary>
public enum MathMode
{
    /// <summary>
    /// Default. Read the expression as a mathematician would say it, preserving operators, bounds,
    /// indices, negation, conditioning and quantifiers. Never a vague paraphrase.
    /// </summary>
    NaturalFaithful,

    /// <summary>
    /// Speak every relevant component in natural syntax. User-selectable; not the mechanical
    /// reading of raw tokens.
    /// </summary>
    DetailedSymbolic,

    /// <summary>
    /// Faithful narration plus a short interpretation, grounded only in the supplied expression and
    /// context. The interpretation is marked separately as generated and can be disabled.
    /// </summary>
    Explain,
}

/// <summary>Where a narration segment's spoken text came from.</summary>
public enum ScriptProvenance
{
    /// <summary>Copied from the source with normalisation only. No model involved.</summary>
    SourceVerbatim,

    /// <summary>Rendered by a local rule (pronunciation dictionary, number expansion).</summary>
    LocalRule,

    /// <summary>Generated by a language model from a recognised expression.</summary>
    ModelGenerated,

    /// <summary>Typed or corrected by the user. Wins over every other source and invalidates downstream caches when edited.</summary>
    UserEdited,

    /// <summary>An explicit announcement that the content could not be read reliably.</summary>
    FailureAnnouncement,

    /// <summary>
    /// A fixed announcement standing in for content that is shown, not read: a table or an
    /// algorithm listing. Contains no document text.
    /// </summary>
    StructureAnnouncement,
}

/// <summary>
/// One span of spoken text linked back to the source regions it speaks for.
/// </summary>
/// <param name="SpokenRange">UTF-16 range into <see cref="NarrationSegment.SpokenText"/>.</param>
/// <param name="AnchorIds">Source anchors this span speaks for. Plural because one clause can cover a whole equation region plus the prose around it.</param>
/// <remarks>
/// The link is spoken-text → anchor ids, never spoken-text → coordinates. Geometry is only ever
/// reached through an anchor, so a model that emits plausible-looking numbers cannot move a
/// highlight: it can only cite an id that either exists or is rejected.
/// </remarks>
public sealed record SourceLink(TextRange SpokenRange, IReadOnlyList<string> AnchorIds);

/// <summary>
/// A piece of the narration script: the text that will be spoken, and exactly which parts of the
/// document it speaks for.
/// </summary>
/// <param name="Id">Stable within a narration plan.</param>
/// <param name="ReadingUnitIds">The units this segment speaks. Usually one; several when short units are joined for prosody.</param>
/// <param name="Kind">Copied from the dominant unit, so highlighting can shape itself without a lookup.</param>
/// <param name="SpokenText">The exact string handed to the speech provider. Alignment is against <i>this</i>, never against the PDF text or the LaTeX.</param>
/// <param name="Language">BCP-47. English first; carried so a future language is data, not a code change.</param>
/// <param name="SourceLinks">Spoken ranges → anchor ids.</param>
/// <param name="MathMode">The mode this segment was rendered under. Changing it invalidates the segment's speech.</param>
/// <param name="Provenance">Whether a model produced this text.</param>
/// <param name="Warnings">Ambiguities and recognition problems. Shown to the user; never silently resolved.</param>
/// <param name="ScriptRevision">Bumped on every edit. Speech assets and timings record the revision they were made from.</param>
public sealed record NarrationSegment(
    string Id,
    IReadOnlyList<string> ReadingUnitIds,
    ReadingUnitKind Kind,
    string SpokenText,
    string Language,
    IReadOnlyList<SourceLink> SourceLinks,
    MathMode MathMode,
    ScriptProvenance Provenance,
    IReadOnlyList<string> Warnings,
    int ScriptRevision)
{
    public bool IsMaths => Kind is ReadingUnitKind.InlineMath or ReadingUnitKind.DisplayMath;

    public bool IsModelGenerated => Provenance is ScriptProvenance.ModelGenerated;

    /// <summary>Every anchor this segment speaks for, in link order.</summary>
    public IReadOnlyList<string> AnchorIds => SourceLinks.SelectMany(l => l.AnchorIds).Distinct().ToList();

    /// <summary>True when the segment reports a problem the user should see before listening.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// The anchors a given spoken offset belongs to — what a word-level cue highlights. Returns
    /// every link covering the offset, so an equation and the clause around it both light up.
    /// </summary>
    public IReadOnlyList<string> AnchorsAt(int spokenUtf16Offset)
        => SourceLinks.Where(l => l.SpokenRange.Contains(spokenUtf16Offset)).SelectMany(l => l.AnchorIds).Distinct().ToList();
}

/// <summary>Plan-level metadata that decides whether a persisted plan still applies.</summary>
/// <param name="PlanId">Stable identity of this plan.</param>
/// <param name="Extraction">The extraction snapshot every anchor in the plan points into.</param>
/// <param name="Language">Narration language.</param>
/// <param name="MathMode">Default maths mode the plan was built under.</param>
/// <param name="PromptVersion">Version of the verbalisation prompt. A prompt change invalidates model-generated segments.</param>
/// <param name="ModelRevision">Model that produced the generated segments, or null when none did.</param>
/// <param name="FilterSettings">The inclusion/exclusion choices in force. Changing them rebuilds the plan rather than editing it.</param>
/// <param name="InputFingerprint">
/// Hash of every input that shaped this plan beyond the extraction identity — planner options,
/// filter settings and the exact reading units (which is where region overrides and unit-builder
/// options land). Computed by <see cref="NarrationPlanner.ComputeInputFingerprint"/>. Empty in
/// plans persisted before it existed; such plans never match and are rebuilt once. Note
/// <see cref="NarrationPlanHeader.ModelRevision"/> is deliberately not an input: it records which
/// model produced the segments, it does not choose one.
/// </param>
public sealed record NarrationPlanHeader(
    string PlanId,
    ExtractionIdentity Extraction,
    string Language,
    MathMode MathMode,
    string PromptVersion,
    string? ModelRevision,
    NarrationFilterSettings FilterSettings,
    string InputFingerprint = "");

/// <summary>
/// Which content classes are narrated. Every exclusion is an inspectable rule, never a silent
/// guess: a reader who wants the running headers read back can turn them on.
/// </summary>
/// <param name="ExcludeRunningHeaders">Drop repeated page headers. On by default.</param>
/// <param name="ExcludeFooters">Drop footers. On by default.</param>
/// <param name="ExcludePageNumbers">Drop page numbers. On by default.</param>
/// <param name="IncludeCitations">Read in-line citation markers such as "(Smith, 2019)".</param>
/// <param name="IncludeFootnotes">Read footnotes inline where they are referenced.</param>
/// <param name="IncludeCaptions">Read figure and table captions. On by default: a caption is usually the only narratable part of a visual region.</param>
/// <param name="IncludeReferences">Read the bibliography. Off by default.</param>
/// <param name="IncludeEquations">Read mathematical content. On by default.</param>
/// <param name="IncludeTables">Announce each table ("Table not read aloud.") with the whole table highlighted, rather than reading its cells. On by default; off skips tables silently. Algorithm listings are announced the same way.</param>
public sealed record NarrationFilterSettings(
    bool ExcludeRunningHeaders = true,
    bool ExcludeFooters = true,
    bool ExcludePageNumbers = true,
    bool IncludeCitations = false,
    bool IncludeFootnotes = false,
    bool IncludeCaptions = true,
    bool IncludeReferences = false,
    bool IncludeEquations = true,
    bool IncludeTables = true)
{
    public static NarrationFilterSettings Default { get; } = new();

    /// <summary>True when a block of this kind is narrated under these settings.</summary>
    public bool Allows(BlockKind kind) => kind switch
    {
        BlockKind.Header => !ExcludeRunningHeaders,
        BlockKind.Footer => !ExcludeFooters,
        BlockKind.PageNumber => !ExcludePageNumbers,
        BlockKind.Footnote => IncludeFootnotes,
        BlockKind.Reference => IncludeReferences,
        BlockKind.Caption => IncludeCaptions,
        BlockKind.DisplayMath or BlockKind.InlineMath => IncludeEquations,
        BlockKind.Table => IncludeTables,
        // A figure has no text of its own; its caption carries the content and is filtered separately.
        BlockKind.Figure or BlockKind.Chart or BlockKind.Decoration => false,
        _ => true,
    };
}

/// <summary>
/// The whole narration of a document: an ordered script, every source region it refers to, and the
/// identity of the extraction it was built from.
/// </summary>
/// <remarks>
/// Immutable. An edit produces a new plan with a bumped segment revision, so in-flight speech for
/// the old plan can be recognised and discarded rather than playing over the top of the new one.
/// </remarks>
public sealed record NarrationPlan(
    NarrationPlanHeader Header,
    IReadOnlyList<ReadingUnit> Units,
    IReadOnlyList<NarrationSegment> Segments)
{
    public int SegmentCount => Segments.Count;

    public NarrationSegment? Segment(string id) => Segments.FirstOrDefault(s => s.Id == id);

    public ReadingUnit? Unit(string id) => Units.FirstOrDefault(u => u.Id == id);

    /// <summary>Segments produced by a model, which is what a prompt or model change invalidates.</summary>
    public IReadOnlyList<NarrationSegment> ModelGeneratedSegments => Segments.Where(s => s.IsModelGenerated).ToList();

    /// <summary>Segments carrying a warning the user should see.</summary>
    public IReadOnlyList<NarrationSegment> SegmentsWithWarnings => Segments.Where(s => s.HasWarnings).ToList();

    /// <summary>
    /// True when this plan still describes <paramref name="extraction"/>. A false result means the
    /// document was re-extracted (re-OCR, different analyser, changed options) and every anchor,
    /// speech asset and timing built from it must be invalidated rather than remapped blindly.
    /// </summary>
    /// <param name="inputFingerprint">
    /// The fingerprint of the inputs a rebuild would use today (see
    /// <see cref="NarrationPlanHeader.InputFingerprint"/>). When supplied, the plan matches only if
    /// it was built from those same inputs — changing the maths mode, filters, pronunciation
    /// dictionary or a region override must rebuild, not silently reuse the cached plan. When
    /// null, only the extraction identity is compared (the original contract).
    /// </param>
    public bool Matches(ExtractionIdentity extraction, string? inputFingerprint = null)
        => Header.Extraction == extraction
           && (inputFingerprint is null || string.Equals(Header.InputFingerprint, inputFingerprint, StringComparison.Ordinal));

    /// <summary>Total spoken characters — a cheap progress denominator for the UI.</summary>
    public int TotalSpokenChars => Segments.Sum(s => s.SpokenText.Length);
}
