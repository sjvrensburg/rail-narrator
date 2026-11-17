# ADR-0005: Coordinate frames, and where word boundaries come from

**Status:** accepted, decision 2 revised after reading real documents · **Date:** 2026-11-17

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

## Decision 2 — a PDF text layer is not a transcription; geometry has to confirm it

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

### Revision: gaps alone were wrong on real typesetting

The measurement above was taken on one generated fixture. Read aloud, a 27-page
typeset journal article, a page of rotated text and an image-only scan showed
that ink gaps mislead in both directions, because a glyph box excludes side
bearings:

| case | measured | result of gaps alone |
|---|---|---|
| tabular figure "1" | 1.97 pt beside it; letters 0.97 pt apart, word spaces from 2.7 pt | "2017" narrated as "two hundred one seven" |
| "f" before a space | 1.02 pt of white where other word spaces left 2.56 pt | "of the" narrated as "ofthe", 38 times |
| text set sideways | every gap along x negative | no spaces on the page at all |
| OCR character boxes | 4–9 pt between letters of one word | "B ac kground and m otivat ion" |

In that article the text layer's own spaces were right in every one of those
cases: the PDF library places them from font metrics this code never sees. So
the rule is now the other way round. **The text layer proposes the word
boundaries and geometry decides whether to believe it**, page by page:

- A page's text-layer spaces are trusted when at least 75 % of them coincide
  with a visible gap and they sit between fewer than 45 % of the glyph pairs.
  The phantom-character fixture fails both tests and still falls back to gaps.
- On a trusted page a space is a text-layer space with any white behind it; a
  gap with no text-layer space must reach 2.5× the geometric threshold.
- Gaps are measured along the direction the line's glyphs advance in, so
  rotated text is handled by the same rule, and a jump back of two glyph
  heights is a wrap inside a merged line.
- OCR lines take the recogniser's spaces outright; their boxes are estimates.

Scored against an independent extractor's tokens for the same 27 pages
(`pdftotext`), reading units went from F1 0.77 to 0.85 overall and from 0.46 to
0.63 on tokens containing digits; "ofthe" went from 38 occurrences to none.

### Glyphs without a Unicode value

The same article's fonts name their ligature glyphs `f_i`, `f_f`, `f_f_i`,
`f_l` and `f_t`. PDFium does not decompose underscore-joined glyph names, so
each arrived as its raw character code (U+0002, U+0000, U+0008, U+0003,
U+000F), about 250 times, and was dropped with every other control character:
"su ciently", "speci cation", "e ect". The codes are per-font and the adapter
cannot see fonts, so `UnmappedGlyphResolver` identifies each code from the
document: an occurrence that exactly one candidate ligature turns into a known
word is a vote, and a code with a clear majority is resolved everywhere,
including in words no list holds. Occurrences that fit two ligatures do not
vote — "a?er" fits both "after" and the rare "affer", and counting those left
the ft glyph short of a majority. A code that wins no majority stays
unidentified and unspoken: symbol fonts leak control characters the same way.

The word list is 18,346 words containing an f-ligature sequence or beginning
with "th", derived from the public-domain list in the Fedora `words` package
and embedded in the domain assembly.

PDFium also reports a hyphen that ends a line as U+0002 — the code that article
gives to "fi" — 13 times there. Position decides: last on its line and attached to a letter, it
is the hyphen, and dehyphenation rejoins the word.

## Consequence worth recording

Because spaces are synthesised rather than read, each one inherits the preceding
glyph's source offset and contributes no highlight geometry. Source ranges stay
exact, and `SourceAnchor.Rects` are per-line rectangles that never cover a gap
the narration is not reading.

## A fixture limitation that was not one

This ADR used to record that Skia's PDF writer drops a trailing hyphen from the
text layer, so end-of-line hyphenation could not be produced as a PDF fixture.
The hyphen was there all along, as PDFium's U+0002 marker, and the unit builder
was discarding it. `AWordHyphenatedAtALineEnd_IsRejoinedFromARealPdf` now covers
it with a real PDF.
