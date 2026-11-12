#!/usr/bin/env sh
# Copies the thumbvideo artifacts of the native workflow (one folder per runtime identifier, named thumbvideo-<rid>)
# to src/FastThumb/runtimes/<rid>/native, where the build and the package expect them.
#
#   sh eng/place-natives.sh <artifact dir>
set -eu
SOURCE="${1:?usage: place-natives.sh <artifact dir>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
found=0
for dir in "$SOURCE"/thumbvideo-*; do
  [ -d "$dir" ] || continue
  rid="${dir##*/thumbvideo-}"
  mkdir -p "$ROOT/src/FastThumb/runtimes/$rid/native"
  cp -R "$dir"/. "$ROOT/src/FastThumb/runtimes/$rid/native/"
  found=$((found + 1))
done
[ "$found" -gt 0 ] || { echo "no thumbvideo-* folders in $SOURCE" >&2; exit 1; }
find "$ROOT/src/FastThumb/runtimes" -type f -exec ls -l {} \;
