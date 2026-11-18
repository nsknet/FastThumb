# Native components

FastThumb's readers, layout and PDF writer are managed code. Four native libraries do the pixel work:

| Library | Version | Job | Comes from | License |
|---|---|---|---|---|
| Skia (through SkiaSharp) | SkiaSharp 3.119.4 | Draws pages, resolves and measures fonts, decodes pictures embedded in documents, decodes BMP and ICO files | NuGet: `SkiaSharp`, `SkiaSharp.NativeAssets.Linux` | BSD-3-Clause (SkiaSharp: MIT) |
| PDFium | 159.0.8152 | Renders PDF pages | NuGet: `bblanchon.PDFium.Win32`, `bblanchon.PDFium.Linux`, `bblanchon.PDFium.macOS` | BSD-3-Clause / Apache-2.0 |
| libvips (through NetVips) | NetVips 3.2.0, libvips 8.18.7 | Decodes image files, resizes, encodes PNG, WebP and JPEG | NuGet: `NetVips`, `NetVips.Native` | LGPL-2.1-or-later (NetVips: MIT), loaded dynamically |
| thumbvideo | FFmpeg n9.0.2, dav1d 1.5.4 | Takes one frame from a video | Built from [native/thumbvideo](../native/thumbvideo) in this repository | LGPL-2.1-or-later as a binary |

The first three are restored by NuGet; nothing has to be built for them. Only `thumbvideo` is built from this
repository. Versions are pinned in [Directory.Packages.props](../Directory.Packages.props) and in
[native/thumbvideo/build.sh](../native/thumbvideo/build.sh). Licenses are detailed in
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

`fastthumb info` prints the versions that are actually loaded and checks that each library works:

```text
libvips    8.18.7
skia       ...
thumbvideo thumbvideo 1, FFmpeg n9.0.2, dav1d 1.5.4
pdfium     renders a page: yes
```

## Supported runtime identifiers

| RID | thumbvideo file | Minimum system |
|---|---|---|
| `win-x86`, `win-x64`, `win-arm64` | `thumbvideo.dll` | Depends only on the Universal C Runtime, `KERNEL32.dll` and `bcrypt.dll`. `win-x86` serves 32-bit .NET Framework processes. |
| `linux-x64`, `linux-arm64` | `libthumbvideo.so` | glibc 2.17 or later (the other native packages may require a newer glibc, see [linux.md](linux.md)) |
| `osx-x64`, `osx-arm64` | `libthumbvideo.dylib` | macOS 11 or later |

musl-based distributions (Alpine) are not supported.

## How thumbvideo is found

The library lives at `runtimes/<rid>/native/` next to the application; the FastThumb NuGet package carries it at
that path for all seven runtime identifiers. At run time, `Video/VideoFrame.cs` looks for
`runtimes/<os>-<arch>/native/<file>` under the application base directory, then for the file directly in the
application base directory, and otherwise falls back to the default native library probing of .NET.

.NET Framework reads neither `runtimes/<rid>` nor a DllImport resolver. The package's
`buildTransitive/net462/FastThumb.targets` copies every Windows build to `x86\`, `x64\` and `arm64\` next to the
application, and `Polyfills/NativePreload.cs` loads thumbvideo and PDFium by full path before their first call: from the
folder named after the process architecture, then `runtimes\win-<arch>\native\`, then the application folder (an ASP.NET
application's `bin` folder first).

If the library is missing, everything except video keeps working; a video input fails with `UnsupportedFormatException`.

Because the binary is LGPL-licensed, you may replace it with your own build: put your library at the same path.

## Where the binaries come from

The built libraries are **not committed**: `src/FastThumb/runtimes/` is git-ignored.

- **CI**: the workflow [.github/workflows/native.yml](../.github/workflows/native.yml) cross-compiles all six
  runtime identifiers with zig 0.17.1 on one Ubuntu runner and uploads each as an artifact named `thumbvideo-<rid>`.
  The result is cached by the hash of `native/thumbvideo/**`, so the native code is rebuilt only when it changes.
  The other jobs download the artifacts and run [eng/place-natives.sh](../eng/place-natives.sh), which copies them
  to `src/FastThumb/runtimes/<rid>/native/`, where the build and `dotnet pack` pick them up.
- **Locally**: run `build.sh` for your own platform (below), or download the `thumbvideo-*` artifacts of a CI run
  into a folder and run `sh eng/place-natives.sh <folder>`.

## Building thumbvideo

One toolchain builds every target: `zig cc`. Any target can be cross-compiled from any host (Linux, macOS, or
Windows with Git Bash).

```bash
sh native/thumbvideo/build.sh <rid>
```

`<rid>` is one of `win-x86`, `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. The output is written
to `src/FastThumb/runtimes/<rid>/native/`.

### Tools

| Tool | Used for | Version used in CI |
|---|---|---|
| zig | C compiler and linker for every target; assembler for the arm64 targets | 0.17.1 |
| make | Building FFmpeg | GNU make |
| git | Fetching the FFmpeg and dav1d sources | any |
| meson and ninja | Building dav1d | meson 1.12.1 |
| nasm | SIMD assembly of FFmpeg and dav1d for the **x64** targets | distribution package |
| POSIX shell | `build.sh` and FFmpeg's `configure` | sh, bash, or Git Bash on Windows |

No pkg-config, Visual Studio, MSYS2, WSL or Docker is needed.

On Ubuntu or Debian:

```bash
sudo apt-get install -y --no-install-recommends make git nasm ninja-build pipx
pipx install meson
```

and install zig from <https://ziglang.org/download/> (unpack it and add the folder to `PATH`).

On Windows, with Git for Windows and Python installed, the tools can be installed without administrator rights, for
example with scoop (`scoop install zig make nasm`) and `pip install meson ninja` in a virtual environment. Run the
build from Git Bash.

On macOS: `brew install zig make nasm meson ninja`.

### What the script does

1. Clones FFmpeg (tag `n9.0.2`) and dav1d (tag `1.5.4`) into the work directory, if they are not there yet.
2. Builds dav1d with meson and ninja as a static library, using a generated zig cross file.
3. Runs FFmpeg's `configure` with `--disable-everything` plus the demuxers, decoders, parsers and bitstream filter
   listed at the top of the script, then builds the four static libraries libavformat, libavcodec, libswscale and
   libavutil.
4. Compiles `thumbvideo.c` and links everything into one shared library that exports only `tv_frame`, `tv_free` and
   `tv_version`.

`configure` runs again only when the component lists, the pinned tags, the target or the compiler flags change (the
script keeps a stamp in the build tree), so a rebuild after editing only `thumbvideo.c` takes seconds. A first build
takes a few minutes per target.

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `ASM` | `1` | `0` builds without assembly (no nasm needed). Decoding then runs on the C fallbacks and is several times slower. Uses a separate build tree (`<rid>-noasm`). |
| `JOBS` | `8` | Parallel `make` jobs. |
| `WORK` | `native/thumbvideo/.work` (git-ignored) | Source checkouts and per-target build trees. On Windows, prefer a short path without spaces. |
| `OUT` | `src/FastThumb/runtimes/<rid>/native` | Where the library is written. |
| `FFMPEG_TAG` | `n9.0.2` | FFmpeg tag. After changing it, delete `$WORK/ffmpeg` so that it is cloned again. |
| `DAV1D_TAG` | `1.5.4` | dav1d tag. After changing it, delete `$WORK/dav1d` and `$WORK/dav1d-*`. |

### Build configuration

- **LGPL, decode only**: no `--enable-gpl`, no `--enable-nonfree`; no encoders, muxers, filters, audio, protocols,
  network, devices or hardware acceleration. `--enable-small` and section garbage collection keep the library at
  about 8 MB.
- **SIMD**: the x64 targets use nasm; the arm64 targets use the assembler built into zig.
- **Hardening**: the Linux targets are built with `-fstack-protector-strong -D_FORTIFY_SOURCE=2`, the macOS targets
  with `-fstack-protector-strong`. zig ignores both flags for the Windows (mingw) targets.
- **Symbols**: only the three `tv_*` functions are exported (`exports.map` on Linux, `exports.txt` on macOS, hidden
  visibility plus `dllexport` on Windows), so the FFmpeg inside cannot clash with another libav in the same process.
- **glibc 2.17** is the link target on Linux (`*-linux-gnu.2.17`), **macOS 11** on macOS.

The list of containers and codecs, the C interface, and the reasons for the less obvious parts of the script are in
[native/thumbvideo/README.md](../native/thumbvideo/README.md).

### Checking a build

```bash
dotnet run --project src/FastThumb.Cli -- info
dotnet test FastThumb.slnx -c Release
```

`info` prints the thumbvideo version line; the test suite decodes the small H.264, VP9 and AV1 fixtures in
`tests/fixtures/video/` (those tests are skipped when the library is missing, so check that they ran).

## Continuous integration and releases

- [ci.yml](../.github/workflows/ci.yml): a formatting check (`dotnet format --verify-no-changes`); the native build;
  build and tests on `ubuntu-24.04`, `ubuntu-24.04-arm`, `windows-2025`, `windows-11-arm`, `macos-15` and
  `macos-15-intel` (x64 and arm64 on each platform), for every target framework of the tests (`net6.0`, `net8.0`,
  `net10.0`, and `net472` on Windows); a package job that packs both packages, checks the package
  layout with [eng/check-package.sh](../eng/check-package.sh) and installs them into a fresh application and tool
  with [eng/smoke/run.sh](../eng/smoke/run.sh); and a Docker job that builds the image and runs `fastthumb info` in
  it.
- [release.yml](../.github/workflows/release.yml): pushing a tag `vX.Y.Z` runs the whole CI, checks that the package
  carries the tag's version, pushes `FastThumb` to nuget.org and creates a GitHub release with
  the package attached. nuget.org Trusted Publishing is used when the repository variable `NUGET_USER` is set;
  otherwise the secret `NUGET_API_KEY` is used.

Package versions come from git tags through MinVer (tag prefix `v`).

## Updating a native component

- **SkiaSharp, NetVips / libvips, PDFium**: change the version in `Directory.Packages.props`, then build and run the
  tests on at least Windows and Linux.
- **FFmpeg or dav1d**: change `FFMPEG_TAG` or `DAV1D_TAG` in `build.sh`. CI rebuilds all targets because the hash
  of `native/thumbvideo` changes. No C# change is needed: no FFmpeg structure crosses the interface.
- **A new codec or container**: add it to `DEMUXERS`, `DECODERS`, `PARSERS` or `BSFS` at the top of `build.sh`
  (names as printed by FFmpeg's `./configure --list-decoders`, `--list-demuxers`, `--list-parsers`); for a new
  container also add its signature or extension to `FormatDetector.IsVideo`. Update
  [docs/formats.md](formats.md) and [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) if needed.
