# thumbvideo: one frame from a video

`thumbvideo` is the small native library (about 8 MB) that FastThumb uses to take a still frame from a video. It
consists of:

- a **decode-only build of FFmpeg** (libavformat, libavcodec, libswscale, libavutil) under the LGPL: no encoders, no
  audio, no filters, no network;
- **dav1d** for AV1 (FFmpeg's built-in AV1 decoder only works with hardware acceleration);
- a **C shim** ([thumbvideo.c](thumbvideo.c)) that statically links the two and exports three functions.

The managed side is [src/FastThumb/Video/VideoFrame.cs](../../src/FastThumb/Video/VideoFrame.cs). No FFmpeg structure
crosses the boundary, so upgrading FFmpeg never breaks the interop.

The built libraries are not committed. CI builds them for every runtime identifier; locally, run `build.sh`. The
output goes to `src/FastThumb/runtimes/<rid>/native/`:

| Runtime identifier | File | Minimum system |
|---|---|---|
| `win-x86`, `win-x64`, `win-arm64` | `thumbvideo.dll` | |
| `linux-x64`, `linux-arm64` | `libthumbvideo.so` | glibc 2.17 |
| `osx-x64`, `osx-arm64` | `libthumbvideo.dylib` | macOS 11 |

## 1. Files in this directory

| File | Purpose |
|---|---|
| [build.sh](build.sh) | The one build script: fetches the sources, builds dav1d, configures and builds FFmpeg, links the shim. Runs on Linux, macOS and Windows (Git Bash). |
| [thumbvideo.c](thumbvideo.c) | The shim: opens the video through read and seek callbacks, seeks, decodes, converts to RGB24. |
| [zigcc.sh](zigcc.sh) | Wraps `zig cc` as FFmpeg's compiler (drops a few arguments zig does not accept). |
| [pkgconf.sh](pkgconf.sh) | Stand-in for `pkg-config`: answers FFmpeg's one question about dav1d. |
| [nm.sh](nm.sh) | Stand-in for `nm`: tells FFmpeg's configure whether C symbols get a leading underscore on the target. |
| [exports.map](exports.map) | Linux: export only the three `tv_*` functions and hide every FFmpeg symbol. |
| [exports.txt](exports.txt) | macOS: the same list of exported symbols. |

## 2. Tools

One toolchain for every target: **zig cc**. Every target can be cross-compiled from any host.

| Tool | Used for | Version used in CI |
|---|---|---|
| zig | C compiler and cross linker for all targets; assembler for the arm64 targets | 0.17.1 |
| make | Building FFmpeg | GNU make |
| git | Fetching FFmpeg and dav1d | any |
| nasm | SIMD assembly of FFmpeg and dav1d for the x64 targets | distribution package |
| meson + ninja | Building dav1d | meson 1.12.1 |
| POSIX shell | `build.sh` and `configure` | sh or bash; Git Bash on Windows |

No pkg-config, Visual Studio, MSYS2, WSL or Docker is needed. Without nasm, build with `ASM=0` (slower decoding,
see section 3).

### Linux (Ubuntu or Debian)

```bash
sudo apt-get install -y --no-install-recommends make git nasm ninja-build pipx curl xz-utils
pipx install meson
```

zig is a self-contained download; unpack it and put its folder on `PATH`:

```bash
mkdir -p ~/zig && curl -fsSL https://ziglang.org/download/0.17.1/zig-x86_64-linux-0.17.1.tar.xz | tar -xJ -C ~/zig
export PATH="$HOME/zig/zig-x86_64-linux-0.17.1:$PATH"
```

### Windows

With Git for Windows (Git Bash) and Python 3 installed, zig, make and nasm can be installed with scoop, without
administrator rights:

```bash
scoop install zig make nasm
```

Install meson and ninja into a virtual environment (in Git Bash) and put it on `PATH`:

```bash
python -m venv ~/thumbvideo-venv
~/thumbvideo-venv/Scripts/python.exe -m pip install meson ninja
export PATH="$HOME/thumbvideo-venv/Scripts:$PATH"
```

### macOS

```bash
brew install zig make nasm meson ninja
```

## 3. Build

Run from the repository root:

```bash
sh native/thumbvideo/build.sh linux-x64
```

The argument is one of `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

The script:

1. clones FFmpeg (tag `n9.0.2`) and dav1d (tag `1.5.4`) into the work directory, if they are not there yet;
2. builds dav1d with meson and ninja into `libdav1d.a`;
3. runs FFmpeg's `configure` with the demuxer, decoder, parser and bitstream-filter lists at the top of the script,
   then builds four static libraries with `make`;
4. compiles `thumbvideo.c` and links everything into one `.dll`, `.so` or `.dylib` in
   `src/FastThumb/runtimes/<rid>/native/`.

Optional environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `WORK` | `native/thumbvideo/.work` (git-ignored) | Source checkouts and build trees. On Windows, prefer a short path without spaces. |
| `OUT` | `src/FastThumb/runtimes/<rid>/native` | Where the library is written. |
| `FFMPEG_TAG` | `n9.0.2` | FFmpeg tag. After changing it, delete `$WORK/ffmpeg` so that it is cloned again. |
| `DAV1D_TAG` | `1.5.4` | dav1d tag. After changing it, delete `$WORK/dav1d` and `$WORK/dav1d-<rid>`. |
| `JOBS` | `8` | Parallel `make` jobs. |
| `ASM` | `1` | `0` builds without assembly (no nasm needed). The decoders then run on plain C and are several times slower. Separate build tree: `$WORK/build-<rid>-noasm`. |

The x64 targets assemble FFmpeg's and dav1d's SIMD code with nasm; the arm64 targets use the assembler built into
zig.

A first build takes a few minutes per target. On Windows most of that is `configure` (Git Bash creates processes
slowly). A rebuild after editing only `thumbvideo.c` takes seconds: `configure` runs again only when the component
lists, the tags, the target or the compiler flags change (the script keeps a stamp in
`$WORK/build-<rid>/.thumbvideo-stamp`).

In CI, [.github/workflows/native.yml](../../.github/workflows/native.yml) runs this script for all six targets on one
Ubuntu runner and caches the result by the hash of this directory.

## 4. Checking a build

Version and loaded native libraries:

```bash
dotnet run --project src/FastThumb.Cli -- info
```

The line `thumbvideo thumbvideo 1, FFmpeg n9.0.2, dav1d 1.5.4` shows that the library loads.

The library should export three functions and depend only on system libraries:

```bash
objdump -p src/FastThumb/runtimes/win-x64/native/thumbvideo.dll | grep -E "DLL Name|tv_"
```

```bash
readelf -d src/FastThumb/runtimes/linux-x64/native/libthumbvideo.so | grep NEEDED
```

Expected: the Windows build needs only the Universal C Runtime (`api-ms-win-crt-*`), `KERNEL32.dll` and
`bcrypt.dll`; the Linux build needs only `libc`, `libm`, `libpthread` and `libdl`, with glibc 2.17 as the highest
symbol version.

The test suite decodes the small fixtures in [tests/fixtures/video](../../tests/fixtures/video) (H.264, VP9 and AV1,
generated from FFmpeg's built-in test pattern by [make-videos.sh](../../tests/fixtures/make-videos.sh)); these tests
are skipped when the library cannot be loaded, so check that they ran:

```bash
dotnet test FastThumb.slnx -c Release
```

## 5. What the shim does

```c
int  tv_frame(tv_read read, tv_seek seek, void *opaque, double fraction, int timeout_ms,
              tv_info *info, uint8_t **rgb, char *error, int error_size);
void tv_free(uint8_t *rgb);
const char *tv_version(void);
```

```c
typedef int     (*tv_read)(void *opaque, uint8_t *buffer, int size);      /* bytes read, 0 = end, <0 = error */
typedef int64_t (*tv_seek)(void *opaque, int64_t offset, int whence);     /* whence 0/1/2 = set/cur/end, 0x10000 = report size; <0 = error */

typedef struct tv_info {
    int32_t width, height;      /* of the returned picture (sample aspect ratio applied, not yet rotated) */
    int32_t rotation;           /* clockwise degrees the caller still has to apply: 0, 90, 180 or 270 */
    int32_t reserved;
    double duration;            /* seconds, < 0 when unknown */
    double frame_time;          /* seconds from the start of the returned frame, < 0 when unknown */
    char codec[32];
    char container[32];
} tv_info;
```

`tv_frame` returns 0 and a tightly packed RGB24 picture (`width * height * 3` bytes, to be released with `tv_free`),
or a negative error code with a message in `error`:

| Code | Meaning | Exception on the managed side |
|---|---|---|
| -1 | Invalid argument | `InvalidDocumentException` |
| -2 | Not a container this build can read | `UnsupportedFormatException` |
| -3 | No video stream | `UnsupportedFormatException` |
| -4 | The video codec is not in this build | `UnsupportedFormatException` |
| -5 | No frame could be decoded | `InvalidDocumentException` |
| -6 | Timed out | `TimeoutException` |
| -7 | Out of memory | `OutOfMemoryException` |
| -8 | A caller callback failed | `IOException` (or `OperationCanceledException` when the call was cancelled) |

Behaviour:

- Data is read through two callbacks, so the source can be any seekable stream (a file, memory, cloud storage). No
  temporary file and no child process.
- The frame is the one at `fraction` of the duration (0.5 is the middle; values above 0.98 are treated as 0.98):
  seek to the keyframe before it, then decode forward towards it. The walk is bounded by a count, not a clock: at
  most 900 packets, and fewer for large pictures (at most 10^9 decoded pixels, so about 480 frames of 1080p or 120 of
  4K, never fewer than 25). When the bound is reached the latest decoded picture is returned. Because the bound does
  not depend on timing, the same input always gives the same frame.
- If the container reports no duration, the position is taken as a fraction of the file size where the container
  allows seeking by bytes.
- If no frame can be decoded there (a truncated file, no keyframe after that point, a container that cannot seek),
  the file is opened again and the **first frame** is taken.
- A stream that holds only cover art yields that picture.
- Only the header and one group of pictures are read, so a large video on a remote stream costs a small read. Streams
  other than the chosen video stream are discarded by the demuxer.
- The sample aspect ratio is applied (anamorphic video), and the rotation of portrait video is returned so that the
  caller can rotate the picture (FastThumb does it with libvips).
- Untagged video is converted as BT.709 when it is 720 lines or taller and as BT.601 otherwise; full-range video is
  detected from the frame.
- Decoding uses up to four threads.
- Limits keep a damaged file from hanging or exhausting the process: `timeout_ms` (60 seconds by default on the
  managed side, checked for every packet and by FFmpeg's interrupt callback), 4000 packets per attempt, and at most
  8192 x 4352 pixels per frame (16384 per side), checked in the stream header, during stream probing and by each
  decoder before it allocates.

Set the environment variable `THUMBVIDEO_LOG` to get FFmpeg's diagnostics on standard error when a file will not
open.

## 6. Supported formats

- **Containers**: MP4, M4V, MOV, 3GP, 3G2, F4V, Matroska (MKV), WebM, AVI, FLV, ASF, WMV, MPEG program stream
  (MPG, MPEG, MPE, VOB), MPEG elementary video (M2V), MPEG transport stream (TS, M2TS), Ogg (OGV), RealMedia (RM,
  RMVB), Vivo, MXF, RoQ.
- **Codecs**: H.264, HEVC (8 and 10 bit), AV1, VP8, VP9, VP3/5/6/7, Theora, MPEG-1/2/4, MS-MPEG4, H.261, H.263,
  FLV1, WMV1/2/3, VC-1, RealVideo 1-4, MJPEG, ProRes, DNxHD, DV, CineForm, JPEG 2000, SVQ1/3, QuickTime RLE,
  Cinepak, Indeo 3/4/5, MS Video 1, MS RLE, TSCC2, HuffYUV, FFV1, Ut Video, RoQ, raw video.

Audio files (`.wav`, `.ogg`, `.mp2`) are recognised as media by FastThumb but fail with "the file has no video
stream". The extensions `.ts` and `.mts` are not treated as video by name (they are usually TypeScript); MPEG
transport streams are still recognised by their signature.

### Adding a codec or container

Edit `DEMUXERS`, `DECODERS`, `PARSERS` or `BSFS` at the top of [build.sh](build.sh) and rebuild every target. The
names are those printed by `./configure --list-decoders` (and `--list-demuxers`, `--list-parsers`) in
`$WORK/ffmpeg`. For a new container, also add its signature or extension to `FormatDetector.IsVideo`.

### Upgrading FFmpeg or dav1d

Change `FFMPEG_TAG` or `DAV1D_TAG` in `build.sh`, delete `$WORK/ffmpeg` (or `$WORK/dav1d` and `$WORK/dav1d-*`),
rebuild every target and repeat section 4. No C# change is needed. Update the versions in
[THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).

## 7. Why the script contains some odd-looking parts

Each of these is a failure that was hit while building FFmpeg with zig; removing it breaks the build again.

| Part of the script | Failure without it |
|---|---|
| `export TMPDIR=...` | Git Bash inherits a Windows-style `TMP` (`C:\...`); `configure` cannot create its test files and stops at "Sanity test failed". |
| `zigcc.sh` drops `-Wl,--pic-executable`, `--image-base`, `--nxcompat` | For mingw targets `configure` adds GNU linker flags that zig refuses, so every link test fails (misreported as a missing aligned malloc). |
| `zigcc.sh` drops `.a` files when `-c` is present | `configure` passes `libdav1d.a` on compile-only command lines too; gcc ignores it, zig fails, and the result is "dav1d not found". |
| The `native()` function and `sed` on `Makefile` and `config.mak` | A native Windows `make` does not understand the `/c/...` paths that `configure` writes. |
| `sed ... HAVE_SYSCTL 0` | For glibc 2.17 targets `sysctl()` links, but its header is gone from the glibc headers zig ships: `sys/sysctl.h not found`. |
| `-fno-sanitize=undefined` | `zig cc` enables undefined-behaviour traps by default; FFmpeg and dav1d would crash at run time. |
| `-fstack-protector-strong -D_FORTIFY_SOURCE=2` (Linux), `-fstack-protector-strong` (macOS) | The input is untrusted: stack canaries and checked string and memory functions turn some buffer overflows into a process abort instead of executing foreign code. zig ignores both flags for mingw targets, so the Windows builds do not have them. The flags are part of the stamp: changing them rebuilds from scratch. |
| `exports.map` / `exports.txt` (instead of `--exclude-libs`) | zig's linker has no `--exclude-libs`; without the list the library exports every FFmpeg symbol, which can clash with another libav in the same process. |
| `-Wl,-Bsymbolic` (Linux) | Makes the library resolve its own FFmpeg symbols internally. |
| `pkgconf.sh` | Build machines may have no pkg-config, and FFmpeg only finds dav1d through pkg-config. |
| `nm.sh` | zig ships no `nm`. FFmpeg's configure runs `nm` once to learn the symbol prefix of the target; with no answer it assumes none, and on macOS the assembly functions then lack the underscore the C code links against ("undefined symbol: _ff_..."). 32-bit Windows (`win-x86`) prefixes too. |
| `--enable-bsf=extract_extradata` | MPEG-TS and MPEG-PS carry parameter sets in the stream; without this filter the decoder cannot start after a seek ("non-existing PPS"). |
| `rm -f thumbvideo.lib` | zig writes an import library next to the DLL; nothing uses it. |
| Threads left enabled | With threads disabled, FFmpeg's static table initialisation is not safe when several threads make their first call at the same time. |

## 8. License

- FFmpeg is configured as **LGPL 2.1 or later** (no `--enable-gpl`, no `--enable-nonfree`). The resulting
  thumbvideo binary is therefore distributed under the LGPL 2.1 or later. The shim source and the build script are
  in this directory so that anyone can rebuild the library, with a modified FFmpeg if they wish, and replace the
  binary shipped with FastThumb.
- dav1d: BSD 2-Clause.
- The shim (`thumbvideo.c`) and the scripts: MIT, like the rest of FastThumb.
- Some codecs (for example H.264 and HEVC) may be covered by patents in some jurisdictions; whether a patent
  license is needed for your use is your responsibility.

See [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).

## 9. Known limitations

- An MPEG-TS or MPEG-PS file with no keyframe after the requested position yields its first frame.
- HDR video (HEVC 10-bit PQ or HLG) is not tone-mapped, so colours look washed out.
- The decoder runs in-process: a crash inside FFmpeg takes the host process down. The narrow codec list, the limits
  and the hardening flags reduce that risk but do not remove it; see [docs/security.md](../../docs/security.md).
- No musl (Alpine) build. To add a target, add a `case` branch in `build.sh` with the matching zig target (for
  example `x86_64-linux-musl`) and, if the folder name differs, a branch in `VideoFrame.Resolve`.
