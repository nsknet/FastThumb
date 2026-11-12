#!/bin/sh
# zig cc as FFmpeg's compiler, with the two places where it is stricter than gcc smoothed over:
#  - FFmpeg's mingw and darwin configure add a few linker flags meant for executables that zig's linker
#    driver refuses; they are irrelevant for static libraries;
#  - configure passes a library's .a file on compile-only (-c) command lines too; gcc ignores it there,
#    zig treats it as a second input and fails.
compile_only=0
for arg in "$@"; do [ "$arg" = "-c" ] && compile_only=1; done
for arg in "$@"; do
  shift
  case "$arg" in
    -Wl,--pic-executable*|-Wl,--image-base*|-Wl,--nxcompat*|-Wl,-dynamic*) ;;
    *.a) [ "$compile_only" = 1 ] || set -- "$@" "$arg" ;;
    *) set -- "$@" "$arg" ;;
  esac
done
exec zig cc -target "$TV_ZIG_TARGET" "$@"
