#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
    echo "Usage: $0 RELEASE_DIRECTORY" >&2
    exit 2
fi

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p -- "$1"
output=$(cd -- "$1" && pwd)
staging=$(mktemp -d)
trap 'rm -rf -- "$staging"' EXIT

package="$staging/Project Harbor Server"
dotnet publish "$repo/server/Harbor.Server.csproj" \
    --configuration Release --runtime linux-x64 --self-contained true \
    --property:DebugType=None --output "$package"

test -x "$package/Harbor.Server"
test -s "$package/libcoreclr.so"
test -s "$package/data/world/harbor_front.json"
test -s "$package/game/data/missions/harbor_lockdown.json"
cp -- "$repo/server/README.md" "$package/README.md"

dotnet_root=$(dirname -- "$(readlink -f "$(command -v dotnet)")")
mkdir -- "$package/third-party"
cp -- "$dotnet_root/LICENSE.txt" "$package/third-party/dotnet-LICENSE.txt"
cp -- "$dotnet_root/ThirdPartyNotices.txt" "$package/third-party/dotnet-ThirdPartyNotices.txt"

archive=project-harbor-server-linux-x86_64.tar.gz
tar -czf "$staging/$archive" -C "$staging" 'Project Harbor Server'
mv -- "$staging/$archive" "$output/"
(cd -- "$output" && sha256sum "$archive")
