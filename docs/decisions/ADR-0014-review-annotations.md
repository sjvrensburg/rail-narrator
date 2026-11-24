# ADR-0014: Review annotations

**Status:** accepted, not yet implemented · **Date:** 2026-11-24 · **Follows:** ADR-0001, ADR-0012

## Decision

A reader can mark up a document while listening to it: pause, write a note on what was just read,
and carry on. This is for reviewing papers and student work by ear. The reader chose the
following (2026-11-24):

- After a note is saved, narration **stays paused**. Ctrl+Enter saves and resumes in one step.
- Annotations are kept in **Rail Narrator's own data directory, keyed by the PDF's content
  hash**. The reader's PDF is never modified. **Export annotated PDF…** writes a copy with
  native PDF annotations in it.
- Tools: a note or highlight on the sentence or passage just read, drag-select markup
  (highlight, underline, strikeout, squiggly), sticky notes and free text, pen and rectangle.
- Export: an annotated PDF copy, the notes as Markdown with the text they refer to, and
  RailReader2's annotation JSON in both directions.

## User annotations are not narration highlights

The brief forbids writing *narration highlights* into the PDF. Those are transient overlays that
follow the audio, and the rule stands. Annotations are a different thing: the reader's own
work, which they asked for and which must survive.

They are a separate layer:
- They are drawn under the narration highlight, so the passage being read stays visible.
- They do not touch the plan, the generation token, any cache key or any version constant.
- A narration saved with the document (ADR-0012) does not carry them, and the headless `narrate`
  and `export` commands ignore them.

## What is reused from RailReaderCore 0.94.0, and what is not

All of these are public in Core 0.94.0. Reused:

- **The model and its JSON**: `RailReader.Core.Models.Annotations`, written and read with
  `AnnotationService.ExportJson` / `ImportJson`. Core's own serialiser context is internal; these
  static helpers take any path and touch neither `AppConfig` nor Core's annotation directory.
  The format is Core's version 1, so RailReader2 can read the files.
- **Drawing**: `RailReader.Renderer.Skia.AnnotationRenderer.DrawAnnotations`. It draws in page
  points under a matrix the caller sets.
- **Hit-testing and bounds**: `AnnotationGeometry`.
- **Undo**: Core's `IUndoAction` implementations, which act on a bare `AnnotationFile`.
- **Highlight boxes from text**: `AnnotationInteractionHandler.BuildHighlightRects` and
  `CreateTextMarkup`. Both are public statics, used without a handler instance. They are fed a
  `PageText` built from the extraction snapshot's own character boxes, so a highlight needs no
  PDFium call.
- **The native PDF writer and reader** (`PdfAnnotationWriter.AddAuthoredAnnotations`,
  `PdfAnnotationReader.Read`), for the exported copy and for showing annotations the PDF already
  has.

Not reused:

- **Core's tool state machine** (`AnnotationInteractionHandler`'s pointer handling). It works
  through `Viewport` and `DocumentModel`, normally built by `DocumentController`. Rail Narrator
  extracts by calling Core directly and never builds those (`RailReaderExtractionSource`).
  Building one only for annotations would open the PDF a second time and run Core's animation
  loop beside this application's camera. The tools this application needs are written in the Pdf
  project instead, as a state machine that can be tested without a window, on top of Core's
  model, geometry and undo actions.
- **Core's stores** (`AnnotationService.Default`, `CompositeAnnotationStore.Default`). Their
  directory is fixed to `~/.config/railreader2`, the same boundary as `AppConfig`.
  `NullAnnotationStore` stays as Core's store. Rail Narrator's annotations live in a store of its
  own.
- **Writing into the original PDF.** `WriteReconciled` rewrites the whole file and breaks a
  digital signature. Only the export writes a PDF, to a path the reader chose, never over the
  source.

## Shape

- **Storage**: `$XDG_DATA_HOME/rail-narrator/annotations/<sha256>/`.
  - `annotations.json` is Core's format.
  - `links.json` is Rail Narrator's own: for each annotation, its id, the segment and reading unit
    it was made on, the extraction identity, the source character ranges, the quoted text and
    the creation time.
  - Writes are atomic (a temporary file, then a rename).
  - A file that cannot be read is renamed aside and reported, never replaced by an empty one.
  - The data directory, not the cache: annotations cannot be regenerated.
- **Links are kept beside the annotation, not in it.** Core's annotation has no field for host
  data, and unknown JSON keys are dropped. `Subject` survives the PDF round trip, but other
  viewers show it as a label. Rail Narrator sets `NativeId` (the PDF `/NM`) to a new GUID when it
  creates an annotation, and keys its links by that.
- **A note on what was just read is a highlight with a comment.** It is a `HighlightAnnotation`
  whose `Contents` is the note. The sentence is the reading unit holding the last cue; with
  Shift, it is the passage. A unit across a page break gives one annotation per page. An
  equation recovered from pixels, with no characters behind it, is highlighted by its anchor's
  own disjoint rectangles.
- **Painting draws a copy.** Undo actions change annotation objects in place, and the draw runs
  on the render thread. So the layer draws a deep copy of each page's annotations, made on the
  UI thread when they change.
- **A revised PDF has another hash.** A path index lets the reader carry notes over to the new
  version. A note is carried over only where its quoted text is found again, and the rest are
  listed as not placed: nothing is guessed.

## Consequences

- Annotations made here open in RailReader2 by export and import. They do not appear there on
  their own, because the two applications keep separate stores.
- The exported copy appends annotations to the original's bytes, so annotations already in the
  PDF (a co-reviewer's, say) are kept. A signed PDF's copy is no longer validly signed, and the
  export says so.
- This ADR records a decision and its basis. Measurements are added as the work lands, phase by
  phase.
