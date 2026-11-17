#!/usr/bin/env bash
set -euo pipefail

# Requires micromamba on PATH. Only this explicit setup command downloads models;
# the application itself never installs dependencies or fetches alignment assets.
alignment_root="${1:-${XDG_DATA_HOME:-$HOME/.local/share}/rail-narrator/alignment}"
alignment_root="$(realpath -m "$alignment_root")"
command -v micromamba >/dev/null || { echo 'Install micromamba first: https://mamba.readthedocs.io/en/latest/installation/micromamba-installation.html' >&2; exit 1; }
micromamba create --no-rc --root-prefix "$alignment_root/mamba" --prefix "$alignment_root/runtime" \
    -c conda-forge montreal-forced-aligner=3.3.9 kalpy=0.10.5 kaldi=5.5.1172 python=3.11 -y
export MFA_ROOT_DIR="$alignment_root/downloads"
export PATH="$alignment_root/runtime/bin:$PATH"
"$alignment_root/runtime/bin/mfa" model download acoustic english_mfa
"$alignment_root/runtime/bin/mfa" model download dictionary english_uk_mfa
mkdir -p "$alignment_root/models/acoustic" "$alignment_root/models/dictionary"
cp "$MFA_ROOT_DIR/pretrained_models/acoustic/english_mfa.zip" "$alignment_root/models/acoustic/"
cp "$MFA_ROOT_DIR/pretrained_models/dictionary/english_uk_mfa.dict" "$alignment_root/models/dictionary/"
sha256sum "$alignment_root/models/acoustic/english_mfa.zip" "$alignment_root/models/dictionary/english_uk_mfa.dict" > "$alignment_root/models/SHA256SUMS"
printf 'Word alignment installed in %s. Restart Rail Narrator to enable it.\n' "$alignment_root"
