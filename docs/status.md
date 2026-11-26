# Verification status

Everything below was measured on the reference machine — Fedora 44, x86-64, AMD
Ryzen AI MAX+ 395 (Strix Halo, Radeon 8060S integrated), 124 GB RAM, 17 November
2026 — unless it is explicitly marked as *not verified*.

The point of this document is that "implemented" and "verified" are different
claims, and only the second one is worth relying on.

## Verified by automated tests — 950 passing, 22 opt-in skips, 0 warnings

| Suite | Tests | What it covers |
|---|---|---|
| `RailNarrator.Domain.Tests` | 448 | A Pause pressed between two passages kept, the next passage loaded and read only at Play; the passage being heard read again from its start when it is edited; a regeneration the model refused leaving a paused passage paused; "Restore as prose" applying to a display block of two lines; intervals ("[0, 1]", "on the interval [1,2]", "x ∈ [1, 3]") and dated asides ("(from January 1990 to March 2015)", "the Great Depression (1929)") kept when citations are left out; A saved reading position found again in another plan of the document by its place on the page (the passage covering it, else the next, else the one before; the saved id only within its own plan and inputs); An edit made while a start loads carrying the start on; A start overtaken while its clip loads not playing, a load overtaken while it sets the resumed speed not publishing over the next document, and a voice chosen after a failed start starting nothing; The section the status bar names: by page while browsing, and on the page being read the heading before one the narration has not reached; Three quick voice changes while reading carrying on in the last voice, and a voice chosen while stopped starting nothing; Thirty quick presses of Next speaking only the passages still wanted, the last within 2 s, with an engine that speaks one passage at a time; A whole narration interpreting its maths ahead of itself all the way and asking about each passage once; The aligner started by the first passage that needs it when the engine times its own words, with the passages ahead still prepared while it starts and no alignment asked before the start is over; The engine's own word timing standing with the aligner not run when the reader prefers it, there at once and the aligner still run otherwise, engine cues placed on the script's words (two names of one letter one cue), a cue in no word leaving none; A recording played without the engine being asked until another voice is chosen, and a whole plan narrated in order with no device; Brackets, "=" and "+" restored character for character on a page whose maths font hides them as "ð", "Þ", "¼" and "þ", Icelandic and "¼ cup" left alone, a page with no evidence of its own restored on another's; The resolver reporting an endpoint that cannot be asked, with its reason, and not an answer that was merely rejected; An equation's number left out of the equation by where it stands (right or left edge, a label in the middle of the text, an argument such as "g(2)" kept, Elsevier's "ð13Þ"); letter names for the speech engine with the script unchanged ("a" told from the article, also after "over", "given", "plus" and an opening bracket, the symbol before a verb such as "denote" or "lies", abbreviations and "I" left alone, an engine's range mapped back to the script); notation set solid in prose read piece by piece ("Yt+h\|Ft") and counted as maths; Computer Modern's epsilon in the text-layer check; A failure on a passage ahead leaving the snapshot saying that audio plays, so that it can be paused; A formula model's LaTeX checked against the page's text layer (a letter taken for another settled by the page and noted, variant letter forms and sum signs not mistaken for differences, any other difference or a number the page has and the transcription lacks reported and left standing, a text layer with no Greek letters or with unmapped glyphs not held against it), and the resolver with a formula model: the image and the transcription, the transcription alone (no image, for a sentence too) or the image alone as chosen, a tight crop of the equation for the formula model and the padded one for the interpreter, a request without a formula keyed as before, a transcription made once and asked again on Regenerate, an unreadable equation left out with a warning, a refused equation read from the transcription before the interpreter's own, a refused lead-in read whole from the interpreter's first, and with no language model an equation read by rule, a lead-in keeping its sentence with the equation's reading linked to the equation, a sentence read as printed; DOIs read as "DOI link" (doi.org links, "doi:", bare identifiers including all-digit JSTOR suffixes; a number with a short registrant left alone) and mapped back to the whole printed DOI; the first page's affiliations, correspondence address and affiliation footnotes skipped, line by line, with the title, authors and abstract kept, a body sentence at the foot without a marker kept, later pages untouched, and the skip turned off on request; A passage longer than the speech provider accepts spoken in pieces (sentence, clause, word, limit) as one clip; The lookahead counted in listening time with a minimum and a cap of passages, a passage played without waiting for its words to be timed (its word highlight following when the timing arrives), the aligner started once when a document loads; A passage highlighted all at once drawn as one shape (one band per line, its lines meeting across the leading, a column gutter and a word the cue does not read never covered); A voice change overtaken by a plan load or a seek neither cancelling its lookahead nor letting the old voice play, and an older load not putting its plan back over a newer one; the heading of a reference list that is not read skipped with it (by the block below it, not the model's reading order; other headings kept; read when references are); A cue with no timing inside it that crosses a page break framed on the page holding most of it (a word on the page it is printed on, an equal split on the earlier page, a page with no geometry never), a seek, a start from a stop and a new document ending a pause in following while a resume from Pause leaves it; One gliding follow mode: the reading line held at the reading height (the line's, not the word's, so the view does not bob), a line wider than the view panned between aligned words at their measured pace, starting with the line's start 20 % in from the left and ending with its end near the middle, a new line reached by a quicker carriage-return glide, the frame-loop follow target present only while playing and following; highlight boxes given one height per line across the paragraph, the other column not taken as part of the line, a sentence or equation cue without word timing framed at its start and held (never panned at a guessed rate), a page change cut rather than glided, the glide eased without overshoot at 20–144 fps, keeping its speed when retargeted and bounded after a stalled frame; a seek to an unprepared passage reports its stage (interpreting, loading, synthesising, timing words) and elapsed time and clears when it plays, a prepared passage starts with no wait reported; Abbreviations expanded only as whole words ("Chap.", "overlap.", "p.adjust" untouched); maths italic letters (U+1D400 block, ℎ) mark their sentence as maths and are spoken as plain letters and Greek names, mapped back to both UTF-16 units; silent marks not counted as words; a zoom re-checks the camera at once unless following is suspended; Decimals and signed numbers after a year cue ("from 0.20", "by 0.75") read as numbers, not handed to the year reading; Play waits for the maths ahead (and starts anyway after the cap), a refused interpretation read mechanically from a transcription and labelled, a failed page crop reported, Front-matter metadata skipped (body sentences that start the same way kept), A lead-in sentence and its equation interpreted as one passage, Citation markers left out (and brackets that are content kept), regenerate re-asks the model, Maths left pending with an honest fallback, interpreted once with surrounding definitions and cached, spoken by the coordinator; Layout roles (model formulas trusted over geometry, display lines merged, headings kept apart, inline maths kept in its sentence, tables announced, publisher cover sheets skipped), years and identifiers read in context, Offset-unit conversion, normalisation mappings, cue validation, cue resolution, source maps, word boundaries from measured glyph geometry, unmapped ligature glyphs, running heads, planner behaviour, coordinator invariants and lock ordering, lookahead, pause/resume, release of played clips, cache keys, camera policy |
| `RailNarrator.Pdf.Tests` | 131 (+5 opt-in) | Notes on an earlier version of a PDF brought to the revised one by their words: a sentence found on another page, a hand mark ending inside a word, a word broken by PDFium's line-end hyphen marker, a sentence that crossed a page break and no longer does (one annotation with its note), a word found as a word before inside a longer one, a short quote found more than once left unplaced, a caption inside a quoted sentence left unmarked, a reply carried with the note it answers, notes from a chosen PDF leaving the earlier version's offer standing, a sentence across a page break found past the page number and running head between its halves; notes brought twice given ids of their own, undoing one leaving the other; a note placed as before taking its replies and refused a page the version lacks; a document opened again in the same session saved; notes not placed kept when the record of their version is lost, and the earlier version peeked at without anything set aside; a cut sentence, a sticky note and a drawing kept unplaced and the earlier version untouched; bringing over, placing as before (on the page its page became) and discarding each undoable; a revised file at the same path offered the earlier notes and what was brought over kept across a save, an undone carry-over leaving no record; notes not placed listed apart in the Markdown; An annotated copy carrying every annotation with its /NM, words and author while the PDF's bytes stay the same, a second export not doubling them, a PDF's own annotations kept in the copy, the copy refused over the PDF itself; RailReader2's JSON imported once and the second time skipped, as one undoable step; the PDF's own annotations shown but neither saved nor counted as the reader's; the notes as Markdown by page with their quotes (Markdown characters in the text escaped) and those already in the PDF after; the words under a markup's boxes as its quote (an underline drawn as a baseline strip too); word-sized anchors joined into one band per line without crossing a word of another sentence; Pen strokes keeping the points given and dropping ones that add nothing, a one-point stroke refused; unfilled rectangles; a stroke stretched by a corner handle scaling every point from the old box to the new and undone in one step, never below Core's 10-point minimum nor started off a handle; Sticky notes and text boxes placed where asked, signed, tied to no text and undoable; a note, a text box or a pen stroke moved with the pointer as one undoable step (a click without moving recording nothing, a move taken away putting it back, markup not movable); a box dragged out in any direction, a small one a click; Text marked by hand in annotate mode: a drag across two lines covering both from where it started (backwards the same), a press on blank paper or in the leading starting nothing, a page with no text saying so, a press without moving not a drag, OCR text marked from its own boxes, the range quoted as reading units quote (an identified ligature written as its letters, PDFium's line-end hyphen marker joined across the break, an unidentified glyph left out) and linked to the passage that reads it, each markup kind made and recoloured as one undoable step, and a mark made by hand never taken for one made on what was just read; What was just read found on the page (the sentence a cue was reading, the passage when asked or when no cue says which sentence, a sentence across a page break on both pages, an equation with no text behind it boxed by its own rectangle, an anchor of another extraction by its rectangles rather than offsets into this one) and quoted with a line-end hyphen joined by the planner's rule; notes and highlights made on it (the note on the first page of a sentence across a break, the pages kept and removed together, edits and highlights undone and redone in order with their links, a mark already on the same sentence found rather than made again); The reader's annotations (ADR-0014): every kind and its link to the narration back as saved, the file read by RailReaderCore's own `ImportJson` and equal byte for byte to its `ExportJson`, a file that cannot be parsed set aside and one that cannot be read never saved over, the newest of twenty quick saves the one on disk with no temporary left, the copy drawn on the render thread unchanged by later edits, a highlight drawn at its page points at the zoom given, and a range over two lines given one box per line from the cached extraction's character boxes; A page with little text recognised only when it has ink outside that text (a nearly empty page not at all, a scan with a stray text layer still), with recognition a stand-in that counts its calls; the ink measure on drawn rasters, grey paper included; Maths crops rendered as a region giving the old crop to within a pixel, and a narrow equation its full width; A layout model's own reading order kept when its blocks arrive most confident first, with and without a duplicate block for Core to remove; Fit-width as a mode: the zoom following the page shown and the view's width, a zoom the reader chose surviving a resize and a page turn, fitting again on request, limits and unusable stored values; Page rasters for the reading surface rendered on a worker: page sizes read at open, rasters delivered in the order asked and once each, a render the reader has paged past abandoned (and one that cannot be cancelled dropped, not handed over), a render still wanted carried on, a page that cannot be rendered reported once, colour filters applied there and blank paper's colour matching each filter's own; Layout analysis that fails to start, or fails mid-document, finishes on the CPU fallback from the page that failed and says so once, and the source then reports it so the snapshot is not cached under the GPU key; opt-in, the `Cpu` setting never uses the GPU, and (`RN_WEBGPU_INTEGRATION=1`) the GPU gives the same blocks, kinds, reading units and geometry within 0.1 pt as the CPU; A passage crop is a PNG of its ink; **Real PDFs** through the real RailReaderCore pipeline, including a cached snapshot building the same plan as a fresh one; opt-in, the real PP-DocLayoutV3 model through the adapter |
| `RailNarrator.Speech.Tests` | 64 (+8 opt-in) | Kokoro's voices found beside the application (where a release carries them) before the NuGet cache; Word timing from a model's attention pinned to the reference on three real Phonon passages (to the sample), accepted whole by the cue validator, across chunks, and refused when the engine spoke other words, the head stopped short or does not follow; word timing from phoneme durations: words counted off, pauses no word's, a compound divided where its first part's sounds end or refused, frames rounded as the model rounds, durations that do not add up refused; opt-in, real Phonon through the patched library and real Kokoro with durations each timing every word; Phonon without its install reporting why instead of throwing; opt-in (`RAIL_NARRATOR_PHONON_DIR`), real Phonon through Gradium's C library speaking at 24 kHz in Maeve by default with British voices first, another engine's voice spoken in Maeve with a warning, and a cancelled passage stopping with the provider still usable; Opt-in, real Kokoro speech holding acronyms and words the dictionary lacks aligned word by word; PCM/WAV codecs, silence trimming, provider contracts, ZONOS3 HTTP contract against a stubbed transport |
| `RailNarrator.Audio.Tests` | 57 (+2 opt-in) | A device that cannot start reported as Error and DeviceFailed rather than Playing in silence; clearing the queue waiting for a clip still being queued; A Resume pressed while a speed change renders still playing (opt-in, also through a real PortAudio device); a speed change just past a passage boundary carrying on in the next passage; Loading a long clip and changing the rate returning before the clip is rendered (a minute of speech), quick rate changes ending at the last with the position held, a clip queued beside a rate change written at the new rate; an earlier stop never emptying a clip loaded after a later one (two quick seeks; a device whose writes are slow); PortAudio's structures as declared, against their C layout on 64-bit Linux and Windows (C `unsigned long` eight bytes on one and four on the other; `PaWasapiStreamInfo` the 56 bytes PortAudio v19.7.0 checks for) and the library looked for under the platform's names; Resampler (polyphase path equal to the direct sinc filter on four rate pairs), time-stretcher (fast grain search equal to the direct search at three rates), playback engine against a controllable fake device, engine lock ordering, coordinator-plus-engine transition stress |
| `RailNarrator.Infrastructure.Tests` | 60 | A reading position read back with its plan and place, and one written by an earlier version still read; A preference not saved at closing said once at the next start, and closing going on when even that cannot be recorded; Preferences written in the background: not on the caller's thread, 200 changes all written in order, a change made now after those still queued, a failed change reported and those after it still written; Phonon's model and voices as the application downloads them: the same files, revisions and digests as `tools/setup-phonon.sh`, the layout and voice list the provider reads, and a file that is not the one expected refused with nothing left looking installed; A settings file that cannot be parsed set aside (keys and all) before the defaults' first save; The annotation preferences (author, colour names, each tool's colour) saved and read back, and absent from an older file; A narration saved with the document (ADR-0012): a clip back at its own length with its sound where it was, a document narrated to a file and played from it with an engine that cannot speak, a rewritten passage not served, another document's file refused with a reason, a failed narration leaving no file, a refused maths endpoint stopping the job unless told to carry on, chapters at the headings, ffmpeg's metadata, and (when ffmpeg is installed) a real M4B; Recent documents: the missing ones forgotten with their thumbnails, and the list cleared; The formula model used when installed and the evidence both unless chosen, for a settings file from before it too, and its two files in the manifest, pinned to a commit with digests and never required; The maths services OpenAI-compatible unless Claude is chosen, for a settings file from before the choice too, and saved by name; Recent documents kept newest first, bounded, with progress kept in place and a damaged list read as empty; The settings file written owner-only (and an older, wider one narrowed), a stored API key winning over the environment variable; Cache round-trips and waveform integrity, including same-length replacement; extraction-cache keying, degraded and encrypted documents never stored, corrupt entries treated as misses |
| `RailNarrator.Inference.Tests` | 190 (+7 opt-in) | "+", "*", "\|", "x/y" and "f(x)" refused in spoken maths (prompt `math-interpret/5`), "and/or" accepted; a count or a time the source gives ("over 100 samples") accepted by the verbaliser, one it does not give refused; The repair turn thinking when the first attempt did, and asked with thinking off when told to; A Claude address pasted as the whole route ("…/v1/messages") reduced to the API's root, Anthropic's own address treated as none, and the Settings test asking the right route; A refused key and an exhausted quota named as the endpoint's problem without the server's words (which may hold part of the key), a rejected answer and a failure followed by an answer not; A name a formula model writes letter by letter in a text face ("\mathsf{d i m}") read as one word; The formula model without its file: the image it is given equal to the reference pipeline's byte for byte on six shapes (digests from Pillow 11.3 and 12.0), centred on black, read on white where transparent, blank crops and rules refused, the box reduction's rounding, the vocabulary read from the model's YAML (plain and quoted tokens, a line it does not understand, a gap or no vocabulary an error), tokens decoded to LaTeX through the byte alphabet with special tokens dropped, the output tidied and refused when its braces do not balance or it ends in a repetition, a transcription read by rule (a lone bar as "given"; one that disagrees with the page or runs away not read), the transcriber of the older modes checking the page too, and the prompt unchanged word for word without a formula and naming the transcription as fallible with one; opt-in (`RAIL_NARRATOR_FORMULA_MODEL`), the real model reading a drawn equation with its 50,000-token vocabulary; Maths through Claude, with the Anthropic SDK's transport stubbed below it: the passage sent as a Messages request (image first, the prompt as the system text, Claude Opus when no model is named, no sampling or thinking fields), the thinking policy setting the effort (medium where the other endpoint would think, low elsewhere), a refusal fallback asked for only against Anthropic's own API and a model that takes it, notation given one repair as a new request carrying the rejected answer and refused after it, a declined passage refused at once, a cut-off answer refused, the effort field dropped for a model that rejects it and remembered, a refused key ending the passage with the key in no message, the clients built and the passage saying why when there is no key anywhere, Claude's answers cached apart by address and model, a refused equation transcribed and read by rule, verbalisation asked for as structured output without string-length keywords and still validated, a gateway that refuses the schema asked without it, the vision transcriber needing no address for Claude and testing its connection once, and the Settings test telling a known model, an unknown one, a refused key and nothing answering apart; opt-in (`RN_CLAUDE_INTEGRATION=1`), the same against Anthropic's real API; Spoken maths with notation written as words beside its meaning accepted, written symbols and glued letters refused, an answer mostly reading notation or far longer than its passage refused; The Settings dialogue's endpoint test telling a listed model, an unlisted one, a refused key, a wrong address and nothing answering apart, without the key in its message; ClearSpeak-style reading of LaTeX (powers, fractions, roots, quantities, derivatives, sums, functions, cases, Unicode, text with `$…$`, malformed input); thinking-policy and out-of-tokens retry; transcription fallback; Maths interpretation: notation validator, image and thinking-switch fallbacks, one repair turn, cut-off responses refused; golden maths semantics, response validator, endpoint client |

Run them with (and see `testing-real-world.md` for the real models, GUI and audio):

```bash
dotnet test RailNarrator.slnx -c Release
```

The default run needs no GPU, network, model file or sound card. The opt-in
integration tests are skipped unless their dependencies are explicitly enabled. The Audio suite
runs stable across repeated invocations; an earlier flake (a test that drained the
device before the writer thread had finished queuing) was fixed by waiting for the
completion event instead of a fixed delay. On 18 November 2026,
`CoordinatorLockingTests.NaturalAdvance_SlidesTheLookaheadWindow` failed once in a full-solution
run, after about 5 s (one of its 5 s waits). It then passed 10 isolated runs and 5 further
full-solution runs. The cause was not identified, and the failure message was not captured. It
failed once more on 19 November 2026, in a full-solution run after about 0.4 s (an assertion, not a
wait), then passed 6 isolated runs and 6 further full-solution runs; the message again was not
captured. It failed a third time on 25 November 2026 in the v0.1.5 release workflow on Windows,
at its first wait ("the first window (0-2)": Play started and exactly three passages
requested), and passed when the job was run again. It did not fail in 20 local runs, 10 more
pinned to one busy core, or with a slow fake engine and Play overtaking the load's first
synthesis; the cause is still not known. The test now waits for passages 0-2 to have been asked
for and for the requests to settle, checks that the advance asks for passage 3 alone, and says
on a timeout how many times Play ran, the coordinator's state and which passages were asked for. One Audio test failed once in a full-solution run while the local LLM was busy on
the same machine and passed in three reruns; it was not identified. On 23 November 2026
`EngineLockingTests.SeeksRacingClipTransitions_DoNotDeadlock` failed once in a full-solution run
after 1 min 1 s, which is its 60 s deadline, with a load average of 11 on the machine; it then
passed three isolated runs. The message was not captured, but running to the deadline means the
simulated UI thread kept ticking (a 5 s silence fails sooner) and fewer than 119 transitions
completed: the UI thread was not deadlocked, and either the reading stopped advancing or the load
slowed it more than eightfold (the suite takes 7 s). This may be that Audio test.

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

### Phonon downloaded from inside the application

`PhononModel.InstallAsync`, the code behind *Models and endpoints* ▸ *Download Phonon speech
model*, run for real (24 November 2026): all 29 files fetched from Hugging Face and verified by
size and SHA-256 in 26 s; its `voices.tsv` byte for byte the one `tools/setup-phonon.sh` writes;
the four real-runtime `PhononProviderTests` passing on the result, with the release archive's
`libptts_ffi.so` (Maeve at 24 kHz, every word timed from its attention). The menu item itself has
not been clicked in a running application.

## Not verified — and what it would take

| Gap | Why | What would close it |
|---|---|---|
| **Audible playback by a human ear** | The signal reaching the output device was recorded and matches the synthesised clips, but nobody has listened, and the Bluetooth leg after the sink was not captured | Play a document through speakers and confirm speech is intelligible and correctly paced |
| **Audible-to-visible delay** | Highlights agree with the device's own DAC timestamps; whether those timestamps are right for a given device is unmeasured | Loopback microphone plus display capture, at several speeds and on wired and Bluetooth outputs |
| **Maths interpretation across documents** | Measured on one statistics paper with one model (Ornith-1.5-35B-A3B, see item 32) | Run other fields' papers (physics, pure maths, ML) and a text-only endpoint; review a sample of interpretations against the originals for meaning errors |
| **Mechanical readings against the page** | The transcription fallback (item 39) ran on the GARCH paper, and its readings were checked for notation, not proof-read symbol by symbol against the page. The formula model's transcriptions (item 85): 15 of 80 compared with the page | Compare a sample of rule readings with the printed equations, and proof-read the rest of the formula model's |
| **The formula model on other documents** | Six papers under Heron with no language model, 25 more transcriptions compared with the page (item 89); with a language model, pages 3 to 7 of one paper | A text-only language model with `--evidence latex`; a pure-mathematics and a physics paper; an equation block too tall for the model's token limit |
| **Scanned-document OCR at 300 dpi** | A 131 ppi scan with handwriting was run and reads as sentences; the handwriting is recognised as stray words | Check a clean 300 dpi scan for coverage, warnings and reading order |
| **Clean-machine install** | The archive was built and run here, not on a fresh system | Install on a stock Fedora/Debian with only `libportaudio2` added |
| **Long-document endurance** | Runs of three to seven minutes of a 27-page paper, 1.5–1.7 GB resident; played clips are now released, but a whole document has not been played through | Play a 40-page paper to the end: peak RSS, preparation lag versus playback, cache growth |
| **Claude on a real paper** | Run against the real API on one drawn equation only (item 84) | `rail-narrator-mathbench --provider claude` on the GARCH paper for the tally, time and cost a passage, beside item 39's numbers for the local model; then listen |
| **Layout on other GPUs** | GPU layout ran on one NVIDIA laptop GPU (Vulkan) | Run `LayoutAccelerationTests` with `RN_WEBGPU_INTEGRATION=1` on AMD and Intel GPUs, including the Fedora reference machine's Radeon 8060S |
| **Windows** | Implemented, not run: the application and `rail-narrator-cli.exe` cross-publish for win-x64 from Linux, and `.github/workflows/windows.yml` builds, tests and packages on a Windows runner, but nobody has run it on Windows yet. Written without a Windows machine: PortAudio through WASAPI (its own DLL, built from v19.7.0 with MSVC), settings in `%APPDATA%` and data in `%LOCALAPPDATA%`, speech-cache folders without the colon of asset ids, Phonon's `ptts_ffi.dll` (built with MSVC on the runner and carried in the package; not yet heard speaking), the GPU through Direct3D 12. Word alignment (MFA) is Linux-only | The workflow green; then on a Windows PC: open and play a PDF, `smoke audio --clicks` against a recording for the WASAPI clock (ADR-0003), Kokoro and Phonon speaking, the GPU tests with `RN_WEBGPU_INTEGRATION=1` |
| **The AppImage and the Windows installer** | Implemented and run on CI only: `packages.yml` starts the AppImage on Ubuntu 22.04 (it prints the narrate usage; its PortAudio needs only ALSA and the C library), and installs, runs and uninstalls the installer on a Windows runner. Neither has opened a window on a desktop, and no AppImageUpdate has been tried | Run the AppImage on a stock desktop (GNOME, KDE) and play a PDF; install with the installer on a Windows PC, open a PDF from *Open with*; update an AppImage of one release to the next with AppImageUpdate |
| **File > Open dialog** | The GUI run loaded documents from the command line only | Open a document through the picker under a desktop portal |

## Open defects found by driving the application

Not fixed yet. Each was observed in the run described above.

- **Word highlighting still falls back on most of a technical paper.** 58 % of that paper's prose
  passages contain a word the pronunciation dictionary lacks ("GARCH", "heteroskedasticity",
  authors' names), and one such word sends the whole sentence to sentence highlighting.
- **Years from 2000 on are read as cardinals** ("two thousand twelve"), which is an ordinary
  reading. (DOIs are now read as "DOI link", item 82.)
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
- **Affiliations are found by their words** (item 82): one that names no institution and has no
  marker beside a recognised one is still read, and a model-free first page whose title names a
  university or college above the abstract would lose the title (a layout model labels titles,
  which are never skipped).
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
- **A page with little text and other ink is OCR'd as well, at 562 DPI on the CPU: about 20 s a
  page** on the i7-12700H. Since item 87 a page whose only ink is its few characters is no longer
  recognised (the two one-line pages of the `crosspage` fixture took 28 s). A figure page with a
  short caption still is, since a figure is ink outside the text; so is a scan with a stray text
  layer, which is the case the rule exists for. Not run with the real OCR engine or on a real scan.
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
- **Sideways pages with no Rotate attribute.** The layout model sees such a page unrotated and
  loses a line. Detecting the orientation and rotating the raster before layout was considered and
  left (the reader's decision, 2026-11-23): such pages are rare and are typically tables, which
  are announced and not read.
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

10. **An earlier stop emptied a clip loaded after a later one.** Two seeks in quick
   succession each stopped the transport. The first stop waited for the writer to leave
   its write; the second found no writer, returned at once, and its seek loaded and
   started the next clip; then the first stop flushed the device and emptied the engine
   under it. The coordinator was left reporting "Playing" over an idle transport, and
   playback never resumed. Found by the Windows runner, where the stress test
   (`SeeksRacingClipTransitions_DoNotDeadlock`) hit it in every run, after 5 to 68 of 119
   transitions; it never showed on Linux, though it could happen there too. The engine now
   runs stops, loads, pauses and seeks one at a time and in order, and a stop waits for a
   writer another call is still stopping (`AnEarlierStopCannotEmptyAClipLoadedAfterALaterOne`,
   which fails on the old engine).

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
    hold during playback; the session's log has one page taking 5,241 ms to become sharp. A first
    fix re-filtered a held unfiltered render on the rasteriser's thread; the reader then saw the
    highlight change at once and the page after it, because that is still a CPU shader pass over
    the whole raster. Now, as in RailReader2, rasters are held unfiltered and the filter is applied
    as the page is drawn (`FilteredPageDraw`, a Skia custom draw operation), so a change is one
    repaint; the first fix was reverted. Checked under Xvfb on a synthetic two-column page at
    235 %: drawn through High Contrast at launch, and after a switch to None the page was
    repainted within 150 ms (the first screenshot taken). The image and the filter are shared by
    reference count with the render thread; Avalonia 12.1.6 disposes a recorded custom operation
    with its frame (`RenderDataStream.DisposeResources`, read from the assembly), which releases
    them. *Not verified:* on the reader's GPU, at 460 %, or during playback; a memory check over
    repeated switches was attempted but the XTEST input did not reliably change the selection,
    so it measured nothing.
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
75. **A Settings dialogue** (File ▸ Settings…, Ctrl+,), laid out as RailReader2's: pages on the
    left (Reading, Speech, Maths, Document, Settings file), every change saved as it is made, model
    paths and the vision endpoint behind "Show advanced settings". Highlight opacity and contrast,
    following and reduced motion apply at once; speech, maths, layout and what is read apply after
    a restart, which the dialogue offers and which reopens the document where the reader was. An
    endpoint's key comes from a named environment variable or is stored in the settings file,
    which is then written mode 0600 from creation (an older, wider file is narrowed); a stored key
    wins over the variable. "Test" asks the endpoint for its models with the values as typed and
    says which part is wrong (address, key or model), never showing the key. Covered by Inference
    tests of the probe and Infrastructure tests of the file mode and key precedence. Checked
    under Xvfb with a local fake endpoint that requires a key: typed address, model and key were
    saved (`-rw-------`, key stored), Test reported "127.0.0.1 answered and offers local-model",
    "Restart now" relaunched with the same document and the dialogue then showed no restart
    pending. *Not verified:* against the reader's Qwen endpoint, and that the restarted
    application then interprets maths through an endpoint configured this way.
76. **The window follows RailReader2's look** (the first of three GUI changes the reader asked for,
    after comparing the two applications side by side). Light by default, dark as a setting; a
    menu and panel text size (75 to 200 %), as RailReader2; both apply at once and at start.
    RailReader2's Lucide icons and icon control replace the text glyphs; its flat tool buttons and
    its tooltips placed above their control (on X11 a tooltip under the pointer swallowed clicks)
    are used throughout. The toolbar holds only what a listener reaches for (transport with an
    accent Play button, voice, speed, follow, colour filter, settings); page and zoom moved to the
    status bar, as in RailReader2. The canvas around the page is RailReader2's grey (128) in the
    light theme, and the opening and preparing cards use theme colours instead of hard-coded dark
    ones; icon buttons have accessible names. Checked under Xvfb on a real paper: light theme with
    the page and the grey canvas; Dark mode and 130 % text applied at once to the main window, its
    canvas and Settings, were saved, and were applied again at the next start. *Not verified:* on
    the reader's desktop, and the opening and preparing cards in either theme (no screenshot of
    them since the change).
77. **The sidebar and status bar follow RailReader2** (the second of the three GUI changes). The
    script, maths review and diagnostics moved from a fixed 380 px panel on the right to a sidebar
    on the left (toggle in the toolbar, F6) with RailReader2's accordion (one section open, filling
    the panel) and a splitter; its width, visibility and open section are remembered. A new
    Outline section shows the PDF's own bookmarks: choosing a heading shows its page and, unless
    reading is under way (then it seeks there), makes the next Play start from the first passage
    on that page ("Play reads from “4 Proposed models”"). The status bar names the section being
    shown ("3 Data and model inputs › 3.2 Daily and hourly…", the paper's title left out), and
    notices with an action take its place, as RailReader2's toasts: speech unavailable → Speech
    settings…, a model downloaded → Restart now. The open document's name is shown as a tab in
    the toolbar and in the window title. Checked under Xvfb on a real paper with an outline: the
    accordion, the outline tree, a heading turning to its page and setting the start, the section
    name, a long notice trimmed with its button, and a dismissed notice staying dismissed (it came
    back with every snapshot until fixed). *Not verified:* Play starting from a chosen heading (no
    voice in the sandbox), dragging the splitter, and the restart notice after a real download.
    The same change adds dropping a PDF onto the window (the reader tried it and found it
    missing; RailReader2 has none): a local `.pdf` shows "Drop to open paper.pdf" over the page
    while dragged and is opened on release, as by File ▸ Open; anything else is refused. Checked
    under Xvfb with a real XDND drag from a GTK drag source: the hint, the paper opened from an
    empty window, and a `.txt` refused with no hint and the open document kept. *Not verified:*
    from the reader's file manager.
78. **Reading stopped for good at an interpreted passage longer than Kokoro accepts.** Reported
    by the reader on nbag025.pdf: playback stopped after equation (2.1) and would not read the
    passage holding (2.2). The model's interpretation of that passage (a lead-in sentence and a
    two-line display equation) was 1,239 characters; Kokoro takes at most 1,200 per request, and
    the coordinator refused a longer passage with an error, so every Play stopped there. The
    reader's session shows it: the cached answer is 1,239 characters, no audio was written for the
    passage while interpretations ahead carried on, and the saved position stayed on it. A passage
    over the limit is now synthesised in pieces cut where a sentence ends (else a clause, a word,
    the limit) and joined into one clip, with the pieces' timings moved into place
    (`SynthesizeWithinLimitAsync`, `SynthesisPieces`). Covered by a Domain test with a fake
    provider limited to 150 characters and a test of the cutting. *Not verified* end to end:
    reproducing it needs the same interpretation, whose cache key depends on how the passages
    before it were interpreted, so a run without the reader's API key fell back to the printed
    sentence (361 characters) and read through; calling the model was avoided. Seen while trying:
    two instances at once (the reader's and a test one) exhausted the 8 GB GPU at start
    (`VK_ERROR_OUT_OF_DEVICE_MEMORY`); one alone settled at about 2.2 GB with no error.
79. **Opening and closing follow RailReader2** (the third GUI change). A welcome screen when
    nothing is open: a Rail Narrator mark drawn in the style of RailReader2's (its rail track, with
    a speaker in a ring where its magnifier is; also the window and launcher icon, now shipped in
    the release archive), Open a PDF…, and the recent documents as cards with a first-page
    thumbnail, how far through and when (`RecentDocumentsStore`, `$XDG_DATA_HOME/rail-narrator/recent.json`;
    thumbnails rendered once per document off the UI thread). File ▸ Open recent and Close
    (Ctrl+W, back to the welcome screen after the position is saved); Help ▸ Keyboard shortcuts
    (F1) and About; View ▸ Go to page (Ctrl+G), with RailReader2's dialog keyboard handling. A
    splash while the services are put together, now off the UI thread, and a window saying why if
    they cannot be. Page and zoom controls are disabled with nothing open. Checked under Xvfb: the
    splash; the welcome screen with a recent card and its thumbnail after Ctrl+W; reopening from
    the card; F1 (closed by Escape); Go to page 4 by typing and Enter; About. *Not verified:* the
    start-up error window (no failure was provoked), the recent menu (menus draw black under
    Xvfb), on the reader's desktop, and the mark at small icon sizes.
80. **RailReaderCore 0.93.0** (ADR-0001, amended). Core answered the two requests this project
    raised. **#223:** surveyed through the application's adapter with Heron INT8 on the CPU, OCR
    off, over the same 767 pages of 49 PDFs (`tools/RailNarrator.OrderSurvey`, Core's
    "after-below" measure: a block read after the nearest content block directly below it in its
    column), blocks out of order fell from 13 to 1 of 1,129 headings and from 107 to 6 of 7,141
    others; the case named in item 70 ("4.1. Regularity Conditions" before "4. ASYMPTOTIC
    PROPERTIES", linton-mammen-2005 page 14) is gone. What is left: a chart legend detected as a
    heading, two figures, and three textless boxes on one page. The survey's corpus is not item
    70's 18 papers, which were not recorded; the comparison is before and after on the same pages.
    **#222:** maths crops are rendered as a region, not a whole page at the crop's scale. Measured
    on a synthetic page, the region crop is the old one's size to within a pixel and its pixels
    within 8 grey levels on average, but not byte-identical, so maths answers cached against old
    crops are asked for again once (the reader's choice). A narrow equation's crop now gets the
    width asked for (1024 px where the whole-page cap left 828). Dependencies are unchanged; the
    Core version is in the extraction cache key, so extractions are redone. All 562 tests pass
    with the opt-in integrations on (Heron and PP-DocLayoutV3 on the CPU and the GPU, Kokoro on
    the CPU and WebGPU, MFA). *Not verified:* listened to in the application, and the new crops'
    effect on the model's answers.
81. **Phonon as a second speech engine** (ADR-0009). `PhononTtsProvider` drives Gradium's C
    library (`libptts_ffi.so`, xn-ptts v0.7.1) in process; `tools/setup-phonon.sh` builds it from a
    pinned commit and fetches `phonon-31m-en` and 25 English voices, each checked by SHA-256.
    `SpeechProviderId` `auto` (the new default) picks Phonon when installed, else Kokoro; each
    engine keeps its own voice, Phonon's defaulting to Maeve. Measured on the i7-12700H over 40
    passages of `nbag025.pdf`: real-time factor 0.061 (Kokoro 0.224 on the CPU, 0.258 on WebGPU),
    first audio after 32 ms, MFA timing every word of 34 passages (Kokoro 35). The opt-in tests
    pass against the real library. In the application under Xvfb, with Phonon chosen and a Kokoro
    voice in the copied settings: Maeve shown and spoken, audio at the null sink 1.3 s after Play,
    9 of the first 11 passages word-timed and highlighted word by word with the camera following,
    the front matter (a DOI) highlighted whole. *Not verified:* listening at length on the reader's
    desktop, the release archive with the library bundled (the build step was run alone, not the
    whole script), and GPU builds of the library (not attempted beyond a WebGPU build that still
    chose the CPU). Found on the way: the Settings dialogue saved ZONOS3 as `zonos3`, an id no
    provider has, so choosing it fell back to another engine; it now saves `zonos3-mini`, and a
    saved `zonos3` is read as that.
82. **The reader's decisions on DOIs, affiliations and spoken maths** (2026-11-19).
    - **DOIs** are read as "DOI link" (`Dois`, in `ExpandForSpeech`), mapped to the whole printed
      identifier so the highlight covers it.
    - **Affiliations** and the correspondence address on the article's first page are skipped
      line by line (`Affiliations`, setting `SkipAffiliations`): a block can hold the title or
      the authors as well. On `nbag025.pdf` with Heron the plan now opens with the title, the
      authors and the abstract; the correspondence address (a text block under Heron) and the
      DOI, which Heron puts in a page header, are gone.
      `ProsePipeline` → `prose/9-doi-affiliations`, so plans and audio are made again.
    - **Spoken maths** (prompt `math-interpret/4`): plain language first, notation allowed when
      it is written as spoken words ("sigma t squared", "x sub t"), named once beside its meaning
      or read out when the model is unsure of the meaning; brief (one or two sentences for a
      display equation; the length cap is now three times the passage's text, at least 500
      characters). The validator still refuses written notation and glued letters, and now
      refuses an answer that is mostly spoken notation (over 35 % of its words, at least four).
      Cached interpretations are asked for again.
    *Not verified:* the new prompt against the model (no endpoint calls were made), and
    listening.

83. **RailReaderCore 0.94.0** (ADR-0001, amended). The seven packages moved from 0.93.0, and
    `RailReader.Core.Vlm.Anthropic` was added. 0.94.0 changes nothing this application already
    used: its VLM block crops became regions (#228; not called here), and it gained a client for
    Claude (item 84). The packages' third-party dependencies are the same as 0.93.0's. Measured
    2026-11-19: the build has 0 warnings; the order survey (Heron INT8 on the CPU, OCR off) gives
    the same output on both versions, finding for finding (1 of 94 headings and 0 of 762 other
    blocks read after the block below, over 92 pages of five econometrics papers); the Pdf suite
    passes with Heron and PP-DocLayoutV3 on, on the CPU and on the Radeon 8060S through WebGPU.
    The survey covers fewer pages than item 80's 767: the 44 RailDLA papers were not on the
    machine. *Not run against 0.94.0:* real Kokoro, MFA and the audio device, which do not touch
    Core.
84. **Claude as an option for maths** (ADR-0010; the reader's decision of 2026-11-19). Each maths
    endpoint has a choice of service in Settings ▸ Maths (`MathLlmProvider`, `VlmProvider`):
    an OpenAI-compatible endpoint, as before and by default, or Claude through Anthropic's API.
    With Claude, interpretation, the reading by rule and verbalisation go through
    `ClaudeMathInterpreter` and `ClaudeMathVerbaliser` (Anthropic's .NET SDK), and the vision
    transcriber through Core's `AnthropicVlmClient`. The prompts, validators and the reading by
    rule are the ones the other endpoint uses. The address is optional, an empty model means
    `claude-opus-5-5`, and a key not given here is found by the SDK. The thinking policy sets the
    effort (medium or low). A passage Claude declines is rejected, after Anthropic's own
    fallback model has had it where that is offered. `rail-narrator-mathbench --provider claude`
    measures it on a document. Verified by 19 tests with the SDK's transport stubbed, so the
    requests checked are the ones the SDK sends. Seen in the running application (headless
    compositor, isolated settings): with Claude chosen and no key the application starts as
    usual, Settings ▸ Maths shows the Service row with Claude's placeholders, and Test, which
    asks Anthropic's real Models API, reports "api.anthropic.com wants an API key, and none was
    found".
    Run against Anthropic's real API with `claude-opus-5-5` (five opt-in tests,
    `RN_CLAUDE_INTEGRATION=1`, 2026-11-19): an equation drawn as a page crop interpreted from its
    image ("The response y equals the intercept a, plus the slope b times the regressor x, plus
    the error e.", about 1 s), the same crop transcribed and read by rule ("y equals a plus b x
    plus e"), an expression verbalised through structured output ("beta hat equals the inverse
    of the quantity X transpose X, times X transpose, times y."), Core's client transcribing the
    crop, and the Settings test finding the model. No optional field was refused: effort, the
    refusal fallback and the response schema went as sent.
    *Not verified:* Claude on a real paper: how its interpretations read against the local
    model's, and what a passage costs in time and money (`rail-narrator-mathbench --provider
    claude`). Not listened to. The vision model's Service row (advanced settings) uses the same
    editor and was not opened.
85. **A formula model for display equations** (ADR-0011; the reader's request of 2026-11-20).
    PP-FormulaNet_plus-M (Baidu, Apache-2.0; OpenDataLab's ONNX export, 591 MB, with a 2 MB
    vocabulary file) runs in process on ONNX Runtime, on the CPU, when its two files are installed
    (Models and endpoints ▸ Download formula model) and `UseFormulaModel` is on. It transcribes
    each display equation from a tight crop of its own. Settings ▸ Maths ▸ Formula model chooses
    what the language model is given (`MathLlmEvidence`): the page image, the LaTeX, or both (the
    default). A refused equation is read by rule from the LaTeX; with no language model, display
    equations are read by rule during playback; in the older reading modes it is the transcriber
    when no vision model is configured. Its LaTeX is compared with the page's text layer: a Greek
    letter taken for another is settled by the page, any other letter difference or a decimal
    number the page has and the transcription lacks is noted, and such a transcription is not
    read by rule. `rail-narrator-mathbench --formula-model` measures it.
    Verified by 63 tests (32 Domain, 29 Inference, 2 Infrastructure) and 2 opt-in ones with the
    real model, which read "x = y + 2" drawn with Skia. Measured 2026-11-20 on the GARCH paper
    (80 display equations as PP-DocLayoutV3 finds them, eight threads):
    - *The image the model is given* equals the reference pipeline's byte for byte on 260 images
      (Pillow 11.3 and 12.0), and the port's LaTeX equals the Python reference's on all 80 crops.
    - *Speed and memory.* 72 s for the 80 equations, median 0.89 s, longest 2.0 s; 1.14 GB
      resident for the recogniser alone.
    - *No language model* (`--no-llm`): 78 of 80 equation passages read by rule, 2 announced
      because the page has a number the transcription lacks; before, all 80 were announced. The
      check put zeta back for xi in 5 equations.
    - *The crop.* With the interpreter's 6 pt padding one equation ran to the 1,536-token limit
      and 34 of 80 transcriptions differed from the unpadded ones; with none above and below, no
      output failed its checks.
    - *Against the page.* 15 transcriptions compared by eye: 14 right (two after the check's
      correction), one missing a standard error printed under a coefficient, which the number
      check now flags.
    - *With the local language model* (pages 3 to 7, 85 passages): image only, 71 interpreted and
      14 by rule in 776 s. With both, stopped after 16 passages: all 16 interpreted against 12 of
      the same 16 with the image alone, in the same model time.
    Seen in the running application (headless compositor, isolated settings, the two files in its
    models directory): Settings ▸ Maths shows the Formula model section as installed with the
    evidence choice, and the notes say each passage is sent "as a page image, with the formula
    model's LaTeX for a display equation and the text around it".
    *Not verified:* a passage played in the application with the formula model (no speech engine
    was installed in that run; the resolver is the one the benchmark tool drives). The download
    through the menu (the pinned URLs answer with the manifest's sizes). The LaTeX-only setting
    against a model, a text-only model, and Claude with a formula. The other 65 transcriptions.
    Any other paper, and Heron as the layout model, the application's default when installed:
    its equation blocks may include the equation number. A GPU. Not listened to.
86. **Recent documents, model downloads and smaller UX fixes** (`fix/ux-recent-and-downloads`;
    the reader's report of 2026-11-20). A recent document could not be removed: the menu item's
    command said it could not run before its parameter was bound and never asked again, its
    binding did not resolve from the context menu's popup, and a missing file's card was a
    disabled button, which takes no right-click. Missing cards are now dimmed but live; opening
    one offers to remove it; the card menu and File ▸ Open recent also remove missing documents
    or clear the list (`RecentDocumentsStore.RemoveMissing`, `Clear`). The Models menu names a
    model already in place ("Kokoro speech model: installed", checked by the file's presence
    each time the menu opens, not its digest) and disables its download; a download states its
    size, licence and host and starts only when the reader agrees. Also: File ▸ Reload
    (Ctrl+Shift+R), which applies maths region corrections, now kept only for the document they
    were made in; Maths ▸ Mark this passage as maths / prose as commands, disabled with no
    passage; the maths review stays on a passage while its correction is being typed; Escape
    resumes following after browsing, else pauses, wherever focus is; the arrow keys, Home and
    End move about the page; zoom, Go to page, Close and Reload disabled with nothing open;
    Maths and Models no longer share an access key; underscores kept in recent file names in the
    menu; status colours from theme brushes readable in both themes; a load refused because
    another task is running says so. Verified by one new test (`RecentDocumentsStoreTests`).
    Seen in the running application (headless compositor, isolated settings): the card menu on a
    missing file with Remove enabled, and the file gone from `recent.json` after it; the Models
    menu with a placeholder Kokoro file shown as installed and fitting its width; a download
    asking first and fetching nothing; Close and Reload enabled after a load; File ▸ Open recent.
    *Not verified:* the keyboard changes, the region-correction commands and the held review in
    the window; the colours in the dark theme; on the reader's desktop.

87. **A nearly empty page is no longer recognised** (`fix/sparse-page-ocr-ink-outside-text`; the
    reader's decision of 2026-11-23). A page with under 0.5 % glyph coverage and under 60
    characters was OCR'd as well as read, to catch a scan with a stray text layer, at about 20 s a
    page. It is now recognised only when at least 0.1 % of its raster is ink that no character
    box covers (`PageInk.FractionOutsideText`: ink is a pixel more than 64 of 255 from the
    commonest brightness, and each box is grown by 0.3 of its height). The raster is the one
    rendered for layout, now rendered before the text-layer decision. `ExtractorVersions.Current`
    is `extraction/4`, so cached extractions are redone. Verified by six tests
    (`SparsePageOcrTests`) with a stand-in for recognition: a page holding one short line is not
    recognised, and a page of drawn bars with one line of text is. The 0.1 % floor is arithmetic
    (one line of sixty 10 pt characters; a hairline rule across the page is about half of it),
    not a measurement on scans; the real OCR engine was not run.

88. **Settings opens where it was asked for, Ctrl+Q quits, and a report about a later passage no
    longer disables Pause** (`fix/settings-page-pause-quit`;
    the reader's report of 2026-11-23). Models and endpoints ▸ Endpoints and API keys… opened the
    Settings dialogue on its first page; it now opens on Maths, where the endpoints and keys are,
    and the "Speech settings…" action of a speech-engine notice opens Speech
    (`MainViewModel.SettingsRequested` carries the page; an open dialogue is turned to it). Ctrl+Q
    had no binding; it now closes the window as File ▸ Exit does, saving the position, and is
    shown beside Exit and in the shortcut list. Seen on a virtual display (Xvfb, input by XTEST):
    the menu item showed the Maths page, and the process ended after Ctrl+Q.
    The third report: the toolbar's Pause did nothing and the reading went on. The button chose
    its action from the snapshot's state, which is the last thing reported; when speech for a
    passage ahead fails while the current one is read, the state becomes Error, the button offers
    Play, and Play is ignored while audio plays. The snapshot now also says whether the transport
    is playing (`PlaybackSnapshot.TransportPlaying`, `IsAudible`), and the button, Escape and the
    outline go by that. Verified by one test (`AFailureAhead_DoesNotHideThatTheCurrentPassageIsPlaying`),
    which failed before the change. Not confirmed as what the reader met: it was not reproduced
    in the application, and why it should go with whole-sentence highlighting is not known. The
    status line shows the error when this is the cause.

89. **The formula model under Heron, on six papers, and the three evidence settings**
    (`feat/formula-model-heron`; measured 2026-11-23 on the laptop, i7-12700H, Heron INT8 on the
    CPU, `rail-narrator-mathbench`, which now takes `--layout`, `--api-key-env` and
    `--dump-crops`). The paper is `nbag025.pdf` (26 pages; the paper of item 85 is not on this
    machine, so its numbers are not comparable).
    - *Heron puts the equation number inside the equation's block.* With `--no-llm`, 12 of the
      paper's 21 equation blocks came back with the number in the LaTeX ("\eqno(1.1)", read as
      "eqno one one"; "(2.2)" in the middle of an equation), the number narrowed the formula in
      its image, and one equation was held back because "the page's text has the number 2.4".
      `EquationLabel` now leaves a parenthesised number out of a display equation when it stands
      at the block's right or left edge with clear space beside it; the unit's text, its
      highlight and the formula model's crop are then without it, as with PP-DocLayoutV3. After:
      no number in any of the six papers' transcriptions, and 20 of 21 equations read by rule
      (19 before). Seen in two crops before and after.
    - *Heron's blocks are larger.* It finds the same equations as PP-DocLayoutV3 in 21 blocks
      where that model makes 32. One block of five stacked equations runs past the formula
      model's token limit (unbalanced braces) and is announced; PP-DocLayoutV3 reads all 32.
      Not fixed.
    - *Other papers, no language model* (equations read by rule / found): XGBoost (ACM, two
      columns) 13/13; a solar position algorithm (Elsevier) 47/48; Dynamic Equicorrelation (JBES)
      23/27; latent-variable graphical models (Annals of Statistics, pages 1 to 12) 14/16;
      covariance change points (pages 1 to 10) 19/20. Two fixes came from them: Elsevier's older
      fonts give parentheses as "ð" and "Þ", so a label is "ð13Þ" (21 numbers were left in before);
      and Computer Modern's epsilon arrives in the text layer as "ǫ", which held back 8 of 20
      equations of one paper for "having epsilon, which the page's text does not". Still held
      back wrongly: two equations whose text layer lacks a capital Theta or Phi that is on the
      page.
    - *Against the page.* 25 transcriptions compared by eye, five from each of those papers: 23
      right. One read kappa as R, which the text-layer check caught. One read "ΔT" as "ΛT",
      which it could not: the text layer gives that Delta as "D".
    - *The rule reader* read "\mathsf{d i m}", "\mathsf{a r g m a x}" and "\mathsf{R M S E}"
      letter by letter; names in a sans-serif, italic or typewriter face are now words, as
      \mathrm's were (`LocalLatexSpeaker` clearspeak/4).
    - *The three evidence settings with a language model* (qwen4-max on a remote endpoint, pages 3
      to 7, 47 passages, 8 of them with a display equation). All 47 were interpreted under each
      setting: image only in 289 s, both in 412 s (106 s of it the formula model, slowed by
      another job on the machine), LaTeX only in 275 s (89 s). The eight equation passages read
      side by side: with the image alone one reading said "the absolute value of omega" where
      the page has omega; with the LaTeX, alone or with the image, it was right. With the LaTeX
      alone one answer left out the sentence leading into its equation. `Both` stays the
      default on that evidence, which is eight equations of one paper with one model.
    Verified by 18 tests (15 Domain, 3 Inference).
    *Not verified:* a text-only model; Claude; a pure-mathematics or physics paper; the other
    transcriptions; any of it listened to. The label rule on a paper that numbers on the left.

90. **Letter names for the speech engine, and notation set solid in prose** (the reader's report
    of 2026-11-23: "Y sub t+h" heard as "yeeth", "a" as "ahh"). Three changes. A token such as
    "Yt+h" or "Yt+h|Ft" in a sentence (a text layer has no subscripts) now marks the sentence as
    having maths, so it is interpreted when a model is configured; read as printed it is said
    piece by piece ("Y t plus h given F t"), each piece keeping its source; and the engine is
    given a lone letter as its name ("eh sub tee", "why", "aitch"; `LetterNames`, applied in the
    coordinator at synthesis for English), while the script shown, the source links and the
    aligner's words stay as written. "a" is a symbol only where its neighbours say so ("a sub
    t", "plus a", "where a is"), "I" and abbreviations are left alone.
    Measured by synthesising sentences with Phonon (Maeve) and Kokoro (bf_isabella) and
    transcribing them with Whisper small.en, which is evidence about pronunciation and not a
    listener. Phonon: "Yt+h|Ft" was heard as "Yeet plus Hates" and spelled out as "yt plus h
    given ft"; "a sub t" as "interceptor subtit" and with the name as "A sub t"; a lone "y" once
    as "ui", a lone "h" once as "8", a lone "e" as "A fur", each right with its name. Kokoro:
    the article for "a" likewise; it reads "ay" and "ey" as "eye" and a capital "A" as the
    article, and "eh" as the letter, which is why "eh" is the name used. Sentences already
    heard correctly stayed correct with names on both engines (two of 24 comparisons were
    worse with names on one engine: "aitch" heard as "ih" and "eff" as "of", once each on
    Phonon).
    Verified by 29 tests. Heard by the reader in the application on 2026-11-23: "a lot better".
    *Not verified:* ZONOS3; "aitch" and "eff" singled out by ear. The prose
    pipeline's version moved (`prose/10-equation-labels`), so plans and audio are made again.

91. **A notice when the maths endpoint cannot be asked, and the mark's track unbroken**
    (`feat/maths-endpoint-notice`; the reader's report of 2026-11-23). With the wrong key for the
    endpoint, every equation was read by rule and the only sign was a note on each passage; the
    reader took it for worse interpretations. The interpreters now say why they cannot be asked
    (`IMathInterpreter.UnavailableReason`, `MathInterpretationResult.EndpointProblem`: a refused
    key, an exhausted quota or rate limit, an unknown address or model, a server error, no
    answer), in words made from the status code and the host and never from the response body,
    which can hold part of the key. `MathInterpretationResolver.EndpointProblem` reports it for
    each passage, and the window shows it once for each reason while it runs: a status-bar
    notice, cause first, with a Maths settings… action, and a line in Notes.
    Verified by 6 tests (2 Domain, 4 Inference). Seen in the application on a virtual display
    (Xvfb, isolated settings, two maths pages of `nbag025.pdf`, a key the endpoint refuses): the
    notice "Maths not interpreted: API key refused (401) by token-plan.maas.qwen…" with its
    button, before Play. *Not verified:* the button pressed; an exhausted quota or a dropped
    connection against a real endpoint (stubbed in tests); Claude's reasons, which reuse its
    existing wording and have no test of their own.
    The mark: its sleepers stopped short of the ring on both sides, which left bare rails and
    read as missing track. The track now runs from edge to edge with sleepers throughout, the
    ring stands on it behind a margin of paper, and the near rail passes in front. Seen on the
    welcome screen, where it is also drawn larger (150 px).

92. **A Claude address pasted as the whole route** (the reader's report of 2026-11-23). With the
    address "https://api.anthropic.com/v1/messages", as the API's curl example shows it, Settings ▸
    Maths ▸ Test answered that the model was not known. The SDK adds "/v1/messages" to the address
    itself, so every request went to ".../v1/messages/v1/…". `ClaudeEndpointSettings` now reduces
    the address to the API's root (a trailing "/v1/messages", "/v1" or "/messages" removed) and
    treats Anthropic's own address as none, so its model check and refusal fallback apply; the
    vision transcriber's address goes through the same rule.
    Verified by 7 tests. Against the real API with the reader's key (two model look-ups, no
    tokens): ".../v1/models/claude-opus-5-5" answers 200 and ".../v1/messages/v1/models/…" 404,
    which is the failure. The reader pressed Test with those settings after the change: it
    passes. *Not verified:* a passage interpreted by Claude after the change.

93. **Brackets and signs restored from older maths fonts** (`fix/legacy-font-encoding`). Some
    publishers' fonts (Oxford's and Elsevier's journals among them) put "(" and ")" in the text
    layer as "ð" and "Þ", "=" as "¼" and "+" as "þ". A sentence read as printed was then noise
    ("ri;j;t ¼ ɛ tanhðfi;j;tÞ"), it lacked the "=" that marks it as having maths, and the
    language model was given a garbled copy. `LegacyMathsEncoding` restores the four, one
    character for one so offsets and character boxes hold, and only in a document one of whose
    pages has "ð" and "Þ" standing as matched pairs (at least three, with few unmatched), as
    brackets do and as the letters in Icelandic do not. Extraction version 5.
    Verified by 7 tests. Measured with `rail-narrator-mathbench --no-llm` under Heron, lines of
    output still carrying one of the four characters, before and after: `nbag025.pdf` 126 and 0,
    the solar position paper 56 and 0, XGBoost (which does not use the encoding) 0 and 0. More
    sentences are recognised as having maths (94 to 108 pending passages, and 59 to 63), which a
    configured model is then asked about; equations read by rule are unchanged (20, 47, 13).
    *Not verified:* a document in Icelandic or Faroese with maths in such a font; the other
    substitutions of these fonts ("6¼" for "≠", "2" for "∈", "j" for "|", "ffiffiffi" under a
    root), which are not one character for one or are ambiguous, and are left.

94. **A narration saved with the document, and an audiobook from it** (ADR-0012,
    `feat/saved-narration`; the reader's request of 2026-11-23). File ▸ Save narration with
    document… and `rail-narrator narrate paper.pdf` interpret the maths, speak every passage and
    time its words, and write `paper.narration` beside the PDF. A document that has one is played
    from it: no language model and no speech engine is asked. File ▸ Export audiobook (M4B)… and
    `rail-narrator export paper.pdf` write an audiobook with a chapter at each heading (ffmpeg).
    Verified by 17 tests (15 Infrastructure, 2 Domain).
    Run for real without a window on two pages of `nbag025.pdf` (Heron, Phonon in Maeve, word
    alignment on): 51 passages and 9 min 2 s of audio in 9 min 35 s, a 2.7 MB file with 44 of 51
    passages word-timed, and a 4.2 MB M4B whose four chapters ffprobe lists at 0, 39.6, 60.6 and
    481.4 s under the document's name and its three headings. The maths endpoint was given a key
    it refuses: the run said so and saved 2 equations as read by rule and 8 sentences as
    printed, without counting the 8. That run is why the job now stops when the model cannot be
    asked and counts what is read as printed; the stop itself is covered by tests, not by a
    second real run. Seen in the application on a virtual display: the document opened with "Playing the
    narration saved with the document (Maeve, phonon)" and no maths notice, although the key was
    still refused; File showed the three new entries; Save narration asked first, showed "1 of
    51 passages, 14 s of audio" with Stop, and ended with "Narration saved beside the document
    (9 min, 2.7 MB)" and Reload to play from it.
    *Not verified:* a saved narration played aloud or listened to, in the application or as an
    M4B; maths interpreted by a model in a saved narration (the run used a refused key); a whole
    paper (an hour and more); Stop pressed during a narration; Export audiobook and "Narrate
    anyway" pressed in the window; Kokoro or ZONOS3; a machine with no engine installed at all.

95. **Word timing from the speech engine itself** (ADR-0013, `feat/engine-word-timing`; the
    reader's requests of 2026-11-23). Phonon's library, with one patch, reports the attention of
    the head that follows the text, and a word's time is read from it; Kokoro's export with a
    durations output gives each phoneme's length, and each word takes its phonemes'. Settings ▸
    Reading ▸ Word timing from chooses between the aligner (the default; the engine's timing is
    shown until the aligner's arrives) and the engine's own (the aligner then not run). Models and
    endpoints ▸ Download Kokoro with word timing fetches the Kokoro export.
    Verified by 22 tests (4 Domain, 18 Speech, the last including 3 pinned to real Phonon
    attention data) and 2 opt-in ones against the real models.
    Measured against MFA on the laptop (ADR-0013 has the table): Phonon word starts a mean 30 ms
    and a median 24 ms off on 505 words, 98 % within 100 ms; Kokoro 16 ms and 13 ms on 376 words,
    all within 105 ms. Each engine timed all 35 test passages. Phonon's audio is the same with the
    patch (identical sample counts) and no slower (3.49 s against 3.57 s for a passage).
    `tools/setup-phonon.sh` applied the patch to a clean clone and built the library (2 min), and
    both it and the unpatched library already installed pass the provider's tests.
    Whole-document narration of two pages without a window, engine timing, no aligner run for a
    timed passage: Phonon 4 min 4 s (9 min 35 s with the aligner), 46 of 51 passages word-timed,
    44 from attention and 2 by the aligner after the engine declined; Kokoro with its timestamped
    export 13 min 28 s (its synthesis runs at about real time on this laptop), 44 of 51, 42 from
    durations and 2 by the aligner. The passages neither engine timed are those whose text the
    engine's own preparation or phonemiser changed ("r1", "ɛ").
    Seen on a virtual display: Settings ▸ Reading shows "Word timing from" with the two choices
    and their descriptions, and the Models menu offers "Download Kokoro with word timing
    (163.2 MB)…".
    *Not verified:* the highlight moving with the words, seen or listened to in the
    application; the Settings choice changed and the download pressed in the window; a whole paper; the
    reader's own install, which keeps the unpatched library until `tools/setup-phonon.sh` is run
    again (its audio cache is then made anew, the library being part of the key); Kokoro's
    timestamped export on the GPU; any model but `phonon-31m-en` and Kokoro-82M v1.0.

96. **Where a narration's time goes, and two things taken out of it** (`fix/narration-performance`,
    2026-11-23). `rail-narrator narrate` now ends with its time by stage. On the two test pages
    with Phonon and the engine's own word timing (9 min of audio, speech and alignment caches
    cold, the build as merged): 202 s in all, 173 s interpreting and speaking, 12 s waiting for
    the aligner (its start, now made when the first of the five passages the engine declined
    needs it, and those five alignments; its worker started in a few seconds in this run, not the
    23 to 56 s measured elsewhere), 17 s encoding and writing. Runs of the same on the two builds
    before it gave 194 s and 189 s: the differences are within what this machine varies by. Speaking is the engine's own speed and nothing
    more: Phonon alone measured 0.30 of real time in the same hour, and 0.30 × 543 s is 163 s.
    - *The laptop was power-limited when this was measured.* Under a four-core load its cores ran
      at 1.2 to 1.4 GHz of 4.6 GHz, and Phonon at 0.30 of real time against the 0.061 measured on
      this machine on 2026-11-19 (ADR-0009). When that began is not known, so the absolute times
      of items 94 and 95, measured the same day, may be several times what an unthrottled run
      gives; their ratios and the accuracy figures do not depend on it.
    - *Speaking several passages at once gave nothing here*: one to six model instances, with 4
      to 12 threads between them, all came to 0.31 to 0.37 of real time on 60 s of audio, which is
      what a power cap does. Whether it helps on a machine that is not capped was not measurable.
    - *The codec is given 16-bit samples.* Concentus works in fixed point and its float entry
      points convert both ways. On 22 s of speech, second pass: encoding 51× real time against
      19×, decoding about 300× against 81×, the same bit rate. Files already written decode as
      before. ADR-0012's earlier figures (2.9 s and 1.8 s for a 30 s tone) were through the float
      entry points and included the first call's compilation.
    - *The aligner is no longer started at load when the engine times its own words* and the
      reader prefers that: its start-up is processor that the speech would be waiting behind. It
      is started by the first passage that needs it, and awaited before that passage is aligned,
      so an alignment's time limit is not spent on the start; while it starts, the lookahead
      prepares the passages ahead without waiting for it. A code review of the first version of
      this found that it did neither, and that Kokoro on the GPU reported the durations of a model
      it was not running; both are corrected (`TtsCapabilities.NativeTiming` is the timing of the
      runner in use).
    Verified by the existing suites, two added assertions and two added tests (the passages
    ahead prepared while the aligner starts, and no alignment asked before the start is over).
    Not measured: any gain in a whole narration from the two changes, which is inside this
    machine's run-to-run variation; the reading in the window when a passage is declined.
    A second pass over the same code, for what a whole narration waits on:
    - *Maths was interpreted ahead only for the first 40 passages of a narration.* The window
      follows the reading position, which a narration never moved, so every later model call was
      waited for with the engine idle. The position now moves with the narration. A passage
      interpreted ahead was also asked about a second time by a caller holding the plan as it
      was (the lookahead, in reading as well as narrating; the disk cache hid it in the
      application): it now gets the answer already there. One test, which failed on both counts
      before. Not measured with a real model: with one, a narration should take about the longer
      of its model time and its speaking time, not their sum.
    - *Encoding and writing go on beside the speaking of the next passage* (a queue four deep),
      so the engine does not wait for the file.
    - *An audiobook is decoded once, not twice*: chapter times come from what is recorded about
      each clip. The same 9 min exported in 9.1 s (twice) against 11.5 s.
    - *Opening a document with a saved narration no longer builds reading units* that the saved
      plan replaces. Extraction still runs, for the page's text and geometry.
    - Kokoro's audio is copied out of the runtime's tensor in one piece.
    With the speech cache warm, saving the two pages again took 16 s (10 s of it encoding). An audiobook of the same 9 min was
    exported in 11.5 s.

97. **The day's features driven in the window, with the reader's key** (`fix/gui-test-findings`,
    2026-11-23; with the reader's leave, Claude `claude-opus-5-5` through Anthropic's API with the
    address as the reader typed it, "…/v1/messages"). On a virtual display (Xvfb, input by XTEST),
    an isolated profile copied from the reader's settings, the patched Phonon library, and audio
    sent to a null sink and recorded from it, so nothing was heard: what follows was seen in
    screenshots and measured in recordings, not listened to. The machine was still
    power-limited (item 96). Two pages of `nbag025.pdf`, 51 passages, 10 with maths.
    - *A narration without a window*: all 10 maths passages interpreted by Claude, 41 of 51
      passages word-timed by Phonon's attention (the interpreted ones are not timed by word), in
      180 s, which is the speaking time alone (173 to 202 s in item 96). With the speech cached
      and the interpretations cleared it took 80 s, so the model's time, about 70 s, was spent
      beside the speaking and not after it: the interpret-ahead change of item 96, measured.
    - *Reading*: Play gave sound at the sink within two seconds and Pause stopped it (19 s of
      speech, then silence, in the recording); words were highlighted one at a time from
      Phonon's own timing with no aligner run; a passage Claude interpreted was highlighted whole
      in violet while it played; Ctrl+Q ended the process and the position was saved.
    - *Models and endpoints ▸ Test text LLM endpoint*: "The text LLM endpoint in use responded."
    - *File ▸ Save narration with document…*: the question, progress with Stop, "Narration saved
      beside the document (9 min, 2.8 MB)", Reload to play from it; after the reload "Playing the
      narration saved with the document", sound and word highlighting from the file. Stop during
      a second narration: "Narration stopped", and the saved file's time unchanged.
    - *File ▸ Export audiobook (M4B)…*: "Audiobook saved as maths.m4b beside the document
      (4.3 MB, 4 chapters)"; ffprobe lists the four chapters and 531.7 s.
    - *A key the endpoint refuses*: the notice "Maths not interpreted: Claude refused the API
      key, or none is set." before Play; its button opened Settings on Maths; Save narration
      stopped with the reason and no file, and Narrate anyway saved one.
    - *Settings ▸ Reading ▸ Word timing from*: the list, the choice written to the settings file
      (mode 0600), and the restart line.
    - *Kokoro*: Download Kokoro with word timing asked first (163 MB, Apache-2.0, huggingface.co),
      fetched the file with the manifest's digest into the profile's models directory and the
      menu then said installed; with the engine's timing chosen it was used without a path being
      set, words were highlighted one at a time, and 13 of 15 clips made were stored with
      `NativeDuration` timing from `kokoro-session`.
    Two defects found and fixed:
    - *The line under a list in Settings described the choice before.* Every list whose
      description is bound as `Choice.Description` (nine of them) saved the choice without saying
      that the property changed. Seen fixed in the window.
    - *A narration saved after a reading had fallen back on its maths under-counted it.* The
      count of maths read as printed was taken from what the plan still had pending, and the
      reading in the window had already fallen back on seven of ten passages, so the file said 4
      not interpreted. It is now told from the passages themselves (10 in the same run: 2 by rule,
      8 as printed), and the question before a narration says how many passages of this reading
      were not interpreted and that reloading tries them again.
    *Not verified:* by ear, anything; that the highlighted word is the word sounding, beyond its
    being the right word for the time elapsed in the screenshots; the reader's own install,
    which still has the unpatched Phonon library; a whole paper.

98. **Phonon's word timing in the window, measured against the sound** (`fix/gui-test-findings`,
    2026-11-23, after `tools/setup-phonon.sh` rebuilt the reader's own library with the timing
    patch; `PhononProviderTests` pass against that install, the real model timing every word).
    Same rig as item 97 (virtual display, silent sink recorded with wall-clock stamps, nothing
    heard), the same two pages, `WordTiming` `Engine`, speech made afresh by the rebuilt library.
    - *Every passage read as printed was timed by the engine*: the clips of the first passages
      were stored `ModelAttention` with a cue for every word, and no aligner ran.
    - *The words agree with the aligner's.* The same clips were then aligned by MFA: over 190
      words Phonon's starts are a median 9 ms after the aligner's, median difference either way
      23 ms, 90th percentile 64 ms.
    - *The application takes up a word when it sounds.* From the application's own log against
      the recording, 61 of 61 words of the first four passages were taken up a median 72 ms after
      their sound reached the sink (10th to 90th percentile, 7 ms before to 164 ms after), on a
      rig drawing a frame every 241 ms: the reading is polled once a frame, so this is within a
      frame, not an offset. The same figure was got with the aligner's timing.
    - *The playback clock is right.* `rail-narrator-smoke audio --clicks` (new: a 10 ms tone each
      second, and the time the device says each was presented) against the same recording: the
      device's presentation time is 3 to 5 ms before the tone's arrival at the sink's monitor,
      14 clicks of 14.
    *Not verified:* what is seen on the reader's screen. On the rig the highlight on screen
    trailed the sound by about 0.7 s (frame-by-frame capture, the same with the aligner's
    timing), which is its four frames a second: a virtual display (Xvfb) is drawn in software whatever GPU
    the machine has, on a processor held near 1.1 GHz while the laptop is fed 67.5 W (15 V, 4.5 A)
    by a monitor over USB-C. It
    says nothing of a display drawing sixty; nor anything after the sink (the output hardware's
    own delay, which a monitor cannot see); nor by ear.

99. **Four honours reports narrated to audiobooks, and what they found** (`fix/gui-test-findings`,
    2026-11-23; `rail-narrator narrate … --m4b` with the reader's settings: Phonon with its own
    word timing, Heron, the formula model, Claude `claude-opus-5-5` for the maths; the laptop on
    its own charger, so not power-limited as in items 96 to 98). 248 pages:

    | Document | Pages | Passages | Audio | Chapters | Maths interpreted | Narrating |
    |---|---|---|---|---|---|---|
    | Potsane | 49 | 811 | 106 min | 58 | 128 | 629 s |
    | Nqophiso | 60 | 871 | 123 min | 74 | 94 | 734 s |
    | Lekala | 61 | 582 | 78 min | 60 | 18 | 464 s |
    | Clustering daily solar irradiance | 78 | 879 | 113 min | 51 | 160 | 12 min |

    No maths passage was read by rule, as printed or announced unreadable; each audiobook's
    length equals its narration's to the millisecond. Speech and interpretation together ran at
    about ten times real time. The times are of the second run, with the interpretations of the
    first already cached for the first three.
    - *Checked, the scripts*: read through for each (headings in order, references skipped,
      tables and code listings announced and not read, no passage over 900 characters), and a
      sample of the model's readings against the source text of their equations in two of them.
    - *Checked, the audio*: 30 passages of each audiobook chosen at random (a third of them
      maths readings), cut from the M4B at the passage's place, recognised by Whisper small.en
      and compared with the script: word error rate 2.7 %, 3.7 %, 2.7 % and 4.1 %, most of it the
      comparison's ("50Hz" for "fifty Hz", "4.3.1" for the words). A passage is therefore where the
      file says it is and says what the script says.
    Four defects found and fixed (`NarrationVersions.ProsePipeline` → `prose/11-contents-section-numbers`):
    - *A document that could not be planned.* A low surrogate standing alone in the text layer
      made `MathsLetters.At` throw, and the fourth document stopped before speaking.
    - *Contents were read aloud*: a title, a row of dots and a page number, for two pages or
      more of each report. `ContentsPages` now skips a page of entries led by dots to a page
      number, a front-matter page of entries that end in rising page numbers with no leaders in
      the text layer, the same lines where a few run on to another page, and a list of figures or
      tables under its heading (over the page only through blocks that still end in a page number
      or hold several "Figure N:" entries). A list of abbreviations is still read.
    - *Section numbers*: "3.2.1" was read "three point two.1" and a heading's "3.10" as "three
      point one zero"; a number of three parts or more, and the number that opens a heading, are
      read part by part.
    *Seen and left:* an acronym is said as the engine guesses ("SBHAR"); Phonon repeated the last
    words of a passage in 2 of some 165 sampled; a word hyphenated over a line in the PDF's own
    text loses its hyphen ("smartphonebased"). *Not verified:* by ear; the passages not sampled;
    the model's maths readings beyond the sample.

100. **The reader's annotations kept and drawn** (ADR-0014, `feat/review-annotations-store`,
    2026-11-24; phase 1 of the review-annotation work). Annotations are kept in
    `$XDG_DATA_HOME/rail-narrator/annotations/<sha256>/` in RailReaderCore's JSON format, with Rail
    Narrator's links to the narration beside them, and drawn under the narration highlight by
    Core's renderer (View ▸ Show annotations, remembered). Nothing yet creates one: the tools
    come in the next phases.
    - *Checked, in the window* (headless compositor, isolated XDG directories, the GARCH paper,
      no layout model): a hand-written file with a highlight carrying a comment, an underline, a
      sticky note, a rectangle and a pen stroke, placed from the plan's own anchor rectangles,
      drawn over the words they were placed on at 286 % and 146 %, and in Core's dark-backdrop
      style under the high-visibility filter; turned off and on from the View menu; opening the
      document left the file's bytes unchanged.
    - *Checked, a damaged file*: set aside as `annotations.unreadable-<time>.json` and said in the
      status bar. **Found and fixed**: the notice was replaced at once by start-up's own (the
      speech engine not installed), so the reader never saw it. A notice about the reader's own
      work now stays until dismissed, and the others wait behind it.
    - **Found and fixed by a test**: a file the store may not read (permissions) threw
      `UnauthorizedAccessException`, which is not an `IOException`, and would have failed the
      document's load instead of being reported.
    *Not verified:* RailReader2 opening a file written here (the tests compare with Core's own
    writer and reader, not with the application); a save failing on a full disk.

101. **Pause, note what was just read, carry on** (ADR-0014, `feat/review-annotations-notes`,
    2026-11-24; phase 2 of the review-annotation work). N pauses and opens a note card on the
    sentence just read (Shift+N: the passage), outlined on the page; Enter saves and stays paused,
    Ctrl+Enter saves and reads on, Escape gives up. H highlights the sentence without stopping
    (Shift+H: the passage). A mark already on the same words is found again, not stacked. Ctrl+Z,
    Ctrl+Shift+Z (Ctrl+Y) and Delete act on annotations outside a text box. A Review notes section
    in the sidebar (the old notes section is now Diagnostics) lists every note in reading order;
    choosing one reads from it while playing, or shows its page and starts there at the next Play.
    A click on a note's mark on the page shows it and does not start reading; a double click edits.
    - *Checked, in the window* (headless compositor, isolated XDG directories, the GARCH paper with
      no layout model, Kokoro `kokoro-fp16.onnx` checked against the manifest's SHA-256, audio to
      a null sink): stopped, N on the first passage turned to its page, outlined it, and placed
      the card below it; a note typed with spaces did not toggle playback, and Enter saved it
      (file and list). Playing, N paused on the sentence being read; Enter left it paused;
      Ctrl+Enter on the next note saved it and read on. Undo and redo from the keyboard changed
      the saved file each time. After a restart the notes were back, and choosing the first in
      the list while playing moved the reading to its passage. A click on a note's mark selected
      it without moving the reading, a double click opened the editor, Delete removed it and
      Ctrl+Z restored it.
    - **Found and fixed in the window**: H on a highlighted sentence stacked a second, identical
      highlight; a quote kept the line-end hyphen ("vari- ance"); after Ctrl+Enter the saved note
      stayed selected and its popup covered the text being read.
    *Not verified:* a screen reader on the card and the list; a note on a sentence across a page
    break made in the window (tested only on synthetic geometry); word-level cues (the run had
    no aligner, so cues were passages and sentences).

102. **Annotate mode: text marked by hand** (ADR-0014, `feat/review-annotations-markup`, 2026-11-24;
    phase 3 of the review-annotation work). Ctrl+E (or the pencil) turns on annotate mode: a left
    drag over text highlights, underlines, strikes out or squiggles it with the chosen tool, a
    click selects an annotation, and a middle drag still pans; Escape leaves. Keys 1 to 5 choose
    one of Core's five colours, and recolour the selected annotation; each colour has a name the
    reader gives it (Settings ▸ Annotations, with the author name that signs new annotations).
    A drag starts only on a line of text and follows the nearest character; a mark made by hand
    is linked to the passage that reads it, so choosing it in Review notes reads from there.
    - *Checked, in the window* (headless compositor, isolated XDG directories, the GARCH paper with
      no layout model): a drag over two lines of the abstract highlighted exactly the dragged
      words, one box per line, quoted "…specification technique…" and linked to its passage; the
      underline tool on "For more than 30 years" made a green underline; Select, a click on the
      highlight and 3 made it red ("Error"); a middle drag panned; Escape left the mode and click
      to read came back. An author and a renamed colour set in Settings applied at once: the next
      highlight was signed, and the toolbar named the colour.
    - **Found and fixed in the window**: a dragged quote kept a ligature glyph the text layer left
      unmapped ("speci▯cation"): quotes now resolve ligatures and PDFium's line-end hyphen marker
      as reading units do, from the document's own inferred table; a colour chosen with the select
      tool also changed the highlight tool's colour.
    *Not verified:* a drag across a page break (a drag is on one page); squiggly and strikeout
    drawn by Core's renderer in the window (made and saved in the tests only); keyboard-only use of
    annotate mode.

103. **Sticky notes and text boxes on the page** (ADR-0014, `feat/review-annotations-notes-on-page`,
    2026-11-24; phase 4 of the review-annotation work). In annotate mode, the Note tool puts a
    sticky note where the page is clicked and the Text box tool writes in a box dragged out (or a
    default box at a click), both through the note card; the select tool drags a note, a text box
    (or, later, a drawing) to a new place as one undoable step (Core's `MoveAnnotationAction`), and
    a double click edits its words. View ▸ Expand all notes shows every note's text. Choosing a
    note in Review notes centres the view on it.
    - *Checked, in the window* (headless compositor, isolated XDG directories, the GARCH paper):
      a note placed in the right margin and a text box beside the keywords, both saved, listed and
      back after a restart; the note dragged 120 points down and Ctrl+Z putting it back; a double
      click on the text box opening "Edit the text" with its words; Expand all notes on and
      remembered; the note chosen in the list selected and brought into view.
    - **Found and fixed in the window**: from its second opening, the note card measured itself
      with the margin it had last been placed with (a control's desired size includes its margin),
      so it was pushed to the corner of the reading area, over the text box being edited.
    *Seen and left:* a note's text is drawn by Core to the right of its icon, so a note in the
    right margin shows its text cut off by the view while the page is fitted to the width (the
    list shows it whole). *Not verified:* a text box's text wrapping at small sizes; moving a note
    off the page's edge (not prevented).

104. **Pen, box and eraser** (ADR-0014, `feat/review-annotations-drawing`, 2026-11-24; phase 5 of
    the review-annotation work). In annotate mode the Pen draws freehand, Box drags out an
    unfilled rectangle (round a figure, a table, an equation), and the Eraser removes what it
    touches (a stylus's eraser end does too, whatever the tool). Pen and Box have Core's three
    line thicknesses, remembered per tool; the select tool stretches a selected stroke by its
    handles (Core's rule: every point scaled, never smaller than 10 points), recorded with Core's
    `ResizeFreehandAction`. Each Review notes entry now names its kind ("Pen", "Box", …).
    - *Checked, in the window* (headless compositor, isolated XDG directories, the GARCH paper): a
      loop of 62 points round the keywords, selected with its handles after drawing; a box round
      the "1. Introduction" heading; the loop stretched by its bottom-right handle; the box erased
      and Ctrl+Z bringing it back; a thick box saved at width 4 and the thickness remembered; the
      loop as stretched still there after a restart.
    - **Found and fixed in the window**: a drawing's entry in Review notes said only its page.
    *Not verified:* a real stylus (its eraser end, pressure); erasing by dragging over several
    annotations in one stroke (each is its own undo step by design).

105. **The notes leave the application, and a PDF's own annotations come in** (ADR-0014,
    `feat/review-annotations-export`, 2026-11-24; phase 6 of the review-annotation work).
    Review ▸ Export copies or saves the notes as Markdown (by page and reading order, each with
    the text it is on, its colour's name and author; the PDF's own annotations after, quoted from
    the words under their boxes), saves an annotated copy of the PDF (Core's
    `AddAuthoredAnnotations` on the original's bytes, written to another path; never the PDF
    itself), and exports or imports Core's JSON for RailReader2 (an import merged by Core's
    `skipEquivalent` rule, one undoable step). The PDF's own annotations (a co-reviewer's) are
    read at open and shown read-only (View ▸ Show the PDF's own annotations): listed with their
    author, selectable, never moved, edited, erased, recoloured or saved.
    - *Checked, in the window*, with `reviewed.pdf`, a copy of the GARCH paper given a
      highlight, an underline, a strikeout, a squiggle and a sticky note by `railmark
      --apply-markup` (author "Co-reviewer"): all five drawn where railmark put them and listed
      with their notes and quotes; Delete, the eraser, a drag and a double click on them did
      nothing but say they are part of the PDF; the reader's own highlight, note and N-note made
      beside them. The Markdown read back from the clipboard of the nested display. The
      annotated copy saved through the file dialog, its source's SHA-256 unchanged, read back
      with all 8 annotations by Core and drawn by poppler (`pdftoppm`) with the co-reviewer's and
      the reader's both. The JSON exported, imported into the other copy of the paper (3 added),
      imported again (none added, "3 were already here"), and Ctrl+Z taking the import out.
    - **Found and fixed in the window**: the reader's colour names were given to another
      reviewer's annotations; a PDF's markup had no quote, and an underline or squiggle written as
      a 2-point strip at the baseline still had none after the first fix.
    - **Found by the wider GUI pass and fixed**: a note or highlight on what was just read was
      one box per word on a paper whose anchors are word-sized (34 quads for one title in the
      exported copy): now one band per line, as the narration highlight is drawn; a note half
      written when the document was closed was dropped with a message that the next one replaced
      at once: it is now kept, as Enter keeps it; a drag while the reading was followed could
      slide under the pointer: annotating holds the camera as browsing does.
    - *Checked by the same pass*: Ctrl+Z inside the note card undoes typing, not annotations;
      Escape in the card cancels without pausing; switching documents shows each one's own
      annotations only; under the high-visibility filter annotations use Core's dark-backdrop
      style; three 40-second plays with every annotation drawn and notes expanded showed 1 to 2
      frame gaps of 50–72 ms each, as with annotations hidden (two runs).
    - **The test harness's own finding**: a file dialog opened by the application under test
      appeared on the desktop of the machine's user, through xdg-desktop-portal on the session
      bus; the application is now driven under `dbus-run-session` for any test that opens one
      (`testing-real-world.md`).
    *Seen and left:* a drag starting inside a word starts there ("…ted GARCH"), as in RailReader2.
    *Not verified:* a digitally signed or password-protected PDF (this application opens neither
    kind with a password); RailReader2 opening the exported JSON (Core's reader does).

106. **Notes carried to a revised PDF** (ADR-0014, `feat/review-annotations-revised`, 2026-11-24;
    phase 7, the last of the review-annotation work). A document opened at a path that last had
    another hash with notes is offered them (a notice, and Review ▸ Bring notes from the earlier
    version); a revision under another name takes them from a chosen PDF (Review ▸ Bring notes
    from another version of this PDF…). A text markup is placed where its quoted words are found
    again (letters and digits compared, so hyphenation, spacing and punctuation do not matter;
    word boundaries first, then the occurrence nearest where it was); everything else is kept
    *not placed*, listed at the end of Review notes with *Place as before* and *Discard*, and in
    the Markdown under its own heading. One undoable step; the earlier version's files only read.
    - *Checked, in the window*, on the GARCH paper revised with `qpdf` (cover page and last page
      dropped, 27 → 25 pages) at the same path: the offer said 6 notes; brought over, the N
      highlight on the Introduction's first sentence and a hand mark ending mid-word ("…slow m")
      landed on the same words one page earlier (rectangles within 1–2 pt of the originals'); the
      pen stroke, two boxes and a note on the dropped references page were listed not placed.
      Discard and Place as before worked and Ctrl+Z undid a placement; a restart kept all of it
      and did not offer again; the earlier version's six annotations were unchanged on disk. A
      second revision at another path took the same notes through the file chooser, and choosing
      that version again added nothing.
    - **Found and fixed in the window**: *Place as before* put the pen circle on the earlier page
      number, one page late; it now uses the page that page's text went to, by the notes placed
      from it (only when they agree), and the list says "was on page 2, now page 1". The notices
      were too long for the status bar and are shorter.
    - **Found by a code review of the whole code base and fixed (same branch)**: a note
      across a page break was not found when a page number or running head stood between its
      halves (now matched first against the text the narration reads); copies kept the earlier
      ids, so bringing the same notes twice made two annotations under one /NM whose undo took
      the other's links (fresh ids now); a second carry-over moved notes waiting from the first to
      its own page map (each is now kept under the page it goes on); unplaced notes were deleted
      by the next save when `earlier.json` had been unreadable; reading the earlier version set
      its unreadable files aside and reported "no notes" (now `Peek`, which moves nothing and
      says why); the offer notice, which outlives its document, brought notes into whichever
      document was open; Place as before dropped a note's replies and could put a group's second
      page past the end. Also in the store and older than this branch: a document opened again in
      the same session was not saved until its edits outnumbered the first opening's, so they
      were lost on quit (each document instance's versions are now its own). Unit tests cover
      each.
    - **Found by a second review and fixed** (`fix/review-findings-carry-over-ux`): a short quote
      ("x", "the") found more than once was put on the nearest occurrence, which is placing by
      position (now: under 12 letters it must be found once, among whole-word occurrences if any,
      else it waits); a highlight across a caption or footnote between its halves marked the
      caption too (ranges now split where unread text lies between letters); a reply to a placed
      note was left unplaced and listed nowhere (replies now follow the note they answer and are
      not counted as notes); notes brought from a chosen PDF replaced the record of the earlier
      version at this path, so its offer disappeared (each document now keeps the versions it took
      notes from, and only the earlier version's offer is marked done); the Bring notes items
      could stay greyed out after a load (they are now refreshed with the other commands); the
      "no notes kept" message now says an exported annotated copy is another file.
    *Seen and left:* a note carried from N or H is boxed from the character boxes, so its band is
    1–2 pt shorter than the narration-style band it had; a sentence reworded by one word is not
    placed (by design).
    *Not verified:* a revision with edited text inside a page (the test PDFs differ by whole
    pages; the unit tests cover changed lines and hyphenation).

107. **Quick seeks left the reader in silence for up to 45 s.** Pressing Next (or Previous) many
   times quickly: each seek synthesised its own passage with the caller's token, which nothing
   cancels, so every seek overtaken by the next kept its passage queued at Phonon, which speaks one
   passage at a time. After 60 presses in 5 s on the GARCH paper the release build was silent for
   22 to 45 s ("Synthesising speech"), and an overtaken seek could also throw a cancellation at
   its caller. The wait for a passage is now cancelled when its generation is overtaken
   (`NarrationCoordinator.GenerationToken`), and an overtaken wait returns quietly. With real
   Phonon, the GARCH plan and the same 60 seeks: 2 passages spoken in full instead of about 47,
   playback again 0.85 s after the burst instead of 14.7 s
   (`ManyQuickSeeks_DropTheSynthesisOfThoseOvertaken`, which fails on the old coordinator with
   20 of 30 passages spoken).

108. **Changing the voice twice quickly stopped the reading.** The second change found the
   transport stopped by the first (which was waiting for its passage in the new voice), took that
   for "not reading", and did not start again; the first, overtaken, gave up its start. The status
   stayed at "Synthesising speech" with Cancel on the button until the reader pressed it and Play.
   Found by changing the voice three times in two seconds during playback. A start under way is now
   recorded for its generation (`_startPendingFor`, set by Play, a seek, a voice change while
   reading and a passage giving way to the next, cleared once the clip plays), and a voice change
   resumes reading when the transport is playing or filling or a start is under way
   (`QuickVoiceChangesWhileReading_CarryOnReadingInTheLastVoice`, which timed out on the old code;
   `AVoiceChosenWhileStopped_StartsNothing`).

109. **The smaller findings of the v0.1.1 stress run** (`fix/stress-test-findings`, 2026-11-25),
   each checked again in the release build under the nested display:
   - *A quick Play/Pause press was lost* (one in 41). A press decided from the last snapshot, which
     arrives after the transport has acted, so two presses 40-100 ms apart read the same state.
     A press now goes by what the previous one asked for until a snapshot shows it done (at most
     1.5 s). Three runs of 41 presses ended paused and two of 40 ended playing, every time.
   - *The Open dialogue was not modal*: the main window took Space, H, C and N while it was up.
     Keys and typed text are now held back while a file dialogue is open; with the dialogue up those
     four keys did nothing, and after it closed Space played.
   - *The section name lagged the reading.* An outline entry gives a page, not a position, so a
     heading half way down a page was named while the section before it was still read. On the
     page being read, a heading the narration has not reached is passed over, found in the plan by
     its words (`OutlineSections`, six tests). On the GARCH paper's page 10 the bar said 4.2 until
     passage 230 (the heading of 4.3) was read, then 4.3. The name is trimmed from the start, so
     the innermost heading stays visible ("4. Miss…ng the conditional variance component").
   - *The last word stayed highlighted after "Finished"*; it is cleared at the end.
   - *The welcome screen kept the last document's progress bar, page and zoom*; they are hidden
     without a document, and the progress is reset on closing.
   - *The Phonon download notice was cut off before its question mark.* A notice may take two
     lines, the section name steps aside while one is shown, and the question is shorter; it fits
     on one line at 1440 px.
   Not fixed: OCR repeating "band sentences as horizontal bands" where two rotated lines meet on
   the synthetic scan, and the 145-306 ms freezes when annotating and when starting playback
   (not investigated in this change, so their cause is not known).

110. **The short freezes of the v0.1.1 stress run** (`fix/ui-freezes`, 2026-11-25). With
   `RN_FOLLOW_LOG=1` each frame gap now carries the runtime's counters, and a probe thread logs
   `ui stall N ms` when the UI thread takes longer than 50 ms to answer (`StallProbe`, see
   `docs/testing-real-world.md`). Measured on the GARCH paper in the nested display, Phonon, 1.5×:
   - *Changing the speed* froze the window 54-180 ms on 13 of 15 changes in a seeded 5-minute
     mixed run (175 actions), with 10-40 MB allocated and GC pauses of 0-3 ms. `SetPlaybackRate`
     ran on the UI thread, waited there for the writer to stop, and resampled and time-stretched
     the current and queued clips inside the engine lock, which every frame takes to read the
     position.
   - *Starting a passage whose audio was already prepared* (Play, Replay or Next while paused, Play
     after Cancel) froze it 65-75 ms on a 9 s passage and 122-167 ms on the paper's longest (56 s),
     with 18-37 MB allocated on the UI thread: nothing in that chain yielded, so
     `AudioPlaybackEngine.LoadAsync` decoded, resampled to the device's 44.1 kHz and time-stretched
     on the caller's thread, the time-stretch again inside the engine lock. A console bench put the
     56 s passage at 50-110 ms of resampling and 92 ms of WSOLA; PortAudio's open is 6-12 ms. The
     306 ms of the stress run is this path on a long passage, cold.

   Fixed: a load and a rate change render on the thread pool and outside the engine lock; the
   rate change is `IAudioTransport.SetPlaybackRateAsync`, in turn with the engine's other
   operations, as queueing the next clip now is too (a clip rendered beside a change could have
   been queued at the old rate), and changes asked for while one renders are applied as one at the
   last rate. Four engine tests; the two that check a long clip is not rendered on the caller's
   thread fail on the old engine. In the release build: no stall on the 56 s passage (Play and
   three Replays while paused), none in Play, Replay or Next while paused on a short one, none in
   ten speed changes, six speed changes 0.4 s apart left it reading at the last one chosen, and
   the same seeded 5-minute run (175 actions, 15 speed changes) gave no stall and no frame gap,
   against 14 stalls before.

   Not reproduced here: the 145 ms freeze while annotating (see 111).

111. **The first opening of the review notes list** (`fix/review-notes-first-open`, 2026-11-25).
   The stress run's 145 ms was measured over a step that opened the sidebar for the first time in
   the process, cycled the colour filter and then annotated. Annotating gave no stall: about 40
   highlights, undos and redos, cold and warm, with the notes list open or closed, on the old
   engine and the new. Opening the sidebar on *Review notes* for the first time in a process did:
   62, 69, 74 and 186 ms in four fresh starts (31 notes), and never again in that process; the
   other four sections, the menus, the voice list and the note editor stayed under 50 ms, and the
   Settings window's first opening gave a 69 ms frame gap with no UI stall. A CPU sampling trace of
   one opening (EventPipe's sample profiler, 1 ms; `docs/testing-real-world.md`) put about half of
   the UI thread's busy time in laying out the notes' text, most of it the system's font lookups:
   a typeface for the list's italic and semibold styles (`SKFontManager.MatchFamily`, 2-5 ms each,
   not cached by Skia) and a fallback for each character of maths the notes quote
   (`FontManager.TryMatchCharacter`); the rest was building the items and writing the settings.
   The window now lays out the notes' text and a set of Greek letters and maths symbols in those
   styles when a document opens, a piece at a time at background priority (`WarmReviewNoteFonts`).
   In the release build three fresh starts opened the list with no stall and no frame gap, and
   none appeared while the document loaded.
   Still on the UI thread, measured but small: `PersistSettings` reads, parses, serialises and
   writes `settings.json` for each preference changed (about 7 ms of the opening above); see 112.

112. **File writes off the UI thread** (`fix/review-notes-first-open`, 2026-11-25). A preference
   the main window changes (speed, follow mode, zoom, the sidebar's visibility, width and section,
   colours, the colour filter, the voice, annotation tools) read, parsed, changed and wrote
   `settings.json` on the UI thread, and every passage change rewrote `recent.json` there: a few
   milliseconds on this machine, more on a slow disk, a network home or under a virus scanner.
   They now go to `SettingsStore.UpdateLater`, which writes on the thread pool in the order asked,
   several changes asked for during one write written together, a failure noted as before
   (four tests); the recent list is updated inside the position save, off the UI thread. The
   settings dialogue still writes at once (it shows a failure where the change was made), after
   whatever is queued, and reads the file only once the queue is written; closing, and so a
   restart, waits for the queue. In the release build a speed, zoom, sidebar and colour-filter
   change made 0.1-0.3 s before the app was told to close were all in `settings.json` afterwards.
   Not changed: the app's first frame after start is about 60 ms late (JIT and the first
   collection, before anything can be done in it), and an annotation change serialises the
   annotations on the UI thread before writing them off it, which must see them as they are.

113. **Review findings** (`fix/review-findings`, 2026-11-25). A review of the whole codebase found
   nine problems, all confirmed in the code; each of the first five has a test that failed before
   its fix.
   - *A Resume during a speed change left the reader silent on Buffering.* The change renders on
     the thread pool, and Resume and Play did not wait for it: the device was started, then
     stopped by the change's flush with nothing to start it again. Play and Resume now take
     their turn with the engine's other operations.
   - *An overtaken start still played.* After its clip loaded (which now takes the thread pool's
     time) a start stopped by a seek or Stop went on to play and to say Playing. It now stops
     after each await whose generation has moved on.
   - *An overtaken load published over the next document.* A load waiting for the resumed speed to
     apply said Idle afterwards, over the next document's Play that was preparing.
   - *A voice chosen after a failed start began to read.* The start stayed marked as under way;
     every start now clears its own mark when it ends, however it ends.
   - *The article "a" read as the letter* after "over", "given", "plus", "minus", "times",
     "equals", "point" and "let" ("over eh window") and a capital "A" after an opening bracket or
     quotation mark. Beside those words "a" is now the symbol only when a symbol-like word,
     another lone letter or an operator follows; prepositions ("over a of x", "from a to b") and
     operators ("a + b") mark it as the symbol anywhere (`ProsePipeline` →
     `prose/12-article-a-after-plus-over-given`).
   - Not tested, by reasoning only: a speed change just after the device has presented the start
     of the queued passage no longer writes that start again (the queued clip is made current
     first); the font warm-up disposes its text layouts; misplaced doc comments moved; a
     preference that fails to save while closing reaches the notes list (which a closing window
     rarely shows) instead of nothing.

114. **"Let a denote" read with the article** (`fix/letter-a-verbs`, 2026-11-25). Item 113 made
   "let" and "point" words the article follows as often as the symbol, and "a" after them the
   symbol only when the next word said so; "Let a denote the intercept", "Let a satisfy the bound"
   and "the point a lies on the line" then gave the article. Verbs an article never precedes
   (denote, satisfy, satisfies, lies, represent, belong, belongs, exceed, exceeds, converges,
   tends) now mark "a" as the symbol, wherever it stands; "lie" is left out ("a lie" is a noun).
   Five cases added (`ProsePipeline` → `prose/13-symbol-a-before-a-verb`).

115. **The review's open items, and a review of 113-114** (`fix/review-open-items`, 2026-11-25).
   - *Tested now*: a speed change just past a passage boundary carries on in the next passage
     (the test fails without the fix of 113); through a real PortAudio device (opt-in
     `RN_AUDIO_INTEGRATION=1`, into a PipeWire null sink), a Resume pressed while a two-minute
     clip's speed change renders plays on, and the engine as it was before 113 failed it, left on
     Buffering.
   - *A preference not saved at closing* is written to stderr and kept in
     `$XDG_STATE_HOME/rail-narrator/unsaved-preferences.txt` (the message only), and the next start
     says it as a lasting notice and removes the file.
   - *A second review of 113-114 found*: an edit or a regenerated maths reading made while a start
     was under way left the reader at "Preparing speech" with nothing playing (113's generation
     checks stopped the start, and an edit takes nothing over); such an edit now carries the start
     on in the new words (a test that timed out without it). A load overtaken by a voice change
     while stopped now says Idle; a Resume overtaken while it waited behind a speed change says
     nothing; a passage's completion marks its start in the same lock as its generation check.
     Letter names: "a" glued to a bracket or a prime ("a(t)", "a′") is the letter after any word;
     "by" and "then" no longer mark it ("a by now classic argument"); a capital "A" after a
     sentence closed by a bracket or quotation mark, or before a capitalised word ("3.2 A Simple
     Model"), is the article (`ProsePipeline` → `prose/14-symbol-a-glued-and-title-case`). Still
     read wrong: "the point a (the origin) lies" (the article) and "(i) A model" (the letter).
   - *In the GUI* (release build, Kokoro on the CPU, headless compositor, XTEST, a null sink):
     three times paused, changed the speed and pressed Ctrl+P straight after: sound each time,
     reading on. Three quick Nexts: reading two passages on, with sound. Next then Ctrl+P 50 ms
     later, three times: Paused, Play offered, silence; Play then read with sound. A zoom changed
     with the settings directory read-only, then Ctrl+Q: the failure on stderr and in the note,
     and the notice on the next start. Not driven in the GUI: an edit during a start, and two
     documents opened in quick succession (tests only).

116. **A reading position survives a new plan of the document** (`fix/reading-position-anchor`,
   2026-11-25). A position was saved as a passage id, which is the passage's number in one plan
   ("s00042"): after a planner version change, a layout model installed or a filter changed, the
   same number named another passage, or, past the new end, the start. A position now also keeps
   the plan it was saved against (`ResumePoint.PlanKey`: the plan's id and the fingerprint of
   its inputs, since the id alone does not change with the filters) and where the passage starts
   on the page (page and offset into the page's text). Restored against another plan, the
   passage covering that place is resumed, else the one after it, else the one before it
   (`ResumePoint.Find`). Ten tests and two of storage (a file from an earlier version still read).
   *Measured on the GARCH paper* (every passage of one plan restored into another):
   - Heron, references and footnotes read and captions not: of the 552 passages still read,
     the saved id alone named another passage for 214; the place found all 552, and the 33 no
     longer read resumed at a neighbour.
   - A position saved with no layout model, restored after Heron was installed: the id alone was
     wrong for 518 of 547; the place found all 547, and none fell back to the start. The page text
     was identical under both analyzers on all 27 pages, which is what the offsets rely on.
   Not covered: a position saved by 0.1.5 or earlier has no plan or place and is restored by its
   id as before, once (the next save records both); a page read by OCR has text of its own, so a
   change of OCR can move offsets.
   *Driven in the window* (headless compositor, the same paper): a position saved in the old
   form at passage 300 of the model-free plan restored there by its id, and closing saved it
   with its plan and place (page 14, offset 863); with Heron chosen and the app started again,
   the new plan (586 passages; same plan id, another input fingerprint) resumed at `s00349`,
   the same sentence ("Rejection frequencies for the robust LM-type test…"), where `s00300` now
   names a passage of Section 4.1.
117. **What a maths passage read by rule costs now, and a repair without thinking measured and not
   adopted** (`fix/cheaper-rule-reads`, 2026-11-26). `rail-narrator-mathbench` now prints every
   request (interpretation, repair or transcription, thinking or not, its time, tokens and finish)
   and totals them by kind of passage and outcome. *Measured on the GARCH paper* with the
   application's defaults (Heron INT8, the formula model, evidence both, thinking for equations
   and lead-ins, Ornith-35B on the local server, one slot): 208 maths passages, 186 interpreted,
   21 read by rule, 1 as printed; 2,069 s of model time against about 85 minutes of narration
   (item 39's run, before the formula model, took 3,700 s). A lead-in read by rule took 35 s on
   average (item 39: 66 s), of which the first thinking attempt was 21 s, the thinking repair 8 s
   and the transcription 4 s; a sentence read by rule took 7 s. Thinking ran out of tokens twice
   (item 39: 22 times). A smaller thinking budget would gain little: a 2,000-token cap would have
   cut 11 of 63 first attempts, 6 of them in passages that ended interpreted.
   A repair with thinking off (the bench's `--repair-no-thinking`, `ThinkOnRepair = false`) took
   1,783 s for the whole paper (−14 %), but 176 interpreted and 31 by rule; pooled over four runs,
   its repairs rescued 46 of 90 passages against 56 of 93 with thinking. Two runs of the 38
   passages that needed a repair gave 25 and 23 interpreted with thinking, 26 and 20 without, and
   a whole-paper rerun changes the outcome of about 20 passages either way, so the loss is likely
   rather than proven. Not adopted: interpretation already runs well ahead of the narration, and
   the reader asked for plain language first. Not measured: Claude's repair (its effort is set
   per passage, and the runs cost money).
118. **A document with a saved narration opens without waiting for its pages to be read**
   (`feat/saved-narration-fast-open`, 2026-11-26). The narration brings its plan, but the open
   still ran the whole extraction, layout model and all, before the window was ready. The pages
   are now read after the document opens (`MainViewModel.ExtractBehindAsync`), for what needs
   their text: notes, highlights and bringing notes over, which say "The pages are still being
   read" until then. The next open, a close and quitting cancel it. *Measured in the window*
   (headless compositor, the GARCH paper with a Kokoro narration of 586 passages, Heron INT8, the
   extraction cache emptied before each open, reference machine): "Ready" at 4.8 s before and
   2.2 s after, in two runs each. H pressed at once said the pages were still being read; then
   playing gave sound at the sink, H highlighted the sentence being read, and a close while the
   pages were read left no extraction in the cache and the application running. Not measured: a
   slower machine or a scanned document, where reading the pages takes longer and the saving is
   larger.

119. **A Windows test reading a fixture's text as NULs** (`fix/windows-pdf-text-nul`, 2026-11-26).
   Twice on Windows CI a Pdf test got the right blocks and the right number of characters, every
   one U+0000 ("Acknowledgments" as fifteen NULs). Not PDFium: every call in Core 0.94.0 is behind
   `PdfiumGate`, and the document bytes are pinned. The fixture's PDF itself had lost its text.
   Skia's PDF writer builds each font's ToUnicode table from the platform's font manager, and on
   Windows DirectWrite's `GetGlyphIndices` intermittently returns a glyph past the font's glyph
   count (Skia m119, `SkTypeface_win_dw.cpp`, crbug.com/470146), at which Skia stops filling the
   table. `SyntheticPdf` now reads each PDF it writes back through PDFium and writes it again,
   up to five times, if a letter or digit it drew does not come back (`WriteChecked`). *Checked
   here*: the whole suite passes with the check on; with a letter that was never drawn injected
   into the expected counts, the fixture is written five times and the test fails with the
   message. *Not checked*: the cause on Windows itself, which this machine cannot run; it is
   inferred from the symptom and Skia's source. If a fixture there still fails, the message says
   how many characters read as U+0000.

