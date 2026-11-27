#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CACHE_DIR="${HARBOR_TOOLS_CACHE:-/tmp/harbor-tools}"
GODOT_VERSION="4.4.1"
DOTNET_DIR="$CACHE_DIR/dotnet"
GODOT_DIR="$CACHE_DIR/godot-std"
GODOT_BIN="$GODOT_DIR/Godot_v${GODOT_VERSION}-stable_linux.x86_64"
TEMPLATES_DIR="${HOME}/.local/share/godot/export_templates/${GODOT_VERSION}.stable"

mkdir -p "$CACHE_DIR"

# 0. Ensure Git LFS binary assets (.glb, .ogg, .ttf) are materialized
MAGIC="$(head -c 4 "$ROOT/game/assets/characters/chr_harbor_clerk.glb" || true)"
if [ "$MAGIC" != "glTF" ]; then
  echo "[vercel-build] Pulling Git LFS assets..."
  if ! command -v git-lfs >/dev/null 2>&1 && ! git lfs version >/dev/null 2>&1; then
    curl -fL --retry 3 \
      "https://github.com/git-lfs/git-lfs/releases/download/v3.5.1/git-lfs-linux-amd64-v3.5.1.tar.gz" \
      -o "$CACHE_DIR/git-lfs.tar.gz"
    mkdir -p "$CACHE_DIR/git-lfs"
    tar -xzf "$CACHE_DIR/git-lfs.tar.gz" -C "$CACHE_DIR/git-lfs" --strip-components=1
    export PATH="$CACHE_DIR/git-lfs:$PATH"
  fi
  git -C "$ROOT" lfs pull
fi

# 1. .NET 8 SDK + wasm-tools workload
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if [ ! -x "$DOTNET_DIR/dotnet" ]; then
  echo "[vercel-build] Installing .NET 8 SDK..."
  mkdir -p "$DOTNET_DIR"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$CACHE_DIR/dotnet-install.sh"
  bash "$CACHE_DIR/dotnet-install.sh" --channel 8.0 --install-dir "$DOTNET_DIR"
fi
export DOTNET_ROOT="$DOTNET_DIR"
export DOTNET="$DOTNET_DIR/dotnet"
export PATH="$DOTNET_DIR:$PATH"

if ! "$DOTNET" workload list | grep -q "wasm-tools"; then
  echo "[vercel-build] Installing .NET 8 wasm-tools workload..."
  "$DOTNET" workload install wasm-tools --skip-manifest-update
fi

# 2. Standard (non-.NET) Godot 4.4.1 Linux x86_64 + Web export template
if [ ! -x "$GODOT_BIN" ]; then
  echo "[vercel-build] Downloading standard Godot ${GODOT_VERSION}..."
  mkdir -p "$GODOT_DIR"
  curl -fL --retry 3 \
    "https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}-stable/Godot_v${GODOT_VERSION}-stable_linux.x86_64.zip" \
    -o "$CACHE_DIR/godot-std.zip"
  node "$ROOT/tools/extract-zip.mjs" "$CACHE_DIR/godot-std.zip" "$GODOT_DIR"
  chmod +x "$GODOT_BIN"
  rm -f "$CACHE_DIR/godot-std.zip"
fi
export GODOT="$GODOT_BIN"

if [ ! -f "$TEMPLATES_DIR/web_nothreads_release.zip" ]; then
  echo "[vercel-build] Downloading Godot ${GODOT_VERSION} Web export template..."
  mkdir -p "$TEMPLATES_DIR"
  curl -fL --retry 3 \
    "https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}-stable/Godot_v${GODOT_VERSION}-stable_export_templates.tpz" \
    -o "$CACHE_DIR/templates.tpz"
  node "$ROOT/tools/extract-zip.mjs" "$CACHE_DIR/templates.tpz" "$TEMPLATES_DIR" \
    "web_nothreads_release.zip" "web_nothreads_debug.zip" "web_release.zip" "web_debug.zip" "version.txt"
  rm -f "$CACHE_DIR/templates.tpz"
fi

# 3. Build web/dist
echo "[vercel-build] Building Project Harbor web bundle..."
cd "$ROOT/web"
npm ci
npm run build
