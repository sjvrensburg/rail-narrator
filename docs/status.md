# Verification status

Everything below was measured on the reference machine — Fedora 44, x86-64, AMD
Ryzen AI MAX+ 395 (Strix Halo, Radeon 8060S integrated), 124 GB RAM, 17 November
2026 — unless it is explicitly marked as *not verified*.

The point of this document is that "implemented" and "verified" are different
claims, and only the second one is worth relying on.

## Verified by automated tests — 535 passing, 9 opt-in skips, 0 warnings

| Suite | Tests | What it covers |
|---|---|---|
| `RailNarrator.Domain.Tests` | 257 | The lookahead counted in listening time with a minimum and a cap of passages, a passage played without waiting for its words to be timed (its word highlight following when the timing arrives), the aligner started once when a document loads; A passage highlighted all at once drawn as one shape (one band per line, its lines meeting across the leading, a column gutter and a word the cue does not read never covered); A voice change overtaken by a plan load or a seek neither cancelling its lookahead nor letting the old voice play, and an older load not putting its plan back over a newer one; the heading of a reference list that is not read skipped with it (by the block below it, not the model's reading order; other headings kept; read when references are); A cue with no timing inside it that crosses a page break framed on the page holding most of it (a word on the page it is printed on, an equal split on the earlier page, a page with no geometry never), a seek, a start from a stop and a new document ending a pause in following while a resume from Pause leaves it; One gliding follow mode: the reading line held at the reading height (the line's, not the word's, so the view does not bob), a line wider than the view panned between aligned words at their measured pace, starting with the line's start 20 % in from the left and ending with its end near the middle, a new line reached by a quicker carriage-return glide, the frame-loop follow target present only while playing and following; highlight boxes given one height per line across the paragraph, the other column not taken as part of the line, a sentence or equation cue without word timing framed at its start and held (never panned at a guessed rate), a page change cut rather than glided, the glide eased without overshoot at 20–144 fps, keeping its speed when retargeted and bounded after a stalled frame; a seek to an unprepared passage reports its stage (interpreting, loading, synthesising, timing words) and elapsed time and clears when it plays, a prepared passage starts with no wait reported; Abbreviations expanded only as whole words ("Chap.", "overlap.", "p.adjust" untouched); maths italic letters (U+1D400 block, ℎ) mark their sentence as maths and are spoken as plain letters and Greek names, mapped back to both UTF-16 units; silent marks not counted as words; a zoom re-checks the camera at once unless following is suspended; Decimals and signed numbers after a year cue ("from 0.20", "by 0.75") read as numbers, not handed to the year reading; Play waits for the maths ahead (and starts anyway after the cap), a refused interpretation read mechanically from a transcription and labelled, a failed page crop reported, Front-matter metadata skipped (body sentences that start the same way kept), A lead-in sentence and its equation interpreted as one passage, Citation markers left out (and brackets that are content kept), regenerate re-asks the model, Maths left pending with an honest fallback, interpreted once with surrounding definitions and cached, spoken by the coordinator; Layout roles (model formulas trusted over geometry, display lines merged, headings kept apart, inline maths kept in its sentence, tables announced, publisher cover sheets skipped), years and identifiers read in context, Offset-unit conversion, normalisation mappings, cue validation, cue resolution, source maps, word boundaries from measured glyph geometry, unmapped ligature glyphs, running heads, planner behaviour, coordinator invariants and lock ordering, lookahead, pause/resume, release of played clips, cache keys, camera policy |
| `RailNarrator.Pdf.Tests` | 59 (+5 opt-in) | A change of colour filter re-filtering the page held instead of rendering it again; A layout model's own reading order kept when its blocks arrive most confident first, with and without a duplicate block for Core to remove; Fit-width as a mode: the zoom following the page shown and the view's width, a zoom the reader chose surviving a resize and a page turn, fitting again on request, limits and unusable stored values; Page rasters for the reading surface rendered on a worker: page sizes read at open, rasters delivered in the order asked and once each, a render the reader has paged past abandoned (and one that cannot be cancelled dropped, not handed over), a render still wanted carried on, a page that cannot be rendered reported once, colour filters applied there and blank paper's colour matching each filter's own; Layout analysis that fails to start, or fails mid-document, finishes on the CPU fallback from the page that failed and says so once, and the source then reports it so the snapshot is not cached under the GPU key; opt-in, the `Cpu` setting never uses the GPU, and (`RN_WEBGPU_INTEGRATION=1`) the GPU gives the same blocks, kinds, reading units and geometry within 0.1 pt as the CPU; A passage crop is a PNG of its ink; **Real PDFs** through the real RailReaderCore pipeline, including a cached snapshot building the same plan as a fresh one; opt-in, the real PP-DocLayoutV3 model through the adapter |
| `RailNarrator.Speech.Tests` | 48 (+3 opt-in) | Opt-in, real Kokoro speech holding acronyms and words the dictionary lacks aligned word by word; PCM/WAV codecs, silence trimming, provider contracts, ZONOS3 HTTP contract against a stubbed transport |
| `RailNarrator.Audio.Tests` | 43 (+1 opt-in) | Resampler (polyphase path equal to the direct sinc filter on four rate pairs), time-stretcher (fast grain search equal to the direct search at three rates), playback engine against a controllable fake device, engine lock ordering, coordinator-plus-engine transition stress |
| `RailNarrator.Infrastructure.Tests` | 22 | Cache round-trips and waveform integrity, including same-length replacement; extraction-cache keying, degraded and encrypted documents never stored, corrupt entries treated as misses |
| `RailNarrator.Inference.Tests` | 106 | ClearSpeak-style reading of LaTeX (powers, fractions, roots, quantities, derivatives, sums, functions, cases, Unicode, text with `$…$`, malformed input); thinking-policy and out-of-tokens retry; transcription fallback; Maths interpretation: notation validator, image and thinking-switch fallbacks, one repair turn, cut-off responses refused; golden maths semantics, response validator, endpoint client |

Run them with (and see `testing-real-world.md` for the real models, GUI and audio):

```bash
dotnet test RailNarrator.slnx -c Release
```

The default run needs no GPU, network, model file or sound card. Two opt-in
integration tests are skipped unless their dependencies are explicitly enabled. The Audio suite
runs stable across repeated invocations; an earlier flake (a test that drained the
device before the writer thread had finished queuing) was fixed by waiting for the
completion event instead of a fixed delay. On 18 November 2026,
`CoordinatorLockingTests.NaturalAdvance_SlidesTheLookaheadWindow` failed once in a full-solution
run, after about 5 s (one of its 5 s waits). It then passed 10 isolated runs and 5 further
full-solution runs. The cause was not identified, and the failure message was not captured. One Audio test failed once in a full-solution run while the local LLM was busy on
the same machine and passed in three reruns; it was not identified.

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
- a sentence crossing a line break staying one unit with individual word rectangles
  on both lines
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

### Audio device, media clock and word alignment
The corrected PortAudio binding and callback backend pass a real-device test with
silent PCM: pause freezes position, seek retains its target, a 1.5x rate change
preserves source position, and completion waits for final drain. Stream output
latency is available through `Pa_GetStreamInfo`. The old blocking-backend
wall-clock comparison did not measure acoustic synchronisation; ADR-0003 records
that correction.

A separate real Kokoro/MFA test aligned all nine words in a 2.952-second sentence.
The resident worker took 2.913 seconds for its first request and 0.017 seconds for
a warm repeat. Unit tests additionally exercise worker cancellation/timeout and
restart, missing-word fallback, repeated words, number expansion, dehyphenation,
model revision changes and cache reuse. See [setup and reproducible checks](word-highlighting.md).

These establish working integration and structural timing checks. Manually labelled
word-boundary error and audible-to-visible delay are still unmeasured.

### ZONOS3 sidecar, through this application's adapter
A real `zonos3-server` was started from the operator's existing build and driven
through `ZonosHttpTtsProvider` (not curl):

```
voices: 30
[s00000] 4.50s audio in 2742 ms (RTF 0.61)  format=44100Hz/1ch/Float32  peak=0.6204 rms=0.0711
         WARN Trimmed 232 ms of leading silence.
[s00001] 4.36s audio in 2615 ms (RTF 0.60)  format=44100Hz/1ch/Float32  peak=0.7346 rms=0.0898
         WARN Trimmed 244 ms of leading silence.
```

`speaker_cfg_scale: 1.5` and `condition_values: {"loudness": -18}` were accepted;
the response headers (`X-Audio-Sample-Rate`, `X-Audio-Format`, `X-Audio-Channels`,
`X-Seed`, `X-Decoder`) were read and honoured rather than assumed. Imogen is
`default_b78b28baf5c31a1d`. The leading-silence trim matters here: this runtime
pads clips, and without trimming the first ~240 ms of every clip would hold the
previous highlight on screen while nothing is being said.

### Model compatibility probing
Feeding the provider a Kokoro export built for a different runtime
(`kokoro-v1.0.onnx`, inputs `input_ids`/`speed`/`style` instead of `tokens`) used
to **abort the process**, because KokoroSharp's dispatch thread does not catch ONNX
errors. It now reports a recoverable error naming the tensors the file declares:

```
provider: kokoro rev=kokoro-sharp/0.8.4:kokoro-v1.0.onnx available=False
Provider unavailable: This ONNX model is not a KokoroSharp-compatible Kokoro
export: it declares no 'tokens' input (its inputs are: input_ids, speed, style).
```

Verified in both directions — the incompatible model is refused cleanly (exit 0, no
crash) and `kokoro-fp16.onnx` still synthesises at RTF 0.15 in the same build. Two
regression tests cover it; the incompatible-model one is env-gated on
`RAIL_NARRATOR_KOKORO_INCOMPATIBLE_MODEL` because it needs the real 325 MB file.

### Full chain in one pass
`rail-narrator-smoke all` — fixture, extraction, units, plan, audio clock and real
Kokoro synthesis, in a single process:

```
PASS  fixture      synthetic PDF written
PASS  extraction   247 chars over 1 page(s)
PASS  units        4 reading units from 245 source characters
PASS  plan         4 segments, all source-linked
PASS  audio-clock  queued 100800 frames; mid-clip position 2.100s of 4.200s
PASS  kokoro       1.72s of audio, peak 0.404, in 1200 ms
```

The audio-clock line is the one worth reading closely: advancing the device to
exactly half of a 4.200 s clip reports 2.100 s. The clock is exact against the
device's own presented-frame count, not approximate against wall time.

### Desktop application
Built, packaged and launched under X11:

- `dotnet build RailNarrator.slnx -c Release` → **0 errors, 0 warnings**
- `./packaging/build-release.sh` → `rail-narrator-0.1.0-linux-x64.tar.zst`, 138 MB
- the **published self-contained binary** launches, loads a PDF passed on the
  command line, and stays up 25 s with no exceptions

### The desktop application, driven as a user would drive it

On 17 November 2026 the built application was run in a headless wlroots compositor, driven with
synthetic pointer and keyboard input, with its output on a real device (Bluetooth headphones through
PipeWire's ALSA bridge) recorded from the sink monitor and cross-correlated against the cached
clips. Three PDFs were used: a 27-page typeset paper with heavy mathematics, a 10-page image-only
scan with handwriting, and a page of rotated text. That run found the defects listed under
"Found by driving the application" below. What follows was measured **after** fixing them, except
the two items marked as measured before.

- **Continuous playback without freezing**: seven minutes across about 68 passage changes, and a
  further six minutes on the final build, the menu answering at every one-minute check. Before the
  fixes the window froze within two to three minutes, three sessions out of three.
- **Device output matches the synthesised clip.** With the PipeWire quantum forced to 2048 frames,
  a 12-second clip's onset and midpoint were found in the recording exactly 8.63 s apart
  (correlation 1.00), with no host underflows. Under the same condition the previous stream
  parameters delivered 1 % of real time.
- **Word highlighting on real audio.** On a sentence the dictionary covers, the highlight stepped
  through all 13 words on the correct glyphs, each switch 0.23–0.29 s (median 0.27 s) after the
  same word in the sink signal — the output latency the DAC timestamps report for this device
  (about 0.21 s), plus up to one 80 ms capture interval. That is agreement with the clock, not a measured
  audible-to-visible bound: the Bluetooth leg itself was not captured.
- **Highlight placement** checked by eye in screenshots of typeset text at 243 %, 306 % and
  800 %. The scan and the rotated page were checked at fit width before the fixes; the only
  drawing change since is the padding.
- **Transport.** Click-to-read, Stop, and Play after Pause continuing from the pause point with no
  slice of the clip heard twice or skipped. Measured before the fixes and not repeated: Next,
  Previous and Replay each started the right clip within 0.4 s, 1.5× speed shortened a clip by the
  expected ratio at 203 Hz median pitch against 202 Hz, and a voice change re-synthesised and
  resumed in about three seconds.
- **Silence between passages**: median 0.45 s, maximum 0.60 s over four consecutive prose
  transitions.
- **Exit and reopen.** File > Exit ends the process in 0.2–0.4 s with or without playback. Four
  reopens with a saved mid-document position restored the passage, stayed silent and reported
  "Restored your position".
- **A correction typed into the maths review box** is saved, re-synthesised (script revision 2),
  spoken on the next Play and still present after a restart.
- **The narration script of the same three documents**, after the word-boundary and ligature
  fixes. For the 27-page paper, reading units scored against `pdftotext`'s tokens for the same
  pages went from F1 0.77 to 0.85 (0.46 to 0.63 on tokens containing digits); 96.2 % of prose
  words are dictionary words against 93.2 %; "ofthe" fell from 38 occurrences to none and all five
  ligature glyphs (ff, fi, fl, ffi, ft) were identified. 49 running-head and page-number lines are
  suppressed across all 27 pages, against 2. The rotated page yields its four sentences with
  every space. The scan reads as sentences ("How does a market practitioner decide, on any given
  trading day, whether the market is in a calm or turbulent state?") where it read "B ac kground
  and m otivat ion".
- **Word highlighting reaches more of a real paper**: 42 % of its prose passages are fully covered
  by the pronunciation dictionary, against 30 %. A sentence that fell back to sentence
  highlighting because of "su ciently" now steps through its fourteen words, and word
  highlighting works on the scan.

### Layout analysis on the GPU

Measured 18 November 2026 on a different machine from the rest of this document: Ubuntu 24.04,
i7-12700H (20 threads), 31 GB, NVIDIA RTX A2000 8 GB Laptop GPU. The machine was otherwise idle;
earlier timings taken while .NET build servers were running were 4–5 times slower and are not
used. Details in ADR-0006.

- A 24-page pdfTeX chapter extracted with PP-DocLayoutV3 in 26.9 s and 29.5 s on the CPU, and in
  11.7 s and 14.7 s on the GPU. Building the backend at start-up took 0.6 s on the CPU and 1.2–1.5 s
  on the GPU.
- CPU and GPU snapshots of that chapter have the same blocks, kinds, line counts and reading
  sequence on every page. Geometry differs by at most 0.02 pt and confidence by at most 5 × 10⁻⁴.
- With the Vulkan drivers hidden, `Auto` fell back to the CPU, said why ("No supported adapters"),
  and extracted normally.
- The self-contained publish includes `libonnxruntime_providers_webgpu.so` (15.8 MB).

### Word highlighting on a real chapter

Measured 18 November 2026 on the Ubuntu laptop described above, with the 24-page forecasting
chapter that showed some sentences highlighted word by word and others whole.

- Every cached clip from the reader's session that was highlighted whole (8 of 10) had been
  refused for the same reason: a word missing from the pronunciation dictionary. Over the
  chapter, 160 of 354 prose sentences (45%) held such a word.
- With generated pronunciations (ADR-0004 amendment), 40 sentences were sampled from those still
  holding a missing word, then synthesised by Kokoro and aligned through the adapter: 38 aligned
  word by word, and 2 fell back on "2a" and "ggplot2".
- Before the fix, three sentences holding maths italic letters could not be synthesised at all
  (Kokoro's phonemiser threw). All 71 pending maths sentences in the chapter can now be spoken in
  their fallback reading.
- The window was driven on an Xvfb display (XTEST input, audio on a null sink) with the chapter:
  at 173% and at 400% the view followed the highlighted word across lines and onto the next
  page; ◀/▶ and the wheel turned pages and paused following; Resume following returned to the
  word being read. Frames were inspected by eye; no timing of the camera was measured.
- Not measured: how accurate the boundaries of generated words are.

## Not verified — and what it would take

| Gap | Why | What would close it |
|---|---|---|
| **Audible playback by a human ear** | The signal reaching the output device was recorded and matches the synthesised clips, but nobody has listened, and the Bluetooth leg after the sink was not captured | Play a document through speakers and confirm speech is intelligible and correctly paced |
| **Audible-to-visible delay** | Highlights agree with the device's own DAC timestamps; whether those timestamps are right for a given device is unmeasured | Loopback microphone plus display capture, at several speeds and on wired and Bluetooth outputs |
| **Maths interpretation across documents** | Measured on one statistics paper with one model (Ornith-1.5-35B-A3B, see item 32) | Run other fields' papers (physics, pure maths, ML) and a text-only endpoint; review a sample of interpretations against the originals for meaning errors |
| **Mechanical readings against the page** | The transcription fallback (item 39) ran on the GARCH paper, and its readings were checked for notation, not proof-read symbol by symbol against the page | Compare a sample of rule readings with the printed equations; a dedicated transcription model (a math OCR) was not tried |
| **Scanned-document OCR at 300 dpi** | A 131 ppi scan with handwriting was run and reads as sentences; the handwriting is recognised as stray words | Check a clean 300 dpi scan for coverage, warnings and reading order |
| **Clean-machine install** | The archive was built and run here, not on a fresh system | Install on a stock Fedora/Debian with only `libportaudio2` added |
| **Long-document endurance** | Runs of three to seven minutes of a 27-page paper, 1.5–1.7 GB resident; played clips are now released, but a whole document has not been played through | Play a 40-page paper to the end: peak RSS, preparation lag versus playback, cache growth |
| **Layout on other GPUs** | GPU layout ran on one NVIDIA laptop GPU (Vulkan) | Run `LayoutAccelerationTests` with `RN_WEBGPU_INTEGRATION=1` on AMD and Intel GPUs, including the Fedora reference machine's Radeon 8060S |
| **File > Open dialog** | The GUI run loaded documents from the command line only | Open a document through the picker under a desktop portal |

## Open defects found by driving the application

Not fixed yet. Each was observed in the run described above.

- **Word highlighting still falls back on most of a technical paper.** 58 % of that paper's prose
  passages contain a word the pronunciation dictionary lacks ("GARCH", "heteroskedasticity",
  authors' names), and one such word sends the whole sentence to sentence highlighting.
- **DOIs are read as numbers and punctuation** ("ten point one zero one six slash …"). Whether a
  DOI should be read at all, spelled, or skipped is a product decision, so it is left as it was.
  Years from 2000 on are read as cardinals ("two thousand twelve"), which is an ordinary reading.
- **A first-page copyright line and a footnote are read mid-sentence.** They occur once, so no
  repetition rule can find them; they need a layout role or a position rule.
- **An unmapped glyph that is not an f-ligature stays unspoken.** The resolver knows ff, fi, fl,
  ffi, ffl, ft, fj, fb, fh, fk and Th. A font with other unmapped ligatures (ct, st, tt) would
  still lose those letters, and a code that two fonts in one document use differently is resolved
  for neither unless one clearly dominates.
- **One display equation becomes several passages**, each announcing that it could not be read,
  and two of three display equations tried did not respond to a click.
- **A sideways page with no Rotate attribute loses a line under the layout model.** The
  synthetic landscape scan comes out as three captions and drops "Final line of the sideways
  body."; the model-free analyzer read all four lines. The model sees the page unrotated.
- **Author affiliations on the first page are read.** Front-matter labels are skipped (item 36),
  but an affiliation carries no label. Whether to skip them is open (`next-steps.md`).
- **A restored position is a passage id.** When a document's units change (a new layout model, a
  new pipeline version), the saved id can name a different passage than the one the reader left.
- **The silence between passages has not been re-measured with a recording since gapless
  queueing** (item 56); by ear the gap is gone.
- **On the CPU, Kokoro cannot keep up with playback on the current development laptop**
  (i7-12700H): RTF 0.75 at best and 1–2 typically; fp32, int8 and thread counts were all tried
  (ADR-0007). With the converted model installed it runs on the laptop's GPU instead (item 60);
  without it, reading still pauses every few passages there.
- **Start-up still blocks for about 14 s before the window appears on the laptop** (item 61): the
  layout probe (6-7 s on its GPU) runs before the window is shown and could run in the background.
  On the reference machine the window opens 1.0 s after the process starts (object graph 0.4 s, of
  which the layout model's GPU session 0.27 s; 2026-11-19), so the delay cannot be measured there. Kokoro's
  initialisation no longer waits for extraction to finish: it starts beside it (item 65), so
  "Ready" waits for whichever is slower, not for both in turn. The 15 s CPU figure for Kokoro on
  the i7-12700H has not been re-measured since.
- **A page with almost no text is OCR'd as well, at 562 DPI on the CPU: about 20 s a page** on the
  i7-12700H (the two one-line pages of the `crosspage` fixture took 28 s). The rule
  (`CoverageLooksSparse`: under 0.5 % ink and under 60 characters) exists to catch scans with a stray
  text layer; whether to keep it, lower its DPI or check the page's ink first is open.
- **An equation crop renders its whole page, at up to 8192 px.** The crop is scaled so the passage
  is 1400 px wide, and the whole page is rasterised at that scale under PDFium's lock: 32-49 ms
  for a passage 300-450 pt wide and 134 ms for an 80 pt-wide equation on the reference machine;
  by arithmetic the latter is a 5734 x 8192 raster and a 140 MB array. It no longer delays the
  window (item 63) but it is work and memory spent on pixels that are thrown away.
  Before it, about 0.4 s was inserted at every boundary (on top of the ~0.5 s each Kokoro clip
  ends with): output latency, ~320–335 ms from a write to the first presented frame on this
  machine, paid because the next clip was written only once the previous one had drained. With
  the next passage queued the device should not run dry, leaving only Kokoro's own pauses; a
  passage whose audio is not ready in time still takes the old path.

## Deliberately not implemented

These are choices, not omissions. Each is recorded so it does not look like an
oversight.

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
9. **An incompatible ONNX model aborted the whole process.** KokoroSharp does not
   catch ONNX errors on its dispatch thread, so a wrong model file crashed the app
   instead of reporting an error. Found by actually trying the duration-enabled
   kokoro-onnx export; the provider now probes the graph first, and the wait for a
   synthesis is bounded so a dead worker cannot hang the caller forever.

## Found by driving the application, and fixed

No test asserted against any of these, because each needs the window, a real device or two
threads meeting.

10. **The window froze during ordinary playback.** The transcript list seeks when the reader picks
    a passage; it also did so when its selection merely followed the audio, so every passage change
    issued a seek. That seek read the engine under the coordinator's lock while the engine was
    raising its "playing" event under its own lock into a handler that wanted the coordinator's.
    The handler is gone, the engine raises events outside its lock, and the coordinator reads the
    transport before taking its own.
11. **Removing the accidental seek exposed that lookahead never advanced.** It was restarted only
    by Play and by seeks, so the seek had been hiding that passages past the first window were
    synthesised when already due. The window now slides on every natural advance, without
    cancelling work in flight.
12. **File > Exit never exited.** The UI thread blocked on a disposal whose continuations needed
    the UI thread. Disposal now runs on the thread pool and the wait is bounded.
13. **Audio starved whenever another client raised the PipeWire quantum.** The stream asked for
    the device's low-latency suggestion (about 10 ms). It now asks for at least 100 ms, and the
    ring is two seconds long, because ring space is counted against presented frames and a ring
    shorter than the output latency locks the writer out. ADR-0003 has the measurements.
14. **Reopening a document could start speaking.** Same handler as 10: restoring the selection
    seeked, and a seek plays. One reopen in three auto-played; the others reported a cancelled task.
15. **Play after Pause restarted the passage.** Nothing called the resume path.
16. **Highlight padding was scaled by the zoom twice**, so at 800 % each box covered a line and a
    half.
17. **Space and Escape fired wherever focus was.** A space typed into the maths correction box
    started playback, and Escape used to dismiss a menu stopped it.
18. **Ligatures vanished from a typeset paper.** PDFium returned glyphs named `f_i`, `f_f`,
    `f_f_i`, `f_l` and `f_t` as control characters, which were discarded: "su ciently",
    "speci cation". They are now identified from the document and expanded.
19. **Word boundaries came from ink gaps alone**, which split numbers at a tabular "1", lost the
    space after an "f", lost every space in rotated text and put spaces inside OCR words. The
    text layer's spaces are now used wherever the page's geometry confirms them. ADR-0005.
20. **A hyphen ending a line was dropped**, so "vari-" and "ance" were two words. PDFium marks it
    with U+0002; this had been recorded as a limitation of the fixture writer.
21. **Alternating running heads and journal page numbers were narrated.** Each head is on fewer
    than half the pages, and "422" is not page 2.
22. **Play while already playing restarted the passage**, and a click on the page issued a seek
    and a Play that raced each other.
23. **A loaded document sat at "State: Preparing"** until Play, and the status line kept "Seeking"
    or "Preparing speech" for as long as a passage lasted. It now reads "Reading passage 17 of
    833."
24. **Prepared audio was kept for the whole document** — by arithmetic about 650 MB for the
    27-page paper. Clips more than two passages behind the reading position are released, and
    come back from the asset store, or by synthesis, if the reader goes back.
25. **Speed, voice and follow mode were never saved as preferences**, and a saved Kokoro voice was
    replaced by the provider's default on every document load. A restored position also changed
    the voice, speed and follow mode actually used while the toolbar kept showing the old ones.
    Choices made in the toolbar are now written to settings and apply to the next document; a
    document's saved position still takes precedence, and the toolbar shows it. Verified in the
    GUI: `am_echo` / 1.25 / ReadingRail chosen on one PDF were shown on a different PDF after a
    restart; with the global settings reset to `af_heart` / 1 / KeepVisible, reopening a document
    saved with `af_bella` / 1.5 / Off showed those values and left the global settings untouched;
    the speech cache held `af_bella` clips only after that voice was chosen. The ZONOS3 speaker id
    is not saved this way, because it also decides whether that provider is registered.
26. **Extraction was not cached**, so a scan was OCRed again on every open. Snapshots are now kept
    gzipped under `$XDG_CACHE_HOME/rail-narrator/extraction`, keyed on the document bytes,
    extraction options, filters, page selection, view rotation, revision, the extractor and
    RailReaderCore assembly versions and the layout model file. A run where OCR was requested but
    unavailable, and any password-protected document, are never stored. Measured on the 10-page
    annotated thesis scan, launch to "Ready": 34.5 s on first open, 3.5 s and 5.7 s on the next
    two (entry 0.9 MB). The plan file was written once and matched on every cached open, so the
    cached snapshot produced identical reading units. Fixing this exposed a race: a progress
    report posted just before extraction returned overwrote "Ready" with "Loaded from cache:
    page 10 of 10"; late reports are now ignored.
27. **Rendering each clip sat on the critical path between passages.** Measured with the real engine
    and device on cached Kokoro clips: the inserted silence was ≈ 550 ms at 1× and ≈ 720 ms at
    1.25×. The earlier note blamed reopening the device; that costs 11–19 ms. The time went to
    resampling (245 ms for a 12 s sentence: 32 sine evaluations per output frame) and, at any
    rate other than 1×, to WSOLA's grain search (~300 ms). The resampler now uses a polyphase
    weight bank (the rates are integers, so 24→48 kHz has 2 phases and 24→44.1 kHz 147), and the
    grain search computes the overlap and its energy once per grain, slides the candidate energy,
    and uses a SIMD dot product. Both are tested equal to the direct versions. Inserted silence
    is now 366–396 ms at 1× and 426–454 ms at 1.25×; a loopback recording of the sink gave
    373–405 ms at 1×, agreeing with the engine's clock. The remaining time is output latency
    (see Open defects). Not re-measured in the GUI: other audio was playing on the sink.
28. **The reading position was saved only by File > Exit or the close button.** A `SIGTERM`
    (logout, `kill`), Ctrl+C, a crash or a power cut lost the session's progress, and opening a
    second document did not save the first one's place. The position is now saved on every move
    to another passage (the document's hash is computed once at load, not on every save, and
    saves are serialised so an older one cannot land last), before another document is opened,
    and SIGTERM, SIGINT and SIGHUP close the window the ordinary way. Verified in the GUI: two
    Next presses wrote s00009 while the app was running; after `kill -KILL` a relaunch reported
    "Restored your position" and Next continued to s00010; `kill -TERM` then exited cleanly in
    under 2 s, saving on the way out.
29. **Years and identifiers were read as cardinals.** "(Hamilton, 1989)" was "one thousand nine
    hundred eighty nine" and "ISSN 1532-4168" two cardinals joined by a hyphen. A four-digit
    number from 1100 to 1999 is now read as a year ("nineteen eighty nine", "nineteen oh five")
    where the context says it is one: a citation bracket or list, "et al.", a temporal word or
    month before it, "May 1, 1997", or either end of a year range — not a page range after a
    volume colon or "pp.", and not before a lowercase plural ("in 1500 patients"). ISSN and ISBN
    digits are spoken one at a time, including a second ISSN later on the same line. On the
    27-page paper the spoken text changed in 39 places, every one intended: 34 years, the two
    ISSNs and two dates; on the thesis, 7 citation years. `ProsePipeline` was bumped so cached
    plans and audio are rebuilt.
30. **A configured layout model produced nothing at all.** The adapter rasterised each page for
    line detection (up to 8192 px) and handed that raster to PP-DocLayoutV3, which accepts only its
    800 px input size and threw on every page; the warning named only "ArgumentException". The
    model now gets a raster at its input size and line detection keeps the high-resolution one.
    A model downloaded from the new Models menu entry (131 MB, checksum from RailReaderCore's
    registry) is used without further setup and changes the extraction cache key.
31. **First-reading skips by layout role.** With the model, on the 27-page GARCH article: 150
    blocks are skipped by role (footnotes, references, figures, page furniture), the publisher's
    download cover sheet is skipped (it read a DOI digit by digit and "Article views: nine hundred
    seventy six"), and the passages drop from 833 to 595. Display maths falls from 297 units to
    80 — the model's formula class is trusted, where the geometric fallback promoted prose lines
    carrying a symbol — and each equation is one unit instead of its summation limits as
    fragments. Headings no longer run into the paragraph after them. A sentence with inline maths
    stays one unit, flagged as containing maths (147 on that paper) instead of being cut into
    "where", "ε ∗", "t". Tables and algorithm listings are announced ("Table not read aloud.") with
    the whole region highlighted, instead of their cells being read as sentences of numbers. On
    the annotated thesis scan, OCR'd handwriting ("lact plact", "rewoucd") is no longer read.
32. **Maths is interpreted, not read.** With `MathMode` Interpret (now the default) and an LLM
    endpoint configured, each display equation and each sentence with maths in it is sent to the
    model — page image (see item 38: until then the image never arrived), garbled text layer, 1,500 characters before it (where notation is
    defined) and 400 after — and the narrator says what it means. The rules are the author's
    narrate skill: never voice notation, use the paper's names for quantities, keep a sentence's
    own wording; output with symbols, Greek letters or variable residue ("h t") gets one repair
    turn and is then refused. Interpretation runs during playback, up to 40 passages ahead of the
    reading position, and is cached on disk; the plan holds an honest fallback until then.
    Measured, text-only as it turned out, on the GARCH paper with Ornith-1.5-35B-A3B on `:8088` (multimodal, local): 14 of 14
    passages interpreted correctly with thinking on (median 10.1 s, max 21.5 s) — e.g. equation
    (3) became "the short-run component of the variance equals a constant plus the sum of the
    previous squared standardized innovations, each multiplied by a weight, plus … an additional
    weight that is added only when the corresponding innovation was negative…"; with thinking
    off 13 of 14 (median 1.3 s, one refused and read verbatim, one detail wrong). In the GUI,
    playing from just before equation (1) with nothing cached: 90 s of continuous narration,
    the only silences over 1.2 s being 2.2 s at the start and 1.8 s before the first maths
    passage; the transcript and the maths review panel show the interpreted words.
33. **Citation markers were read although the setting said they were not.** `IncludeCitations`
    is off by default and documented as leaving out "(Smith, 2019)", but nothing implemented it:
    the GARCH paper was read as "Amado and Teräsvirta (two thousand eight, two thousand
    thirteen) in which…". Narrative ("Engle (1982)"), parenthetical ("(Hamilton, 1989; Ang &
    Timmermann, 2012)") and numeric (" [12]") markers are now left out of the spoken text, with
    the dropped characters still mapped so highlights are unaffected; a bracket not tied to an
    author ("(1990–1995)", "(2)", "v[2]") is kept. On the paper: 48 changes, 200 words, every one
    a citation. "et al." is now read "and others" without an invented comma. The interpreter's
    prompt (version 2) also leaves citations and equation numbers out.
34. **"Regenerate" on an interpreted passage returned the cached answer.** It now asks the model
    again for that passage only, bypassing the cache, and keeps the previous wording if the new
    answer is unusable. Verified in the GUI: equation (1) was re-read as "The current return is
    decomposed into its best forecast based on all previously available information, plus a
    shock term capturing the unexpected component." It waits behind the interpret-ahead loop on
    a single-slot server (over 30 s here).
35. **A sentence that leads into an equation was read in two halves.** "Let the return series
    {yt} be given as", a pause, then the equation's meaning as a sentence of its own — and "Let"
    alone before another. With interpretation on, an unfinished prose unit (no full stop) and the
    display equation it introduces are one passage; the model returns one sentence ("Let the
    return series be given as the sum of the conditional expectation of the return given the
    historical information available at the previous time period, and an innovation term."). Its
    fallback reads the lead-in as printed, then announces the equation, each linked to its own
    region. On the GARCH paper with the local model: 8 of 8 passages interpreted, median 8.3 s;
    one took 82 s. The validator now also rejects "phi", "rho", "mu" and the other Greek letter
    names (one answer had said "two plus phi"); prompt version 3 so no cached answer escapes it.
36. **Front matter and run-on blocks.** "JEL CLASSIFICATION C12; C22; C51; C52" was read; keyword
    lines, classification codes, article history and contact lines on the first two article
    pages are now skipped when they carry their standard label ("Received wisdom holds…" in the
    body is still read: ordinary-word labels count only in capitals, before a colon or a date,
    or beside an e-mail address). Two blocks of the same kind are now separate units when the
    first ends finished (a full stop, a digit, a bracket) or is a single line, and the next
    starts with a capital; a paragraph flowing into the next column ends mid-sentence and stays
    whole. On the GARCH paper this separated "…, j = 1, …, p." from the next sentence and a
    figure's axis label ("SPD/USD") from its caption; on the thesis, a list item from the
    paragraph after it.
37. **Thinking ran out of tokens on a quarter of the passages.** On the GARCH paper's first 144
    pending passages with thinking on, 37 used the whole 4,000-token budget deliberating and
    returned no answer — not a loop, just long reasoning. A thinking answer cut off at the token
    limit is now asked again once with thinking off (this does not use the repair turn), and the
    new default `MathLlmThinking: EquationsOnly` thinks only for display equations and the
    sentences that lead into them; a sentence with brief inline maths is answered directly. On the
    first 100 passages (text only): thinking on everything 80 of 100 in 3,146 s, thinking off
    88 of 100 in 171 s, equations-only 92 of 100 in 2,318 s. Sentences take ~16 s with thinking and
    ~1.5 s without; equations and lead-ins ~39 s and ~2.3 s. The legacy `MathLlmDisableThinking`
    still means Off.
38. **The model never saw the page.** Rendering a passage's crop always threw — PDFium's 3-byte
    RGB pixmap was installed as a 4-byte Skia format — and the resolver dropped the exception, so
    every interpretation measured until then (items 32 and 35 included) came from the text layer
    and surrounding text alone. The crop is now copied into an RGBA buffer and encoded as PNG
    (a test checks the PNG holds the passage's ink), and a crop that fails is a warning on the
    passage instead of silence.
39. **Maths the model cannot interpret is read as notation, by rule, not announced.** When an
    interpretation is refused, the same model transcribes the crop (thinking off: LaTeX for an
    equation, text with `$…$` for a sentence) and a rewritten `LocalLatexSpeaker` reads it in
    ClearSpeak style: "x squared", "z to the n-th power", "the quantity h sub t g sub t, end
    quantity, to the one half power", "the fraction with numerator …, and denominator …", "the
    partial derivative of ell … with respect to theta sub one", "converges in distribution to".
    The old reader produced "minusi", "open parenthesish" and leftover braces. The passage is
    labelled "says the notation, not its meaning" and appears in the maths review panel; a
    transcription that still leaves notation, or is far longer than the passage, is not read and
    the earlier fallback stands. The whole GARCH paper (224 pending passages) with page images,
    equations-only thinking and this fallback: 180 interpreted, 43 read by rule, 1 announced;
    22 answered with thinking off after running out of tokens; median 4.6 s, max 82.9 s, 3,700 s
    of model time against roughly 85 minutes of narration. Sentences: 106 interpreted in 3.0 s
    on average, 29 read by rule in 8.7 s; equations 14 in 19 s, 5 by rule; lead-ins 51 in 39 s,
    9 by rule in 66 s. Meaning errors remain with the image: the order of the transition
    polynomial (K) is twice called "the number of regimes". The full run found two reader bugs,
    fixed: `\text{…}` lost its spaces ("bethetruevalueof") and `\pmb` was unknown; a sentence
    crop holds whole lines, and the model transcribed the neighbouring sentences on them even when
    told not to (two of seven re-run passages), so transcribed sentences whose ordinary words are
    not in the passage's text layer are now dropped before reading.
40. **Play buffers before it starts.** On Play the coordinator waits until about 60 s of
    narration ahead is ready (estimated at 15 characters a second, divided by the speed) or 20 s
    have passed, showing "Interpreting maths before reading (n of 60 s ready)"; with nothing
    pending, or a warm cache, it starts at once.
41. **A document with "from 0.20" in it could not be opened.** The year reading ran on any
    four-character number after a year cue, so a decimal ("0.75") or signed number ("-100") reached
    `int.Parse` and the narration plan threw. A 24-page forecasting chapter that RailReader2 opens
    fine failed to load. The year reading now requires four digits. Six cases are tested; the
    chapter plans to 607 source-linked segments.
42. **Some sentences were highlighted word by word, others whole.** The alignment worker refused any
    clip holding a word missing from its pronunciation dictionary. That was 45% of a technical
    chapter's sentences: acronyms, technical terms, names, possessives. Such words now get a
    pronunciation (acronyms spelled from the dictionary's letter names, the rest from MFA's G2P
    model) and are still forced-aligned. 38 of 40 sampled sentences now align.
43. **"p." expanded inside other words.** The abbreviation table matched anywhere, so "[17, Chap. 3]"
    was spoken "Chapage three", "overlap." became "overlapage" and "p.adjust()" became "page
    adjust()". An entry that begins or ends with a letter now matches only as a whole word.
44. **Accent marks counted as words.** A maths font's lone circumflex (U+02C6) or variation
    selector (U+FE01) was a token that no dictionary has and no voice says. Tokens now need a
    letter or a digit.
45. **Sentences with maths italic letters were not spoken.** pdfTeX sets maths italics in the
    Mathematical Alphanumeric Symbols block, which Kokoro's phonemiser rejected, and the sentence
    was not even marked as maths. Such letters now mark the sentence as maths, and its fallback
    reading says "C and sigma" for "𝐶 and 𝜎".
46. **Zooming in stopped the view following the word.** The mouse wheel suspended following as if
    the reader had panned away, and the zoom buttons scaled about the page's corner, so the word
    went off screen until the next cue. Zooming now keeps following, scales about the pointer or
    the middle of the view, and re-checks the camera at once.
47. **The camera kept the word being read at the edge of the view.** A move was the minimum that
    brought the cue back inside the safe zone, so the reading line sat at the bottom edge and the
    view scrolled a line at a time (and, at high zoom, panned word by word). A move now leads: the
    cue goes to the top and left of the zone, so what follows is in view.
48. **There was no way to change page, and a click stopped following.** The only page keys needed
    keyboard focus, the wheel only zoomed and dragging stopped at the page edge. Any click on blank
    space suspended following, so the view stayed put while reading moved on to the next page. The
    toolbar now has ◀ Page n / N ▶, the wheel scrolls through pages (Ctrl+wheel zooms), and only an
    actual drag suspends following. A page change by the camera now places the word at once rather
    than keeping the old page's scroll position.
49. **The toolbar was wider than the window.** With the page controls added, the follow-mode and
    Resume following buttons were cut off; the toolbar now wraps.

50. **Following stayed paused without the reader noticing.** Any scroll, drag or page turn paused
    following until F9, and the only sign was a toolbar button. A reader who zoomed in and then
    scrolled to find their place saw the view stop following altogether, sideways and onto the next
    line. Following now resumes by itself 6 s after the last scroll, drag or page turn
    (`FollowAutoResumeSeconds`, 0 to keep the old behaviour), and the status bar says so. This was
    checked in the window: a wheel scroll during playback showed Resume following, and 5.9 s later
    the camera moved back to the word being read.

51. **Following moved in jumps, in two modes that differed little.** "Keep visible" and "Reading
    rail" both left the view still until the cue crossed the edge of a safe zone, then moved it at
    once; the planned animation time was never used. They are replaced by one mode, modelled on
    RailReader's auto-scroll: the line being read is held at the reading height, a line wider than
    the view is panned along with the aligned words, and every move is a critically damped glide
    stepped by the compositor's frame time. Without word timing (maths, interpreted passages) the
    passage start is framed and held. Settings saved with the old names load as the new mode.
    Covered by the tests above; **not yet watched in the running app.**
52. **A wait for a passage said only "Seeking".** Clicking a passage a few pages ahead, or pressing
    Play before its maths was interpreted, could wait for a minute with that one word in the status
    bar. The wait now names its stage and elapsed time ("Synthesising speech · 4 s"), a card over
    the page says which passage is being prepared, the passage pulses with a dashed outline, and
    the Play button reads "Preparing…". The coordinator part is covered by tests; **the card and
    outline have not yet been watched in the running app.**
53. **Following, refined after first use.** Panning along a wide line stepped from word to word,
    because each word was a new target. While audio plays the camera now follows the media clock
    every frame, moving between two aligned words' positions over the measured time between their
    starts (camera only; the highlight still changes at the measured boundary), and a move to a new
    line uses a quicker glide (0.14 s against 0.3 s). The view may now go half a screen past a wide
    page's left or right edge, so a line starts 20 % in from the left and the camera stops with
    its end near the middle. Highlight boxes no longer change height from word to word: each takes
    its line's top and the paragraph's typical line height. Covered by the tests above; the motion
    has **not yet been watched in the running app.**
54. **The window was simplified.** One Play/Pause button (Cancel while a start is waiting for
    audio) replaces Play, Pause and Stop; Escape pauses. The script, maths review and diagnostics
    panel is hidden unless chosen in View; the fixed zoom levels moved to the View menu; the status
    bar shows a progress bar instead of the raw state. RailReader's colour filters (high contrast,
    high visibility, amber, invert) are offered on the toolbar, applied to the page raster with
    Core's shaders and saved as `ColourEffect` / `ColourEffectIntensity`; on a filtered page the
    highlight uses the colour RailReader pairs with that filter. Checked under Xvfb with a synthetic
    two-column PDF: the window loads, and High Visibility renders yellow on black. Playback was not
    exercised there (no speech model in the isolated directories).
55. **The colour filter was hard to find.** It was an unlabelled drop-down at the end of the
    toolbar, and RailReader2's C key did nothing. The drop-down is now labelled "Colour:", View has
    a "Colour filter: <current>" submenu, and C cycles the filters as in RailReader2, saying which
    one is on in the status bar. Checked under Xvfb with XTEST: two presses of C went from None to
    High Contrast to High Visibility, the setting was saved, and the View menu named the filter.
56. **Gapless playback.** The next passage is now handed to the audio engine as soon as its audio
    is prepared (`IAudioTransport.QueueNextAsync`). The engine renders it ahead of time and
    writes it straight after the current clip's last frame, so the device does not wait for a
    drain and pay the output latency again. The media clock and the current segment switch when
    the device reports the boundary frame presented, and `ClipAdvanced` tells the coordinator,
    which moves the reading position without loading anything. A script edit or regeneration
    clears the queued clip (flushing it if already written), a rate change re-renders it, and a
    seek, stop or voice change discards it. A passage not ready in time, or a transport without
    queueing, still takes the old completion-then-load path. Covered by engine tests on the fake
    device (both clips written before any drain, the clock switching at the boundary frame,
    clearing a written clip, refusing a late queue, a rate change) and coordinator tests (advance
    without a load, the fallback, an edit clearing the queue). Smoke-tested by the maintainer in
    the running app on 18 November 2026: passages followed one another without the gap. The
    remaining pause has **not been measured** with a loopback recording.
57. **A wait for a passage on another page showed its outline nowhere.** Next or Previous across a
    page break raised the preparing card, but the pulsing outline was drawn on the page in view,
    not the one being prepared. Once a start has to wait, the view now moves to that passage
    (framing its start) unless it is already fully in view, and a pause in following from
    browsing ends, since the reader chose where to read. Nothing moves when following is off.
    Watched in the running app (item 59): four Ctrl+Right presses from passage 1 of a four-page
    fixture moved the view to page 2 with "Preparing passage 5 of 16" and passage 5 outlined.
58. **Colour filter strength had no control.** A slider beside the colour filter, shown only while a
    filter is on, sets `ColourEffectIntensity` (0–100 %) and redraws the page at once. Checked under
    Xvfb: High Visibility at 50 % gives the half-way blend (grey paper, olive text).
59. **Keyboard and button presses were swallowed during waits** (found by driving the GUI with real
    Kokoro speech under Xvfb, audio on a null sink). A command stayed disabled until its handler
    finished, so while a seek waited for audio (seconds on this laptop) further Next or Previous
    presses did nothing: four Ctrl+Right moved three passages. For the same reason the Play button's
    "Cancel" could not be pressed after Play, and Space did nothing. Next, Previous, Replay and
    Play/Pause now accept presses while an earlier one is waiting, and the coordinator counts Next,
    Previous and Replay from the passage a seek in flight is heading to (tested with a Stop that
    yields, as the real engine's does; the tests fail without the fix). A gapless switch from
    before a seek no longer moves the reading position. The View menu's script-panel item also did
    nothing (its check state never reached the view model); it now opens the panel. Re-checked in
    the GUI: four Ctrl+Right went from passage 1 to 5 and six from 8 to 14, each showing the card on
    the target's page; Cancel during "Preparing passage 14 of 16" stopped it, and it stayed stopped
    8 s later; View > Show script opened the panel.
60. **Kokoro on the GPU, through WebGPU** (ADR-0007). With `tools/kokoro-webgpu-model.sh` run once
    (it takes the fp32 export and rewrites its `tokens` input to int32) and `KokoroAcceleration`
    Auto, Kokoro runs on a WebGPU device, Vulkan on Linux, sharing the GPU binding with layout
    analysis; no CUDA or ROCm. On the RTX A2000: RTF 0.30–0.32 through the provider against 1.72
    on the CPU in the same session; audio matching the CPU's (envelope correlation 1.000). Every GPU
    clip is checked for NaN and silence, and the first bad one moves synthesis to the CPU for the
    rest of the run and is redone there; the fp16 export gives NaN on WebGPU, which is why fp32 is
    used. Covered by tests of the input construction (identical to KokoroSharp's), the output
    check and the fallback chain, plus an opt-in real-GPU test (`RN_WEBGPU_INTEGRATION=1` with
    `RAIL_NARRATOR_KOKORO_MODEL` and `RAIL_NARRATOR_KOKORO_GPU_MODEL`) that passed on the A2000. In
    the window (layout model also on the GPU, audio on a null sink) all 16 passages of a four-page
    document played to the end with no "Synthesising speech" wait. Only the NVIDIA card was
    measured; Intel and AMD integrated GPUs are untested.
61. **Opening a document was slow** (about 48 s for a four-page fixture in the window). Measured
    step by step with the application's own composition root:
    - every page was rendered at 8192 px (156 MB) because `AnalysisRenderSize` (1920, a size in
      pixels) was read as a DPI: 0.4-1.6 s a page to render, plus line detection over it. It is now
      sized in pixels; `ExtractorVersions.Current` is `extraction/2` so old snapshots are rebuilt;
    - the layout model's GPU session (about 6 s to build) was built at start-up to probe the device,
      thrown away, and built again for every document. Start-up's analyzer is now kept and lent to
      every extraction (`SharedAnalyzer`; one that failed is discarded and rebuilt);
    - the OCR engine (0.5-1.7 s) was built for every document; it is now built on the first page
      that needs it.
    Blocks, roles, line geometry and the narration script are identical to before on all six
    synthetic fixtures. With the GPU and a warm driver shader cache, a first document now extracts
    in 3.8 s and later ones in 0.7-2.7 s, against 10-15.5 s; in the window, "Reading the document"
    to the last page went from about 19 s to about 4 s. NVIDIA's driver caches compiled shaders under
    `$XDG_CACHE_HOME/nvidia`: with an empty cache (as in an isolated test run) the first inference
    takes 8 s instead of 2 s, so test runs with fresh XDG directories overstate first-open time.
62. **Docling Heron is the layout model** (ADR-0008). Chosen and built through Core's registry and
    factory as RailReader2 does: FP32 on the GPU, INT8 on the CPU, XY-cut++ reading order; files
    from the application's models directory or RailReader2's. On four RailDLA papers it extracted
    1.5-2x faster than PP-DocLayoutV3 on the GPU and 2.1-2.4x faster on the CPU, with the same
    content read apart from page furniture each model handles differently and one Elsevier sentence
    Heron dropped. Without a reference class, a References section is found by its heading
    (`ReferenceSections`); on three of the papers it caught 60, 21 and 41 blocks, and no script
    read a bibliography. Opt-in tests: Heron INT8 through the adapter (left column first), Heron
    FP32 on the GPU matching the CPU.
63. **Turning pages stalled the window.** The page was rendered inside the paint, on the UI thread,
    holding Core's process-wide PDFium lock. Measured in the window with an opt-in log of late
    frames (`RN_FOLLOW_LOG=1`, "frame gap"): on a 10-page scanned thesis chapter at fit-width, 26 of
    28 quick page turns each gave a frame 62-83 ms late (render 54 ms on average, 68 ms at most),
    on the reference machine with nothing else running. A render of the same page costs 10 ms for
    the GARCH article's text pages and 1.7 s for a page of architectural drawing, and it also
    queued behind whatever held the lock (an equation crop for the interpreter: 32-134 ms). Pages
    are now rendered by `PageRasterSource` on a worker thread, filtered and copied for the
    compositor there, and the paint only draws what has arrived: the sharp page, else a 72 dpi
    preview fetched ahead for the two pages either side, else blank paper in the filter's colour.
    A render the reader has paged past is cancelled or dropped. Same document, same keys: 0 late
    frames over 31 page turns. In a further run of 18 turns each page was sharp 40-70 ms after it
    was turned to, with its preview shown until then; three pages reached faster than their
    previews could be fetched were blank for those 50-60 ms, and a page with nothing to show now
    has its own preview rendered first (how soon that arrives was not timed separately). With the
    Invert filter on, the interpreter running and narration playing, 7 quick page turns gave 0
    late frames and the pages stopped on were sharp 118-120 ms later, shown from previews until
    then. Not measured: a HiDPI display, and the i7-12700H laptop.
64. **The view stayed on the page before while the next page was read.** A lead-in and the
    equation it introduces are one interpreted passage with no timing inside it, and the camera
    framed such a passage on the first page it touches. On the GARCH article, "It is a linear
    combination of bounded | transition functions defined as" with equation (4) has seven words at
    the foot of page 3 and the rest on page 4: the view sat at the foot of page 3 for the 15 s the
    equation was explained and moved to page 4 only when the next passage began (follow log, with
    the interpreter on). A cue with no timing inside it is now framed on the page that holds most
    of it, by highlighted area (`SourceMap.FramingPage`); in the same run the view moves to page 4
    as the passage starts. A word-aligned sentence across the same kind of break already turned
    the page at the right word and still does. Also: Next, Previous, Replay, a click on a passage
    and Play from a stop now end a pause in following at once (measured: the view returned to the
    reading 45 ms after Next, against up to the 6 s auto-resume before); a new document does not
    inherit the pause (both in the coordinator's tests); and a reopened document shows the
    passage it resumes from, not page 1. A drag whose pointer capture is lost now ends, where it
    would have left the page following the pointer and following paused; that was read in the
    code, not observed. Of the
    article's 534 passages with the interpreter on, 6 cross a page break and 3 of those have no
    word timing; documents with more display maths at page breaks have not been counted.
65. **Opening a document showed one line in the status bar, and the window froze as it finished.**
    A card in the middle of the reading area now names the file, says what is being done ("Reading
    the pages", "Finding the reading order", "Preparing narration", "Starting the voice"), counts
    pages or passages on a progress bar and shows the time taken; a veil covers the previous
    document, which no longer responds to clicks, Play or Next while it is being replaced, and the
    pointer shows busy. The file hash, reading order, planning and Kokoro's start-up ran on the UI
    thread between awaits: frames 1681 ms and 790 ms late at the end of each load on the reference
    machine. They run on the thread pool, Kokoro's start-up beside the extraction; the same load
    now has one frame 59-63 ms late. Seen in the window for fresh and cached opens of the GARCH
    article and a fresh open of the thesis chapter; not tried with a failing open, or through the
    file picker.
66. **Fit-width was a number worked out once, and any resize threw the reader's zoom away.** Fit
    was computed from the page shown when the document opened, so on the GARCH article (cover
    sheet 634 pt wide, article pages 504 pt) every page after the first filled 79 % of the
    window; and every change in the reading area's size re-fitted, so opening the script panel
    turned 235 % into 173 %. Fit-width is now a mode (`ViewZoom`): while it is on, the zoom is
    re-derived when the page shown or the reading area's width changes; a zoom the reader chooses
    (toolbar, keyboard, Ctrl+wheel) turns it off and is kept through page turns and resizes. The
    toolbar's Fit button and the View menu's item show and toggle the mode. Both the mode and the
    reader's zoom are saved, so the next document and the next session open the same way; a
    settings file from before has no mode and opens fitted, as it always did. A page narrower
    than the reading area sits in the middle of it. Seen in the window on the GARCH article at
    1440 px: the cover at 227 % and page 2 at 286 %, both filling the width; after two zoom-ins
    (446 %) a page turn and opening the script panel left 446 %, with the passage being read
    still in view; Fit with the panel open gave 210 %, and 286 % again when it closed; after
    zooming out to 117 % and a SIGTERM, a relaunch opened at 117 % with the page centred. No
    frame over 50 ms late in any of it. Not tried: Ctrl+wheel in the window (it takes the same
    path as the toolbar), a document whose pages differ in size mid-reading with follow on, a
    HiDPI display.
67. **A voice change could cancel the preparation of whatever came after it.** The application
    does not await a voice change, and each coordinator operation awaits the transport's stop. A
    voice change still waiting for that stop when a plan load or a seek finished carried on
    afterwards for its own, older generation: it cleared the prepared clips and restarted
    preparation, which cancelled the newer operation's lookahead and prepared nothing. A seek that
    overtook it could also find the old voice's clip still prepared and play it. Three tests with
    a transport whose stop yields reproduce this (nothing prepared after a load; the next passage
    played in the old voice; an older load's plan put back over a newer one) and fail on the code
    before. Now prepared clips are discarded when the voice changes, an overtaken operation stops
    after its await, `StartPreparation` refuses an older generation, and a plan load takes a fresh
    generation when it installs its plan. Not seen in the window: with the real engine idle its
    stop completes at once, and a fresh launch prepared its four lookahead passages and started
    Play 70 ms after the click. The "Synthesising speech" for 2 s noted under item 65 was not
    reproduced and is not explained by this.
68. **"References" was read aloud with nothing after it.** The list is skipped by default but its
    heading was spoken. A heading or title whose next block is a reference that is not read is
    now skipped with it (`ReferenceSections.HeadingsOfUnreadLists`), judged by the block that
    follows and not by the heading's words. "Next" is the nearest block below in the same column,
    and only for a heading that ends its column or page the next block in reading order: on the
    GARCH article page 26 was then ordered as acknowledgement, reference list, funding note
    and then the headings "Acknowledgments", "References", "Funding", so a rule that went by
    reading order would have dropped "Funding" and kept "References" (a first version did, and
    was caught on that page's extraction). That order was the adapter's fault and is fixed
    (item 70), but the rule by position stays: the model itself puts "References" directly after
    its list in 2 of the 18 papers surveyed there. `ProsePipeline` is `prose/8`, so cached plans
    and audio are rebuilt. With references switched on, heading and list are both read.
69. **The status line forgot that following was paused.** "Following paused while you browse…" was
    replaced within a moment by "Reading passage N of M", leaving only the Resume button to say
    why the page no longer moved. While following is paused the line now keeps the notice:
    "Reading passage 6 of 593. Following paused while you browse; it resumes 6 s after you stop (or
    press F9)." Seen in the window: the notice stayed while browsing and went when following
    resumed by itself.
70. **Some pages were read in order of detection confidence, not in reading order.** Found by
    asking why page 26 of the GARCH article had its headings after their paragraphs (item 68).
    A model with its own reading order (PP-DocLayoutV3) returns its blocks most confident first,
    with the order in a field. The adapter handed them to Core's `BlockPostProcessor` unsorted,
    although Core documents that it expects reading order and its own `AnalysisWorker` sorts
    first. The pass that removes a duplicate prose block (in Core 0.91.0, not in 0.89.0)
    renumbers the survivors by their place in the list, so every page holding such a duplicate
    lost the model's order: paragraphs first, then the reference list, then the headings.
    Surveyed with PP-DocLayoutV3 on the CPU over 18 papers, 455 pages, counting a block read
    after the nearest content block below it in its column: 11 pages in 8 documents were ordered
    by confidence, often the page where the references begin; 11 of 494 headings and 14 of 3,208
    other pairs were out of order. The adapter now passes every analyzer's blocks through a
    resolver before post-processing (`ModelOrderResolver` for a model's own order, chosen from
    the capabilities when the backend names none). After it: none of the 11 pages is ordered by
    confidence, and 2 headings and 0 other pairs are out of order. On the GARCH article the plan still has 593
    passages; "Acknowledgments" and "Funding" come before their paragraphs, and page 19 reads
    its two figure captions before the text below them instead of after it.
    `ExtractorVersions.Current` is `extraction/3`, so cached extractions are redone; cached maths
    answers are keyed by text, surrounding context and crop, so they are reused except on the
    pages whose order changed. A stand-in
    analyzer reproduces the page in the tests with no model; the case with a duplicate fails on
    the adapter before the fix. Left as they are: the 2 headings, both "References" placed by the
    model directly after its own list (heard only with references switched on); and Heron, which
    never had an order of its own and is untouched by the fix: with XY-cut++ 4 of 521 headings
    and 25 of 3,585 other pairs are out of order on the same papers ("4.1. Regularity
    Conditions" before "4. ASYMPTOTIC PROPERTIES" on one page, a title after the side column of
    an MDPI first page, footnotes and table captions). Not listened to in the application, and
    not run on the GPU. Measurements on PP-DocLayoutV3 since Core 0.91.0 (items 63-69) were made
    with pages 19, 26 and 27 of that article in the wrong order; none of them measured order.
71. **A passage highlighted all at once looked like a stack of overlapping boxes.** Reported by the
    reader on an interpreted maths passage: every word had its own padded box, the boxes overlapped
    by their padding, and each overlap was tinted twice. The words of a cue are now joined into one
    band per line across the spaces between them, consecutive lines of a column meet half-way
    across the leading (`HighlightLines.Passage`), and the surface fills the union once. A gap
    wider than the line height (a column gutter) or holding a word the cue does not read is not
    bridged. This applies to every cue; a single word looks as before. A passage spoken in other
    words than the page's (`ModelGenerated` or `LocalRule`, read from the coordinator's plan so an
    interpreted passage counts once it is resolved) is drawn in violet, with its own magenta tint
    under each colour filter and a violet variant of the high-contrast style. Covered by three
    Domain tests on the geometry; *not verified* in the application: no screenshot of the new
    shape or colour has been taken.
72. **Reading stopped mid-paragraph to prepare the next sentence.** Reported by the reader, who
    asked for a longer buffer. The follow log of the session shows an 11.1 s silence between two
    sentences of plain prose on page 9 (passages 193 and 194; no maths). The speech cache shows no
    audio written before that moment: everything earlier had been cached by an earlier session,
    and passage 194 was the first to need fresh audio. A passage became playable only once its
    words had been aligned, and the first alignment of a session starts the MFA worker, measured
    at 55.7 s with an empty lexicon directory and 23.0 s with one. The lookahead was four
    passages, which for short sentences was about 15 s. Once warm, preparation kept up: 71.5 s of
    audio in 47.2 s on the GPU, word timing included. Three changes: a passage is playable as
    soon as it is synthesised and its word timing attaches when it arrives (`AlignInBackground`;
    whole-sentence highlight until then, timing from an older aligner revision not shown while it
    is measured again); the lookahead is listening time (`LookaheadSeconds`, 60 s at the playback
    rate), at least `LookaheadSegments` and at most `MaxLookaheadSegments` (40) passages; the
    aligner is started when a document loads (`IForcedAligner.WarmUpAsync`). Covered by Domain
    tests with a fake aligner that holds its answer; *not verified* in the application: the stall
    has not been reproduced with the fix in place.
73. **A change of colour scheme was slow to show while reading.** Reported by the reader. A filter
    change rendered the page again through PDFium, behind the process-wide lock the maths crops
    hold during playback; the session's log has one page taking 5,241 ms to become sharp. The
    rasteriser keeps its last unfiltered renders (96 MB at most) and filters a change from them,
    with no PDFium call; the page keeps its old filter until then. A Pdf test counts the renders;
    *not verified* in the application: how long a change takes now was not measured.
74. **Start-up filled the terminal.** PortAudio's probe printed 30 ALSA and JACK lines
    ("Unknown PCM cards.pcm.rear", "jack server is not running or cannot be started"), and
    Kokoro's sessions printed ONNX Runtime warnings (it cannot constant-fold the fp16 model's
    Reciprocal nodes on the CPU). ALSA and JACK are silenced only around `Pa_Initialize`, so a
    JACK message at any other time still reaches standard error; Kokoro's sessions log errors
    only. Measured with the smoke tool against `main`: `audio` 30 lines → 0 (22 output devices
    found by both); `speak` with Kokoro on WebGPU 164 lines → 2. The two left are Dawn's
    "maxDynamic…BuffersPerPipelineLayout artificially reduced" warnings, which Dawn writes to
    standard error itself, outside ONNX Runtime's logging; silencing them would mean redirecting
    the process's standard error while the GPU device is created.
