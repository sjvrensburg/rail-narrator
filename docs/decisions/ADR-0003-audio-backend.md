# ADR-0003: PortAudio blocking interface, with a derived presented-frame clock

**Status:** accepted · **Date:** 2026-11-17

## Decision

Use **PortAudio** (`libportaudio.so.2`) through a hand-written P/Invoke binding,
driving the **blocking** interface from a dedicated writer thread. The media clock
is

```
presentedFrames = framesWritten − (bufferCapacity − Pa_GetStreamWriteAvailable())
```

## Context: this was a spike, and the spike changed the design

The brief requires a Linux-capable backend with a *measurable consumed/presented*
position, validated by a short spike before committing. Three findings on the
reference machine (PortAudio V19.7.0-devel, ALSA host, Fedora 44):

1. **`Pa_GetStreamTime` is unusable.** In callback mode it returned `0.0000 s`
   while the callback had already delivered `0.3947 s` of audio. A clock pinned to
   it would never move.
2. **`Pa_GetStreamLatency` is not exported at all** by the shipped library —
   `EntryPointNotFoundException`. Latency cannot be queried that way.
3. **The blocking interface does report consumption.** `Pa_WriteStream` blocks in
   real time (writing 0.44 s took 0.395 s of wall clock — exactly the 46.4 ms
   buffer depth less), and `Pa_GetStreamWriteAvailable` oscillated 517→6 as the
   device drained 512-frame chunks.

A fourth finding shaped the resampler: `Pa_OpenDefaultStream` at **24 kHz fails
with `paInvalidSampleRate`** on a device whose default rate is 44.1 kHz, because
the ALSA host does not resample. Kokoro synthesises at 24 kHz, so conversion is
mandatory, not optional.

## Why blocking rather than callback

- It yields a real presented-frame count, which the callback path does not here.
- Resampling and time-stretching stay out of the audio callback, so no allocation
  or GC pressure on a real-time thread.
- Seeking is a flush plus a cursor reset.

Cost: a writer thread blocks inside `Pa_WriteStream`. Acceptable — it is one
thread, and backpressure is exactly what bounds memory.

## Verified in application

`rail-narrator-smoke audio` on the real device: 6 outputs enumerated with rates,
channel counts and advertised latency; opened device 0 at 44100 Hz / 2 ch;
measured buffer capacity **3072 frames (69.7 ms)**; wrote 36352 frames (0.824 s)
in 0.767 s wall; presented 33797 frames (0.766 s) with 2555 still queued.
**Clock error versus wall clock: −0.5 ms**, and the queued 2555 frames are
reported separately rather than counted as heard.

## Residual limits, stated rather than hidden

`ReportedLatencySeconds` is `null` because the backend cannot supply it. The clock
therefore carries a residual uncertainty of one device period plus the hardware
buffer — about 12–70 ms measured here. `AudioClockDiagnostics` exposes written,
presented and queued frames so that error can be measured rather than assumed
away. The device's own advertised `defaultLowOutputLatency` (8.7 ms) is surfaced
in enumeration for the same reason.

## Alternatives rejected

- **NAudio** — output drivers are Windows-only; the brief names this trap explicitly.
- **KokoroSharp's OpenAL playback** — game-oriented, and would put the clock
  outside this application's control.
- **libpulse directly** — viable, but Linux-only and a larger binding surface for
  no gain over PortAudio, which also abstracts the PipeWire/Pulse/ALSA choice.

Note: PipeWire *is* present on this machine (`libpipewire-0.3.so.0`), so
PortAudio's ALSA path could later be replaced by a PipeWire client for better
latency reporting. The `IAudioOutputDevice` seam makes that a contained change.
