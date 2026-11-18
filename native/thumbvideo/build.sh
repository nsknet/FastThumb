#!/usr/bin/env sh
# Builds the thumbvideo native library: a tiny C shim (thumbvideo.c) statically linked against a
# stripped-down, LGPL, decode-only build of FFmpeg's libav* plus dav1d (AV1). One toolchain for every
# target: zig cc.
#
#   ./build.sh <rid>   ->  src/FastThumb/runtimes/<rid>/native/<library>
#
#   win-x86, win-x64, win-arm64 thumbvideo.dll        (win-x86: 32-bit .NET Framework applications)
#   linux-x64, linux-arm64      libthumbvideo.so      (glibc >= 2.17)
#   osx-x64, osx-arm64          libthumbvideo.dylib   (macOS >= 11)
#
# Every target is cross-compiled from any host (Linux, macOS, or Windows with Git Bash).
# Needs: zig, make, git, meson + ninja (for dav1d), a POSIX shell, and nasm for the x86 and x64 targets.
# The SIMD code of FFmpeg and dav1d makes decoding several times faster than the C fallback: x86 and x64 use nasm,
# arm64 uses the assembler built into zig. ASM=0 builds without it.
# No pkg-config: pkgconf.sh answers FFmpeg's one question about dav1d.
# WORK (default native/thumbvideo/.work) holds the source checkouts and the per-target build trees.
set -eu

FFMPEG_TAG="${FFMPEG_TAG:-n9.0.2}"
DAV1D_TAG="${DAV1D_TAG:-1.5.4}"
HERE="$(cd "$(dirname "$0")" && pwd)"
RID="${1:?usage: build.sh win-x86|win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64}"
WORK="${WORK:-$HERE/.work}"
OUT="${OUT:-$HERE/../../src/FastThumb/runtimes/$RID/native}"

# stack canaries and checked string/memory calls: the input is untrusted (zig ignores both for mingw)
HARDEN_GNU="-fstack-protector-strong -D_FORTIFY_SOURCE=2"
case "$RID" in
  win-x86)     TARGET=x86-windows-gnu;        OS=mingw32; ARCH=x86;     LIB=thumbvideo.dll;      HARDEN="" ;;
  win-x64)     TARGET=x86_64-windows-gnu;     OS=mingw32; ARCH=x86_64;  LIB=thumbvideo.dll;      HARDEN="" ;;
  win-arm64)   TARGET=aarch64-windows-gnu;    OS=mingw32; ARCH=aarch64; LIB=thumbvideo.dll;      HARDEN="" ;;
  linux-x64)   TARGET=x86_64-linux-gnu.2.17;  OS=linux;   ARCH=x86_64;  LIB=libthumbvideo.so;    HARDEN="$HARDEN_GNU" ;;
  linux-arm64) TARGET=aarch64-linux-gnu.2.17; OS=linux;   ARCH=aarch64; LIB=libthumbvideo.so;    HARDEN="$HARDEN_GNU" ;;
  osx-x64)     TARGET=x86_64-macos.11.0;      OS=darwin;  ARCH=x86_64;  LIB=libthumbvideo.dylib; HARDEN="-fstack-protector-strong" ;;
  osx-arm64)   TARGET=aarch64-macos.11.0;     OS=darwin;  ARCH=aarch64; LIB=libthumbvideo.dylib; HARDEN="-fstack-protector-strong" ;;
  *) echo "unknown target $RID" >&2; exit 2 ;;
esac
case "$OS" in
  mingw32) MESON_OS=windows; EXTRA_LIBS="-lbcrypt"; GC="-Wl,--gc-sections" ;;
  linux)   MESON_OS=linux;   EXTRA_LIBS="-lm -lpthread -Wl,--version-script=$HERE/exports.map -Wl,-Bsymbolic"; GC="-Wl,--gc-sections" ;;
  darwin)  MESON_OS=darwin;  EXTRA_LIBS="-Wl,-exported_symbols_list,$HERE/exports.txt -Wl,-install_name,@rpath/$LIB"; GC="-Wl,-dead_strip" ;;
esac
if [ "${ASM:-1}" != 1 ];     then VARIANT="$RID-noasm"; DAV1D_ASM=false; FF_ASM="--disable-asm"
elif [ "$ARCH" = x86_64 ] || [ "$ARCH" = x86 ]; then VARIANT="$RID";       DAV1D_ASM=true;  FF_ASM="--x86asmexe=nasm"
else                              VARIANT="$RID";       DAV1D_ASM=true;  FF_ASM=""; fi

# Containers and video codecs commonly found in uploads. Video only: no audio,
# no encoders, no muxers, no filters, no protocols (the shim feeds bytes through callbacks), no hwaccel.
DEMUXERS="mov matroska avi flv asf mpegps mpegts mpegvideo ogg rm vivo mxf roq"
DECODERS="h264 hevc vp8 vp9 vp7 vp6 vp6f vp6a vp5 vp3 theora mpeg1video mpeg2video mpegvideo mpeg4 \
msmpeg4v1 msmpeg4v2 msmpeg4v3 h261 h263 h263i flv wmv1 wmv2 wmv3 vc1 rv10 rv20 rv30 rv40 mjpeg mjpegb \
prores dnxhd dvvideo cfhd jpeg2000 svq1 svq3 qtrle cinepak rawvideo roq indeo3 indeo4 indeo5 msvideo1 \
msrle tscc2 huffyuv ffvhuff ffv1 utvideo libdav1d"
PARSERS="h264 hevc vp8 vp9 vp3 mpegvideo mpeg4video mjpeg vc1 h261 h263 rv34 dnxhd av1"
# streams that carry their parameter sets in-band (MPEG-TS/PS): without this the decoder cannot start after a seek
BSFS="extract_extradata"

# a path make can use: scoop's make is a native Windows program and needs C:/... instead of /c/...
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

mkdir -p "$WORK" "$OUT"
[ -d "$WORK/ffmpeg" ] || git clone --depth 1 --branch "$FFMPEG_TAG" -c core.autocrlf=false https://github.com/FFmpeg/FFmpeg.git "$WORK/ffmpeg"
[ -d "$WORK/dav1d" ]  || git clone --depth 1 --branch "$DAV1D_TAG"  -c core.autocrlf=false https://github.com/videolan/dav1d.git "$WORK/dav1d"

# zig cc turns undefined-behaviour traps on by default; neither FFmpeg nor dav1d is written for that.
CFLAGS="-fno-sanitize=undefined -ffunction-sections -fdata-sections $HARDEN"

# --- dav1d: FFmpeg's own AV1 decoder only works with hardware acceleration ---------------------------
DAV1D="$WORK/dav1d-$VARIANT"
if [ ! -f "$DAV1D/build.ninja" ]; then
  cat > "$WORK/zig-$RID.ini" <<EOF
[binaries]
c = ['zig', 'cc', '-target', '$TARGET']
ar = ['zig', 'ar']
ranlib = ['zig', 'ranlib']
strip = 'true'
nasm = 'nasm'

[host_machine]
system = '$MESON_OS'
cpu_family = '$ARCH'
cpu = '$ARCH'
endian = 'little'
EOF
  (cd "$WORK/dav1d" && meson setup "$DAV1D" --cross-file "$WORK/zig-$RID.ini" --buildtype=release --default-library=static \
    -Db_staticpic=true -Denable_asm=$DAV1D_ASM -Denable_tools=false -Denable_tests=false -Denable_examples=false -Dc_args="$CFLAGS")
else
  meson configure "$DAV1D" -Dc_args="$CFLAGS"   # flags changed since the tree was set up: ninja rebuilds
fi
ninja -C "$DAV1D"
export TV_DAV1D_INCLUDE="$(native "$WORK/dav1d/include")" TV_DAV1D_LIB="$(native "$DAV1D/src/libdav1d.a")"   # read by pkgconf.sh

# --- FFmpeg ----------------------------------------------------------------------------------------
BUILD="$WORK/build-$VARIANT"
mkdir -p "$BUILD/tmp"
cd "$BUILD"
# configure's scratch files: Git Bash inherits a backslash TMP from Windows, which configure cannot use
export TMPDIR="$BUILD/tmp" TMP="$BUILD/tmp" TEMP="$BUILD/tmp"
export TV_ZIG_TARGET="$TARGET"   # read by zigcc.sh

# reconfigure whenever the component lists (or a pinned release) change
STAMP="$FFMPEG_TAG|$DAV1D_TAG|$TARGET|$FF_ASM|$CFLAGS|$DEMUXERS|$DECODERS|$PARSERS|$BSFS"
if [ ! -f ffbuild/config.mak ] || [ "$(cat .thumbvideo-stamp 2>/dev/null)" != "$STAMP" ]; then
  flags=""
  for b in $BSFS;     do flags="$flags --enable-bsf=$b";     done
  for d in $DEMUXERS; do flags="$flags --enable-demuxer=$d"; done
  for d in $DECODERS; do flags="$flags --enable-decoder=$d"; done
  for p in $PARSERS;  do flags="$flags --enable-parser=$p";  done
  ../ffmpeg/configure \
    --enable-cross-compile --target-os="$OS" --arch="$ARCH" \
    --cc="sh $HERE/zigcc.sh" --ar="zig ar" --ranlib="zig ranlib" --nm="sh $HERE/nm.sh" --strip="true" \
    --host-cc="zig cc" --pkg-config="sh $HERE/pkgconf.sh" \
    --disable-everything --disable-programs --disable-doc --disable-network --disable-autodetect \
    --disable-avdevice --disable-avfilter --disable-swresample \
    $FF_ASM --disable-debug --disable-stripping \
    --enable-static --disable-shared --enable-pic --enable-small \
    --enable-libdav1d \
    --extra-cflags="$CFLAGS" \
    $flags
  # sysctl() links against glibc 2.17 but its header is gone from the glibc headers zig ships: configure
  # only tests the link, so tell it the truth (only used to count CPUs, which has other ways)
  sed -i 's/#define HAVE_SYSCTL 1/#define HAVE_SYSCTL 0/' config.h
  posix="$(cd ../ffmpeg && pwd)"
  [ "$(native "$posix")" = "$posix" ] || sed -i "s|$posix|$(native "$posix")|g" Makefile ffbuild/config.mak
  printf '%s' "$STAMP" > .thumbvideo-stamp
fi

make -j"${JOBS:-8}" libavformat/libavformat.a libavcodec/libavcodec.a libswscale/libswscale.a libavutil/libavutil.a

# --- the shim ----------------------------------------------------------------------------------------
# shellcheck disable=SC2086
zig cc -target "$TARGET" -shared -fPIC -Os -fvisibility=hidden $CFLAGS \
  -DTV_FFMPEG_TAG="\"$FFMPEG_TAG\"" -DTV_DAV1D_TAG="\"$DAV1D_TAG\"" \
  -I../ffmpeg -I. "$HERE/thumbvideo.c" \
  libavformat/libavformat.a libavcodec/libavcodec.a libswscale/libswscale.a libavutil/libavutil.a "$DAV1D/src/libdav1d.a" \
  $EXTRA_LIBS $GC -s -o "$OUT/$LIB"

rm -f "$OUT/thumbvideo.lib"   # import library zig writes next to the DLL; nothing links against it
ls -l "$OUT/$LIB"
