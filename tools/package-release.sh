#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
    echo "Usage: $0 RELEASE_DIRECTORY" >&2
    exit 2
fi

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
godot=${GODOT_BIN:-godot}
engine_version=$("$godot" --version)
if [[ "$engine_version" != 4.4.1.stable.mono.* ]]; then
    echo "Godot 4.4.1 .NET is required. Found $engine_version." >&2
    exit 1
fi

mkdir -p -- "$1"
output=$(cd -- "$1" && pwd)
staging=$(mktemp -d)
trap 'rm -rf -- "$staging"' EXIT

dotnet_root=$(dirname -- "$(readlink -f "$(command -v dotnet)")")
notices="$staging/third-party"
mkdir -- "$notices"
curl --fail --silent --show-error --location --retry 3 \
    --output "$notices/Godot-LICENSE.txt" \
    https://raw.githubusercontent.com/godotengine/godot/4.4.1-stable/LICENSE.txt
curl --fail --silent --show-error --location --retry 3 \
    --output "$notices/Godot-COPYRIGHT.txt" \
    https://raw.githubusercontent.com/godotengine/godot/4.4.1-stable/COPYRIGHT.txt
cp -- "$dotnet_root/LICENSE.txt" "$notices/dotnet-LICENSE.txt"
cp -- "$dotnet_root/ThirdPartyNotices.txt" "$notices/dotnet-ThirdPartyNotices.txt"
for notice in "$repo"/game/assets/fonts/*-OFL.txt; do
    if [[ -f "$notice" ]]; then cp -- "$notice" "$notices/"; fi
done

"$godot" --headless --path "$repo/game" --editor --import

export_game() {
    local preset=$1 executable=$2 platform=$3 runtime_library=$4
    local package="$staging/$platform/Project Harbor"
    mkdir -p -- "$package"
    "$godot" --headless --path "$repo/game" --export-release "$preset" "$package/$executable"
    test -s "$package/$executable"
    test -s "$package/ProjectHarbor.pck"
    test -s "$package/data_Harbor_${platform}_x86_64/Harbor.dll"
    test -s "$package/data_Harbor_${platform}_x86_64/$runtime_library"
    cp -R -- "$notices" "$package/third-party"
    cp -- "$repo/docs/desktop-playtest.txt" "$package/README.txt"
}

export_game "Linux x86_64" ProjectHarbor.x86_64 linuxbsd libcoreclr.so
export_game "Windows x86_64" ProjectHarbor.exe windows coreclr.dll

linux_archive=project-harbor-linux-x86_64.tar.gz
windows_archive=project-harbor-windows-x86_64.zip
tar -czf "$staging/$linux_archive" -C "$staging/linuxbsd" 'Project Harbor'
(cd -- "$staging/windows" && zip -qr "$staging/$windows_archive" 'Project Harbor')
(cd -- "$staging" && sha256sum "$linux_archive" "$windows_archive" > SHA256SUMS)
mv -- "$staging/$linux_archive" "$staging/$windows_archive" "$staging/SHA256SUMS" "$output/"
