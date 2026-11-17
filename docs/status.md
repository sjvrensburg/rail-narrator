# Verification status

Everything below was measured on the reference machine — Fedora 44, x86-64, AMD
Ryzen AI MAX+ 395 (Strix Halo, Radeon 8060S integrated), 124 GB RAM, 17 November
2026 — unless it is explicitly marked as *not verified*.

The point of this document is that "implemented" and "verified" are different
claims, and only the second one is worth relying on.

## Verified by automated tests — 231 passing, 2 opt-in skips, 0 warnings

| Suite | Tests | What it covers |
|---|---|---|
| `RailNarrator.Domain.Tests` | 107 | Offset-unit conversion, normalisation mappings, cue validation, cue resolution, source maps, word boundaries from measured glyph geometry, unmapped ligature glyphs, running heads, planner behaviour, coordinator invariants and lock ordering, lookahead, pause/resume, cache keys, camera policy |
| `RailNarrator.Pdf.Tests` | 14 | **Real PDFs** through the real RailReaderCore pipeline |
| `RailNarrator.Speech.Tests` | 36 | PCM/WAV codecs, silence trimming, provider contracts, ZONOS3 HTTP contract against a stubbed transport |
| `RailNarrator.Audio.Tests` | 32 | Resampler, time-stretcher, playback engine against a controllable fake device, engine lock ordering, coordinator-plus-engine transition stress |
| `RailNarrator.Infrastructure.Tests` | 4 | Cache round-trips and waveform integrity, including same-length replacement |
| `RailNarrator.Inference.Tests` | 38 | Golden maths semantics, response validator, endpoint client |

Run them with:

```bash
dotnet test RailNarrator.slnx -c Release
```

The default run needs no GPU, network, model file or sound card. Two opt-in
integration tests are skipped unless their dependencies are explicitly enabled. The Audio suite
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

## Not verified — and what it would take

| Gap | Why | What would close it |
|---|---|---|
| **Audible playback by a human ear** | The signal reaching the output device was recorded and matches the synthesised clips, but nobody has listened, and the Bluetooth leg after the sink was not captured | Play a document through speakers and confirm speech is intelligible and correctly paced |
| **Audible-to-visible delay** | Highlights agree with the device's own DAC timestamps; whether those timestamps are right for a given device is unmeasured | Loopback microphone plus display capture, at several speeds and on wired and Bluetooth outputs |
| **LLM maths narration** | No text LLM endpoint was configured on this machine | Point `MathLlmBaseUrl`/`MathLlmModel` at a local server and run the golden examples in `GoldenMathSemanticsTests` against it — the assertion helpers already check semantics, not wording |
| **VLM equation transcription** | No vision endpoint configured | Configure one and check a real equation crop round-trips |
| **Scanned-document OCR at 300 dpi** | A 131 ppi scan with handwriting was run and reads as sentences; the handwriting is recognised as stray words | Check a clean 300 dpi scan for coverage, warnings and reading order |
| **Semantic layout roles** | The default is the model-free analyzer, so every block arrives as `Text` | Download PP-DocLayoutV3 and set `LayoutModelPath`; role-keyed suppression and display-maths detection then use roles instead of geometry |
| **Clean-machine install** | The archive was built and run here, not on a fresh system | Install on a stock Fedora/Debian with only `libportaudio2` added |
| **Long-document endurance** | Seven minutes of a 27-page paper, 1.7 GB resident; prepared clips are never evicted from memory, so growth over a whole document is unmeasured | Play a 40-page paper to the end: peak RSS, preparation lag versus playback, cache growth |
| **File > Open dialog** | The GUI run loaded documents from the command line only | Open a document through the picker under a desktop portal |

## Open defects found by driving the application

Not fixed yet. Each was observed in the run described above.

- **Word highlighting still falls back on most of a technical paper.** 58 % of that paper's prose
  passages contain a word the pronunciation dictionary lacks ("GARCH", "heteroskedasticity",
  authors' names), and one such word sends the whole sentence to sentence highlighting.
- **Identifiers are read as numbers.** "ISSN 1532-4168" is spoken as two cardinals and a DOI
  digit by digit with "point" between; 1993 is read as "one thousand nine hundred ninety three".
- **A first-page copyright line and a footnote are read mid-sentence.** They occur once, so no
  repetition rule can find them; they need a layout role or a position rule.
- **An unmapped glyph that is not an f-ligature stays unspoken.** The resolver knows ff, fi, fl,
  ffi, ffl, ft, fj, fb, fh, fk and Th. A font with other unmapped ligatures (ct, st, tt) would
  still lose those letters, and a code that two fonts in one document use differently is resolved
  for neither unless one clearly dominates.
- **One display equation becomes several passages**, each announcing that it could not be read,
  and two of three display equations tried did not respond to a click.
- **Extraction is not cached.** The scan was OCRed again, about 25 s, on every open.
- **Speed, voice and follow mode** are saved with the reading position but not to settings, so a
  new document starts from the defaults.
- **The status line keeps stale text** ("Seeking", "Preparing speech") after the state has moved
  on, and a loaded document sits at "State: Preparing" until Play.
- **About half a second of silence separates passages.** Each clip is resampled and rendered, and
  the device stream reopened, when it is due rather than ahead of time.

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
