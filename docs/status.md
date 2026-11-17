# Verification status

Everything below was measured on the reference machine — Fedora 44, x86-64, AMD
Ryzen AI MAX+ 395 (Strix Halo, Radeon 8060S integrated), 124 GB RAM, 17 November
2026 — unless it is explicitly marked as *not verified*.

The point of this document is that "implemented" and "verified" are different
claims, and only the second one is worth relying on.

## Verified by automated tests — 171 passing, 0 warnings

| Suite | Tests | What it covers |
|---|---|---|
| `RailNarrator.Domain.Tests` | 75 | Offset-unit conversion, normalisation mappings, cue validation, cue resolution, source maps, planner behaviour, coordinator invariants, cache keys, camera policy |
| `RailNarrator.Pdf.Tests` | 13 | **Real PDFs** through the real RailReaderCore pipeline |
| `RailNarrator.Speech.Tests` | 25 | PCM/WAV codecs, silence trimming, provider contracts, ZONOS3 HTTP contract against a stubbed transport |
| `RailNarrator.Audio.Tests` | 20 | Resampler, time-stretcher, playback engine against a controllable fake device |
| `RailNarrator.Inference.Tests` | 38 | Golden maths semantics, response validator, endpoint client |

Run them with:

```bash
for p in Domain Pdf Speech Audio Inference; do
  dotnet test tests/RailNarrator.$p.Tests -c Release
done
```

None of these need a GPU, a network, a model file or a sound card. The Audio suite
runs stable across repeated invocations; an earlier flake (a test that drained the
device before the writer thread had finished queuing) was fixed by waiting for the
completion event instead of a fixed delay.

## Verified by running the real thing

### Extraction from real PDFs
`RailNarrator.Pdf.Tests` generates PDFs with Skia and pushes them through actual
PDFium text extraction, Core's model-free layout analyzer, the XY-cut reading-order
resolver and Core's line detection. Confirmed working:

- two-column reading order (whole left column before any of the right)
- running header/footer/page-number suppression, *and* the filter toggles that
  turn it back on
- a page with no text layer reported as unreadable with warnings, not as success
- character boxes agreeing with the page text, line rectangles inside page bounds
- reading order contiguous from zero
- a sentence crossing a line break staying one unit with **two disjoint** rectangles
- content-hash stability across paths, and change under different content

### End-to-end narration with real synthesis
`rail-narrator-smoke speak --provider kokoro`:

```
extraction: 1 page(s) in 165 ms
provider: kokoro rev=kokoro-sharp/0.8.4:kokoro-fp16.onnx available=True
  voices: 157
[s00000] 4.50s audio in 744 ms (RTF 0.17)  peak=0.3542 rms=0.0562
[s00001] 4.25s audio in 318 ms (RTF 0.07)  peak=0.3752 rms=0.0541
[s00002] 3.74s audio in 275 ms (RTF 0.07)  peak=0.3472 rms=0.0533
summary: 3 clips, 12.49s of audio in 1.34s wall (aggregate RTF 0.11)
         preparation can keep up with playback
```

This also proves the dependency risk that mattered most: `RailReader.Core.Analysis`
resolves ONNX Runtime 1.31.1 and `KokoroSharp` was built against 1.22.0, and they
coexist in one process. The output WAVs were verified independently with `ffprobe`:
`pcm_f32le`, 24000 Hz, mono, durations 4.498 / 4.250 / 3.740 s.

### Audio device and media clock
`rail-narrator-smoke audio` against real hardware:

```
PortAudio V19.7.0-devel · initialise: ok · 6 output devices enumerated
opened device 0 at 44100 Hz / 2 ch
device buffer capacity: 3072 frames (69.7 ms)
wrote 36352 frames (0.824s) in 0.767s wall
presented 33797 frames (0.766s); queued 2555
clock error vs wall: -0.5 ms
```

The clock is accurate to half a millisecond against wall time **and** correctly
excludes the 2555 still-queued frames. That separation is the whole point: a clock
counting queued samples would run ~58 ms ahead of what the listener hears.

### ZONOS3 sidecar
A real `zonos3-server` was started from the operator's existing build and driven
over HTTP: 4.333 s of audio in 2.516 s (RTF 0.58), peak 0.5117, with
`speaker_cfg_scale: 1.5` and `condition_values: {"loudness": -18}` accepted. The
response headers (`X-Audio-Sample-Rate`, `X-Audio-Format`, `X-Audio-Channels`,
`X-Seed`, `X-Decoder`) were read and honoured. 30 speakers enumerated; Imogen is
`default_b78b28baf5c31a1d`.

### Desktop application
Built, packaged and launched under X11:

- `dotnet build RailNarrator.slnx -c Release` → **0 errors, 0 warnings**
- `./packaging/build-release.sh` → `rail-narrator-0.1.0-linux-x64.tar.zst`, 138 MB
- the **published self-contained binary** launches, loads a PDF passed on the
  command line, and stays up 25 s with no exceptions

## Not verified — and what it would take

| Gap | Why | What would close it |
|---|---|---|
| **Audible playback by a human ear** | This session had no audio-server access initially; the device opens and the clock measures correctly, but nobody has listened | Play a document through speakers and confirm speech is intelligible and correctly paced |
| **Highlight visual accuracy** | Overlay geometry is unit-tested and the surface renders, but no human has looked at a highlight on a real page | Open a real paper, play it, and check the highlight sits on the spoken line at 100%, 400% and 800% |
| **LLM maths narration** | No text LLM endpoint was configured on this machine | Point `MathLlmBaseUrl`/`MathLlmModel` at a local server and run the golden examples in `GoldenMathSemanticsTests` against it — the assertion helpers already check semantics, not wording |
| **VLM equation transcription** | No vision endpoint configured | Configure one and check a real equation crop round-trips |
| **Scanned-document OCR** | Wired to real PP-OCRv5 models, but no actual scan was run | Run a 300 dpi scan through `extract` and check coverage, warnings and reading order |
| **Semantic layout roles** | The default is the model-free analyzer, so every block arrives as `Text` | Download PP-DocLayoutV3 and set `LayoutModelPath`; role-keyed suppression and display-maths detection then use roles instead of geometry |
| **Clean-machine install** | The archive was built and run here, not on a fresh system | Install on a stock Fedora/Debian with only `libportaudio2` added |
| **Long-document endurance** | Verified on 1–4 page fixtures | A 40-page paper: peak RSS, preparation lag versus playback, cache growth |

## Deliberately not implemented

These are choices, not omissions. Each is recorded so it does not look like an
oversight.

- **Word-level highlighting.** No timing source exists on this machine — the Kokoro
  export has no `durations` output and ZONOS3's aligner needs assets a
  synthesis-only install lacks. See ADR-0004. The seam (`IForcedAligner`,
  `TimedCueValidator`, capability-gated `NativeTiming`) is in place.
- **Streaming synthesis.** The buffered sentence-clip path is the verified one. HTTP
  chunk boundaries are not frame boundaries, and streaming before the baseline is
  correct is how you get confident, smooth, wrong highlights.
- **Continuous-scroll / multi-page view.** One page at a time. `PageTransform`
  already carries `PageOffsetX/Y` terms for it, and cross-page anchors and
  highlights already work.
- **Audio8 provider.** Its runtime is an Audio8-patched llama.cpp build with a
  different streaming contract again (SSE frames carrying base64 PCM with sample
  offsets, not word timestamps). Not implemented, and therefore **not offered in
  the UI** — an inert selector implying a backend works is worse than no selector.
- **AppImage.** The plain self-contained archive has not passed a clean-machine
  test yet, so packaging does not claim more than that.
- **Non-English narration.** Language metadata is carried through every contract
  (`NarrationSegment.Language`, BCP-47 on voices), and the English-only Kokoro
  voices are what limit this today. RTL and multilingual reading order are *not*
  supported and are not claimed.
- **Table content and figure description.** Captions are read; table contents are
  announced, never invented. Richer table navigation is a secondary feature.

## Bugs found while writing tests, and fixed

Worth recording because each one was invisible until something asserted against it:

1. **The media clock lost the seek target.** A flush zeroes the device's presented
   count, so after a seek the position read back as zero and the highlight would
   have jumped to the start of the clip.
2. **The coordinator could not play at all without a persistent asset store.** It
   now retains prepared clips itself and treats the store as a cache.
3. **`TransformedText.Identity` had one whole-string mapping**, so composing through
   it collapsed every source link onto the entire unit. Highlights would have
   covered the page.
4. **The cue validator's monotonicity check never advanced its cursor**, so
   overlapping cues passed.
5. **Silence was judged by frame count**, not amplitude. A silent decoder returns a
   correctly sized buffer of zeros — a real ZONOS3/Vulkan failure mode.
6. **`/v2/speakers` is a bare JSON array**, which the client rejected, so a real
   server made the whole capability probe report the provider unavailable.
7. **Running headers were detected per block.** On a sparse page a model-free
   analyzer merges header, body and footer into one block spanning the page, so it
   never fell inside a margin band. Detection is now per line.
8. **The PDF text layer is not a transcription.** See ADR-0005 — this one produced
   "Alp h a o n e…" instead of "Alpha one…".
