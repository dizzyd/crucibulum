#!/usr/bin/env bash
#
# Build the release zip and drop it into a Cairn pack, replacing whatever is there.
#
#   bash scripts/install-to-pack.sh [pack-id]      (default: crucibulum)
#
# The pack loads it through --addModPath, so it is not a ModDB-managed mod and will keep
# showing as "0 mods" in `cairn-cli list`. That is expected.

set -euo pipefail

PACK="${1:-crucibulum}"
PACK_MODS="$HOME/.cairn/packs/$PACK/Mods"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ ! -d "$HOME/.cairn/packs/$PACK" ]; then
    echo "no such pack: $PACK" >&2
    echo "make one with: cairn-cli init \"Crucibulum playtest\" --id $PACK --game 1.22.7" >&2
    exit 1
fi

: "${VINTAGE_STORY:=$(ls -d "$HOME"/.cairn/games/*.app | sort -V | tail -1)}"
export VINTAGE_STORY
echo "building against $VINTAGE_STORY"

cd "$REPO"
rm -rf Releases crucibulum/bin/Release
./build.sh >/dev/null

ZIP="$(ls "$REPO"/Releases/crucibulum_*.zip | head -1)"
mkdir -p "$PACK_MODS"
rm -f "$PACK_MODS"/crucibulum_*.zip
cp "$ZIP" "$PACK_MODS/"

echo "installed $(basename "$ZIP") -> $PACK_MODS"
echo "launch with: ~/src/cairn/artifacts/osx-arm64/cairn-cli launch $PACK"
