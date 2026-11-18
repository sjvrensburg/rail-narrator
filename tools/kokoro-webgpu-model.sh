#!/usr/bin/env bash
set -euo pipefail

# Makes the Kokoro model that runs on a GPU through WebGPU (Vulkan on Linux). See ADR-0007.
#
# Only this explicit command downloads or converts anything; the application never does. It takes
# the fp32 Kokoro export from the same KokoroSharpBinaries release as the default fp16 model (the
# fp16 one gives NaN samples on WebGPU), verifies it, and rewrites its 'tokens' input to int32 (the
# WebGPU plugin aborts on int64 inputs). Needs python3 with onnx, or uv to provide it.
#
# Usage: tools/kokoro-webgpu-model.sh [models-dir]

models="${1:-${XDG_DATA_HOME:-$HOME/.local/share}/rail-narrator/models}"
mkdir -p "$models"
src="$models/kokoro.onnx"
dst="$models/kokoro-fp32-webgpu.onnx"
url="https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx"
src_sha="0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ ! -f "$src" ]]; then
    echo "Downloading the fp32 Kokoro model (325 MB) to $src"
    curl -fL --progress-bar -o "$src.part" "$url"
    mv "$src.part" "$src"
fi
echo "$src_sha  $src" | sha256sum -c --quiet || { echo "Checksum mismatch for $src; delete it and run again." >&2; exit 1; }

if python3 -c 'import onnx' 2>/dev/null; then
    python3 -I "$here/kokoro_int32_tokens.py" "$src" "$dst.part"
elif command -v uv >/dev/null; then
    uv run --no-project --with 'onnx==1.20.1' python3 -I "$here/kokoro_int32_tokens.py" "$src" "$dst.part"
else
    echo "Needs python3 with the onnx package, or uv (https://docs.astral.sh/uv/)." >&2
    exit 1
fi
mv "$dst.part" "$dst"
sha256sum "$dst"
printf 'Wrote %s. Restart Rail Narrator; with KokoroAcceleration "Auto" it runs Kokoro on the GPU.\n' "$dst"
