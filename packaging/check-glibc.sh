#!/usr/bin/env bash
# Fails when a native library in a directory needs a newer glibc than the one given: a release runs
# only where its libraries' glibc symbol versions are met.
#   packaging/check-glibc.sh <dir> <max-version, e.g. 2.35>
set -euo pipefail
dir="$1"; max="GLIBC_$2"
worst=""
for f in "$dir"/*.so "$dir"/*.so.*; do
  [[ -f "$f" ]] || continue
  need=$(objdump -T "$f" | grep -oE 'GLIBC_[0-9.]+' | sort -Vu | tail -1 || true)
  [[ -z "$need" ]] && continue
  echo "$(basename "$f"): $need"
  worst=$(printf '%s\n' "$worst" "$need" | sed '/^$/d' | sort -V | tail -1)
done
echo "Highest glibc symbol version needed: ${worst:-none}"
[[ -z "$worst" || "$(printf '%s\n' "$worst" "$max" | sort -V | tail -1)" == "$max" ]] \
  || { echo "A bundled library needs newer than $max." >&2; exit 1; }
