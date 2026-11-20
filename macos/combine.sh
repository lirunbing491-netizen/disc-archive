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
(cd "$delivery" && /usr/bin/ditto -c -k . "$out/DiscArchive-macOS-Universal.zip")
(cd "$out" && shasum -a 256 DiscArchive-macOS-Universal.zip > SHA256.txt)
