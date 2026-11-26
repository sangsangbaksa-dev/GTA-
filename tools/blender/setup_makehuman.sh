#!/usr/bin/env bash
# Installs MPFB2 (MakeHuman Plugin For Blender 2, v2.0.18) and CC0 MakeHuman asset packs
# into Blender 4.2's user extension directory for headless character builds.
set -euo pipefail

BLENDER_BIN="${BLENDER_BIN:-blender}"
CACHE_DIR="${MAKEHUMAN_CACHE_DIR:-/tmp/harbor-tools/makehuman}"
EXT_DIR="${HOME}/.config/blender/4.2/extensions/user_default"
MPFB_TAG="v2.0.18"

mkdir -p "${CACHE_DIR}/packs" "${EXT_DIR}"

if [ ! -d "${CACHE_DIR}/mpfb2" ]; then
    git clone --depth 1 --branch "${MPFB_TAG}" https://github.com/makehumancommunity/mpfb2.git "${CACHE_DIR}/mpfb2"
fi

ln -sfn "${CACHE_DIR}/mpfb2/src/mpfb" "${EXT_DIR}/mpfb"

PACKS=(
    "makehuman_system_assets/makehuman_system_assets_cc0.zip"
    "glasses01/glasses01_cc0.zip"
    "hair01/hair01_cc0.zip"
    "pants01/pants01_cc0.zip"
    "shirts01/shirts01_cc0.zip"
    "shoes01/shoes01_cc0.zip"
    "suits01/suits01_cc0.zip"
)

for rel in "${PACKS[@]}"; do
    fn="$(basename "${rel}")"
    dest="${CACHE_DIR}/packs/${fn}"
    if [ ! -f "${dest}" ]; then
        echo "[makehuman] downloading ${fn}..."
        curl -fSL "https://files.makehumancommunity.org/asset_packs/${rel}" -o "${dest}" \
            || curl -fSL "https://files2.makehumancommunity.org/asset_packs/${rel}" -o "${dest}"
    fi
done

"${BLENDER_BIN}" -b --factory-startup --python-expr '
import glob, importlib, os, bpy
bpy.ops.preferences.addon_enable(module="bl_ext.user_default.mpfb")
mpfb_mod = importlib.import_module("bl_ext.user_default.mpfb")
S = mpfb_mod.MPFB_CONTEXTUAL_INFORMATION["SERVICES"]
user_data = S["LocationService"].get_user_data()
os.makedirs(user_data, exist_ok=True)
for zp in sorted(glob.glob(os.path.join(os.environ.get("MAKEHUMAN_CACHE_DIR", "/tmp/harbor-tools/makehuman"), "packs", "*_cc0.zip"))):
    marker = os.path.join(user_data, ".installed_" + os.path.basename(zp))
    if not os.path.exists(marker):
        print("[makehuman] extracting", os.path.basename(zp))
        S["AssetService"].fix_and_extract_asset_pack_zip(zp, user_data)
        open(marker, "w").close()
S["AssetService"].update_all_asset_lists()
print("[makehuman] ready:", user_data)
'
