#!/bin/bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
arch="${1:?use x64 or arm64}"; out="${2:?supply an output directory}"
case "$arch" in x64) cpu=x86_64;; arm64) cpu=arm64;; *) exit 2;; esac
[[ "$(uname -s)" == Darwin && "$(uname -m)" == "$cpu" ]] || { echo "Build on a native macOS $cpu host." >&2; exit 2; }
export MACOSX_DEPLOYMENT_TARGET=13.0
mkdir -p "$out"; out="$(cd "$out" && pwd)"; app="$out/DiscArchive.app"
[[ ! -e "$app" ]] || { echo "Use a new output directory." >&2; exit 2; }
contents="$app/Contents"; helpers="$contents/Resources/Runtime/$arch"; frameworks="$contents/Frameworks"; bin="$contents/MacOS"
mkdir -p "$bin" "$frameworks" "$helpers" "$contents/Resources"
cp "$repo/macos/Info.plist" "$contents/Info.plist"; plutil -lint "$contents/Info.plist"
flags=(-arch "$cpu" -mmacosx-version-min=13.0 -fobjc-arc -fblocks -framework Foundation -framework IOKit -framework DiskArbitration -framework DiscRecording)
build_devices() {
xcrun clang "$repo/macos/Devices/DiscDevices.m" "${flags[@]}" -O2 -dynamiclib -Wl,-install_name,@rpath/libDiscDevices.dylib -o "$frameworks/libDiscDevices.dylib"
xcrun clang "$repo/macos/Devices/DiscDevices.m" "${flags[@]}" -DDA_DEVICES_TEST -o "$out/test-devices"
"$out/test-devices"
}
archive="$repo/vendor/7zip/downloads/7z2606-src.7z"
[[ "$(shasum -a 256 "$archive" | awk '{print $1}')" == e0364d39e8208557d939472c9a12d02ed1c12f45ca402e3b408bcaa63b36dd93 ]] || { echo "Unexpected upstream source digest" >&2; exit 1; }
swiftflags=(-swift-version 5 -target "$cpu-apple-macos13.0" -framework AppKit -framework AVFoundation \
  -framework Vision -framework Security -framework ImageIO -framework CoreMedia -framework CoreImage -framework UserNotifications)
build_ui() {
xcrun swiftc "${swiftflags[@]}" -O -parse-as-library "$repo"/macos/App/*.swift "$repo"/macos/Capture/*.swift -o "$bin/DiscArchive"
xcrun swiftc "${swiftflags[@]}" -parse-as-library "$repo"/macos/Capture/*.swift \
  "$repo"/macos/Capture/tests/CapturePrivacyTests.swift -o "$out/test-capture"
"$out/test-capture"
}
test_ipc() {
  xcrun swiftc -swift-version 5 "$repo/macos/App/IPC.swift" "$repo/macos/App/tests/IPCIntegrationTests.swift" -o "$out/test-ipc"
  "$out/test-ipc"
}
mkdir -p "$out/fixtures"
export TMPDIR="$(cd "$out/fixtures" && pwd -P)/"
export DISCARC_MEDIA_HELPER="$bin/DiscArchive" DISCARC_DEVICE_LIB="$frameworks/libDiscDevices.dylib"
export DISCARCHIVE_NATIVE_DIR="$helpers/native" DISCARC_STATE_DIR="$out/synthetic-state" DOTNET_CLI_TELEMETRY_OPTOUT=1
build_archive() {
  bash "$repo/macos/Archive/build-native.sh" "$arch" "$helpers/native"
  bash "$repo/macos/Archive/run-tests.sh" "$helpers/native"
}
build_core() {
  dotnet publish "$repo/macos/Core/DiscArchiveCore.csproj" -c Release -r "osx-$arch" --self-contained true \
    -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=false -o "$helpers"
  dotnet run -c Release --project "$repo/macos/tests/MountedSourceTests.csproj"
  dotnet run -c Release --project "$repo/macos/tests/CoreBehaviorTests.csproj"
}
# Both the workbench and mounted-directory tests use the same SDK-built bridge.
build_devices
build_ui >"$out/ui.log" 2>&1 & ui_pid=$!
test_ipc >"$out/ipc.log" 2>&1 & ipc_pid=$!
build_archive >"$out/archive.log" 2>&1 & archive_pid=$!
build_core >"$out/core.log" 2>&1 & core_pid=$!
result=0
for component in ui:$ui_pid ipc:$ipc_pid archive:$archive_pid core:$core_pid; do
  name="${component%:*}"
  if wait "${component#*:}"; then
    echo "$name passed"; tail -n 3 "$out/$name.log"
  else
    tail -n 100 "$out/$name.log"; echo "Failed to build or test $name" >&2; result=1
  fi
done
[[ -x "$bin/DiscArchive" && -x "$helpers/discarchive-core" && -s "$frameworks/libDiscDevices.dylib" && -s "$helpers/native/libdiscarchive7z.dylib" ]] || exit 1
cp "$repo/LICENSE" "$contents/Resources/LICENSE-DiscArchive"
cp "$repo/vendor/7zip/LICENSE-7zip.txt" "$repo/vendor/7zip/COPYING-LGPL-2.1.txt" "$repo/vendor/7zip/unRarLicense.txt" "$archive" "$contents/Resources/"
cp "$repo/macos/README.md" "$repo/macos/THIRD-PARTY.txt" "$contents/Resources/"
dotroot="$(dotnet --list-sdks | sed -n '1s/.*\[\(.*\)\/sdk\].*/\1/p')"
for file in LICENSE.txt ThirdPartyNotices.txt; do
  cp "$dotroot/$file" "$contents/Resources/dotnet-$file"
done
codesign --force --sign - --timestamp=none "$frameworks/libDiscDevices.dylib"
codesign --force --sign - --timestamp=none "$helpers/native/7z.so"
codesign --force --sign - --timestamp=none "$helpers/native/libdiscarchive7z.dylib"
codesign --force --sign - --timestamp=none "$helpers/discarchive-core"
for native in "$helpers"/*.dylib "$helpers"/native/*; do
  codesign --verify --strict "$native" || codesign --force --sign - --timestamp=none "$native"
  codesign --verify --strict "$native"
done
codesign --force --sign - --timestamp=none "$app"
codesign --verify --deep --strict "$app"
"$bin/DiscArchive" --check-package
python3 - "$out/fixtures/label.png" <<'PY'
import struct, sys, zlib
def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
header = struct.pack(">IIBBBBB", 16, 16, 8, 2, 0, 0, 0)
pixels = (b"\0" + bytes((190, 205, 218)) * 16) * 16
with open(sys.argv[1], "wb") as output:
    output.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b""))
PY
"$bin/DiscArchive" thumbnail "$out/fixtures/label.png" "$out/fixtures/thumbnail.jpg"
sips -g format "$out/fixtures/thumbnail.jpg" | grep -q 'format: jpeg'
"$helpers/discarchive-core" devices
"$bin/DiscArchive" --check-workbench
[[ $result == 0 ]] || exit 1
printf 'Built and tested %s slice in %s\n' "$cpu" "$app"
