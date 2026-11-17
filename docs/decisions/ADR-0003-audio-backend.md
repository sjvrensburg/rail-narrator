# ADR-0003: PortAudio callback output with a DAC presentation clock

**Status:** accepted, revised after timing review · **Date:** 2026-11-17

## Decision

Use PortAudio's callback interface with a bounded, preallocated PCM ring. Keep
resampling, WSOLA, allocation and application events outside the callback. Record
`outputBufferDacTime` for every block and count only actual content frames, excluding
underrun silence. At display refresh, compare `Pa_GetStreamTime` with those block
records to derive the presented content position.

`Pa_GetStreamInfo().outputLatency` is reported in diagnostics. It is not subtracted
again: callback DAC timestamps already describe presentation time. Pause aborts the
stream and rewinds the ring consumer to the presented position, retaining unheard
samples. Seek flushes the ring and starts a new clock epoch. Completion waits for
presentation of the final content frame.

## Correction to the original investigation

The original spike declared `Pa_GetStreamTime` as an integer-returning function
with an output parameter. Its actual signature returns a `double` directly. The
claim that this clock was unusable was therefore invalid. With the corrected
binding, a measured 232.431 ms wall interval advanced the stream clock 232.428 ms.
The stream info reported 34.83 ms output latency in that probe.

There is no `Pa_GetStreamLatency` entry point; latency is in `PaStreamInfo`.
The former blocking-backend result of -0.5 ms versus wall time measured queue
consumption, not acoustic presentation. It did not establish sub-millisecond
highlight synchronisation and is superseded by this design.

## Source-audio position

Kokoro's 24 kHz audio is resampled to the selected device rate. WSOLA returns both
samples and a monotonic map of rendered output frames to source frames. In overlap
regions this map uses the window-weighted source positions; it is an estimate of
correspondence, not a claim that overlapped audio has a unique source instant.
Seek and rate changes preserve source position using the map. Final grains include
the actual clip tail, and output duration includes the specified rate even for
short clips. A rate change stops the producer before flushing and swapping buffers.

## Verification and limits

Deterministic tests cover delayed DAC presentation, underrun silence, jitter,
clock reset, render lengths, source mapping, pitch, and tail preservation. The
opt-in real-device test exercises silent playback, pause, seek, a 1.5x rate change,
final drain and stop against the native backend.

These tests do not establish audible-to-visible latency. PortAudio host timestamps,
output hardware, WSOLA overlap, UI scheduling and display refresh all contribute.
A loopback/display measurement is still required for an end-to-end accuracy bound.
See [word highlighting](../word-highlighting.md) for the verification procedure.
