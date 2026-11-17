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

## Stream latency and ring length

Revised after driving the application on a real device. The first implementation passed the
device's `defaultLowOutputLatency` (8.7 ms here) as `suggestedLatency` and sized the ring at a
quarter of a second. Both were wrong for this product.

Narration needs an accurate clock, not a short buffer: highlights follow DAC timestamps, so device
buffering costs start-up delay and nothing else. Under PipeWire's ALSA bridge a short buffer is
actively harmful, because the stream cannot fill a graph quantum longer than itself. Measured with
a silent callback stream on a Bluetooth sink, with a second client holding the quantum at 2048
frames (43 ms):

| `suggestedLatency` | Frames delivered |
|---|---|
| 10 ms (device low-latency suggestion) | 1 % of real time |
| 35 ms (device high-latency suggestion) | 43 % |
| 100 ms | 100 % |
| 200 ms | 100 % |

Polling `Pa_GetStreamTime` from a second thread every millisecond was tested as an alternative
explanation and made no difference. The stream now requests the larger of 100 ms and the device's
high-latency suggestion.

Ring space is counted against *presented* frames, not consumed ones, so that a pause can rewind to
what the listener actually heard. Frames the callback has copied but the device has not yet
presented therefore still occupy the ring. With a 0.25 s ring and about 0.21 s of Bluetooth
latency the writer was left some 40 ms of working room, and a device whose total latency exceeds
the ring would lock the writer out entirely while the callback played silence. The ring is now two
seconds. With both changes, the real engine on the same device with the quantum forced to 2048
kept about 1.66 s queued, reported no host underflow, and its output correlated 1.00 with the
source clip at the expected offsets.

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
