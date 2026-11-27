# Next steps and open decisions

Handover note, 27 November 2026, on `main` after 0.1.7 (PR #81) and the fixes since. It builds
with 0 warnings; the test runner reports 961 passed and 17 skipped.
`status.md` gives 956 passing and 22 opt-in, because it
counts as opt-in the five tests (four of Phonon, one of Kokoro with durations) that return early
without an install and so report as passed. What is verified, and how it was measured, is in `status.md`. How to run the real models,
the GUI and the audio path on a new machine is in `testing-real-world.md`.

## Where things stand

The pipeline works end to end on a real 27-page statistics paper: layout-aware skipping,
citation removal, maths interpreted during playback by a local multimodal LLM, a rule-based
mathspeak fallback, and pre-roll buffering on Play. The last full maths measurement
(`status.md` item 39): of 224 maths passages, 180 were interpreted, 43 read as notation by
rule and 1 announced. That took about 62 minutes of model time against about 85 minutes of
narration.

Since PR #10:

- **A formula model for display equations** (ADR-0011, `status.md` item 85): PP-FormulaNet_plus-M
  runs in process on the CPU and reads a display equation to LaTeX in about a second. The
  language model can be given that LaTeX with the page image, in place of it, or not at all
  (Settings ▸ Maths ▸ Formula model); a refused equation is read by rule from it; and with no
  language model, display equations are read by rule where they were announced as unreadable.
  Its LaTeX is checked against the page's text layer (Greek letters and decimal numbers). Run on
  the GARCH paper only; 15 of its 80 transcriptions were compared with the page.
- **Claude is an option for maths** (ADR-0010, `status.md` item 84): each maths endpoint in
  Settings ▸ Maths can be an OpenAI-compatible endpoint, as before, or Claude through Anthropic's
  API. Tested against a stubbed transport and, on one drawn equation, against the real API; not
  yet run on a paper.
- **Phonon** (PR #37, ADR-0009) is a second speech engine and the automatic choice when
  installed: Gradium's C library in process, real-time factor 0.061 on the laptop's CPU against
  Kokoro's 0.224. A GPU build was measured and set aside (CUDA 0.017 but vendor-specific; Vulkan
  no faster than the CPU, though it frees the CPU).
- **Core 0.90.0** (PR #12), since moved to **0.91.0** (ONNX Runtime 1.32, WebGPU plugin 0.6) and
  then to **0.93.0** (PR #36: maths crops rendered as a region, XY-cut++ ordering fixes;
  `status.md` item 80) and **0.94.0** (item 83; nothing this application used changed). The
  layout model runs on the GPU when one is usable, falling back to the
  CPU (PR #14, ADR-0006).
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

- **Review annotations landed, all seven phases** (ADR-0014, `status.md` items 100–106):
  annotations are kept by content hash in the data directory in Core's format and drawn on the
  page; N pauses and notes the sentence just read, H highlights it, the sidebar's Review notes
  lists them, annotate mode (Ctrl+E) marks text by hand in five named colours, and sticky notes
  and text boxes go anywhere on the page and move; pen, box and eraser draw and remove; the notes
  leave as Markdown, an annotated copy of the PDF and RailReader2 JSON, and a PDF's own
  annotations are shown read-only; notes on an earlier version of the file are brought to a
  revised one by the words they are on, and the rest wait to be placed as before or discarded.
  The PDF is never modified. Open question: a note in the right margin shows its text cut off
  at fit-width (Core draws it to the icon's right); a Core option to draw it leftwards would
  settle it.

## Recently landed

- **Second review's fixes** (`fix/review-findings-carry-over-ux`): carry-over no longer places a short quote found more than once, marks a caption inside a quoted sentence, leaves replies unlisted, or lets a chosen PDF close the earlier version's offer; the Bring notes menu items are refreshed after a load. In the coordinator, a regeneration no longer overwrites an edit made while the model was asked (a test holds it), and a script edit overtaken by another document no longer clears that document's cached audio. A settings file read once in error no longer blocks saving for the rest of the session.

- **Fixes from a review of the whole code base** (`fix/review-findings-narration-settings-script-edit`): the save-narration notice now narrates the document it was shown for, even after another is opened (it took the open one's path, writing one document's narration over another's), and a failure before the job started no longer leaves Stop narration on a disposed token; a settings file that cannot be parsed is set aside, and one that cannot be read is not saved over, instead of the defaults replacing endpoints and keys at the first zoom; a script correction is applied to the plan under the coordinator's lock, so an interpretation resolved meanwhile is not dropped. The first is checked by reading the code (the App has no tests), the third by the existing coordinator suite only.

- **Four reports narrated to audiobooks** (`fix/gui-test-findings`, `status.md` item 99): 248
  pages, seven hours of audio at about ten times real time, every maths passage interpreted, the
  audio sampled against the script by speech recognition. Fixed on the way: a stray half
  character that stopped planning, contents pages and lists of figures read aloud, section numbers.
- **The window driven with the reader's key** (`fix/gui-test-findings`, `status.md` item 97):
  saving and playing a narration, the audiobook, both engines' own word timing, the endpoint
  notice and the Kokoro download all seen working on a virtual display with Claude interpreting
  the maths; the interpret-ahead change measured (180 s for speaking and model together, the
  model's 70 s beside it). Two defects fixed: the description under a Settings list, and the
  count of uninterpreted maths in a saved narration.
- **Word timing from the speech engine itself** (ADR-0013, `feat/engine-word-timing`, `status.md`
  item 95): Phonon from the attention head that follows its text (a patch to Gradium's library),
  Kokoro from the durations of its timestamped export, each as an alternative to the aligner
  (Settings ▸ Reading ▸ Word timing from). Against MFA: 30 ms and 16 ms mean. A two-page narration
  with Phonon took 4 min instead of 9 min 35 s. Not yet watched in the window.
- **A narration saved with the document** (ADR-0012, `feat/saved-narration`, `status.md` item 94):
  the whole document narrated once to `paper.narration` beside the PDF, from the File menu or
  with `rail-narrator narrate`, then played with no language model and no speech engine; and an
  M4B audiobook with chapters from it. Run on two pages; not yet on a whole paper, and not
  listened to.
- **Brackets and signs from older maths fonts** (`fix/legacy-font-encoding`, `status.md` item 93):
  "ð", "Þ", "¼" and "þ" are read as "(", ")", "=" and "+" in documents that use that encoding.
  The reader heard the letter names ("a lot better") and the Claude Test button passes.
- **A notice when the maths endpoint cannot be asked** (`feat/maths-endpoint-notice`, `status.md`
  item 91): a refused key or an exhausted quota is said once in the status bar, with a button to
  Settings ▸ Maths, where before equations were quietly read by rule. The mark's track is unbroken.
  A Claude address pasted as "…/v1/messages" now works (item 92).
- **The formula model under Heron, and how letters are spoken** (`feat/formula-model-heron`,
  `status.md` items 89 and 90). An equation's number is no longer part of the equation (Heron
  left it in the block, and the formula model read it out); the formula model was run on six
  papers and 25 transcriptions compared with the page; image, LaTeX and both were compared on 47
  passages with a remote model, and `Both` stays the default. The speech engine is given letter
  names for lone symbols, and "Yt+h" in prose is treated as maths and spelled out when read as
  printed. Sideways pages were decided: left as they are.
- **Settings page, Ctrl+Q and Pause** (`fix/settings-page-pause-quit`, `status.md` item 88).
  Endpoints and API keys… opens Settings on Maths; Ctrl+Q quits; and Pause works while a report
  about a later passage (its speech failed) stands as the state. The last is a cause found by
  reading and shown by a test, not confirmed as what the reader met: if Pause is ignored again,
  note what the status line says at that moment.

- **A nearly empty page is not OCR'd** (`fix/sparse-page-ocr-ink-outside-text`, `status.md` item
  87; was open decision 0). A page with little text is recognised only when it has ink outside
  that text, so a scan with a stray text layer still is and a page holding one line no longer
  costs about 20 s. Tested with a stand-in for recognition; not run with the real OCR engine or
  on a real scan, and a figure page with a short caption is still recognised.

- **Recent documents and model downloads** (`fix/ux-recent-and-downloads`, `status.md` item 86).
  Recent documents can be removed (one at a time, the missing ones, or all), the Models menu
  says which models are installed and asks before a download, File ▸ Reload applies maths
  region corrections, and Escape and the page keys work wherever focus is. Seen in the headless
  GUI; not yet tried by the maintainer.

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

- **Listen** (`status.md` items 97, 98; the reader's install now has the patched Phonon library, and
  Phonon's timing measured there agrees with the aligner's to a median 9 ms). Everything of the day's work has now been seen in the window
  and measured at a silent audio sink; none of it has been heard. The one question that needs
  ears: is the highlighted word the one sounding, with Phonon's timing and with Kokoro's?
- **Watch the engine's word timing in the window on the reader's install** (`status.md` item 95). Run
  `tools/setup-phonon.sh` again (it rebuilds the library with the timing patch), choose the
  speech engine's own timing in Settings ▸ Reading, restart, and read a page: does the highlight
  sit on the word being said? Then the same with Kokoro and its timestamped model. If Phonon's
  80 ms frames show, the fitted two-head combination (ADR-0013) is the next thing to try.
- **Measure on a machine that is not power-limited** (`status.md` item 96). The laptop was
  capped at about a third of its clock when narration was profiled, so the day's absolute times
  are suspect and one question could not be asked at all: whether speaking several passages at
  once (several model instances) shortens a whole-document narration. It would need the alignment
  tap in the Phonon patch made per utterance instead of per process.
- **A whole narration with a working maths model, timed** (`status.md` item 96). Maths is now
  interpreted ahead of a narration all the way through, one passage at a time. Check with
  `rail-narrator narrate` and its time report that model time and speaking time overlap; if the
  model is then what a narration waits for, several interpretations at once is the next step
  for an endpoint that allows it.
- **Offer the Phonon patch upstream** (gradium-ai/xn-ptts): `tools/phonon-word-timing.patch`
  adds `ptts_speak_aligned`. Accepted, the application would stop carrying a patch against a
  pinned commit.
- **A whole paper narrated and listened to** (`status.md` item 94). Narrate `nbag025.pdf` with the
  maths model working, play it from the file, and listen to the M4B on another device. Things
  to watch: the time (alignment was most of it on two pages), a seek to a long passage (decoding
  takes about a sixteenth of its length), and whether Stop and a second start pick up from the
  cache as intended.
- **An equation block too tall for the formula model** (`status.md` item 89). Heron groups
  stacked equations into one block; one of 21 on `nbag025.pdf` ran past the 1,536-token limit and
  is announced when no language model is configured. Cutting such a block into its rows before
  transcribing needs a rule for where a row ends (a fraction and a summation's limits have blank
  bands too).
- **The formula model on more fields.** Six papers so far, all statistics, econometrics, machine
  learning or applied physics. Still to do: a pure-mathematics and a physics paper, a text-only
  language model with `--evidence latex`, and the false disagreement when a text layer lacks a
  capital Greek letter that is on the page (2 of 27 equations in one paper).
- **What is left of the older fonts' encoding** (`status.md` item 93). Brackets, "=" and "+" are
  restored. "6¼" for "≠", "2" for "∈", "j" for "|" and "ffiffiffi" under a root are not: they
  are ambiguous or change the text's length, so they need the mapping machinery, not a
  character swap.
- **Sentences with maths in the line** get nothing from the formula model: it reads a formula's
  crop, not a line of text. They are 135 of the GARCH paper's 224 maths passages, the cheap ones
  (about 3 s each on the local model). Reading them locally needs either the inline formulae's
  boxes (the layout models have an inline-maths class; whether its boxes are tight enough to crop was
  not looked at)
  or a line-level model.
- **Claude after its first whole paper** (`status.md` item 122). On the GARCH paper Claude Opus 5.5
  interpreted 206 of 208 maths passages (the local model 186) in 556 s, for about US$2.84. Still to
  do: listen to it; the Settings dialogue's Test button with a key; whether caching the shared
  instructions lowers the cost; and whether low effort for equations does as well as medium.
- **The order survey on the whole corpus for Core 0.94.0.** It was the same before and after on
  the 92 pages that were on the machine; the 44 RailDLA papers of item 80's 767 pages were not.
  Nothing in 0.94.0 touches the resolver, so this is a formality.
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
- **What is left of reading order after Core 0.93.0** (`status.md` item 80). Core answered both
  requests (#222 region rendering, now used for maths crops; #223 XY-cut++ ordering). Through the
  adapter, Heron INT8: 1 of 1,129 headings and 6 of 7,141 other blocks are still read after the
  block below them over 767 pages, mostly figures and empty text boxes; the one heading is a chart
  legend ("SPMLGARCH") detected as a heading. PP-DocLayoutV3 keeps its own order and still places
  "References" after its list in 2 of 18 papers (heard only with references on).

- **Kokoro's CPU thread default.** 4–6 threads were about 10 % faster than the provider's 8 on the
  i7-12700H; re-measure on the reference machine before changing it.

- **Make rule-read passages cheaper** (`status.md` item 117). Measured again with today's
  defaults: model time is about 40 % of narration time on the GARCH paper (2,069 s), so it no
  longer holds narration back; a lead-in read by rule costs about 35 s, most of it the first
  thinking attempt that every lead-in makes. A repair with thinking off saves 14 % but likely
  rescues fewer passages, and was not adopted. What is left: knowing before the first attempt
  that a passage will be refused (they are mostly long lead-ins whose answer reads notation), or
  a second server slot for the transcription.
- **Proof-read a sample of rule readings against the printed equations**, symbol by symbol. So
  far they have been checked only for leftover notation.
- **Identify the flaky Audio test.** It failed once in a full-solution run while the LLM was
  busy on the same machine, then passed four times. On 2026-11-19 the Audio suite passed 40 times
  out of 40 on the reference machine with one and a half busy loops per core, so plain CPU
  starvation does not bring it out. Next time it fails, keep the test's name and message; if it
  must be hunted, try memory pressure or the LLM server running, which is what was different.
  Settled on 2026-11-27 (`status.md` item 120): `EngineLockingTests.StateEvents_AreRaisedOutsideTheEngineLock`
  failed once on the Windows runner for 0.1.7 ("Buffering was raised while the engine lock was
  held"). That Buffering is raised outside the lock; the test's probe waited on the thread pool from
  a handler that already blocked a pool thread. The probe now has a thread of its own.
  Also on 2026-11-23, `NarrationCoordinatorTests.APassageLongerThanTheProviderAccepts_IsSpokenInPieces_AsOneClip`
  failed once in a full-solution run and passed in ten runs of its suite alone; its message was
  not kept either. A guess to check: the load and the Play each prepare the passage under their
  own generation, so the fake engine may record the pieces twice.
  A candidate since 2026-11-23: `EngineLockingTests.SeeksRacingClipTransitions_DoNotDeadlock` ran
  into its 60 s deadline once in a full-solution run with a load average of 11, then passed three
  isolated runs. The message was lost again, but reaching the deadline means the UI thread kept
  ticking and the transitions fell short: the reading stopped advancing, or was slowed more than
  eightfold. Run it in a loop under that load and keep the count of transitions it reports.
  On Windows CI, a Pdf test twice read a fixture's text as NUL characters (2026-11-24 and
  2026-11-26). The cause is DirectWrite losing the fixture PDF's glyph-to-Unicode table in Skia's
  writer, and the fixture now writes such a PDF again (`status.md` item 119). If it comes back,
  the failure says "did not read back after 5 attempts".
- **More documents.** Everything about maths was measured on one paper. Try a physics paper,
  a pure-maths paper and an ML paper.

Considered and not built: a banner saying whether the passages on a page being browsed are
ready. The preparing card covers the case that mattered (a start that has to wait).

## Decisions needed from you

1. **Spoken maths, after the prompt change** (`status.md` item 82). The reader chose plain
   language first, with notation spoken as words where the model is unsure or where a symbol
   recurs, and shorter answers. Run it against the model (`rail-narrator-mathbench --only …`)
   and listen; adjust `MaxNotationShare` and the length cap from what is heard.
2. **Which LLM endpoint on the new system?** Set in Settings ▸ Maths: the service
   (an OpenAI-compatible endpoint or Claude), address, model, and a key from an environment
   variable or stored in the owner-only settings file. Claude, or any remote API, means sending
   page images and the text around each passage off the machine; a local model does not, and
   costs nothing a passage. Which is the default for your own reading is still yours to choose,
   once Claude has been heard on a paper (see the first item above).
