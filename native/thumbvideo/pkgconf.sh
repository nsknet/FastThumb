#!/bin/sh
# Stand-in for pkg-config while configuring FFmpeg. The only external library is dav1d, which build.sh
# has just built, so the answers are known: TV_DAV1D_INCLUDE and TV_DAV1D_LIB come from build.sh.
mode=""
known=1
for arg in "$@"; do
  case "$arg" in
    --version) echo "2.0"; exit 0 ;;
    --exists|--cflags|--libs) mode="$arg" ;;
    --variable=includedir) mode="$arg" ;;
    --*) ;;
    dav1d*|">="|[0-9]*) ;;
    *) known=0 ;;
  esac
done
[ "$known" = 1 ] || exit 1
case "$mode" in
  --cflags) echo "-I$TV_DAV1D_INCLUDE" ;;
  --libs) echo "$TV_DAV1D_LIB" ;;
  --variable=includedir) echo "$TV_DAV1D_INCLUDE" ;;
esac
exit 0
