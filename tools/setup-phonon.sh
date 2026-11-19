#!/usr/bin/env bash
set -euo pipefail

# Installs Phonon, Gradium's on-device speech model, as Rail Narrator's second speech engine. See
# ADR-0009.
#
# Only this explicit command builds or downloads anything; the application never does. It
#   1. builds Gradium's C library (libptts_ffi.so) from xn-ptts at a pinned release commit; this
#      needs git and a Rust toolchain (cargo, edition 2024), and takes about a minute;
#   2. downloads phonon-31m-en (Apache 2.0) at the revision that release pins, and the 25 British
#      and American English voices of Gradium's voice catalog (Apache 2.0), each verified by its
#      SHA-256.
#
# Usage: tools/setup-phonon.sh [install-dir]
#   default install-dir: ${XDG_DATA_HOME:-~/.local/share}/rail-narrator/phonon
#   PHONON_RUNTIME_ONLY=1 builds the library and stops (packaging/build-release.sh uses it).

dir="${1:-${XDG_DATA_HOME:-$HOME/.local/share}/rail-narrator/phonon}"
model="$dir/phonon-31m-en"
runtime_tag="v0.7.1"
runtime_commit="5f9522b3e245846d312caaeb460d3dcae5d1a991"
model_rev="92741c5b07d085ff840e26fea8ce43110b87852f"
voices_rev="da6c9ec0d47be8df5c87c730a3aa97187f3229c3"
hf="https://huggingface.co"

command -v git >/dev/null || { echo "Needs git." >&2; exit 1; }
command -v cargo >/dev/null || { echo "Needs a Rust toolchain (cargo): https://rustup.rs" >&2; exit 1; }
mkdir -p "$model/voices"

fetch() {   # url, destination, sha256
    if [[ -f "$2" ]] && echo "$3  $2" | sha256sum -c --quiet 2>/dev/null; then return; fi
    curl -fsSL -o "$2.part" "$1"
    echo "$3  $2.part" | sha256sum -c --quiet || { echo "Checksum mismatch for $1" >&2; rm -f "$2.part"; exit 1; }
    mv "$2.part" "$2"
}

# ---- the runtime -------------------------------------------------------------------------------
if [[ ! -f "$dir/libptts_ffi.so" || "$(cat "$dir/runtime-commit" 2>/dev/null)" != "$runtime_commit" ]]; then
    build="$(mktemp -d)"
    trap 'rm -rf "$build"' EXIT
    echo "Building Gradium's runtime $runtime_tag (about a minute)…"
    git -c advice.detachedHead=false clone -q --depth 1 --branch "$runtime_tag" https://github.com/gradium-ai/xn-ptts.git "$build/xn-ptts"
    [[ "$(git -C "$build/xn-ptts" rev-parse HEAD)" == "$runtime_commit" ]] || { echo "The $runtime_tag tag no longer points at $runtime_commit; refusing to build it." >&2; exit 1; }
    CARGO_TARGET_DIR="$build/target" cargo build -q --release --manifest-path "$build/xn-ptts/Cargo.toml" -p ptts-ffi
    cp "$build/target/release/libptts_ffi.so" "$dir/libptts_ffi.so.part"
    mv "$dir/libptts_ffi.so.part" "$dir/libptts_ffi.so"
    echo "$runtime_commit" > "$dir/runtime-commit"
fi

# The release build wants the library alone.
[[ "${PHONON_RUNTIME_ONLY:-}" == 1 ]] && exit 0

# ---- the model ---------------------------------------------------------------------------------
echo "Downloading phonon-31m-en (64 MB)…"
fetch "$hf/gradium/phonon-31m-en/resolve/$model_rev/config.json" "$model/config.json" b5d0f88353638370d94035b7d73d7d6577c0554d78f8d7ecfd40490aae39ae85
fetch "$hf/gradium/phonon-31m-en/resolve/$model_rev/tokenizer.json" "$model/tokenizer.json" 691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936
fetch "$hf/gradium/phonon-31m-en/resolve/$model_rev/model.q8.gguf" "$model/model.q8.gguf" a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae
fetch "$hf/gradium/phonon-31m-en/resolve/$model_rev/default-voice.safetensors" "$model/default-voice.safetensors" dc537478eaadc1a5257fcda2e3501ab81e14c111a5d95a5838f350db2afd0f1a

# ---- the voices: name, catalog file, sha256, locale, gender ---------------------------------------
echo "Downloading 25 English voices (6 MB)…"
: > "$model/voices.tsv.part"
while read -r name file sha locale gender; do
    fetch "$hf/gradium/phonon-voices/resolve/$voices_rev/$file" "$model/voices/$name.safetensors" "$sha"
    printf '%s\t%s\t%s\n' "$name" "$locale" "$gender" >> "$model/voices.tsv.part"
done <<'VOICES'
Zoey voices/NbpkqMVS3CJeq2j8.safetensors 97c7f8c253413574406ab80dd23f49c3ebb2517163b00a9bf86b9c4cf43e7e23 en-us female
Juniper voices/NIWIOv5vTi5eRMj8.safetensors 444378eb06b7c492b86e8fc7d13e4db1a3c0cf3e68891a960bf6f0a41a13ef35 en-us female
Everly voices/vjFyp2BcCyTlUmYb.safetensors 958ea94da14f46dd48c7f64adac11ed2ad2aa45f7ccd165dfebab5272664402e en-us female
Harper voices/4SZHfMpw-p46Ywgs.safetensors 6b3da6e2d796a20c51af892c482bd6509fb88267a221aff83c870843dee2a123 en-us female
Hadley voices/bXwrkHhURL1VFJdG.safetensors a879938aa9a2df0a8220bdadef88137929f71e887bd91e7db205bd5fa11661d2 en-us female
Delia voices/nGzMHMPAWGaJFHOo.safetensors a0c3dcb91b2247eca56536be9ae8e8becc1c807b1136c5fe101552d6a155b24a en-us female
Easton voices/LMDIYE0Oa377Qh6Z.safetensors 45c582673cb20edb04ddcca2b5a8865ddfb0a82599efadb269021e87d20fbdd4 en-us male
Micah voices/40QrMVgAxFiUnP9f.safetensors 0de1098d1051686a138d068e83ff09deb5053328bc74ec9541a1fdb90bfc24da en-us male
Trevor voices/0zLApsZfVLLNaEGl.safetensors dc1341c8937af5534e6bbcc5c0fc5e7c10312229b59061228074f93683fe2261 en-us male
Russell voices/_6Aslh2DxfmnRLmP.safetensors beae9e4030efce0a935fcb0f807dc0784d598bcaa87ad4aacb047436b1eaa411 en-us male
Preston voices/qIxlZ7Ei8KvWlyQH.safetensors 6e04b84d3ae8751b22bca0a4f63936f9cb648f4f2182b2d0788071ea1c0b68a1 en-us male
Miles voices/d9XCyhjo4JhkHF1d.safetensors eab20e4f159b48cfbc01b1885804e24f681f80e7a64f909182763a4e3361bee8 en-us male
Maeve voices/6PWnV0Nq4wu7RVBT.safetensors 56494ee67d533ad227ff0dfd0b1c13ff5aa91f1ad6e596e66c2986ba3005e844 en-gb female
Freya voices/GgfEkEJtxZR7gnpy.safetensors eb3cd47418349e618408c16bea7153988b3ad04b41a6033039dd474a9a4dcd23 en-gb female
Pippa voices/uem82D50GRv2Dwma.safetensors d41a8f01d6e33450f1ce062d332914da10beee99e585dab531750706f187c48a en-gb female
Elodie-Rose voices/y9bQqIDnyxwqK01t.safetensors 21eade9d83b2e66b9364e1b6cd999a3a6704c000a71f27f34e05ace5cdfd58bb en-gb female
Imogen voices/gDT1nz7Ie36ZhL-C.safetensors 1980ab3c35fa98a25b5f3b5e643fd852e14ca8a12a4a26b06aaf1abf2a8c68b1 en-gb female
Lottie voices/m9MxpY6IUGXtWp76.safetensors a906ebf17e43fded12b3d6bd077abb2df33bc86eff51c1beeb82e5d416527db3 en-gb female
Alfie voices/X6NceqF285Gr04gb.safetensors 4de75497fff3030168e8aa6dca23ffeb332b1bc8c25d0bf3507ceadac953ae9c en-gb male
Hugh voices/YyKcxPG7iIgS3jsK.safetensors 3169d74b47a54d1eb15e4c243f56393abe9fe90c7e1c308ecf96ee67618b152a en-gb male
Edwin voices/zUCxzrOrugTSis03.safetensors ff1d0735b8b45710462b1d1f846da040f970c6eb62c7c2f36dbfb8e5bde74f1e en-gb male
Spencer voices/JOW0vchyswkQUMDS.safetensors cc9ac1107b0ffce282374a19beb7bfa95847724e6b7afbac991b4ae4277d5e6d en-gb male
Toby voices/dME3IWyZBvmh1n1q.safetensors 09ab315c7f4b72b4d71e66cb756184cedfb1f14d222d096e115bf95bfe7b29dc en-gb male
Teddy voices/Zn7g6NxLqXlJb7Qi.safetensors baea0ea9506f3237a5ebfc61afb7c72dbd189c5603b199ef42fbb00dbda379e0 en-gb male
Bryony voices/gfP44RlHwr2kEwk7.safetensors bcc207a73b7885e45b49ab633d3377a10158292e3a7cef9cd6764ad107f1cb9b en-gb female
VOICES
mv "$model/voices.tsv.part" "$model/voices.tsv"

printf 'Installed Phonon in %s.\nIn Rail Narrator choose Settings ▸ Speech ▸ Engine ▸ Phonon, and restart when asked.\n' "$dir"
