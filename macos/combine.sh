#!/bin/bash
set -euo pipefail
[[ "$(uname -s)" == Darwin ]] || exit 2
x64="${1:?Intel .app}"; arm="${2:?arm64 .app}"; out="${3:?output directory}"
mkdir -p "$out"; out="$(cd "$out" && pwd)"; app="$out/DiscArchive.app"
[[ ! -e "$app" ]] || { echo 'Use a new output directory.' >&2; exit 2; }
cmp "$x64/Contents/Info.plist" "$arm/Contents/Info.plist"
ditto "$arm" "$app"
ditto "$x64/Contents/Resources/Runtime/x64" "$app/Contents/Resources/Runtime/x64"
for part in MacOS/DiscArchive Frameworks/libDiscDevices.dylib; do
  lipo -create "$x64/Contents/$part" "$arm/Contents/$part" -output "$app/Contents/$part"
  lipo "$app/Contents/$part" -verify_arch x86_64 arm64
done
for item in 'x64 x86_64' 'arm64 arm64'; do
  set -- $item
  for part in discarchive-core native/7z.so native/libdiscarchive7z.dylib libcoreclr.dylib; do
    lipo "$app/Contents/Resources/Runtime/$1/$part" -verify_arch "$2"
  done
done
codesign --force --sign - --timestamp=none "$app/Contents/Frameworks/libDiscDevices.dylib"
codesign --force --sign - --timestamp=none "$app"
codesign --verify --deep --strict "$app"
"$app/Contents/MacOS/DiscArchive" --check-package
"$app/Contents/MacOS/DiscArchive" --check-workbench
# Ship the same human-readable handbook alongside the app, accessible in Finder.
delivery="$out/distribution"
mkdir "$delivery"
ditto "$app" "$delivery/DiscArchive.app"
cp "$app/Contents/Resources/使用说明.txt" "$delivery/"
(cd "$delivery" && /usr/bin/ditto -c -k . "$out/ditto.zip")
# ditto writes UTF-8 entry names without the ZIP language-encoding bit.
# Keep its permissions, symlinks and metadata, and encode names unambiguously
# for both Finder and ZIP readers on other platforms.
python3 - "$out/ditto.zip" "$out/DiscArchive-macOS-Universal.zip" <<'PY'
from copy import copy
from zipfile import ZipFile
import sys
with ZipFile(sys.argv[1]) as source, ZipFile(sys.argv[2], "x") as target:
    target.comment = source.comment
    for original in source.infolist():
        info = copy(original)
        if not (info.flag_bits & 0x800) and not info.filename.isascii():
            info.filename = info.filename.encode("cp437").decode("utf-8")
        target.writestr(info, source.read(original))
PY
unpacked="$(mktemp -d "$out/verify-zip.XXXXXXXX")"
ditto -x -k "$out/DiscArchive-macOS-Universal.zip" "$unpacked"
cmp "$unpacked/使用说明.txt" "$app/Contents/Resources/使用说明.txt"
cmp "$unpacked/DiscArchive.app/Contents/Resources/使用说明.txt" "$unpacked/使用说明.txt"
codesign --verify --deep --strict "$unpacked/DiscArchive.app"
"$unpacked/DiscArchive.app/Contents/MacOS/DiscArchive" --check-package
(cd "$out" && shasum -a 256 DiscArchive-macOS-Universal.zip > SHA256.txt)
