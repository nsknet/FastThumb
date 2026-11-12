#!/usr/bin/env sh
# Checks the layout of the packed FastThumb package: both target frameworks, the readme and notices, and thumbvideo for
# every runtime identifier at runtimes/<rid>/native (nowhere else). Set RIDS to check a different list.
#
#   sh eng/check-package.sh <package dir>
set -eu
DIR="${1:?usage: check-package.sh <package dir>}"
RIDS="${RIDS:-linux-x64 linux-arm64 win-x64 win-arm64 osx-x64 osx-arm64}"
package="$(ls "$DIR"/FastThumb.[0-9]*.nupkg | head -n 1)"
echo "checking $package"
list="$(python3 -c 'import sys, zipfile; print("\n".join(zipfile.ZipFile(sys.argv[1]).namelist()))' "$package" 2>/dev/null \
  || python -c 'import sys, zipfile; print("\n".join(zipfile.ZipFile(sys.argv[1]).namelist()))' "$package")"
fail=0
need() { printf '%s\n' "$list" | grep -qx "$1" || { echo "missing: $1" >&2; fail=1; }; }
need lib/net8.0/FastThumb.dll
need lib/net10.0/FastThumb.dll
need lib/net10.0/FastThumb.xml
need README.md
need THIRD-PARTY-NOTICES.md
for rid in $RIDS; do
  case "$rid" in
    win-*) need "runtimes/$rid/native/thumbvideo.dll" ;;
    osx-*) need "runtimes/$rid/native/libthumbvideo.dylib" ;;
    *)     need "runtimes/$rid/native/libthumbvideo.so" ;;
  esac
done
if printf '%s\n' "$list" | grep -E '^(content|contentFiles)/' >/dev/null; then echo "unexpected content files" >&2; fail=1; fi
[ "$fail" = 0 ] && echo "package layout ok"
exit "$fail"
