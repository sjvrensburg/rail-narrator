# Verification status

Everything below was measured on the reference machine — Fedora 44, x86-64, AMD
Ryzen AI MAX+ 395 (Strix Halo, Radeon 8060S integrated), 124 GB RAM, 17 November
2026 — unless it is explicitly marked as *not verified*.

The point of this document is that "implemented" and "verified" are different
claims, and only the second one is worth relying on.

## Verified by automated tests — 416 passing, 6 opt-in skips, 0 warnings

| Suite | Tests | What it covers |
|---|---|---|
| `RailNarrator.Domain.Tests` | 206 | The camera leads when it moves (the cue goes to the top and left of the safe zone, not just inside its edge); Abbreviations expanded only as whole words ("Chap.", "overlap.", "p.adjust" untouched); maths italic letters (U+1D400 block, ℎ) mark their sentence as maths and are spoken as plain letters and Greek names, mapped back to both UTF-16 units; silent marks not counted as words; a zoom re-checks the camera at once unless following is suspended; Decimals and signed numbers after a year cue ("from 0.20", "by 0.75") read as numbers, not handed to the year reading; Play waits for the maths ahead (and starts anyway after the cap), a refused interpretation read mechanically from a transcription and labelled, a failed page crop reported, Front-matter metadata skipped (body sentences that start the same way kept), A lead-in sentence and its equation interpreted as one passage, Citation markers left out (and brackets that are content kept), regenerate re-asks the model, Maths left pending with an honest fallback, interpreted once with surrounding definitions and cached, spoken by the coordinator; Layout roles (model formulas trusted over geometry, display lines merged, headings kept apart, inline maths kept in its sentence, tables announced, publisher cover sheets skipped), years and identifiers read in context, Offset-unit conversion, normalisation mappings, cue validation, cue resolution, source maps, word boundaries from measured glyph geometry, unmapped ligature glyphs, running heads, planner behaviour, coordinator invariants and lock ordering, lookahead, pause/resume, release of played clips, cache keys, camera policy |
| `RailNarrator.Pdf.Tests` | 20 (+3 opt-in) | Layout analysis that fails to start, or fails mid-document, finishes on the CPU fallback from the page that failed and says so once, and the source then reports it so the snapshot is not cached under the GPU key; opt-in, the `Cpu` setting never uses the GPU, and (`RN_WEBGPU_INTEGRATION=1`) the GPU gives the same blocks, kinds, reading units and geometry within 0.1 pt as the CPU; A passage crop is a PNG of its ink; **Real PDFs** through the real RailReaderCore pipeline, including a cached snapshot building the same plan as a fresh one; opt-in, the real PP-DocLayoutV3 model through the adapter |
| `RailNarrator.Speech.Tests` | 36 (+2 opt-in) | Opt-in, real Kokoro speech holding acronyms and words the dictionary lacks aligned word by word; PCM/WAV codecs, silence trimming, provider contracts, ZONOS3 HTTP contract against a stubbed transport |
| `RailNarrator.Audio.Tests` | 39 | Resampler (polyphase path equal to the direct sinc filter on four rate pairs), time-stretcher (fast grain search equal to the direct search at three rates), playback engine against a controllable fake device, engine lock ordering, coordinator-plus-engine transition stress |
| `RailNarrator.Infrastructure.Tests` | 9 | Cache round-trips and waveform integrity, including same-length replacement; extraction-cache keying, degraded and encrypted documents never stored, corrupt entries treated as misses |
| `RailNarrator.Inference.Tests` | 106 | ClearSpeak-style reading of LaTeX (powers, fractions, roots, quantities, derivatives, sums, functions, cases, Unicode, text with `$…$`, malformed input); thinking-policy and out-of-tokens retry; transcription fallback; Maths interpretation: notation validator, image and thinking-switch fallbacks, one repair turn, cut-off responses refused; golden maths semantics, response validator, endpoint client |

Run them with (and see `testing-real-world.md` for the real models, GUI and audio):

```bash
dotnet test RailNarrator.slnx -c Release
```

The default run needs no GPU, network, model file or sound card. Two opt-in
integration tests are skipped unless their dependencies are explicitly enabled. The Audio suite
runs stable across repeated invocations; an earlier flake (a test that drained the
device before the writer thread had finished queuing) was fixed by waiting for the
completion event instead of a fixed delay. One Audio test failed once in a full-solution run while the local LLM was busy on
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
- **About 0.4 s of silence is still inserted between passages** (on top of the ~0.5 s each Kokoro
  clip ends with). It is output latency: from a write to the first presented frame takes
  ~320–335 ms on this machine (PipeWire through its ALSA bridge, Bluetooth sink), even on a
  stream that is already running, and the next clip is only written once the previous one has
  drained. Removing it needs gapless queueing — writing the next clip before the current one
  drains and moving the clock across the boundary — which changes `IAudioTransport` and the
  coordinator's clip-completion path.

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
