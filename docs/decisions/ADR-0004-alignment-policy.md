# ADR-0004: Clip-boundary highlighting ships; word timing is capability-gated and not currently available

**Status:** accepted · **Date:** 2026-11-17

## Decision

Ship **sentence / phrase / equation** highlighting derived from clip boundaries.
Do not ship word-level highlighting in this release, and do not synthesise word
boundaries by interpolation.

## Context

Two independent checks showed no timing source is available on this machine:

- **Kokoro.** `kokoro-fp16.onnx` (KokoroSharpBinaries v2.0.0) has inputs
  `tokens`, `style`, `speed` and output `audio` — and **no `durations` output**.
  Verified two ways: scanning the 163 MB ONNX file finds zero occurrences of the
  string `durations`, and `KokoroSharp` reports `PhonemeTimestamp[] == null` for
  every step at runtime. A duration-enabled export exists upstream
  (`kokoro-v1.0.onnx`, 325.5 MB) and KokoroSharp would pick its `durations`
  output up automatically, so this is a model-file choice, not a code limit.
- **ZONOS3.** `/v2/edit/align` belongs to the speech-editing feature set and needs
  an edit backbone, encoder companions and a Python worker with `fusealign-hub`.
  A synthesis-only install has none of them. Its presence in documentation is not
  evidence this install can use it.

## Why clip boundaries are sufficient, not a compromise

Each segment is synthesised as one independent clip, so its sample interval and
its source anchors are both **known exactly**. That is measurement, not
estimation: `TimedCue.ForWholeClip(frameCount, anchors, Sentence|Equation)`.

For a paraphrased equation this is also the *correct* granularity. There is no
one-to-one correspondence between a spoken word and a PDF glyph, so word cues over
an equation would highlight text that was never read.

## What is explicitly not done

Interpolating word positions uniformly, or proportionally by character count, and
presenting the result as accurate alignment. It is indistinguishable from measured
alignment in the UI and wrong in a way the listener cannot see. When timing
evidence is insufficient the scheduler falls back to a coarser granularity and
**says so in diagnostics**.

## Guard rails that are implemented and tested

`TimedCueValidator` rejects, and the rejections are covered by tests:
out-of-range cues, overlapping/non-monotonic cues, sub-8-sample cues, cues citing
an anchor id the plan does not contain, low-confidence cues, word-granularity cues
with no confidence value, and spoken ranges beyond the script. Coverage is
reported, and `ShouldFallBackToCoarse` trips below 90%.

The failure mode this exists for is concrete: an aligner that drops or repeats one
word shifts every later anchor by a word. The audio still sounds right while the
highlight reads the wrong line.

## Path to word timing later

Add a duration-enabled Kokoro export (or a standalone aligner) behind
`IForcedAligner`. `KokoroTtsProvider` already upgrades
`TtsCapabilities.NativeTiming` from `None` to `NativeDuration` the first time a
model actually returns timestamps, so nothing else changes — and the validator
still gates the result. Until then the word path is reported as unavailable rather
than offered.
