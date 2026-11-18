# ADR-0004: Local forced alignment for word highlighting, with validated fallback

**Status:** accepted, revised after timing review · **Date:** 2026-11-17

## Decision

Align English prose against its final trimmed PCM using a resident local
MFA 3.3.9 / Kalpy 0.10.5 worker. Use the exact normalised script sent to TTS, not an
independent transcription. The same token sequence is used for alignment and
script offsets. Equation paraphrases retain whole-equation highlighting.

A first real integration check aligned all nine words in 2.952 seconds of Kokoro
speech. Initial worker preparation took 2.913 seconds; a warm repeat took 0.017
seconds. Keeping models and the compiled lexicon resident avoids per-sentence
Python startup. Requests are serialised, cancellable and bounded by a timeout.

## Why native phoneme durations are insufficient

The compatible `kokoro-fp16.onnx` graph emits audio without durations. The tested
`kokoro-v1.0.onnx` duration export uses `input_ids` instead of KokoroSharp's `tokens`
input and is rejected by the compatibility probe. Even a compatible duration output
would still need a reliable phoneme-to-script-word map. `CanSupplyWordTiming` no
longer infers that capability solely from the presence of phoneme durations.

Kokoro raw-step timestamps now receive their step offset followed by one actual
clip-trim adjustment. Forced alignment runs after trimming, so its offsets already
refer to the cached playback waveform and need no further trim adjustment.

## Acceptance and source geometry

Every script word must have exactly one ordered interval. Reject missing, repeated
or unexpected words, overlaps, out-of-range intervals, invalid confidence values,
and cues without valid source anchors. Ignore blank and `<eps>` silence entries;
`<unk>` is a mismatch. Word coverage is lexical coverage, not the fraction of audio
occupied by speech: legitimate pauses do not invalidate a complete alignment.

MFA does not return calibrated word probabilities. Confidence remains null, allowed
only with a recognised native/forced timing provenance and valid word ranges.
Structural validation cannot prove correct acoustic boundaries; corpus evaluation
is still needed. Failed evidence falls back to sentence highlighting with a reason.

PDF prose anchors cover individual words. Source mappings preserve repeated words,
ligatures, dehyphenation across lines and number expansions. Cached timing ranges
are rebound to the current plan's geometry instead of reusing foreign anchor IDs.

## Cache identity and optional resources

Alignment identity includes PCM, script, model/dictionary hashes and runtime/worker
revision. Changing alignment assets retimes existing cached audio. Stored PCM
hashes detect same-length waveform substitutions. No aligner resources are fetched
implicitly, and missing resources never prevent ordinary narration. See
[setup and verification](../word-highlighting.md).

No uniform or character-weighted interpolation is used as a substitute for timing
evidence. Animation-frame rendering reads the media clock; camera following is
throttled independently so it does not limit word-highlight refresh.

## Amendment 2026-11-18: pronunciations for words the dictionary lacks

**Measured.** On a 24-page pdfTeX forecasting chapter, 160 of 354 prose sentences (45%)
held at least one token missing from `english_uk_mfa`, and the worker refused each of them,
so they were highlighted whole. In use this looked inconsistent: some sentences were
highlighted word by word, others whole. The missing tokens were acronyms (svr, rmse, pv,
nwp, ghi), technical words (predictors, irradiance, climatology, reanalysis), names
(Diebold, Giacomini), possessives (tomorrow's, Holm's), and text bugs that are now fixed:
"p." expanded inside "Chap." and "overlap.", and accent marks from maths fonts counted as
words.

**Decision.** A word the dictionary lacks gets candidate pronunciations before alignment,
the way MFA's own `align_one` handles unknown words (`add_pronunciation` on the loaded
lexicon, then rebuild the graph compiler, which copies the lexicon when it is built):

- an all-capitals token of 2–6 letters is also tried spelled out from the dictionary's
  own letter names (r → ɑː, m → ɛ m). Kokoro reads such acronyms as letters: "RMSE" took
  0.87 s and "NWP" 0.77 s in a real clip;
- the optional `english_uk_mfa` G2P model (11 MB, same phone set, downloaded by the setup
  script) proposes one pronunciation from the spelling.

When there are several candidates, the aligner chooses between them acoustically. A word
with no candidate still fails the clip, now with a reason that names it ("No pronunciation
for: ggplot2"). `<unk>` is still a mismatch. This does not weaken the evidence rule: every
interval still comes from forced alignment against the audio, and the timing notes name the
words that were aligned against a generated pronunciation. The G2P model's hash is part of
the alignment revision (`worker-3`).

**Verified.** In the opt-in test `RealKokoroAudioWithWordsTheDictionaryLacks_AlignsEveryWord`,
"The RMSE of the SVR forecasts of irradiance from NWP predictors is low." aligns all 13
words. On the chapter, 40 sentences were sampled from those holding a missing word, then
synthesised by Kokoro and aligned through the adapter: 38 aligned word by word, and 2 fell
back on letter–digit tokens ("2a", "ggplot2").

**Not verified.** That the boundaries of generated words are as accurate as those of
dictionary words. As before, no millisecond accuracy is claimed for any word.
