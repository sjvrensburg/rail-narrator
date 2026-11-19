# Next steps and open decisions

Handover note, 19 November 2026, after PR #26 and on the `fix/heading-order`
branch. The branch builds with 0 warnings; 528 tests pass and 9 opt-in tests are skipped. What is verified, and how it was measured, is in `status.md`.
How to run the real models, the GUI and the audio path on a new machine is in
`testing-real-world.md`.

## Where things stand

The pipeline works end to end on a real 27-page statistics paper: layout-aware skipping,
citation removal, maths interpreted during playback by a local multimodal LLM, a rule-based
mathspeak fallback, and pre-roll buffering on Play. The last full maths measurement
(`status.md` item 39): of 224 maths passages, 180 were interpreted, 43 read as notation by
rule and 1 announced. That took about 62 minutes of model time against about 85 minutes of
narration.

Since PR #10:

- **Core 0.90.0** (PR #12), since moved to **0.91.0** (ONNX Runtime 1.32, WebGPU plugin 0.6), and the layout model runs on the GPU when one is usable, falling
  back to the CPU (PR #14, ADR-0006).
- **Word highlighting for words the dictionary lacks** (PR #15): acronyms are spelled from the
  dictionary's letter names, other words get a G2P pronunciation, and both are still
  forced-aligned. 38 of 40 sampled sentences now align (`status.md` item 42). This settles the
  old open decision about missing words.
- **Following** (PR #16): one gliding mode modelled on RailReader's auto-scroll replaces the two
  safe-zone modes. The camera pans between aligned words at their measured pace (camera only),
  uses a quick carriage return, and may go past a wide page's margins. Highlight boxes have one
  height per line. Tried in the running app by the maintainer.
- **Feedback while waiting** (PR #16): a wait names its stage and elapsed time, a card over the
  page says which passage is being prepared, and the passage pulses.
- **Simpler window** (PR #16): one Play/Pause button (Cancel while preparing), the script panel
  hidden by default, and RailReader's colour filters on the toolbar, in View and on the C key.

## Recently landed

- **Page turns, page following and the loading card** (`fix/paging-stalls-follow-loading`,
  `status.md` items 63-65). Pages are rendered on a worker and the paint only draws what has
  arrived, so turning pages no longer stalls the window. A passage with no word timing that
  crosses a page break is shown on the page holding most of it, and asking to read from
  somewhere ends a pause in following. Opening a document shows a card over the reading area
  and no longer freezes the window as it finishes. Measured in the headless GUI on the reference
  machine; not yet tried by the maintainer, on a HiDPI display or on the laptop.

- **Fit-width, a race in the coordinator and the "References" heading**
  (`fix/fit-width-and-followups`, `status.md` items 66-69). Fit-width follows the page shown and
  the reading area's width, and a zoom the reader chose survives page turns, resizes and a
  relaunch. A voice change overtaken by a load or a seek no longer cancels its preparation or
  lets the old voice play. The heading of a reference list that is not read is not read either.
  The status line keeps saying that following is paused. The fit-width behaviour was seen in the
  headless GUI on the reference machine; the race only in tests.

- **Reading order on pages with a duplicate block** (`fix/heading-order`, `status.md` item 70).
  With PP-DocLayoutV3, a page on which Core removed a duplicate block was read in order of
  detection confidence: 11 of 455 pages over 18 papers, among them the page of the GARCH article
  whose headings came after their paragraphs. The adapter now sorts a model's blocks into its
  reading order before Core's post-processing. Measured on extractions; not listened to.

- **Gapless playback** (was open decision 4, PR #17). The next passage is queued in the audio
  engine and written straight after the current one, so the ~0.4 s of output latency between
  passages is gone; smoke-tested by ear in the running app. Kokoro's own trailing silence
  (~0.5 s) remains. Not yet measured with a loopback recording; if Kokoro's pause feels long,
  trimming trailing silence per clip is the next lever.

## Work that needs no decision

In rough priority order:

- **Heron with maths interpretation, end to end.** ADR-0008 compared the layout models without an
  LLM endpoint. Run a maths-heavy paper with the interpreter and compare what is sent and read.
- **Kokoro on an Intel or AMD integrated GPU.** ADR-0007 measured only the NVIDIA card. Run the
  opt-in `OnTheGpu_KokoroGivesTheCpusAudio` test and the smoke tool's `speak --gpu-model` on one;
  the output check falls back to the CPU if it gives NaN or silence, but the speed is unknown.
- **Start-up on the laptop** (`status.md` item 61 gives about 14 s before the window appears
  there). Not reproducible on the reference machine: its window opens 1.0 s after the process
  starts, with the object graph built in 0.4 s and the layout model's GPU session in 0.27 s
  (Radeon 8060S, measured 2026-11-19). Measure on the laptop first, which part is the layout
  probe and which the driver's shader cache. If it is the probe: build the backend on the thread
  pool and let the first extraction and the cache lookup await it (the device is part of the
  cache key). Two things to keep: Kokoro's WebGPU hook (`KokoroGpu`) is built after the layout
  probe today and whether the two may overlap is unknown, and the start-up notes list is not
  thread-safe.
- **Render only the crop for the maths interpreter.** A narrow equation makes the whole page
  render at up to 8192 px (`RenderCropPngAsync`). This needs a region render in RailReaderCore
  (for example `RenderRegionPixmap(page, rect, scale)` beside `RenderPagePixmap`), requested in
  [RailReaderCore #222](https://github.com/sjvrensburg/RailReaderCore/issues/222): Core's
  `PdfiumGate` and `PdfiumNative` are internal, so the application cannot call PDFium under
  Core's lock itself, and capping the resolution instead changes what the model sees. It no
  longer costs the reader anything visible (painting does not wait for PDFium); it costs the
  page rasteriser up to 134 ms and a 140 MB array per narrow equation.
- **What is left of headings read late** (`status.md` item 70). PP-DocLayoutV3 itself places
  "References" directly after its list in 2 of 18 papers; it is heard only with references
  switched on, and a heading ordered straight after the block below it could be moved in front.
  Heron with Core's XY-cut++ has 4 of 521 headings and 25 of 3,585 other neighbouring pairs out
  of order on the same papers, among them a subsection heading before its section heading
  (linton-mammen-2005, page 14) and a title read after the side column of an MDPI first page.
  Those belong to Core's resolver and are reported, with six pages reduced to a few boxes each,
  in [RailReaderCore #223](https://github.com/sjvrensburg/RailReaderCore/issues/223); three of
  them trace to its heading-attachment pass. Do not work round them here before Core answers.
- **Kokoro's CPU thread default.** 4–6 threads were about 10 % faster than the provider's 8 on the
  i7-12700H; re-measure on the reference machine before changing it.

- **Saving a restored position as a passage id.** When the plan changes (new layout model, a
  version bump), the saved id can point at a different passage. Store a page plus text-offset
  anchor and map it onto the new plan.
- **Make rule-read passages cheaper.** A passage that ends up read by rule costs a thinking
  attempt (often running out of tokens), a thinking-off retry, a refusal and then a
  transcription: about 66 s for a lead-in. That is the main reason model time is close to
  narration time. Options: a smaller thinking budget for the first attempt, or transcribing in
  parallel when the server has a second slot. Measure with `rail-narrator-mathbench`.
- **Proof-read a sample of rule readings against the printed equations**, symbol by symbol. So
  far they have been checked only for leftover notation.
- **Identify the flaky Audio test.** It failed once in a full-solution run while the LLM was
  busy on the same machine, then passed four times. On 2026-11-19 the Audio suite passed 40 times
  out of 40 on the reference machine with one and a half busy loops per core, so plain CPU
  starvation does not bring it out. Next time it fails, keep the test's name and message; if it
  must be hunted, try memory pressure or the LLM server running, which is what was different.
- **More documents.** Everything about maths was measured on one paper. Try a physics paper,
  a pure-maths paper and an ML paper.

Considered and not built: a banner saying whether the passages on a page being browsed are
ready. The preparing card covers the case that mattered (a start that has to wait).

## Decisions needed from you

0. **Sparse pages and OCR.** A page with under 0.5 % ink and under 60 characters is OCR'd as well, at
   562 DPI on the CPU, about 20 s a page here, to catch scans with a stray text layer. Keep it, lower
   the DPI, or OCR only when the page has ink outside its text?

1. **How should equations be read by default?** The interpreter still makes meaning errors
   even with the page image (the order of the transition polynomial was twice called "the
   number of regimes"). The choices:
   - keep interpretation as the default (current);
   - read the notation for display equations and interpret only inline maths;
   - interpret, but follow every interpreted equation with a short notation reading.
2. **DOIs.** They are currently read as "ten point one zero one six slash …". Skip them, spell
   them, or say "DOI link" in their place?
3. **Author affiliations** on the first page are still read. Skip them like the other front
   matter, or keep them, since some readers want to know where the work is from?
4. **Sideways pages with no Rotate attribute.** The layout model sees the page unrotated and
   loses a line. Detect the orientation and rotate the raster before layout (application side,
   since Core cannot change)? Or leave it, since such pages are rare?
5. **Which LLM endpoint on the new system?** Defaults assume a local server on `:8088`. A
   remote API would need `MathLlmApiKeyEnvVar` and means sending page images off the machine.
   Say whether that is acceptable.
