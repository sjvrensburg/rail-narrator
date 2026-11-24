using Avalonia.Media;
using RailReader.Core.Models;

namespace RailNarrator.App.ViewModels;

/// <summary>One entry of the sidebar's Review notes: an annotation (or a group made as one), where it is and what it says.</summary>
/// <param name="Annotation">The annotation, or the first of its group, which carries the note.</param>
/// <param name="PageIndex">Zero-based page it starts on.</param>
/// <param name="SegmentId">The passage it was made on, when it was made while listening.</param>
/// <param name="Quote">The document's text it is on; empty for a mark not made on text.</param>
/// <param name="Note">What the reader wrote; null for a highlight without a note.</param>
public sealed record ReviewNoteItem(Annotation Annotation, int PageIndex, string? SegmentId, string Quote, string? Note)
{
    public string PageLabel => $"Page {PageIndex + 1} · {Kind}";

    /// <summary>What the annotation is, in the reader's words: a drawing has no quote or note to show.</summary>
    public string Kind => Annotation switch
    {
        HighlightAnnotation => "Highlight",
        UnderlineAnnotation => "Underline",
        StrikeOutAnnotation => "Struck out",
        SquigglyAnnotation => "Squiggle",
        TextNoteAnnotation => "Sticky note",
        FreeTextAnnotation => "Text box",
        FreehandAnnotation => "Pen",
        RectAnnotation => "Box",
        _ => "Annotation",
    };

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public bool HasQuote => Quote.Length > 0;

    /// <summary>The annotation's own colour, for the swatch beside it.</summary>
    public IBrush Swatch => Color.TryParse(Annotation.Color, out var colour)
        ? new SolidColorBrush(colour)
        : Brushes.Gold;

    /// <summary>For a screen reader: the whole entry in one sentence.</summary>
    public string Description => $"{PageLabel}. " + (HasNote ? $"Note: {Note}. " : "") + (HasQuote ? $"On: {Quote}" : "");
}
