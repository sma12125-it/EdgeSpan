#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/publish/win-x64}"
rm -rf "$out"
mkdir -p "$out"
# Framework-dependent: the .NET 8 runtime stays on the PC, so the installer
# can stay small enough to transfer. Trimming requires a self-contained app.
dotnet publish "$root/src/EdgeSpan.Windows/EdgeSpan.Windows.csproj" \
  -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "$out"
dex="$root/publish/android-server/edgestpan-server.dex"
if [[ -f "$dex" ]]; then
  cp "$dex" "$out/edgestpan-server.dex"
fi
adb_src="${EDGESPAN_ADB_DIR:-/tmp/edgestpan-ptwin/platform-tools}"
if [[ -f "$adb_src/adb.exe" ]]; then
  cp "$adb_src/adb.exe" "$adb_src/AdbWinApi.dll" "$adb_src/AdbWinUsbApi.dll" "$out/"
fi
echo "Published EdgeSpan.exe to $out"
stage="$root/publish/win-x64"
if [[ "$out" != "$stage" ]]; then
  mkdir -p "$stage"
  cp -f "$out/EdgeSpan.exe" "$stage/EdgeSpan.exe"
  for extra in adb.exe AdbWinApi.dll AdbWinUsbApi.dll edgestpan-server.dex; do
    if [[ -f "$out/$extra" ]]; then
      cp -f "$out/$extra" "$stage/$extra"
    fi
  done
fi
if command -v makensis >/dev/null 2>&1; then
  makensis "$root/installer/edgestpan.nsi"
  echo "Installer: $root/publish/EdgeSpan-Setup.exe"
fi
