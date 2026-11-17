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
