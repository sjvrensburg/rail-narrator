# ADR-0012: A narration saved with the document

**Status:** accepted · **Date:** 2026-11-23 · **Follows:** ADR-0004, ADR-0009

## Decision

A whole document can be narrated once and the result kept beside it: `paper.pdf` gets
`paper.narration`, one file holding the script (maths already interpreted), every passage's audio
and its timings. Rail Narrator plays a document that has one from the file, asking neither the
language model nor the speech engine. The same file can be exported as an M4B audiobook with a
chapter at each heading. The job is started from the window (File ▸ Save narration with document…)
or without one (`rail-narrator narrate paper.pdf`), and uses the reader's own settings and engines.

These were the reader's choices (2026-11-23): both entry points, one file beside the PDF, M4B with
chapters, word timing when the aligner is installed.

## Shape

- **The file is a zip**: `manifest.json` (layout version, the PDF's SHA-256, voice, counts),
  `plan.json` (the `NarrationPlan` with the scripts that were spoken), and for each passage
  `clips/<id>.json` (its `SpeechAsset`, timings included, and a hash of its script) with
  `clips/<id>.audio`. It is written under a temporary name and renamed when complete: a
  narration that failed or was stopped leaves nothing.
- **It carries its own plan.** Playing it does not depend on the plan the application would build
  today, which changes with the layout model, the filters and every version bump. A narration is
  refused for a PDF with other bytes, and a passage is served only with the script it was
  recorded with: one the reader has rewritten goes to the engine.
- **Audio is Opus**, 40 kbit/s mono, encoded and decoded by Concentus, a C# port of the reference
  codec, so neither saving nor playing needs a native library or ffmpeg. Not an Ogg file: packets
  behind a small header that records the clip's length and the codec's delay, so a decoded clip
  has exactly the recorded number of samples with its sound where it was. Timings are in samples
  of the clip. A sample rate Opus does not have is stored as 16-bit PCM.
- **The coordinator plays a recording as it plays a cached clip.** `IRecordedNarration` is
  consulted before the engine (`PrepareFromRecordingAsync`), and the engine is asked for only
  when a passage has to be spoken, so a document with a narration is read with no engine
  installed. Choosing another voice drops the recording for the session.
- **Narrating is the coordinator's own preparation, run to the end.** `NarrateAllAsync` prepares
  each passage exactly as playback does (interpretation, letter names, pieces, the silence check,
  alignment) and hands it over; `NarrationJob` runs it on a coordinator of its own with a silent
  transport, so a document being listened to is not disturbed. Clips land in the ordinary speech
  cache as well, so a run that was stopped does not start from nothing.
- **A refused maths endpoint stops the job.** What is saved is heard every time. If the model
  cannot be asked, the narration stops with the reason and saves nothing, unless told to carry
  on (`--allow-uninterpreted`, or "Narrate anyway"); the manifest then counts what was read by
  rule, as printed, or announced.
- **The audiobook is ffmpeg's.** AAC in an M4B with chapters at the headings that are read. ffmpeg
  is looked for on the PATH and its absence is said; nothing else needs it.

## Measured (2026-11-23, i7-12700H laptop)

- Two pages of a statistics paper (51 passages, 9 min 2 s of audio, Phonon, word alignment on):
  narrated in 9 min 35 s without a window, most of it alignment; the file is 2.7 MB, the M4B
  4.2 MB with four chapters at the right headings.
- The audio is 40.2 kbit/s as stored (3.18 MB for 633 s in an earlier run).
- The codec, on a 30 s tone: encoding takes 2.9 s and decoding 1.8 s. A long passage therefore
  takes a second or two to decode, which the lookahead absorbs and a seek to it does not.
- A clip decodes to its own length with a tone's onset within 5 ms of where it was (test).

## Not done

- No recording is made piecemeal: a narration is of the whole document or not at all.
- The audiobook's title is the file's name and it has no author: PDF metadata is not read.
- A narration is not replaced when the application's own plan changes; it is replaced by
  narrating again.
- Nothing shows, per passage, that it is being played from the file rather than spoken.
