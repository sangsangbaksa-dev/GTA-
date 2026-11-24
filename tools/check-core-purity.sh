#!/usr/bin/env bash
# Fails if the engine-agnostic core references Godot. See docs/adr/0002-portable-core.md
set -euo pipefail
cd "$(dirname "$0")/.."
if grep -rnE 'using[[:space:]]+Godot|Godot\.NET\.Sdk|GodotSharp' core/ --include='*.cs' --include='*.csproj'; then
  echo "ERROR: core/ must not reference Godot." >&2
  exit 1
fi
echo "core purity OK"
