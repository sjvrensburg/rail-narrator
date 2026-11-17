# ADR-0005: Coordinate frames, and deriving word spacing from ink gaps

**Status:** accepted · **Date:** 2026-11-17

## Decision 1 — one declared frame, stamped on every snapshot

All narration geometry is expressed in **displayed page points**: origin top-left,
Y down, page `/Rotate` and any view rotation already composed. This matches
RailReaderCore's own `RectF`/`CharBox`/`LineInfo`/`LayoutBlock.BBox`, so the
adapter performs no conversion and cannot introduce an off-by-a-rotation.

It is *not* raw PDF user space (bottom-left origin, Y up). Every snapshot carries
a `CoordinateFrameId(units, origin, pageRotationTurns, viewRotationTurns,
cropApplied)` and `IsCompatibleWith` gates comparisons, so a rotation or crop
change is a new frame rather than a silent reinterpretation of old numbers.

`PageTransform` is the explicit form of Core's convention
(`screen = page × Zoom + Offset`, taken from `Viewport.ResolvePoint`) with its
inverse, so both directions use one arithmetic and it is unit-testable without a
renderer. HiDPI `RenderScaling` is applied **outside** the camera transform:
folding it into zoom is the classic cause of highlights that sit a few pixels off
on a scaled display but look correct at 100%. Continuous-page offsets are carried
as separate terms for the same reason.

## Decision 2 — a PDF text layer is not a transcription; word spacing comes from geometry

Measured on a real generated PDF, a line whose text range read back as

```
"Alp \nh \na o \nn \ne i \nn t \nh \ne le \nft c \nolu \nm \nn \n."
```

stood for `"Alpha one in the left column."`. The producer had embedded `" \n"`
pairs *inside words*. Those phantom characters carry `CharBox` entries with zero
area — **exactly like a real space** — so neither index filtering nor a whitespace
test can separate them. Reading the text layer directly produced "Alp h a o n e…"
and every assertion about sentence content failed.

Two candidate signals were measured on the same line:

| signal | real word space | largest intra-word | separable? |
|---|---|---|---|
| advance (left edge → left edge) | 7.4 pt (`left│column`) | **10.0 pt** (`m│n`) | **no** |
| ink gap (right edge → left edge) | ≥ 3.88 pt | ≤ 2.07 pt | **yes, 1.85×** |

Advance is confounded by glyph width — `m` is simply wide — so no threshold
separates it. Ink gap does separate, cleanly, with nothing in between. The run
builder therefore emits only **marking glyphs** and inserts a space when the ink
gap exceeds `median(gap) × 1.6`, with a floor of `0.30 × median glyph height` so a
face whose glyphs touch cannot have a near-zero median that splits ordinary
kerning.

The multiple is set towards the *joining* error deliberately: missing a space
joins two words and costs a stumble in the audio, whereas inventing one puts a
word in the narration that is not in the document.

## Consequence worth recording

Because spaces are synthesised rather than read, each one inherits the preceding
glyph's source offset and contributes no highlight geometry. Source ranges stay
exact, and `SourceAnchor.Rects` are per-line rectangles that never cover a gap
the narration is not reading.

## Fixture limitation found here

Skia's PDF writer drops a **trailing** hyphen from the text layer (mid-string
hyphens survive). End-of-line hyphenation therefore cannot be produced as a PDF
fixture, and dehyphenation is covered in the domain suite where the input is
controlled. Recorded in the test that hit it so nobody rediscovers it.
