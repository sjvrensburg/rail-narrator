# Testing against the real thing

The unit suites need no models, no network and no sound card. This page is about the other
half: running the real models, the real GUI and the real audio path. All the measurements in
`status.md` were made this way, on the reference machine (Fedora 44, labwc/Wayland, PipeWire).

## What a machine needs

| Thing | Where it came from | Setting / variable |
|---|---|---|
| Kokoro TTS (`kokoro-fp16.onnx`) | `docs/model-manifest.json` | `KokoroModelPath`, or `~/.local/share/rail-narrator/models/kokoro-fp16.onnx`; tests: `RAIL_NARRATOR_KOKORO_MODEL` |
| Docling Heron layout model (default; ADR-0008) | Models menu, or RailReader2's downloads in `~/.config/railreader2/models` (found there automatically) | `LayoutAnalyzer: "heron"` or automatic; tests: `RAIL_NARRATOR_HERON_MODEL` (INT8), `RAIL_NARRATOR_HERON_GPU_MODEL` (FP32) |
| PP-DocLayoutV3 layout model (`.onnx`) | RailReader2's model download (was at `~/.config/railreader2/models/PP-DocLayoutV3.onnx`) | `LayoutAnalyzer: "pp-doclayout-v3"`, `LayoutModelPath`; tests: `RAIL_NARRATOR_LAYOUT_MODEL` |
| A multimodal LLM behind an OpenAI-compatible endpoint | llama.cpp `llama-server` (Vulkan build), loading `Ornith-1.5-35B-Q4_K_M.gguf` with `--mmproj mmproj-Ornith-1.5-35B-A3B-f16.gguf --jinja --parallel 1 --ctx-size 131072 --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 --alias ornith-1.5-35b-a3b --port 8088`. `--jinja` is needed for the thinking switch (`chat_template_kwargs.enable_thinking`) and `--mmproj` for page images | `MathLlmBaseUrl`, `MathLlmModel`, `MathLlmThinking` |
| MFA word alignment (optional, several GB) | `bash tools/setup-word-alignment.sh` | `MfaExecutable`, `MfaDictionaryPath`, `MfaAcousticModelPath`, `MfaG2pModelPath`; tests: `RN_MFA_*` including `RN_MFA_G2P` |
| `libportaudio.so.2` | distro package | — |
| WebGPU Kokoro model (optional) | `bash tools/kokoro-webgpu-model.sh` (downloads the fp32 export, converts it; ADR-0007) | `KokoroAcceleration: "Auto"` (default), `KokoroGpuModelPath`; tests: `RN_WEBGPU_INTEGRATION=1` with `RAIL_NARRATOR_KOKORO_MODEL` and `RAIL_NARRATOR_KOKORO_GPU_MODEL` |
| A WebGPU-capable GPU (optional) | Vulkan loader (`libvulkan.so.1`) and driver; the plugin is bundled | `LayoutAcceleration: "Auto"` (default) or `"Cpu"`; tests: `RN_WEBGPU_INTEGRATION=1` with the layout model |

Models are never bundled and never downloaded implicitly. Copy them, or download them on
purpose.

An example `settings.json` (in `$XDG_CONFIG_HOME/rail-narrator/`). The setting names are the
properties of `AppSettings` in `src/RailNarrator.Infrastructure/Storage.cs`:

```json
{
  "KokoroModelPath": "/path/to/kokoro-fp16.onnx",
  "LayoutAnalyzer": "pp-doclayout-v3",
  "LayoutModelPath": "/path/to/PP-DocLayoutV3.onnx",
  "MathLlmBaseUrl": "http://127.0.0.1:8088/v1",
  "MathLlmModel": "ornith-1.5-35b-a3b",
  "MathLlmThinking": "EquationsOnly"
}
```

The reference document for maths was *Specification and testing of multiplicative
time-varying GARCH models with applications* (Amado & Teräsvirta), 27 pages, 224 maths
passages. It is not in the repo. Use any maths-heavy paper, but numbers in `status.md` refer to
that one.

## Measuring maths interpretation

```bash
dotnet build tools/RailNarrator.MathBench -c Release
tools/RailNarrator.MathBench/bin/Release/net10.0/rail-narrator-mathbench paper.pdf PP-DocLayoutV3.onnx \
    [--endpoint URL] [--model NAME] [--pages 2-8] [--only 12,24] [--thinking off|equations|always] \
    [--max-tokens 4000] [--no-images] [--no-reader]
```

It prints each pending passage (`#index`, kind, page, seconds, provenance), the printed text,
the spoken text and any warnings, and then a tally. Nothing is cached, so each line costs a
real model call. The whole GARCH paper takes about an hour on a single-slot local server, so
run it in the background, redirected to a file, with `timeout`. `--only` takes the `#index`
values from an earlier run, to repeat just the interesting passages. Answers vary from run to
run, so compare tallies, not single passages.

## Driving the GUI without taking over the desktop

- **Display.** Run the app in a second, headless compositor so the real pointer is left alone:
  `WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 labwc -C <empty dir> -s "sh -c 'env | grep DISPLAY > env.sh'"`,
  then `wlr-randr --output HEADLESS-1 --custom-mode 1920x1080`. The app runs under Xwayland, so
  use the nested `DISPLAY`. Take screenshots with `grim` against the nested `WAYLAND_DISPLAY`.
  **Source the nested env in the same shell command as every `grim`/input call.** A bare `grim`
  captures the user's real desktop.
- **Input.** `wtype` and `wlrctl` are unreliable for Xwayland clients. Inject with XTEST instead,
  through a small Python `ctypes` script over `libX11.so.6` and `libXtst.so.6`. The first click
  only focuses the window.
- **Isolation.** Point `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME` and `XDG_STATE_HOME`
  at a scratch directory with its own `settings.json`, so the user's real settings and caches
  are untouched.
- **Audio.** Record with `parecord -d <sink>.monitor --latency-msec=20 --raw`. Without the latency
  flag the recorder raises the PipeWire quantum and starves the app's stream. To find exact clip
  start times, cross-correlate the recording with the cached clips
  (`$XDG_CACHE_HOME/rail-narrator/speech/rn/*/audio.pcm`, 24 kHz float32).
- **Camera decisions.** Start the app with `RN_FOLLOW_LOG=1` to append every follow request to
  `$XDG_STATE_HOME/rail-narrator/logs/follow.log`: the cue's rectangle, the visible region in page
  points, the control's size, zoom, offsets before and after, and the decision with its reason. No
  lines while the word moves means no follow was requested (paused, or no word timing).
- **Route the app's audio with `PIPEWIRE_NODE`.** Start the app with
  `PIPEWIRE_NODE=rn_test` after creating the null sink below: PipeWire's ALSA plugin honours it, so
  the very first stream goes to the null sink (checked: the real sink stayed `SUSPENDED` while the
  app played). Keep the mover loop as a backup, and note that a stream started with
  `dotnet …/rail-narrator.dll` is named `PipeWire ALSA [dotnet]`, not `[rail-narrator]`, so match
  both names.
- **Fresh XDG directories make the GPU look slow.** NVIDIA's driver caches compiled shaders under
  `$XDG_CACHE_HOME/nvidia`; with an empty cache the layout model's first inference takes about 8 s
  instead of 2 s. For timings, copy that directory into the isolated cache first.
- **`PULSE_SINK` does not redirect this app.** PortAudio opens PipeWire's ALSA device, whose streams
  are named `PipeWire ALSA [rail-narrator]` and carry no process id, and it ignored `PULSE_SINK`: a
  test run played to the user's default headset. Create a null sink
  (`pactl load-module module-null-sink sink_name=rn_test`) and, before pressing Play, run a loop that
  moves every sink input whose `application.name` contains `rail-narrator` to it
  (`pactl move-sink-input <id> rn_test`); the app opens a new stream per clip. Match the app with
  `pgrep -x rail-narrator`: `pkill -f` on its path also kills the shell running the command.
- **The default sink may be someone's headphones.** Before playing anything, record a few
  seconds of the monitor; if it isn't silent, someone is listening to something, so don't
  play test audio.
- Process-attach diagnostics (`dotnet-stack`, gdb) need the user's permission first.
