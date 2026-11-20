#!/bin/bash
# Usage: bash macos/Archive/build-native.sh arm64|x64 /absolute/output [extracted-source]
# Builds only repository source; no download and no package/system installation.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
arch="${1:?arm64 or x64}"; out="${2:?absolute output directory}"
[[ "$out" == /* ]] || { echo 'Output directory must be absolute' >&2; exit 2; }
case "$arch" in arm64) apple_arch=arm64;; x64) apple_arch=x86_64;; *) exit 2;; esac
mkdir -p "$out"
work="$(mktemp -d "${TMPDIR:-/tmp}/discarchive7z.XXXXXXXX")"
trap 'rm -rf "$work"' EXIT
src="$work/source"; mkdir -p "$src"
if [[ -n "${3:-}" ]]; then
  cp -R "$3/." "$src/"
else
  # macOS bsdtar supports .7z. CI can instead pass an already extracted tree.
  tar -xf "$repo/vendor/7zip/downloads/7z2606-src.7z" -C "$src"
fi
if [[ "$(uname -s)" == Darwin ]]; then
  cc=clang; cxx=clang++; extra="-arch $apple_arch"; bridge=libdiscarchive7z.dylib
  link='-dynamiclib -fPIC'; dl=''
else
  [[ "$(uname -m)" == x86_64 && "$arch" == x64 || "$(uname -m)" == aarch64 && "$arch" == arm64 ]] || { echo 'Linux validation requires matching architecture' >&2; exit 2; }
  cc=cc; cxx=c++; extra=""; bridge=libdiscarchive7z.so; link='-shared -fPIC'; dl='-ldl'
fi
make -C "$src/CPP/7zip/Bundles/Format7zF" -f makefile.gcc -j "${JOBS:-4}" \
  CC="$cc" CXX="$cxx" MY_ARCH="$extra" LDFLAGS="$link" LFLAGS_STRIP= \
  LFLAGS_NOEXECSTACK= LFLAGS_SRC_VER=
cp "$src/CPP/7zip/Bundles/Format7zF/_o/7z.so" "$out/7z.so"
# MyWindows.cpp supplies the same Unix PROPVARIANT/BSTR allocation ABI.
$cxx -std=c++17 -O2 -fPIC $extra $link -I "$src" \
  "$here/native/discarchive7z.cpp" "$src/CPP/Common/MyWindows.cpp" \
  $dl -o "$out/$bridge"
test -s "$out/7z.so"
test -s "$out/$bridge"
printf 'Built %s and 7z.so in %s\n' "$bridge" "$out"
