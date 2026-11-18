# Next steps and open decisions

Handover note, 18 November 2026, after PR #17. `main` builds with 0 warnings; 442 tests pass
and 6 opt-in tests are skipped. What is verified, and how it was measured, is in `status.md`.
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

- **Core 0.90.0** (PR #12), and the layout model runs on the GPU when one is usable, falling
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

- **Gapless playback** (was open decision 4, PR #17). The next passage is queued in the audio
  engine and written straight after the current one, so the ~0.4 s of output latency between
  passages is gone; smoke-tested by ear in the running app. Kokoro's own trailing silence
  (~0.5 s) remains. Not yet measured with a loopback recording; if Kokoro's pause feels long,
  trimming trailing silence per clip is the next lever.

## Work that needs no decision

In rough priority order:

- **Kokoro on an Intel or AMD integrated GPU.** ADR-0007 measured only the NVIDIA card. Run the
  opt-in `OnTheGpu_KokoroGivesTheCpusAudio` test and the smoke tool's `speak --gpu-model` on one;
  the output check falls back to the CPU if it gives NaN or silence, but the speed is unknown.
- **Start-up and "Ready" still block on two initialisations** (`status.md` item 61): build the
  layout probe and initialise Kokoro in the background, so the window appears at once and Kokoro
  loads while the document is being extracted.
- **Kokoro's CPU thread default.** 4–6 threads were about 10 % faster than the provider's 8 on the
  i7-12700H; re-measure on the reference machine before changing it.
- **A resize resets the zoom to fit-width** (opening the script panel, resizing the window). Keep
  the reader's magnification and only re-fit when the zoom was "fit".

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
  busy on the same machine, then passed four times. Loop the Audio suite under CPU load and
  look for a timing assumption.
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
