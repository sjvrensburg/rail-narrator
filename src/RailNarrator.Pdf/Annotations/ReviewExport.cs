using System.Globalization;
using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailNarrator.Pdf.Annotations;

/// <summary>One annotation as an export lists it.</summary>
/// <param name="Page">Zero-based page.</param>
/// <param name="Kind">What it is, in the reader's words ("Highlight", "Sticky note").</param>
/// <param name="Category">What its colour means to the reader, if it is one of the five.</param>
/// <param name="Quote">The document's text it is on; empty for a mark not made on text.</param>
/// <param name="Note">What was written; null when nothing was.</param>
/// <param name="Author">Who signed it, if anyone.</param>
/// <param name="FromPdf">Carried by the PDF itself (a co-reviewer's), not made here.</param>
public sealed record ExportedNote(int Page, string Kind, string? Category, string Quote, string? Note, string? Author, bool FromPdf)
{
    /// <summary>Brought from an earlier version of the PDF and not placed on this one; <see cref="Page"/> is its page there.</summary>
    public bool Unplaced { get; init; }
}

/// <summary>The reader's notes as Markdown: in page and reading order, each with the text it is on.</summary>
public static class ReviewMarkdown
{
    /// <summary>What an annotation is, in the reader's words.</summary>
    public static string KindOf(Annotation annotation) => annotation switch
    {
        HighlightAnnotation => "Highlight",
        UnderlineAnnotation => "Underline",
        StrikeOutAnnotation => "Struck out",
        SquigglyAnnotation => "Squiggle",
        TextNoteAnnotation => "Sticky note",
        FreeTextAnnotation => "Text box",
        FreehandAnnotation => "Pen",
        RectAnnotation => "Box",
        CaretAnnotation => "Insertion",
        _ => "Annotation",
    };

    /// <param name="title">The document's name.</param>
    /// <param name="notes">In the order they are to be read.</param>
    /// <param name="date">The day of the export.</param>
    public static string Build(string title, IReadOnlyList<ExportedNote> notes, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var md = new StringBuilder();
        md.Append("# Review notes: ").Append(Escape(title)).Append("\n\n");
        int own = notes.Count(n => !n.FromPdf && !n.Unplaced);
        int inThePdf = notes.Count(n => n.FromPdf), unplaced = notes.Count(n => n.Unplaced);
        md.Append(date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)).Append(" · ")
          .Append(own == 1 ? "1 annotation" : $"{own} annotations");
        if (inThePdf > 0) md.Append($", and {inThePdf} already in the PDF");
        if (unplaced > 0) md.Append($", and {unplaced} from an earlier version not placed in this one");
        md.Append("\n");

        foreach (var group in notes.Where(n => !n.FromPdf && !n.Unplaced).GroupBy(n => n.Page))
        {
            md.Append("\n## Page ").Append(group.Key + 1).Append("\n");
            foreach (var note in group) Entry(md, note);
        }
        var inPdf = notes.Where(n => n.FromPdf).ToList();
        if (inPdf.Count > 0)
        {
            md.Append("\n## Already in the PDF\n");
            foreach (var note in inPdf) Entry(md, note, withPage: true);
        }
        var notPlaced = notes.Where(n => n.Unplaced).ToList();
        if (notPlaced.Count > 0)
        {
            md.Append("\n## From an earlier version, not found in this one\n");
            foreach (var note in notPlaced) Entry(md, note, withPage: true, pageWord: "earlier page ");
        }
        return md.ToString();
    }

    private static void Entry(StringBuilder md, ExportedNote note, bool withPage = false, string pageWord = "page ")
    {
        md.Append("\n**").Append(note.Kind);
        if (note.Category is { Length: > 0 } category) md.Append(" · ").Append(Escape(category));
        if (withPage) md.Append(" · ").Append(pageWord).Append(note.Page + 1);
        md.Append("**");
        if (note.Author is { Length: > 0 } author) md.Append(" (").Append(Escape(author)).Append(')');
        md.Append("\n");
        if (note.Quote.Length > 0) md.Append("\n> ").Append(Escape(note.Quote).Replace("\n", "\n> ")).Append("\n");
        if (note.Note is { Length: > 0 } text) md.Append('\n').Append(text.Trim()).Append('\n');
    }

    /// <summary>Text from a PDF read as text, not as Markdown (an asterisk in a formula, a leading "#").</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#' or '|') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>What <see cref="AnnotatedPdfExport.Write"/> did.</summary>
/// <param name="Written">Annotations written into the copy.</param>
/// <param name="Warning">Something the reader should know about the copy (a signature it no longer carries).</param>
public sealed record AnnotatedPdfResult(int Written, string? Warning);

/// <summary>
/// A copy of the PDF with the reader's annotations in it as native PDF annotations, for any
/// viewer (ADR-0014). The reader's own PDF is never written.
/// </summary>
/// <remarks>
/// Written with Core's <c>PdfAnnotationWriter.AddAuthoredAnnotations</c>, which appends what was made
/// here to the original's bytes: annotations the PDF already carries stay as they are. It is not
/// idempotent, which does not matter here, because each export starts again from the original.
/// The writer saves the whole file again, so a digitally signed PDF's copy is no longer validly
/// signed, and the result says so.
/// </remarks>
public static class AnnotatedPdfExport
{
    public static AnnotatedPdfResult Write(string sourcePath, ReviewDocument document, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.Ordinal))
            throw new IOException("The annotated copy cannot replace the PDF it is made from. Choose another name.");

        // A copy of the annotations: the writer may fill in what it writes, and the reader's file is
        // the store's, not the export's.
        var copy = ReviewAnnotationJson.Deserialize(ReviewAnnotationJson.Serialize(document.File))
            ?? throw new InvalidOperationException("The annotations could not be copied for export.");
        foreach (var annotation in copy.Pages.Values.SelectMany(p => p)) annotation.Source = AnnotationSource.RailReader;
        int count = copy.Pages.Values.Sum(p => p.Count);

        byte[] original = File.ReadAllBytes(sourcePath);
        byte[] annotated = new PdfAnnotationWriter().AddAuthoredAnnotations(original, copy);

        string temp = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, annotated);
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        string? warning = new CompositeAnnotationStore(NullAnnotationStore.Instance).GetSidecarFallbackReason(sourcePath)
            == SidecarFallbackReason.Signed
            ? "The PDF is digitally signed; the annotated copy is saved again whole, so it is no longer validly signed."
            : null;
        return new AnnotatedPdfResult(count, warning);
    }

    /// <summary>The annotations a PDF carries itself, read-only (Core's reader; never throws, may be empty).</summary>
    public static AnnotationFile ReadExisting(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pdfPath);
        return new PdfAnnotationReader().Read(File.ReadAllBytes(pdfPath));
    }
}
