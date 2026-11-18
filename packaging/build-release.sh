#!/usr/bin/env bash
# Builds a reproducible Linux release archive for Rail Narrator.
#
# The archive is self-contained by default so it does not depend on the target
# machine's .NET runtime, and models are deliberately NOT included: they are
# large, separately licensed, and downloaded on explicit user approval at runtime
# (see docs/model-manifest.json).
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"

CONFIG="${CONFIG:-Release}"
RID="${RID:-linux-x64}"
SELF_CONTAINED="${SELF_CONTAINED:-true}"
OUT="${OUT:-$ROOT/dist}"
VERSION="$(grep -m1 '<VersionPrefix>' Directory.Build.props | sed -E 's#.*<VersionPrefix>(.*)</VersionPrefix>.*#\1#')"
NAME="rail-narrator-${VERSION}-${RID}"

echo "==> Rail Narrator ${VERSION} (${RID}, self-contained=${SELF_CONTAINED})"

# A clean publish: stale native assets from a previous RID are the usual cause of
# "works here, segfaults there".
rm -rf "$ROOT/src/RailNarrator.App/obj" "$ROOT/src/RailNarrator.App/bin"
dotnet publish "$ROOT/src/RailNarrator.App/RailNarrator.App.csproj" \
  -c "$CONFIG" -r "$RID" --self-contained "$SELF_CONTAINED" \
  -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false \
  -o "$OUT/$NAME/usr/lib/rail-narrator"

# Layout follows the Filesystem Hierarchy Standard so it can be staged into a
# prefix, and the launcher resolves its own directory so relocation works.
mkdir -p "$OUT/$NAME/usr/bin" "$OUT/$NAME/usr/share/applications"
cp "$ROOT/packaging/rail-narrator" "$OUT/$NAME/usr/bin/rail-narrator"
chmod 0755 "$OUT/$NAME/usr/bin/rail-narrator"
# The real binary sits next to its native dependencies; the launcher execs it.
ln -sf ../lib/rail-narrator/rail-narrator "$OUT/$NAME/usr/bin/rail-narrator-bin"
sed -i 's#exec "$bin/rail-narrator" "$@"#exec "$bin/../lib/rail-narrator/rail-narrator" "$@"#' \
  "$OUT/$NAME/usr/bin/rail-narrator"
cp "$ROOT/packaging/rail-narrator.desktop" "$OUT/$NAME/usr/share/applications/rail-narrator.desktop"

cat > "$OUT/$NAME/NATIVE-DEPENDENCIES.txt" <<'TXT'
Native libraries Rail Narrator needs at runtime, beyond what the archive bundles.

Required
  libportaudio.so.2   Audio output. Fedora: portaudio. Debian/Ubuntu: libportaudio2.
  libSkiaSharp        Bundled by SkiaSharp.NativeAssets.Linux in the archive.
  libPDFium           Bundled by RailReader.Core.Pdfium in the archive.

Required for scanned documents
  PP-OCRv5 models     Bundled by RailReader.Core.Ocr.RapidOcr beside the application.

Optional: layout analysis on the GPU (setting LayoutAcceleration, default Auto)
  libvulkan.so.1      Vulkan loader plus a GPU driver. Fedora: vulkan-loader.
                      Debian/Ubuntu: libvulkan1. The WebGPU plugin
                      (libonnxruntime_providers_webgpu.so) is bundled. Without a
                      usable GPU the layout model runs on the CPU and the app says why.

Downloaded on explicit user approval (never bundled, never fetched implicitly)
  Kokoro ONNX model   163.6 MB, Apache-2.0. See docs/model-manifest.json.
  ZONOS3-Mini GGUFs   ~2.9 GB total, Apache-2.0 weights / MIT runtime. Only if the
                      ZONOS3 provider is selected; a sidecar built from source.

Optional
  A local OpenAI-compatible text endpoint, for natural maths narration.
  A local OpenAI-compatible vision endpoint, for equation transcription.
TXT

cat > "$OUT/$NAME/README.txt" <<TXT
Rail Narrator ${VERSION}

Run:  usr/bin/rail-narrator [file.pdf]

Install into a prefix, e.g.:
  cp -r usr /usr/local        # or \$HOME/.local

Models are not included. On first run the Models panel shows the size and licence
of each asset before anything is downloaded. See docs/model-manifest.json.

A PDF passed on the command line is opened but never read aloud automatically.
TXT

ARCHIVE="$OUT/${NAME}.tar.zst"
if command -v zstd >/dev/null 2>&1; then
  tar -C "$OUT" --zstd -cf "$ARCHIVE" "$NAME"
else
  ARCHIVE="$OUT/${NAME}.tar.gz"
  tar -C "$OUT" -czf "$ARCHIVE" "$NAME"
fi

echo "==> $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"
echo "==> staging dir: $OUT/$NAME"
