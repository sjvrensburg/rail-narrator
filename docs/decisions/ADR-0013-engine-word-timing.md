# ADR-0013: Word timing from the speech engine itself

**Status:** accepted · **Date:** 2026-11-23 · **Follows:** ADR-0004, ADR-0009

## Decision

A speech engine that knows where it is in its text while it speaks is asked, and its answer is a
second source of word timing beside the forced aligner (ADR-0004):

- **Phonon** reports, for each 80 ms frame, the attention of one head of its language model over
  the text's tokens. That head follows the text, and a word starts where its position passes the
  word (`AttentionWordTiming`, `TimingProvenance.ModelAttention`).
- **Kokoro**, in the export that has a `durations` output, reports the frames it gave each
  phoneme. Those add up to the clip to the sample, and each word of the script takes the samples of
  its phonemes (`PhonemeWordTiming`, `TimingProvenance.NativeDuration`).

Which source is shown is the reader's choice (Settings ▸ Reading ▸ Word timing from,
`WordTimingSource`): the aligner when installed, with the engine's timing shown until the
aligner's arrives and wherever there is no aligner (the default); or the engine's own, in which
case the aligner is not run for a passage the engine timed. Either way a passage the engine could
not time goes to the aligner if there is one, and otherwise is highlighted whole. ADR-0004's rule
stands: nothing is interpolated or shared out by character count.

## Why

The aligner (MFA) is a multi-gigabyte install, takes 23 to 56 s to start, cannot pronounce some
words, and in a whole-document narration was most of the time (ADR-0012: 9 min 35 s for 9 min of
audio). The engines already hold the answer.

## Phonon: an attention head

Phonon is given its text as a prefix and then generates audio latents frame by frame. Its
runtime exposes no timing, and its README says so. Scoring every head of the 6-layer model for how
steadily it moves through the text gave one clear answer, layer 3 head 0: 96 % of its attention
on the text tokens, correlation 0.995 between frame and position, about one token at a time.
blixt/phonon, an independent browser runtime for the same model, uses the same head; its
`timing.ts` is the published recipe.

- **The library** (`tools/phonon-word-timing.patch`, applied to xn-ptts v0.7.1 by
  `tools/setup-phonon.sh`): `ptts_speak_aligned` also reports, per chunk of text, the text as the
  model was given it with each token's end offset, and per frame that head's attention over the
  tokens. A process-wide tap counts the flow LM's attention calls within a step on the generating
  thread; the audio is unchanged (identical sample counts) and the cost is not measurable (3.49 s
  against 3.57 s for one passage). A library without the call still works, without timing.
- **The recipe**, fitted to MFA, not taken from the published one, which is about 40 ms late
  against it: position = attention-weighted mean index over the most-attended token and one either
  side, never moving back; a word starts when the position is 0.375 into its first token, plus
  0.375 frame; a start in silence (below −60 dB) moves to the sound; a word ends where the next
  starts, or where the sound stops (−45 dB) before a pause.
- **Script words are found by their letters.** The engine capitalises, punctuates and splits long
  text into chunks; its text and the script are compared as runs of letters and digits, and a
  script word takes the time of the token holding its first letter. If the runs differ, there is
  no timing.
- **Refusals**: the head did not reach the end of the text, or is not peaked on it (mean peak
  share under 0.4); a word with no time of its own. Each gives no timing and a reason.

## Kokoro: phoneme durations

`onnx-community/Kokoro-82M-v1.0-ONNX-timestamped` (Apache-2.0; `model_fp16.onnx`, 163 MB, pinned
by commit and SHA-256 in the manifest as `kokoro-timed-fp16`) has a second output, `durations`.
Token k lasts max(1, round(duration k)) frames of 600 samples, and that sum equals the audio's
length exactly on every clip tried. It names its phoneme ids `input_ids`, which KokoroSharp's
engine cannot feed, so it runs in `KokoroSessionRunner` on the CPU, with inputs built as
KokoroSharp builds them. `kokoro-fp16.onnx` through KokoroSharp's engine is unchanged.

- **Three frames of lead.** Counted from the leading pad, every word was 76 ms late against MFA
  (median 76, 90th percentile 109: an offset, not noise). Kokoro's reference code takes three
  frames off the pad's duration; so does `KokoroSessionRunner.LeadFrames`.
- **Words** are the runs of phonemes between spaces and punctuation, counted off against the
  script's words. A compound read as one word ("vine-based") is divided where its first part's
  own phonemes end, compared without stress marks; if they are not found there, no timing. The
  space after a word is its tail, as an aligner places it; a pause at punctuation is no word's.
- Used automatically when the reader chose the engine's timing and the file is installed, unless
  a Kokoro model path is set.

## Measured (2026-11-23, i7-12700H laptop; MFA is the reference, not ground truth)

| | Phonon, attention | Kokoro, durations |
|---|---|---|
| Passages / words compared | 34 / 505 | 29 / 376 |
| Word starts: mean, median | 30 ms, 24 ms | 16 ms, 13 ms |
| Within 50 ms / 100 ms | 83 % / 98 % | 94 % / 100 % |
| Bias, worst | +2 ms, 194 ms | +1 ms, 105 ms |
| Word ends: median, 90th percentile | 20 ms, 54 ms | 17 ms, 212 ms |
| Passages the engine timed | 35 of 35 | 35 of 35 |

- Phonon's constants were found by cross-validation on 504 words in one voice (Maeve): 44 ms mean
  held out, against 79 ms for the published recipe; unchanged on two other voices (41 and 44 ms on
  246 words each). The table is the same recipe through the patched library on new audio of
  largely the same sentences, so it is not a fully held-out figure. A fitted combination of two
  heads trimmed the tail (90th percentile 98 to 75 ms) and was left out: nine more constants.
- Kokoro's has two constants, both from its reference code, and was not fitted to the data.
- Whole-document narration of two pages with Phonon and the engine's timing: 4 min 4 s against
  9 min 35 s with the aligner, 46 of 51 passages word-timed against 44 (44 from attention, 2 by the
  aligner after the engine declined, where the runtime had rewritten the text). With Kokoro's
  timestamped export: 44 of 51 (42 from durations, 2 by the aligner), in 13 min 28 s, its synthesis
  running at about real time on this machine.

## Not done, and risks

- The head and the constants belong to one model (`phonon-31m-en`, signature `5f0a2aab`); another
  model gets no timing until its head is found. The Kokoro lead is the reference's.
- A skipped or repeated word is hidden by a position that never moves back; only the end of the
  text is checked.
- The patch is carried against a pinned upstream commit. It is small (306 lines, one new file) and
  should be offered upstream.
- Kokoro's timestamped export on the GPU (WebGPU) was not tried; the GPU path keeps its own model
  and has no durations.
- Not seen or listened to in the application.
