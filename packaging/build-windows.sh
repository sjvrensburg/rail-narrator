#!/usr/bin/env bash
# Builds a portable Windows release folder and zip for Rail Narrator (win-x64).
#
# Runs on Windows under Git Bash (as the CI job does), where it also builds the two native
# libraries Windows has no package for: PortAudio (audio output, MIT-style licence) from a pinned
# commit with CMake and MSVC, and Phonon's runtime (MIT OR Apache-2.0) with cargo. On Linux it
# cross-publishes the .NET part only, which is enough to check the publish, and leaves a release
# without audio unless PORTAUDIO_DLL names a portaudio.dll built elsewhere.
#
# Like the Linux archive: self-contained, and models are NOT included (they are downloaded on
# explicit approval at runtime, see docs/model-manifest.json). ffmpeg is never included either:
# it is a separate program the reader installs, used only to export an audiobook.
#
# Environment:
#   PORTAUDIO_DLL=path     use this portaudio.dll instead of building one
#   WITH_PORTAUDIO=yes|no  require / skip PortAudio (default: build it when cmake is available)
#   WITH_PHONON=yes|no     require / skip Phonon's runtime (default: build it when cargo is available)
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"

CONFIG="${CONFIG:-Release}"
RID="${RID:-win-x64}"
OUT="${OUT:-$ROOT/dist}"
VERSION="$(grep -m1 '<VersionPrefix>' Directory.Build.props | sed -E 's#.*<VersionPrefix>(.*)</VersionPrefix>.*#\1#')"
NAME="rail-narrator-${VERSION}-${RID}"
STAGE="$OUT/$NAME"

# PortAudio v19.7.0, the release whose PaWasapiStreamInfo layout PortAudioNative mirrors.
PORTAUDIO_TAG="v19.7.0"
PORTAUDIO_COMMIT="147dd722548358763a8b649b3e4b41dfffbcfbb6"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) ON_WINDOWS=1 ;;
    *) ON_WINDOWS=0 ;;
esac

echo "==> Rail Narrator ${VERSION} (${RID})"
rm -rf "$STAGE" "$ROOT/src/RailNarrator.App/obj" "$ROOT/src/RailNarrator.App/bin" \
       "$ROOT/src/RailNarrator.Cli/obj" "$ROOT/src/RailNarrator.Cli/bin"

# The application (a GUI program) and rail-narrator-cli.exe (the narrate and export commands,
# which need a console) into one folder: they share every assembly.
for project in RailNarrator.App RailNarrator.Cli; do
  dotnet publish "$ROOT/src/$project/$project.csproj" \
    -c "$CONFIG" -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false \
    -o "$STAGE"
done
# Import libraries and native debug symbols that NuGet packages bring along are not needed to run.
find "$STAGE" -maxdepth 1 \( -name '*.lib' -o -name 'lib*.pdb' \) -delete

# ---- PortAudio -------------------------------------------------------------------------------
if [[ -n "${PORTAUDIO_DLL:-}" ]]; then
  cp "$PORTAUDIO_DLL" "$STAGE/portaudio.dll"
elif [[ "${WITH_PORTAUDIO:-auto}" != "no" && $ON_WINDOWS == 1 ]] && command -v cmake >/dev/null; then
  PA_BUILD="$(mktemp -d)"
  git -c advice.detachedHead=false -c core.autocrlf=false clone -q --depth 1 --branch "$PORTAUDIO_TAG" \
    https://github.com/PortAudio/portaudio.git "$PA_BUILD/src"
  [[ "$(git -C "$PA_BUILD/src" rev-parse HEAD)" == "$PORTAUDIO_COMMIT" ]] \
    || { echo "PortAudio's $PORTAUDIO_TAG tag no longer points at $PORTAUDIO_COMMIT; refusing to build it." >&2; exit 1; }
  # WASAPI is what the application uses; MME stays as PortAudio's own fallback. ASIO needs
  # Steinberg's SDK under its own licence and is left out.
  cmake -S "$PA_BUILD/src" -B "$PA_BUILD/build" -A x64 \
    -DPA_USE_ASIO=OFF -DPA_USE_WASAPI=ON -DPA_USE_WMME=ON -DPA_USE_DS=OFF -DPA_USE_WDMKS=OFF \
    -DPA_BUILD_SHARED=ON -DPA_BUILD_STATIC=OFF -DPA_DLL_LINK_WITH_STATIC_RUNTIME=ON >/dev/null
  cmake --build "$PA_BUILD/build" --config Release >/dev/null
  dll="$(find "$PA_BUILD/build" -iname 'portaudio*.dll' | head -1)"
  [[ -n "$dll" ]] || { echo "The PortAudio build produced no DLL." >&2; exit 1; }
  cp "$dll" "$STAGE/portaudio.dll"
  cp "$PA_BUILD/src/LICENSE.txt" "$STAGE/LICENSE-portaudio.txt"
  rm -rf "$PA_BUILD"
elif [[ "${WITH_PORTAUDIO:-auto}" == "yes" ]]; then
  echo "WITH_PORTAUDIO=yes needs Windows with CMake and MSVC, or PORTAUDIO_DLL." >&2; exit 1
else
  echo "==> No PortAudio: this folder has no audio output (set PORTAUDIO_DLL, or build on Windows)."
fi

# ---- Phonon's runtime (ADR-0009) ---------------------------------------------------------------
if [[ "${WITH_PHONON:-auto}" != "no" && $ON_WINDOWS == 1 ]] && command -v cargo >/dev/null; then
  PHONON_STAGE="$(mktemp -d)"
  PHONON_RUNTIME_ONLY=1 "$ROOT/tools/setup-phonon.sh" "$PHONON_STAGE"
  cp "$PHONON_STAGE/ptts_ffi.dll" "$STAGE/ptts_ffi.dll"
  rm -rf "$PHONON_STAGE"
elif [[ "${WITH_PHONON:-auto}" == "yes" ]]; then
  echo "WITH_PHONON=yes needs Windows with a Rust toolchain (cargo)." >&2; exit 1
else
  echo "==> No Phonon runtime in this folder (tools/setup-phonon.sh, run from Git Bash, builds it)."
fi

cat > "$STAGE/NATIVE-DEPENDENCIES.txt" <<'TXT'
Native libraries Rail Narrator uses on Windows.

Bundled in this folder
  portaudio.dll       Audio output (WASAPI), PortAudio v19.7.0, MIT-style licence
                      (LICENSE-portaudio.txt).
  pdfium.dll          PDF rendering (RailReader.Core.Pdfium).
  libSkiaSharp.dll    Drawing (SkiaSharp).
  onnxruntime*.dll    Layout, OCR, Kokoro and the formula model (ONNX Runtime), with its
                      WebGPU plugin and Direct3D 12 (dxcompiler.dll, D3D12\) for the GPU.
  ptts_ffi.dll        Phonon's runtime, MIT OR Apache-2.0, when the release was built with Rust.
  voices\*.npy        Kokoro's 54 voices, 27 MB, Apache-2.0 (from KokoroSharp's package). The
                      Kokoro model they belong to is not bundled.

Downloaded on explicit user approval (never bundled, never fetched implicitly)
  Kokoro ONNX model, layout models, formula model: see Models and endpoints in Settings.
  Phonon model and voices: tools/setup-phonon.sh from the source tree (Git Bash).

Not bundled, installed by you if wanted
  ffmpeg              Only to export an audiobook (M4B). Install it, for example with
                      'winget install ffmpeg', so that ffmpeg.exe is on the PATH.

Not available on Windows yet
  Word alignment (MFA). Word highlighting from Phonon's or Kokoro's own timing works.
TXT

cat > "$STAGE/README.txt" <<TXT
Rail Narrator ${VERSION} for Windows (x64)

Run:  rail-narrator.exe [file.pdf]
Narrate or export from a terminal:  rail-narrator-cli.exe narrate file.pdf

Settings:      %APPDATA%\\rail-narrator
Models, cache: %LOCALAPPDATA%\\rail-narrator

Models are not included. Settings ▸ Models and endpoints shows the size and licence of each
before anything is downloaded.

A PDF passed on the command line is opened but never read aloud automatically.
TXT

ARCHIVE="$OUT/${NAME}.zip"
rm -f "$ARCHIVE"
if command -v 7z >/dev/null; then
  (cd "$OUT" && 7z a -tzip -bso0 "$ARCHIVE" "$NAME")
elif command -v zip >/dev/null; then
  (cd "$OUT" && zip -qr "$ARCHIVE" "$NAME")
else
  python3 -m zipfile -c "$ARCHIVE" "$STAGE" 2>/dev/null || python -m zipfile -c "$ARCHIVE" "$STAGE"
fi

echo "==> $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"
echo "==> staging dir: $STAGE"
