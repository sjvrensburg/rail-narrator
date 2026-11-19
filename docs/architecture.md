# Architecture

## The problem this design is shaped around

There are three representations of the same content, and they do not share words,
positions or lengths:

1. **PDF source** — text and geometry, in displayed page points.
2. **Narration script** — what will actually be spoken, after normalisation and, for
   maths, after paraphrase.
3. **Audio timeline** — samples, at the provider's own rate.

For prose these stay loosely aligned. For `\bar{x} = \frac{1}{n}\sum_{i=1}^{n} x_i`
the spoken form is "x bar equals one over n times the sum from i equals one to n of
x sub i" — there is no word-for-word correspondence with the glyphs at all. Any
design that assumes one representation can be indexed into another will highlight
the wrong text, and will do it smoothly and confidently.

So the two mappings are separate, explicit, and tested separately:

```
audio sample position ──► spoken script range     (a property of audio + script)
spoken script range   ──► original source anchors  (a property of the plan)
```

## Layers

```
RailNarrator.App          Avalonia views, commands, Skia-backed PDF surface
   │
   ├─► RailNarrator.Infrastructure   XDG settings, caches, model manifest, sidecars
   ├─► RailNarrator.Inference        maths transcription + verbalisation + validation
   ├─► RailNarrator.Speech           ITtsProvider: Kokoro, ZONOS3, fake
   ├─► RailNarrator.Audio            device, resampling, speed, media clock
   └─► RailNarrator.Pdf              RailReaderCore adapter
              │
              ▼
        RailNarrator.Domain          contracts + all narration logic (no dependencies)
```

`RailNarrator.Domain` references nothing — not Skia, not ONNX, not HTTP, not
RailReaderCore. Every external capability is a **port** declared in the domain
(`ITtsProvider`, `IAudioOutputDevice`, `IAudioTransport`, `IMathVerbaliser`,
`IMathTranscriber`, `IForcedAligner`, `ISpeechAssetStore`, `IPlanStore`,
`IExtractionSource`, `IMonotonicClock`) and implemented outward. That is what lets
domain tests run with no GPU, no network, no model and no sound card.

## Data contracts

`SourceAnchor`, `ReadingUnit`, `NarrationSegment`, `SpeechAsset` and `TimedCue` are
the versioned types the brief specifies, with these properties enforced by design
rather than convention:

- **Offsets are UTF-16 and say so.** `TextOffsetUnit` distinguishes UTF-16, code
  points and UTF-8 bytes; `TextOffsets` converts and refuses to split a surrogate
  pair. An LLM returning code-point offsets cannot be handed to `Substring`.
- **An anchor is single-page and holds disjoint rectangles.** Prose has one anchor per visible word fragment; equations retain a region
  per page. A dehyphenated word can reference anchors on both sides of a line break. Nothing ever stores a union over unrelated
  lines or columns — `PageRect.UnionAll` exists only for camera *framing* decisions
  and is named so it cannot be confused with highlight geometry.
- **Geometry belongs to a versioned snapshot.** `ExtractionIdentity(contentHash,
  extractionRevision)` is stamped on every anchor and every plan; indices into a
  mutable layout list are never persisted alone, because re-OCR renumbers them.
- **Timebase is declared.** `TimedCue` samples are counted in the clip's own
  `AudioTimebase`, never in device frames. `NarrationTimeline` converts explicitly
  when rates differ.

## One authority for narration state

`NarrationCoordinator` owns playback state. The camera, overlays, transcript cursor
and progress bar all read from it; none runs its own reading clock. A time-based
auto-scroll left running alongside would fight the audio clock and visibly disagree
with it.

Two mechanisms make concurrency safe:

- **Generation token.** Document change, seek, script edit and voice change each
  advance it. Every in-flight job captures the value it started with, and results
  from a superseded generation are dropped rather than stored — so a synthesis that
  completes after a seek cannot play over the new position.
- **Bounded lookahead.** Four segments by default, one concurrent synthesis. OCR,
  vision, LLM and TTS all contend for the same memory; tuning concurrency before
  measuring is how a reader runs out of RAM. At the measured RTF of 0.11 there is
  ~9× headroom, so the bound is comfortable rather than tight.

## The media clock

`AudioPlaybackEngine` derives presented content frames from PortAudio callback
DAC timestamps and the corrected `Pa_GetStreamTime` binding. Silence inserted by
underruns consumes no content. `PaStreamInfo.outputLatency` is reported separately;
it is already represented in the DAC timestamps and is not subtracted twice.

The engine maps that output position through the WSOLA render's monotonic source
correspondence and resampling to the source-audio timebase used by cues. Rate
changes stop the writer before replacing buffers; seek starts a new device epoch
while retaining the requested source position. Completion waits for final drain.
See ADR-0003 for the corrected investigation and remaining measurement limits.

Alignment uses the final trimmed PCM and the exact spoken script. The resident
MFA worker returns ordered word intervals; the coordinator requires complete
lexical coverage and binds ranges to individual PDF word anchors. Invalid evidence
falls back to whole-sentence highlighting. Equations remain coarse. The UI samples
the media clock on animation frames and rejects queued cues from older generations.
Camera following has an independent 120 ms throttle.

## The reading surface

Painting the page never renders it and never calls PDFium. Core serialises every PDFium
call in the process behind one lock, and extraction, OCR and the equation crops made for
the maths interpreter all take it. A page rendered inside the paint therefore cost the UI
thread a whole render per page turn (10 ms for a page of text, 50 to 110 ms for a scanned
page, 1.7 s for a page of vector drawing, all measured) plus the wait for whoever held the
lock.

`PageRasterSource` (in `RailNarrator.Pdf`) owns the document for display and one worker
thread. The surface tells it which rasters it wants, most urgent first: the page shown at
the present zoom and colour filter, then 72 dpi previews of the two pages either side. A
new request replaces the old one, and a render the reader has paged past is cancelled
between bands (Core's tiled renderer takes the lock one band at a time) or, where it cannot
be cancelled, dropped when it finishes. The colour filter and the copy into a compositor
bitmap happen on the worker too. The paint draws the best raster it holds for the page: the
sharp one, else the page at another zoom or its preview, else blank paper in the filter's
colour, with the highlights over it either way. Page sizes are read once at open, off the
UI thread, and answered from memory.

Opening a document is the same shape. `MainViewModel.LoadDocumentAsync` runs the file
hash, extraction, reading order, planning and the speech model's start-up on the thread
pool and only awaits and reports them, so the loading card over the reading area keeps
moving.

The magnification is either fitted to the width of the page shown or the reader's own
(`ViewZoom`). Fitting is a mode: the zoom is re-derived when the page shown or the reading
area's width changes, because pages differ in size (a publisher's cover sheet in front of
an article). A zoom the reader chose is never replaced by a page turn or a resize, and both
the mode and the zoom are saved as preferences.

Where the camera goes is the coordinator's decision (`FollowTarget`), made from the media
clock. A word is shown on the page it is printed on. A cue with no timing inside it (a
sentence with maths, an equation, an interpreted lead-in and its equation) can cross a page
break, and nothing measured says when the reading crosses it; the camera frames the page
that holds most of the passage, by highlighted area, and holds (`SourceMap.FramingPage`).
That is a fact about the page, not a guess about the audio (ADR-0004).

## Extraction

`RailReaderExtractionSource` drives Core's analyzer, reading-order resolver,
orientation detection and line detection **directly**, off the UI thread, rather
than through `DocumentController`'s animation loop. Those entry points are public
and pure, so an extraction is a deterministic function of the document plus options
— which is what makes cached plans and cached audio safe to reuse, and what makes
the geometry tests reproducible.

Word boundaries are the text layer's own spaces wherever the page's geometry confirms
them, and ink gaps between marking glyphs where it does not; neither is trusted alone.
ADR-0005 has both measurements. On one generated PDF the text layer read
`"Alp \nh \na o \nn…"` for `"Alpha one in the left column."`, with phantom
characters indistinguishable from real spaces, and only ink gaps separated the words.
On a typeset article the text layer was right and ink gaps were not: the white beside
a tabular "1" read as a space and the space after an overhanging "f" did not, so
"2017" was narrated as "two hundred one seven" and "of the" as "ofthe".

Control characters in a page's text are information, not noise. They are ligature
glyphs whose names PDFium could not map — identified from the document by
`UnmappedGlyphResolver` — or PDFium's marker for a hyphen that ends a line.

## Host services are supplied, not inherited

Core.Pdfium's `AppConfig` hard-codes `~/.config/railreader2` and creates it on
first access; its `AnnotationService.Default` persists under the same tree. Rail
Narrator supplies its own `IRecentFilesStore` (its own XDG directory) and an
**inert** `IAnnotationStore`, and builds `CoreSettings` directly. Two reasons, and
the second is a product boundary: narration highlights must never be written into
the reader's PDF, and a companion application must not read or overwrite another
product's settings.

## Maths

Two stages, separate services, because they fail differently and are corrected
differently:

1. **Transcribe** — `VlmMathTranscriber` reuses Core's `IVlmService` /
   `BlockAction.LaTeX` transport unchanged, producing an inspectable artefact.
2. **Verbalise** — `OpenAiCompatibleMathVerbaliser`, a new service. Core's
   interface semantics are deliberately not stretched to cover narration planning.

Between them sits `MathResponseValidator`, the boundary between untrusted model
output and the plan. It rejects LaTeX in the spoken text, invented source ids,
coordinates and audio times, over-length output, malformed JSON; it drops an
unsolicited explanation outside Explain mode; and it retries a bounded number of
times with the complaint fed back before becoming a recoverable error. An LLM's
fluency is not evidence of correctness — it can reverse a quantifier and still read
beautifully — so what cannot be checked mechanically is surfaced as a warning for
the review panel rather than silently accepted.

`LocalLatexSpeaker` is the labelled fallback when no model is configured. It is
honest about its limit: it flattens grouping, so `(X^\top X)^{-1}` loses its "the
inverse of the quantity…" phrasing. That is why its output carries
`ScriptProvenance.LocalRule` and a warning telling the reader to check it.

## Caching

Each stage is keyed separately, and each key folds in exactly the inputs that can
change that stage's output — so editing an equation rebuilds only the affected
verbalisation, speech and timings; changing the voice rebuilds only speech;
re-OCR rebuilds everything downstream. `CacheKey.ForSpeechAsset` includes both the
script revision *and* a hash of the spoken text, because either alone would let a
stale clip play against new words. Audio and timings are stored and evicted together. Alignment metadata carries a
separate revision derived from the exact PCM and script hashes plus runtime, worker,
dictionary and acoustic-model revisions. An aligner change retimes cached audio
without resynthesis. PCM reads verify the stored hash before using its timings.
