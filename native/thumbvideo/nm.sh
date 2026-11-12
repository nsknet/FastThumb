#!/bin/sh
# Stand-in for nm while configuring FFmpeg (zig ships none). configure runs nm exactly once, on a test object that defines
# ff_extern, to learn whether C symbols get a leading underscore on the target: that decides how the assembly sources name
# their functions. The answer only depends on the object format, so it is given directly: Mach-O prefixes, ELF and PE
# (64-bit) do not. TV_ZIG_TARGET comes from build.sh.
case "$TV_ZIG_TARGET" in
  *macos*) echo "0000000000000000 T _ff_extern" ;;
  *)       echo "0000000000000000 T ff_extern" ;;
esac
