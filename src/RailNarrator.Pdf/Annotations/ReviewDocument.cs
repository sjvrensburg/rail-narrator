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
/// The reader's annotations on one document, with their links. Owned and changed on the UI thread.
/// </summary>
/// <remarks>
/// Whoever changes <see cref="File"/> or <see cref="Links"/> calls <see cref="MarkChanged"/>, which
/// moves <see cref="Version"/> on: the render layer rebuilds its copy and the store saves.
/// </remarks>
public sealed class ReviewDocument
{
    public ReviewDocument(string documentContentHash, string? sourcePath, AnnotationFile? file = null,
        IEnumerable<ReviewLink>? links = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentContentHash);
        DocumentContentHash = documentContentHash;
        SourcePath = sourcePath;
        File = file ?? new AnnotationFile();
        Links = links?.ToDictionary(l => l.NativeId, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
    }

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

    private sealed record LinkFile(int Version, List<ReviewLink> Links);
}
