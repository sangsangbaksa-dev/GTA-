#!/usr/bin/env bash
# Copies prop .glb files from a local checkout of sangsangbaksa-dev/Set into game/assets/props/.
# Usage: tools/sync-props.sh [path-to-Set-checkout]   (default: ../set)
set -euo pipefail
src="${1:-$(dirname "$0")/../../set}/props"
dst="$(dirname "$0")/../game/assets/props"
[ -d "$src" ] || { echo "Set props not found at $src" >&2; exit 1; }
mkdir -p "$dst"
cp "$src"/*.glb "$dst"/
cp "$src"/manifest.json "$dst"/manifest.json
( cd "$src/.." && git rev-parse HEAD 2>/dev/null ) > "$dst/SOURCE_COMMIT" || true
echo "synced $(ls "$dst"/*.glb | wc -l) props from $src"
