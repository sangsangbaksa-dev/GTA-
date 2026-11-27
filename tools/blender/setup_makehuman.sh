#!/usr/bin/env bash
# Install the same MPFB2 release and CC0 asset packs used by the Harbor character generator.
set -euo pipefail

cache=${MAKEHUMAN_CACHE_DIR:-${TMPDIR:-/tmp}/harbor-makehuman}
export BLENDER_USER_RESOURCES=${BLENDER_USER_RESOURCES:-"$cache/blender"}
export MAKEHUMAN_CACHE_DIR=$cache
blender=${BLENDER_BIN:-blender}

mkdir -p "$cache/packs" "$BLENDER_USER_RESOURCES/extensions/user_default"
if [[ ! -d "$cache/mpfb2" ]]; then
    git clone --depth 1 --branch v2.0.18 https://github.com/makehumancommunity/mpfb2.git "$cache/mpfb2"
fi
ln -sfn "$(realpath "$cache/mpfb2/src/mpfb")" "$BLENDER_USER_RESOURCES/extensions/user_default/mpfb"

for pack in makehuman_system_assets glasses01 hair01 pants01 shirts01 shoes01 suits01; do
    file="${pack}_cc0.zip"
    dest="$cache/packs/$file"
    if ! unzip -tq "$dest" >/dev/null 2>&1; then
        for host in files.makehumancommunity.org files2.makehumancommunity.org; do
            if curl -fSL --retry 2 "https://$host/asset_packs/$pack/$file" -o "$dest.part"; then
                unzip -tq "$dest.part"
                mv "$dest.part" "$dest"
                break
            fi
        done
        test -s "$dest"
    fi
done

"$blender" -b --factory-startup --python-exit-code 1 --python-expr '
import importlib
import os
from pathlib import Path

import bpy

module = "bl_ext.user_default.mpfb"
bpy.ops.preferences.addon_enable(module=module)
services = importlib.import_module(module).MPFB_CONTEXTUAL_INFORMATION["SERVICES"]
data = Path(services["LocationService"].get_user_data())
data.mkdir(parents=True, exist_ok=True)
for archive in sorted(Path(os.environ["MAKEHUMAN_CACHE_DIR"], "packs").glob("*_cc0.zip")):
    marker = data / (".installed_" + archive.name)
    if not marker.exists():
        services["AssetService"].fix_and_extract_asset_pack_zip(str(archive), str(data))
        marker.touch()
print("[harbor] MakeHuman asset packs installed:", data)
'
