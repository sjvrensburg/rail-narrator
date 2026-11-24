using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;

namespace RailNarrator.Pdf.Annotations;

/// <summary>
/// A range of one page's text that an annotation was made on, as a UTF-16 offset into that page's
/// extracted text.
/// </summary>
public sealed record LinkedRange(int PageIndex, int Start, int Length);

/// <summary>
/// What Rail Narrator knows about an annotation that the annotation itself cannot carry: the
/// passage it was made on and the text it refers to (ADR-0014).
/// </summary>
/// <remarks>
/// Core's annotation has no field for a host's data, and its JSON contract drops unknown keys, so
/// links are kept beside the annotations and keyed by <see cref="Annotation.NativeId"/>, which Rail
/// Narrator sets when it creates one. <see cref="QuotedText"/> is kept so the notes can be exported,
/// and carried to a revised PDF, without the extraction they were made against.
/// </remarks>
/// <param name="NativeId">The annotation's <see cref="Annotation.NativeId"/>.</param>
/// <param name="SegmentId">The narration segment (passage) it was made on, if any.</param>
/// <param name="UnitId">The reading unit (sentence) it was made on, if any.</param>
/// <param name="ExtractionRevision">The extraction revision the ranges index into.</param>
/// <param name="Ranges">The source text it covers, per page.</param>
/// <param name="QuotedText">That text, as it was when the annotation was made.</param>
/// <param name="CreatedUtc">When the link was made.</param>
public sealed record ReviewLink(
    string NativeId,
    string? SegmentId,
    string? UnitId,
    int ExtractionRevision,
    IReadOnlyList<LinkedRange> Ranges,
    string QuotedText,
    DateTimeOffset CreatedUtc)
{
    /// <summary>
    /// The annotations made together as one: a sentence across a page break is one annotation per
    /// page, and they are kept, edited and removed as one. The first one's id; null when alone.
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>Made over a range of text by hand (annotate mode), not on what was just read.</summary>
    public bool ByHand { get; init; }
}

/// <summary>
/// The version of the same file (the same path) that the reader annotated before this one, by its
/// content hash: its notes can be brought over by their quoted text (<see cref="ReviewCarryOver"/>).
/// </summary>
/// <param name="DocumentContentHash">The earlier version's hash: where its notes are kept, untouched.</param>
public sealed record EarlierVersion(string DocumentContentHash)
{
    /// <summary>When its notes were brought over; null while they have not been.</summary>
    public DateTimeOffset? CarriedUtc { get; init; }
}

/// <summary>What <c>unplaced-links.json</c> holds: the links of the notes not placed, and each one's page in the earlier version.</summary>
public sealed record UnplacedState(IReadOnlyList<ReviewLink> Links, IReadOnlyDictionary<string, int> EarlierPages);

/// <summary>
/// The reader's annotations on one document, with their links. Owned and changed on the UI thread.
/// </summary>
/// <remarks>
/// Whoever changes <see cref="File"/> or <see cref="Links"/> calls <see cref="MarkChanged"/>, which
/// moves <see cref="Version"/> on: the render layer rebuilds its copy and the store saves.
/// </remarks>
public sealed class ReviewDocument
{
    public ReviewDocument(string documentContentHash, string? sourcePath, AnnotationFile? file = null,
        IEnumerable<ReviewLink>? links = null, EarlierVersion? earlier = null, AnnotationFile? unplaced = null,
        UnplacedState? unplacedState = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentContentHash);
        DocumentContentHash = documentContentHash;
        SourcePath = sourcePath;
        File = file ?? new AnnotationFile();
        Links = links?.ToDictionary(l => l.NativeId, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        Earlier = earlier;
        Unplaced = unplaced ?? new AnnotationFile();
        UnplacedLinks = unplacedState?.Links.ToDictionary(l => l.NativeId, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        UnplacedEarlierPages = unplacedState?.EarlierPages.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            ?? new(StringComparer.Ordinal);
    }

    /// <summary>The earlier version of this file whose notes may be (or were) brought over; null when there is none.</summary>
    public EarlierVersion? Earlier { get; set; }

    /// <summary>
    /// Notes brought from an earlier version that could not be placed on this one (their text is not
    /// here, or they were tied to no text), by the page of this version "Place as before" puts them
    /// on. Kept until the reader
    /// places or discards them; never drawn, never exported into a PDF.
    /// </summary>
    public AnnotationFile Unplaced { get; }

    /// <summary>The links of <see cref="Unplaced"/>, as they were in the earlier version.</summary>
    public Dictionary<string, ReviewLink> UnplacedLinks { get; }

    /// <summary>Each note of <see cref="Unplaced"/>, by id: its page in the version it came from (for the reader only).</summary>
    public Dictionary<string, int> UnplacedEarlierPages { get; }

    /// <summary>True when notes from an earlier version are waiting to be placed or discarded.</summary>
    public bool HasUnplaced => Unplaced.Pages.Values.Any(p => p.Count > 0);

    /// <summary>SHA-256 of the PDF's bytes: the key the annotations are stored under.</summary>
    public string DocumentContentHash { get; }

    /// <summary>Where the PDF was opened from, when known.</summary>
    public string? SourcePath { get; }

    /// <summary>The annotations, in Core's model.</summary>
    public AnnotationFile File { get; }

    /// <summary>Links by <see cref="Annotation.NativeId"/>.</summary>
    public Dictionary<string, ReviewLink> Links { get; }

    /// <summary>Moves on with every change.</summary>
    public long Version { get; private set; }

    /// <summary>Raised on the thread that made the change, after <see cref="Version"/> has moved on.</summary>
    public event Action? Changed;

    /// <summary>True when there is nothing to keep.</summary>
    public bool IsEmpty => Links.Count == 0 && File.Bookmarks.Count == 0 && File.Pages.Values.All(p => p.Count == 0);

    /// <summary>The annotations on a page, live; never hand these to another thread.</summary>
    public IReadOnlyList<Annotation> PageAnnotations(int pageIndex)
        => File.Pages.TryGetValue(pageIndex, out var list) ? list : [];

    private Dictionary<int, List<Annotation>> _fromPdf = [];

    /// <summary>
    /// Annotations the PDF itself carries (a co-reviewer's comments, say), shown read-only. They are
    /// never saved into this document's file and never edited here; an exported copy keeps them
    /// because it is written from the PDF's own bytes.
    /// </summary>
    public IReadOnlyList<Annotation> PdfAnnotations(int pageIndex)
        => _fromPdf.TryGetValue(pageIndex, out var list) ? list : [];

    /// <summary>Every page that has annotations the PDF itself carries.</summary>
    public IEnumerable<(int Page, IReadOnlyList<Annotation> Annotations)> AllPdfAnnotations
        => _fromPdf.OrderBy(p => p.Key).Select(p => (p.Key, (IReadOnlyList<Annotation>)p.Value));

    /// <summary>True for an annotation the PDF itself carries, which is not this document's to change.</summary>
    public bool IsFromPdf(Annotation annotation)
        => _fromPdf.Values.Any(list => list.Any(a => ReferenceEquals(a, annotation)));

    /// <summary>Shows the PDF's own annotations (or none). A change of view: nothing is saved.</summary>
    public void SetPdfAnnotations(AnnotationFile? file)
    {
        _fromPdf = file?.Pages.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value) ?? [];
        MarkViewChanged();
    }

    public void MarkChanged()
    {
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Raised when only how the annotations look has changed (a note opened): repaint, nothing to save.</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// For a change that is not kept (a note's popup opened or closed): the drawn copy is made again,
    /// and nothing is saved.
    /// </summary>
    public void MarkViewChanged()
    {
        Version++;
        ViewChanged?.Invoke();
    }
}

/// <summary>
/// Core's annotation JSON, written and read here so that it can be produced in memory.
/// </summary>
/// <remarks>
/// Core writes this format with an internal, source-generated context (snake_case names, indented,
/// no converters, the polymorphic <c>"type"</c> discriminator declared on <see cref="Annotation"/>)
/// and offers only file-based helpers. The same options here give the same document, so a save is
/// serialised on the UI thread, where the model is owned, and written to disk on another. The tests
/// hold this to Core's own <c>AnnotationService.ExportJson</c> and <c>ImportJson</c> both ways.
/// </remarks>
public static class ReviewAnnotationJson
{
    private static readonly JsonSerializerOptions CoreFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions LinkFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(AnnotationFile file) => JsonSerializer.Serialize(file, CoreFormat);

    /// <summary>Null when <paramref name="json"/> is not an annotation file.</summary>
    public static AnnotationFile? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AnnotationFile>(json, CoreFormat); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static string SerializeLinks(IEnumerable<ReviewLink> links)
        => JsonSerializer.Serialize(new LinkFile(1, [.. links]), LinkFormat);

    /// <summary>Null when <paramref name="json"/> is not a link file.</summary>
    public static IReadOnlyList<ReviewLink>? DeserializeLinks(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<LinkFile>(json, LinkFormat);
            if (file?.Links is null) return null;
            return file.Links.Any(l => string.IsNullOrEmpty(l.NativeId)) ? null : file.Links;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// A deep copy that keeps everything, including the state Core's JSON leaves out (a note's
    /// <see cref="TextNoteAnnotation.IsExpanded"/>, <see cref="Annotation.WrittenByRailReader"/>).
    /// Unlike Core's <c>CloneForPaste</c>, the copy keeps its identity and timestamps.
    /// </summary>
    public static Annotation Clone(Annotation source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = JsonSerializer.Deserialize<Annotation>(JsonSerializer.Serialize(source, CoreFormat), CoreFormat)
            ?? throw new InvalidOperationException("An annotation copied to nothing.");
        clone.WrittenByRailReader = source.WrittenByRailReader;
        if (source is TextNoteAnnotation note && clone is TextNoteAnnotation copy) copy.IsExpanded = note.IsExpanded;
        return clone;
    }

    public static string SerializeEarlier(EarlierVersion earlier)
        => JsonSerializer.Serialize(new EarlierFile(1, earlier.DocumentContentHash, earlier.CarriedUtc), LinkFormat);

    /// <summary>Null when <paramref name="json"/> is not an earlier-version file.</summary>
    public static EarlierVersion? DeserializeEarlier(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<EarlierFile>(json, LinkFormat);
            return file is null || string.IsNullOrEmpty(file.Hash) ? null : new EarlierVersion(file.Hash) { CarriedUtc = file.CarriedUtc };
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static string SerializeUnplaced(IEnumerable<ReviewLink> links, IReadOnlyDictionary<string, int> earlierPages)
        => JsonSerializer.Serialize(new UnplacedFile(1, [.. links], earlierPages.ToDictionary(p => p.Key, p => p.Value)), LinkFormat);

    /// <summary>Null when <paramref name="json"/> is not a file of unplaced notes' links.</summary>
    public static UnplacedState? DeserializeUnplaced(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<UnplacedFile>(json, LinkFormat);
            if (file?.Links is null || file.Links.Any(l => string.IsNullOrEmpty(l.NativeId))) return null;
            return new UnplacedState(file.Links, file.EarlierPages ?? []);
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private sealed record LinkFile(int Version, List<ReviewLink> Links);

    private sealed record EarlierFile(int Version, string Hash, DateTimeOffset? CarriedUtc);

    private sealed record UnplacedFile(int Version, List<ReviewLink> Links, Dictionary<string, int>? EarlierPages);
}
