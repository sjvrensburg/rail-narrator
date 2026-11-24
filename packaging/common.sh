# Shared by build-release.sh and build-windows.sh: the files every release carries besides the
# application. Sourced, not run. Needs ROOT and the staging directory as $1.

# The licence, the third-party notices, the licence texts that come with bundled components, and
# install-phonon.sh: tools/setup-phonon.sh in its model-only mode, since a release already carries
# Phonon's runtime beside the application.
add_release_files() {
  local stage="$1"
  cp "$ROOT/LICENSE" "$stage/LICENSE"
  cp "$ROOT/packaging/THIRD-PARTY-NOTICES.txt" "$stage/THIRD-PARTY-NOTICES.txt"
  mkdir -p "$stage/licenses"
  local packages version
  packages="$(dotnet nuget locals global-packages --list | sed -E 's/^global-packages: *//')"
  version="$(grep -m1 'Include="Microsoft.ML.OnnxRuntime"' "$ROOT/Directory.Packages.props" | sed -E 's/.*Version="([^"]+)".*/\1/')"
  cp "$packages/microsoft.ml.onnxruntime/$version/ThirdPartyNotices.txt" "$stage/licenses/onnxruntime-third-party-notices.txt"
  local webgpu
  webgpu="$(ls -d "$packages"/microsoft.ml.onnxruntime.ep.webgpu/*/ | sort -V | tail -1)"
  cp "$webgpu/ThirdPartyNotices.txt" "$stage/licenses/onnxruntime-webgpu-third-party-notices.txt"
  sed -e 's/^model_only="\${PHONON_MODEL_ONLY:-}"$/model_only="${PHONON_MODEL_ONLY:-1}"   # a release carries the runtime/' \
      "$ROOT/tools/setup-phonon.sh" > "$stage/install-phonon.sh"
  grep -q 'PHONON_MODEL_ONLY:-1' "$stage/install-phonon.sh" || { echo "install-phonon.sh: the model-only default was not set." >&2; return 1; }
  chmod 0755 "$stage/install-phonon.sh"
}
