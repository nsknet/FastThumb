#!/bin/sh
# Creates the tiny test videos in tests/fixtures/video/ (64x48, 1 s, no audio, a few KB each) from ffmpeg's built-in
# testsrc2 pattern, so no third-party footage is needed. Requires ffmpeg with libx264, libvpx-vp9 and an AV1 encoder
# (libsvtav1 or libaom-av1).
#   sh tests/fixtures/make-videos.sh
set -eu

command -v ffmpeg >/dev/null 2>&1 || { echo "ffmpeg not found on PATH" >&2; exit 1; }

out="$(cd "$(dirname "$0")" && pwd)/video"
mkdir -p "$out"

src="testsrc2=size=64x48:rate=25:duration=1"
common="-hide_banner -loglevel error -y -f lavfi -i $src -an -pix_fmt yuv420p"

# H.264 in MP4
ffmpeg $common -c:v libx264 -preset veryslow -b:v 40k -movflags +faststart "$out/h264.mp4"

# VP9 in WebM
ffmpeg $common -c:v libvpx-vp9 -b:v 40k -deadline good -cpu-used 4 "$out/vp9.webm"

# AV1 in MP4: SVT-AV1 if it accepts the size, libaom otherwise
if ! ffmpeg $common -c:v libsvtav1 -b:v 40k -preset 8 "$out/av1.mp4" 2>/dev/null; then
  ffmpeg $common -c:v libaom-av1 -b:v 40k -cpu-used 8 -row-mt 1 "$out/av1.mp4"
fi

ls -l "$out"
