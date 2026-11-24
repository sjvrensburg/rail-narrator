#!/usr/bin/env bash
# Builds rail-narrator-x86_64.AppImage (and its .zsync, when zsyncmake is installed) from the
# staging tree packaging/build-release.sh leaves in dist/.
#
# The AppImage carries PortAudio as well, built here from a pinned commit with ALSA only (needs the
# ALSA headers: libasound2-dev or alsa-lib-devel), so it runs without a system PortAudio package;
# ALSA itself, which PipeWire and PulseAudio serve, is the system's. Everything else is the Linux
# archive's: the .NET runtime, Phonon's runtime, Kokoro's voices, no models.
#
# Build it on the oldest system it must run on (the release does, on Ubuntu 22.04): the AppImage
# runs where its libraries' glibc does.
#
# Environment:
#   STAGE=dir           the build-release.sh staging tree (default: dist/rail-narrator-<version>-linux-x64)
#   APPIMAGETOOL=path   appimagetool to use (default: downloaded from its continuous release)
#   PORTAUDIO_SO=path   use this libportaudio.so.2 instead of building one
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
OUT="${OUT:-$ROOT/dist}"
VERSION="$(grep -m1 '<VersionPrefix>' Directory.Build.props | sed -E 's#.*<VersionPrefix>(.*)</VersionPrefix>.*#\1#')"
STAGE="${STAGE:-$OUT/rail-narrator-${VERSION}-linux-x64}"
APP_ID="io.github.sjvrensburg.RailNarrator"
APPIMAGE="$OUT/rail-narrator-x86_64.AppImage"
# The same PortAudio the Windows build bundles (packaging/build-windows.sh).
PORTAUDIO_TAG="v19.7.0"
PORTAUDIO_COMMIT="147dd722548358763a8b649b3e4b41dfffbcfbb6"

[[ -x "$STAGE/usr/lib/rail-narrator/rail-narrator" ]] || { echo "No staging tree at $STAGE: run packaging/build-release.sh first." >&2; exit 1; }
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
APPDIR="$WORK/AppDir"

echo "==> AppImage of Rail Narrator ${VERSION}"
mkdir -p "$APPDIR"
cp -a "$STAGE/usr" "$APPDIR/usr"
LIB="$APPDIR/usr/lib/rail-narrator"

# ---- PortAudio, beside the application, where it is looked for first ----------------------
if [[ -n "${PORTAUDIO_SO:-}" ]]; then
  cp "$PORTAUDIO_SO" "$LIB/libportaudio.so.2"
else
  git -c advice.detachedHead=false clone -q --depth 1 --branch "$PORTAUDIO_TAG" https://github.com/PortAudio/portaudio.git "$WORK/portaudio"
  [[ "$(git -C "$WORK/portaudio" rev-parse HEAD)" == "$PORTAUDIO_COMMIT" ]] \
    || { echo "PortAudio's $PORTAUDIO_TAG tag no longer points at $PORTAUDIO_COMMIT; refusing to build it." >&2; exit 1; }
  # ALSA only: JACK would add a library the AppImage would have to carry or expect; CMake 4 needs
  # the policy floor (see build-windows.sh).
  cmake -S "$WORK/portaudio" -B "$WORK/portaudio-build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DPA_USE_ALSA=ON -DPA_USE_JACK=OFF -DPA_BUILD_SHARED=ON -DPA_BUILD_STATIC=OFF >/dev/null
  cmake --build "$WORK/portaudio-build" -j >/dev/null
  so="$(find "$WORK/portaudio-build" -name 'libportaudio.so.2*' -type f | head -1)"
  [[ -n "$so" ]] || { echo "The PortAudio build produced no libportaudio.so.2." >&2; exit 1; }
  cp "$so" "$LIB/libportaudio.so.2"
  mkdir -p "$APPDIR/usr/share/doc/rail-narrator"
  cp "$WORK/portaudio/LICENSE.txt" "$APPDIR/usr/share/doc/rail-narrator/portaudio-LICENSE.txt"
fi

# ---- desktop integration, under the reverse-DNS id AppStream asks for ----------------------
rm -f "$APPDIR/usr/share/applications/rail-narrator.desktop"
mkdir -p "$APPDIR/usr/share/applications" "$APPDIR/usr/share/metainfo" "$APPDIR/usr/share/doc/rail-narrator"
sed -e "s/^Icon=.*/Icon=${APP_ID}/" -e 's/^TryExec=.*//' "$ROOT/packaging/rail-narrator.desktop" > "$APPDIR/usr/share/applications/${APP_ID}.desktop"
for size in 256x256 scalable; do
  dir="$APPDIR/usr/share/icons/hicolor/$size/apps"
  for f in "$dir"/rail-narrator.*; do [[ -e "$f" ]] && mv "$f" "$dir/${APP_ID}.${f##*.}"; done
done
cp "$ROOT/packaging/${APP_ID}.metainfo.xml" "$APPDIR/usr/share/metainfo/${APP_ID}.metainfo.xml"
cp "$STAGE/LICENSE" "$STAGE/THIRD-PARTY-NOTICES.txt" "$APPDIR/usr/share/doc/rail-narrator/"
cp -r "$STAGE/licenses" "$APPDIR/usr/share/doc/rail-narrator/"
# The top-level copies are what appimagetool embeds.
cp "$APPDIR/usr/share/applications/${APP_ID}.desktop" "$APPDIR/${APP_ID}.desktop"
cp "$APPDIR/usr/share/icons/hicolor/256x256/apps/${APP_ID}.png" "$APPDIR/${APP_ID}.png"

cat > "$APPDIR/AppRun" <<'RUN'
#!/bin/bash
# Rail Narrator's AppImage entry point. Arguments pass through: a PDF is loaded (never played),
# and "narrate" or "export" run without a window, as with the installed rail-narrator.
SELF="$(readlink -f "$0")"
HERE="${SELF%/*}"
exec "${HERE}/usr/lib/rail-narrator/rail-narrator" "$@"
RUN
chmod 0755 "$APPDIR/AppRun"

# ---- appimagetool ----------------------------------------------------------------------------
tool="${APPIMAGETOOL:-}"
if [[ -z "$tool" ]]; then
  tool="$WORK/appimagetool"
  curl -fsSL --retry 3 -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$tool"
fi
rm -f "$APPIMAGE" "$APPIMAGE.zsync"
# -u: update information for AppImageUpdate; with zsyncmake installed appimagetool also writes the
# .zsync, which is published beside the AppImage. zstd squashfs: much faster to start than gzip.
(cd "$OUT" && ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --comp zstd \
  --mksquashfs-opt -Xcompression-level --mksquashfs-opt 19 \
  -u "gh-releases-zsync|sjvrensburg|rail-narrator|latest|rail-narrator-x86_64.AppImage.zsync" \
  "$APPDIR" "$APPIMAGE")
echo "==> $APPIMAGE ($(du -h "$APPIMAGE" | cut -f1))"
[[ -f "$APPIMAGE.zsync" ]] && echo "==> $APPIMAGE.zsync"
true
