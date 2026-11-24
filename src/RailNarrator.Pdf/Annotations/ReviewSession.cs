using RailReader.Core;
using RailReader.Core.Models;

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
    {
        ArgumentNullException.ThrowIfNull(target);
        var made = new List<(int Page, HighlightAnnotation Annotation)>();
        string? groupId = null;
        var time = _now();
        foreach (var page in target.Pages)
        {
            var annotation = (HighlightAnnotation)AnnotationInteractionHandler.CreateTextMarkup(
                AnnotationTool.Highlight, [.. page.Rects], colour, opacity);
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
            target.Pages.Single(p => p.PageIndex == m.Page).Ranges, target.QuotedText, time) { GroupId = groupId }).ToList();

        Apply(new Step(
            made.Select(m => (IUndoAction)new AddAnnotationAction(m.Page, m.Annotation)).ToList(),
            Added: links, Removed: []));
        Selected = made[0].Annotation;
        return made[0].Annotation;
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
        var link = Document.Links.Values.FirstOrDefault(l =>
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

    /// <summary>Stamps the modification time, and puts the old one back on undo.</summary>
    private sealed class TouchAction(Annotation annotation, DateTimeOffset time) : IUndoAction
    {
        private readonly DateTimeOffset? _before = annotation.ModifiedUtc;

        public void Undo(AnnotationFile file) => annotation.ModifiedUtc = _before;

        public void Redo(AnnotationFile file) => annotation.ModifiedUtc = time;
    }
}
