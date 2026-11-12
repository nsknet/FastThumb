# Third-party notices

FastThumb itself is licensed under the MIT License (see [LICENSE](LICENSE)). It depends on, and in one case
redistributes, the third-party components listed here. Each component remains under its own license.

| Component | Version | Used for | License | How it is distributed |
|---|---|---|---|---|
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 3.119.4 | .NET bindings for Skia | MIT | NuGet dependency (`SkiaSharp`, `SkiaSharp.NativeAssets.Linux`) |
| [Skia](https://skia.org) | as bundled in SkiaSharp 3.119.4 | Drawing pages, fonts, decoding pictures embedded in documents | BSD-3-Clause | Native library inside the SkiaSharp packages |
| [NetVips](https://github.com/kleisauke/net-vips) | 3.2.0 | .NET bindings for libvips | MIT | NuGet dependency (`NetVips`) |
| [libvips](https://www.libvips.org) | 8.18.7 | Decoding, resizing and encoding images | LGPL-2.1-or-later | Shared library inside the `NetVips.Native` packages, loaded dynamically |
| [PDFium](https://pdfium.googlesource.com/pdfium/) | 159.0.8152 | Rendering PDF pages | BSD-3-Clause and Apache-2.0 | Shared library inside the `bblanchon.PDFium.Win32`, `.Linux` and `.macOS` packages ([pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)) |
| thumbvideo | 1 | Taking one frame from a video | LGPL-2.1-or-later as a binary (see below); the shim source is MIT | Shared library built from this repository, shipped in the FastThumb package under `runtimes/<rid>/native/` |
| [FFmpeg](https://ffmpeg.org) | n9.0.2 | Demuxing and decoding video (inside thumbvideo) | LGPL-2.1-or-later | Statically linked into thumbvideo |
| [dav1d](https://code.videolan.org/videolan/dav1d) | 1.5.4 | Decoding AV1 (inside thumbvideo) | BSD-2-Clause | Statically linked into thumbvideo |

FastThumb does not copy the SkiaSharp, NetVips, libvips or PDFium binaries into its own package: they are restored
from their own NuGet packages, which carry their license files. The Docker image
does contain those binaries, unmodified, as published by their packages.

## thumbvideo, FFmpeg and dav1d

`thumbvideo` (`thumbvideo.dll`, `libthumbvideo.so`, `libthumbvideo.dylib`) is a small C shim
([native/thumbvideo/thumbvideo.c](native/thumbvideo/thumbvideo.c)) statically linked with libavformat, libavcodec,
libswscale and libavutil from FFmpeg and with dav1d.

**FFmpeg** is built from the unmodified upstream source at tag `n9.0.2`
(<https://github.com/FFmpeg/FFmpeg/tree/n9.0.2>), configured as an LGPL build: neither `--enable-gpl`,
`--enable-nonfree` nor `--enable-version3` is used, and only a fixed list of demuxers, video decoders and parsers is
enabled (no encoders, muxers, filters, audio, network or hardware acceleration). The exact configuration is in
[native/thumbvideo/build.sh](native/thumbvideo/build.sh). The only change made at build time is to one generated
configuration header (`HAVE_SYSCTL` is set to 0 so that the library links against old glibc versions); no FFmpeg
source file is modified.

FFmpeg is licensed under the GNU Lesser General Public License, version 2.1 or later. The license text is in the
FFmpeg source tree as `COPYING.LGPLv2.1` (<https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/COPYING.LGPLv2.1>) and
FFmpeg's licensing overview is `LICENSE.md` (<https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/LICENSE.md>). FFmpeg is a
trademark of Fabrice Bellard, originator of the FFmpeg project.

Because it contains FFmpeg, **the thumbvideo binary as a whole is distributed under the LGPL, version 2.1 or later**.

**Rebuilding and relinking.** To let you modify the LGPL-covered code and use your modified version with FastThumb:

- thumbvideo is a separate shared library that FastThumb loads at run time from `runtimes/<rid>/native/` (or from
  the application directory). You may replace it with your own build; nothing else needs to be rebuilt.
- The complete source of the shim and the build script that fetches FFmpeg and dav1d at the tags above and produces
  the library are in [native/thumbvideo](native/thumbvideo). [docs/native.md](docs/native.md) describes the build.
- The interface between FastThumb and thumbvideo is three C functions (`tv_frame`, `tv_free`, `tv_version`),
  documented in [native/thumbvideo/README.md](native/thumbvideo/README.md).

**dav1d** is licensed under the BSD 2-Clause License
(<https://code.videolan.org/videolan/dav1d/-/blob/1.5.4/COPYING>):

```text
Copyright © 2018-2019, VideoLAN and dav1d authors
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGES.
```

The library is compiled and linked with `zig cc`; small parts of the toolchain's runtime support (compiler-rt, and
the mingw-w64 runtime on Windows) are linked into the binary under their own permissive licenses
(<https://ziglang.org>, <https://compiler-rt.llvm.org>, <https://www.mingw-w64.org>).

**Patents.** Some of the video codecs that thumbvideo can decode (for example H.264 and HEVC) may be covered by
patents in some jurisdictions. FastThumb grants no patent rights; whether you need a patent license for your use is
your responsibility.

## Skia and SkiaSharp

SkiaSharp is licensed under the MIT License (<https://github.com/mono/SkiaSharp/blob/main/LICENSE.md>). Skia is
licensed under the BSD 3-Clause License (<https://skia.googlesource.com/skia/+/refs/heads/main/LICENSE>). The native
Skia library statically links further third-party libraries, each under its own license (see
<https://github.com/mono/SkiaSharp/blob/main/External-Dependency-Info.txt> and
<https://github.com/mono/SkiaSharp/blob/main/THIRD-PARTY-NOTICES.txt>).

## libvips and NetVips

NetVips is licensed under the MIT License (<https://github.com/kleisauke/net-vips/blob/master/LICENSE>). libvips is
licensed under the GNU Lesser General Public License, version 2.1 or later
(<https://github.com/libvips/libvips/blob/master/LICENSE>). FastThumb uses libvips only as a dynamically loaded
shared library from the `NetVips.Native` packages, so you can replace it with another compatible build. The prebuilt
library bundles image codecs and support libraries under their own licenses; they are listed with the packaging
project (<https://github.com/kleisauke/libvips-packaging>).

## PDFium

PDFium is licensed under the BSD 3-Clause License and the Apache License 2.0
(<https://pdfium.googlesource.com/pdfium/+/main/LICENSE>), and includes third-party libraries under their own
licenses, which are listed in the same file. The binaries come from the pdfium-binaries project
(<https://github.com/bblanchon/pdfium-binaries>), whose packages include the license files.

## Build and test dependencies (not redistributed)

| Component | Used for | License |
|---|---|---|
| [MinVer](https://github.com/adamralph/minver) | Version from git tags | Apache-2.0 |
| [Microsoft.SourceLink.GitHub](https://github.com/dotnet/sourcelink) | Source link metadata | MIT |
| [xunit](https://github.com/xunit/xunit), [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | Tests | Apache-2.0, MIT |
| [Zig](https://ziglang.org), [NASM](https://www.nasm.us), [Meson](https://mesonbuild.com), [Ninja](https://ninja-build.org) | Building thumbvideo | MIT, BSD-2-Clause, Apache-2.0, Apache-2.0 |

## Fonts

FastThumb ships no fonts. The Docker image built from the [Dockerfile](Dockerfile) installs font packages from the
Debian archive (Liberation, Carlito, Caladea, DejaVu and optionally Noto CJK); they are distributed by Debian under
their own licenses.
