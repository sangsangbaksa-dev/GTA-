#!/usr/bin/env bash
set -euo pipefail

test -s public/release.json
test -s public/index.html
test -s public/godot/harbor.wasm
test -s public/godot/harbor.pck
test -s public/runtime/_framework/Harbor.Core.wasm
test -s public/world/harbor_front.json
