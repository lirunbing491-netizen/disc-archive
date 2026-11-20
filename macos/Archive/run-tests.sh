#!/bin/bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
export DISCARCHIVE_NATIVE_DIR="${1:?absolute native directory}"
work="$(mktemp -d "${TMPDIR:-/tmp}/discarchive-fixtures.XXXXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir "$work/content"
printf hello > "$work/content/café-文.txt"
if [[ "$(uname -s)" == Darwin ]]; then
  hdiutil makehybrid -quiet -udf -udf-volume-name SYNTHETIC_UDF -o "$work/udf.iso" "$work/content"
  hdiutil makehybrid -quiet -iso -joliet -joliet-volume-name SYNTHETIC_JOLIET -o "$work/joliet.iso" "$work/content"
  dotnet run --project "$here/ArchiveBackend.Tests.csproj" -- ISO "$work/joliet.iso" UDF "$work/udf.iso"
else
  dotnet run --project "$here/ArchiveBackend.Tests.csproj"
fi
