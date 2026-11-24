using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailNarrator.Pdf.Annotations;

/// <summary>
/// Editing one document's annotations: what is made, changed and removed, undone and redone.
/// Used on the UI thread; every change ends with <see cref="ReviewDocument.MarkChanged"/>, which
/// saves and repaints.
/// </summary>
/// <remarks>
/// Each change is one undoable step, built from Core's own undo actions
/// (<see cref="AddAnnotationAction"/>, <see cref="RemoveAnnotationAction"/>,
/// <see cref="EditContentsAction"/>) with the links to the narration kept in step beside them. A
/// sentence across a page break is one annotation per page made, changed and removed together:
/// its note is on the first.
/// </remarks>
public sealed class ReviewSession
{
    /// <summary>The colour of a highlight made while listening: Core's first, yellow.</summary>
    public const string DefaultColour = "#FFFF00";

    /// <summary>Core's default opacity for a highlight.</summary>
    public const float HighlightOpacity = 0.35f;

    private readonly Stack<Step> _undo = new();
    private readonly Stack<Step> _redo = new();
    private readonly Func<DateTimeOffset> _now;
    private Annotation? _selected;

    public ReviewSession(ReviewDocument document, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public ReviewDocument Document { get; }

    /// <summary>The name annotations are signed with (PDF /T); none by default.</summary>
    public string? Author { get; set; }

    /// <summary>The annotation picked on the page or in the list; drawn with its handles and note.</summary>
    public Annotation? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            SelectionChanged?.Invoke();
        }
    }

    public event Action? SelectionChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Highlights a target, with a note on it when <paramref name="note"/> has text. Returns the
    /// annotation that carries the note (the first page's).
    /// </summary>
    public HighlightAnnotation Highlight(ReviewTarget target, string? note = null,
        string colour = DefaultColour, float opacity = HighlightOpacity)
        => (HighlightAnnotation)Markup(target, AnnotationTool.Highlight, note, colour, opacity);

    /// <summary>
    /// Marks a target with a text markup (highlight, underline, strikeout or squiggly), with a note
    /// when <paramref name="note"/> has text. Returns the annotation that carries the note.
    /// </summary>
    public TextMarkupAnnotation Markup(ReviewTarget target, AnnotationTool tool, string? note, string colour, float opacity)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!AnnotationInteractionHandler.IsTextMarkupTool(tool))
            throw new ArgumentException($"{tool} is not a text markup.", nameof(tool));
        var made = new List<(int Page, TextMarkupAnnotation Annotation)>();
        string? groupId = null;
        var time = _now();
        foreach (var page in target.Pages)
        {
            var annotation = AnnotationInteractionHandler.CreateTextMarkup(tool, [.. page.Rects], colour, opacity);
            annotation.NativeId = Guid.NewGuid().ToString();
            annotation.Source = AnnotationSource.RailReader;
            annotation.Author = Author;
            annotation.CreatedUtc = time;
            if (made.Count == 0 && !string.IsNullOrWhiteSpace(note)) annotation.Contents = note.Trim();
            groupId ??= target.Pages.Count > 1 ? annotation.NativeId : null;
            made.Add((page.PageIndex, annotation));
        }

        var links = made.Select(m => new ReviewLink(
            m.Annotation.NativeId!, target.SegmentId, target.UnitId, target.ExtractionRevision,
            target.Pages.Single(p => p.PageIndex == m.Page).Ranges, target.QuotedText, time)
            { GroupId = groupId, ByHand = target.ByHand }).ToList();

        Apply(new Step(
            made.Select(m => (IUndoAction)new AddAnnotationAction(m.Page, m.Annotation)).ToList(),
            Added: links, Removed: []));
        Selected = made[0].Annotation;
        return made[0].Annotation;
    }

    /// <summary>Core's opacity for a sticky note.</summary>
    public const float NoteOpacity = 0.9f;

    /// <summary>A text box's default size, in points, when it is placed with a click rather than dragged out.</summary>
    public const float DefaultTextWidth = 180, DefaultTextHeight = 48;

    /// <summary>A sticky note at a point of a page, not tied to any text (the icon's centre).</summary>
    public TextNoteAnnotation AddNote(int pageIndex, float x, float y, string text, string colour = DefaultColour)
    {
        var note = Stamp(new TextNoteAnnotation { X = x, Y = y, Text = text.Trim(), Color = colour, Opacity = NoteOpacity });
        Apply(new Step([new AddAnnotationAction(pageIndex, note)], [], []));
        Selected = note;
        return note;
    }

    /// <summary>Text written on the page in a box (a PDF FreeText annotation).</summary>
    public FreeTextAnnotation AddText(int pageIndex, float x, float y, float width, float height, string text, string colour)
    {
        var box = Stamp(new FreeTextAnnotation { X = x, Y = y, W = width, H = height, Contents = text.Trim(), Color = colour, Opacity = 1f });
        Apply(new Step([new AddAnnotationAction(pageIndex, box)], [], []));
        Selected = box;
        return box;
    }

    /// <summary>
    /// Records a move already made on the page (<see cref="AnnotationMove"/>) as one undoable step.
    /// Nothing is recorded when it did not move.
    /// </summary>
    public void Moved(AnnotationMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        if (!move.Moved) return;
        var after = PositionSnapshot.Capture(move.Annotation);
        Apply(new Step([new MoveAnnotationAction(move.Annotation, move.Before, after), new TouchAction(move.Annotation, _now())], [], []));
    }

    /// <summary>Core's opacities for a pen stroke and a rectangle.</summary>
    public const float PenOpacity = 0.8f, RectangleOpacity = 0.5f;

    /// <summary>A pen stroke through the given points.</summary>
    public FreehandAnnotation AddStroke(int pageIndex, IEnumerable<PointF> points, string colour, float width, float opacity = PenOpacity)
    {
        var stroke = Stamp(new FreehandAnnotation { Points = [.. points], Color = colour, StrokeWidth = width, Opacity = opacity });
        if (stroke.Points.Count < 2) throw new ArgumentException("A stroke needs two points or more.", nameof(points));
        Apply(new Step([new AddAnnotationAction(pageIndex, stroke)], [], []));
        Selected = stroke;
        return stroke;
    }

    /// <summary>A rectangle drawn round something (unfilled).</summary>
    public RectAnnotation AddRectangle(int pageIndex, float x, float y, float width, float height, string colour, float strokeWidth,
        float opacity = RectangleOpacity)
    {
        var box = Stamp(new RectAnnotation { X = x, Y = y, W = width, H = height, Color = colour, StrokeWidth = strokeWidth, Opacity = opacity });
        Apply(new Step([new AddAnnotationAction(pageIndex, box)], [], []));
        Selected = box;
        return box;
    }

    /// <summary>Records a resize already made on the page (<see cref="StrokeResize"/>) as one undoable step.</summary>
    public void Resized(StrokeResize resize)
    {
        ArgumentNullException.ThrowIfNull(resize);
        if (!resize.Resized) return;
        Apply(new Step([new ResizeFreehandAction(resize.Stroke, resize.Before, [.. resize.Stroke.Points]), new TouchAction(resize.Stroke, _now())], [], []));
    }

    /// <summary>
    /// Adds annotations exported from RailReader2 (or from here), skipping any this document
    /// already has (Core's <c>MergeInto</c> with <c>skipEquivalent</c>: the same /NM on the same page,
    /// or the same annotation by content), as one undoable step.
    /// </summary>
    /// <returns>How many were added and how many were already here.</returns>
    public (int Added, int Skipped) Import(AnnotationFile imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        int offered = imported.Pages.Values.Sum(p => p.Count);
        // Merged into copies of what is here, so the ones that are new can be told apart and added
        // with Core's undo actions rather than by the merge itself.
        var scratch = new AnnotationFile();
        var copies = new HashSet<Annotation>(ReferenceEqualityComparer.Instance);
        foreach (var (page, list) in Document.File.Pages)
        {
            var copy = list.Select(ReviewAnnotationJson.Clone).ToList();
            foreach (var c in copy) copies.Add(c);
            scratch.Pages[page] = copy;
        }
        AnnotationService.MergeInto(scratch, imported, skipEquivalent: true);

        var added = new List<IUndoAction>();
        foreach (var (page, list) in scratch.Pages.OrderBy(p => p.Key))
            foreach (var annotation in list.Where(a => !copies.Contains(a)))
            {
                annotation.NativeId ??= Guid.NewGuid().ToString();
                // Ours now: written into an exported copy like any annotation made here.
                annotation.Source = AnnotationSource.RailReader;
                added.Add(new AddAnnotationAction(page, annotation));
            }
        if (added.Count > 0) Apply(new Step(added, [], []));
        return (added.Count, offered - added.Count);
    }

    private T Stamp<T>(T annotation) where T : Annotation
    {
        annotation.NativeId = Guid.NewGuid().ToString();
        annotation.Source = AnnotationSource.RailReader;
        annotation.Author = Author;
        annotation.CreatedUtc = _now();
        return annotation;
    }

    /// <summary>Sets an annotation's note; an empty text removes it.</summary>
    public void SetNote(Annotation annotation, string? text)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        string? old = annotation is TextNoteAnnotation n ? n.EffectiveContents : annotation.Contents;
        string? value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (old == value || (string.IsNullOrEmpty(old) && value is null)) return;
        Apply(new Step([new EditContentsAction(annotation, old, value), new TouchAction(annotation, _now())], [], []));
    }

    /// <summary>Gives an annotation (and the others made with it) another colour, as one undoable step.</summary>
    public void Recolour(Annotation annotation, string colour)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var group = GroupOf(annotation).Select(g => g.Annotation).ToList();
        if (group.All(a => a.Color == colour && a.ColorComponents is null)) return;
        Apply(new Step([.. group.Select(a => (IUndoAction)new RecolourAction(a, colour)), new TouchAction(group[0], _now())], [], []));
    }

    /// <summary>Removes an annotation, and the others made with it.</summary>
    public void Delete(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var group = GroupOf(annotation);
        var removed = group.Select(g => g.Annotation.NativeId is { } id && Document.Links.TryGetValue(id, out var l) ? l : null)
            .OfType<ReviewLink>().ToList();
        Apply(new Step(group.Select(g => (IUndoAction)new RemoveAnnotationAction(g.Page, g.Annotation)).ToList(), [], removed));
        if (group.Any(g => ReferenceEquals(g.Annotation, Selected))) Selected = null;
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var step)) return;
        foreach (var action in Enumerable.Reverse(step.Actions)) action.Undo(Document.File);
        foreach (var link in step.Added) Document.Links.Remove(link.NativeId);
        foreach (var link in step.Removed) Document.Links[link.NativeId] = link;
        _redo.Push(step);
        if (Selected is not null && !Contains(Selected)) Selected = null;
        Document.MarkChanged();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var step)) return;
        Do(step);
        _undo.Push(step);
        if (Selected is not null && !Contains(Selected)) Selected = null;
        Document.MarkChanged();
    }

    /// <summary>
    /// The annotation already made on exactly this target (the same sentence, or the same whole
    /// passage), if any: its group's first. A second note or highlight on the same words is the
    /// first one again, not another layer of yellow.
    /// </summary>
    public Annotation? MadeOn(ReviewTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ByHand || target.SegmentId is null) return null;
        var link = Document.Links.Values.FirstOrDefault(l => !l.ByHand &&
            l.SegmentId == target.SegmentId && l.UnitId == target.UnitId
            && (l.GroupId is null || l.GroupId == l.NativeId)
            && Document.File.Pages.Values.Any(p => p.Any(a => a.NativeId == l.NativeId)));
        if (link is null) return null;
        return Document.File.Pages.Values.SelectMany(p => p).First(a => a.NativeId == link.NativeId);
    }

    /// <summary>The page an annotation is on; null when it is not in the document.</summary>
    public int? PageOf(Annotation annotation)
    {
        foreach (var (page, list) in Document.File.Pages)
            if (list.Any(a => ReferenceEquals(a, annotation))) return page;
        return null;
    }

    /// <summary>The annotation made with <paramref name="annotation"/> that carries the group's note: the first.</summary>
    public Annotation Head(Annotation annotation) => GroupOf(annotation)[0].Annotation;

    /// <summary>The link of an annotation, or of its group's head.</summary>
    public ReviewLink? LinkOf(Annotation annotation)
        => annotation.NativeId is { } id && Document.Links.TryGetValue(id, out var link) ? link : null;

    private List<(int Page, Annotation Annotation)> GroupOf(Annotation annotation)
    {
        var self = (Page: PageOf(annotation) ?? -1, Annotation: annotation);
        if (LinkOf(annotation)?.GroupId is not { } group) return [self];
        var members = Document.Links.Values.Where(l => l.GroupId == group).Select(l => l.NativeId).ToHashSet(StringComparer.Ordinal);
        var found = Document.File.Pages.OrderBy(p => p.Key)
            .SelectMany(p => p.Value.Where(a => a.NativeId is { } id && members.Contains(id)).Select(a => (Page: p.Key, Annotation: a)))
            .ToList();
        return found.Count > 0 ? found : [self];
    }

    private bool Contains(Annotation annotation) => PageOf(annotation) is not null;

    private void Apply(Step step)
    {
        Do(step);
        _undo.Push(step);
        _redo.Clear();
        Document.MarkChanged();
    }

    private void Do(Step step)
    {
        foreach (var action in step.Actions) action.Redo(Document.File);
        foreach (var link in step.Removed) Document.Links.Remove(link.NativeId);
        foreach (var link in step.Added) Document.Links[link.NativeId] = link;
    }

    /// <param name="Actions">Core's actions, done in order and undone in reverse.</param>
    /// <param name="Added">Links the step makes.</param>
    /// <param name="Removed">Links the step takes away.</param>
    private sealed record Step(IReadOnlyList<IUndoAction> Actions, IReadOnlyList<ReviewLink> Added, IReadOnlyList<ReviewLink> Removed);

    /// <summary>
    /// A new colour. The faithful components read from a PDF are dropped with it, or they would
    /// win over the new colour when written (Core: "authoritative over Color for round-trip").
    /// </summary>
    private sealed class RecolourAction(Annotation annotation, string colour) : IUndoAction
    {
        private readonly string _before = annotation.Color;
        private readonly float[]? _componentsBefore = annotation.ColorComponents;

        public void Undo(AnnotationFile file)
        {
            annotation.Color = _before;
            annotation.ColorComponents = _componentsBefore;
        }

        public void Redo(AnnotationFile file)
        {
            annotation.Color = colour;
            annotation.ColorComponents = null;
        }
    }

    /// <summary>Stamps the modification time, and puts the old one back on undo.</summary>
    private sealed class TouchAction(Annotation annotation, DateTimeOffset time) : IUndoAction
    {
        private readonly DateTimeOffset? _before = annotation.ModifiedUtc;

        public void Undo(AnnotationFile file) => annotation.ModifiedUtc = _before;

        public void Redo(AnnotationFile file) => annotation.ModifiedUtc = time;
    }
}
