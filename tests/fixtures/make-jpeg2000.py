"""Creates the JPEG 2000 test files in tests/fixtures/jpeg2000/ and, for each, the picture OpenJPEG decodes from it at every
resolution reduction (name.rN.png), which the managed decoder is compared with. FastThumb has no JPEG 2000 encoder, so these are
committed. The picture is drawn here (gradients, shapes, fine stripes) so no third-party image is needed.

Three codestreams of the JPEG 2000 conformance suite (ITU-T T.803 | ISO/IEC 15444-4) are downloaded from openjpeg-data for
what neither encoder writes: progression order changes (POC) and packed packet headers (PPM, PPT). Their copyright notice is
kept next to them, as it asks.

Requires Pillow built with OpenJPEG; ffmpeg (its native jpeg2000 encoder) for the files written by a second encoder.
    python tests/fixtures/make-jpeg2000.py
"""

import os
import shutil
import subprocess
import tempfile
import urllib.request

from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "jpeg2000")
CONFORMANCE = "https://raw.githubusercontent.com/uclouvain/openjpeg-data/master/input/conformance/"


def picture(width, height, mode="RGB"):
    image = Image.new("RGB", (width, height))
    pixels = image.load()
    for y in range(height):
        for x in range(width):
            pixels[x, y] = (x * 255 // max(1, width - 1), y * 255 // max(1, height - 1), 128)
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 0, width // 4, height // 4], fill=(255, 255, 255))
    draw.ellipse([width // 2, height // 3, width * 7 // 8, height * 5 // 6], fill=(200, 30, 60), outline=(0, 0, 0), width=3)
    for x in range(width // 10, width // 3, 4):
        draw.line([x, height // 2, x, height - 10], fill=(20, 20, 20), width=1)
    if mode == "RGBA":
        alpha = Image.new("L", (width, height))
        alpha_pixels = alpha.load()
        for y in range(height):
            for x in range(width):
                alpha_pixels[x, y] = 255 if x < width // 2 else (x * 7 + y * 3) % 256
        image.putalpha(alpha)
        return image
    if mode == "L":
        return image.convert("L")
    if mode == "I;16":
        grey = image.convert("L")
        wide = Image.new("I;16", (width, height))
        wide_pixels, grey_pixels = wide.load(), grey.load()
        for y in range(height):
            for x in range(width):
                wide_pixels[x, y] = grey_pixels[x, y] * 257 + (x & 0xFF)
        return wide
    return image


def references(name, fallback=True, last=99):
    """
    The decoded picture at every reduction: OpenJPEG through Pillow, or ffmpeg's decoder where Pillow refuses one (Pillow
    computes some reduced sizes differently from OpenJPEG). Stops at the first reduction neither decodes. No fallback for YCbCr
    files: ffmpeg turns them into RGB as video (limited range), not as sYCC. <last> stops earlier where a reference is known wrong.
    """
    path = os.path.join(OUT, name)
    reduce = 0
    while reduce <= last:
        target = os.path.join(OUT, f"{name}.r{reduce}.png")
        try:
            with Image.open(path) as image:
                image.reduce = reduce
                image.load()
                if image.mode == "I;16":
                    image = image.point(lambda v: v / 256).convert("L")
                image.save(target)
                size = image.size
        except Exception:
            if not fallback or not shutil.which("ffmpeg") or subprocess.run(
                    ["ffmpeg", "-hide_banner", "-loglevel", "quiet", "-y", "-lowres", str(reduce), "-i", path, "-frames:v", "1", target]).returncode != 0:
                break
            with Image.open(target) as image:
                size = image.size
        if min(size) <= 1:
            break
        reduce += 1


def pillow(name, mode="RGB", size=(320, 200), **options):
    picture(*size, mode=mode).save(os.path.join(OUT, name), **options)
    references(name)


def ffmpeg(name, pix_fmt, *codec, fallback=True, last=99):
    with tempfile.TemporaryDirectory() as scratch:
        source = os.path.join(scratch, "source.png")
        picture(320, 200).save(source)
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-pix_fmt", pix_fmt,
                        "-c:v", "jpeg2000", *codec, os.path.join(OUT, name)], check=True)
    references(name, fallback, last)


def main():
    os.makedirs(OUT, exist_ok=True)
    for old in os.listdir(OUT):
        os.remove(os.path.join(OUT, old))

    # OpenJPEG: both wavelets, all five progression orders, layers, tiles, precincts, odd sizes, an image offset
    pillow("lossless-lrcp.jp2", num_resolutions=6, progression="LRCP")
    pillow("lossy-rpcl.jp2", irreversible=True, quality_mode="rates", quality_layers=[24], progression="RPCL", precinct_size=(64, 64))
    pillow("layers-rlcp.j2k", irreversible=True, quality_mode="rates", quality_layers=[80, 30, 10], progression="RLCP",
           codeblock_size=(32, 32), no_jp2=True)
    pillow("tiled-pcrl.jp2", progression="PCRL", tile_size=(128, 96), num_resolutions=4)
    pillow("offset.jp2", offset=(5, 3), tile_offset=(2, 1), tile_size=(100, 70), num_resolutions=3)
    pillow("grey-cprl.jp2", mode="L", progression="CPRL", precinct_size=(32, 32), codeblock_size=(16, 16))
    pillow("rgba.jp2", mode="RGBA", irreversible=True, quality_mode="rates", quality_layers=[16])
    pillow("grey16.jp2", mode="I;16", num_resolutions=4)
    pillow("odd-333x211.jp2", size=(333, 211), irreversible=True, quality_mode="rates", quality_layers=[30], num_resolutions=4)

    # ffmpeg's own encoder: SOP and EPH markers, subsampled chroma without a component transform, tiles of its default size
    if shutil.which("ffmpeg"):
        ffmpeg("ffmpeg-sop-eph.j2k", "rgb24", "-format", "j2k", "-pred", "dwt53", "-sop", "1", "-eph", "1", "-prog", "rpcl")
        ffmpeg("ffmpeg-yuv420.jp2", "yuv420p", "-pred", "dwt97int", "-tile_width", "160", "-tile_height", "128", fallback=False,
               last=2)   # below that, Pillow places the subsampled chroma of the second tile row a row off
    else:
        print("ffmpeg not found: the ffmpeg-*.j2k/jp2 files were not written")

    # conformance codestreams: POC in a tile-part header, PPM, PPT
    urllib.request.urlretrieve(CONFORMANCE + "COPYRIGHT", os.path.join(OUT, "conformance-COPYRIGHT.txt"))
    # e1 has an odd image offset, where Pillow's reduced sizes are a row or column off: its full size only
    for name, last in (("e1_colr.j2c", 0), ("g1_colr.j2c", 99), ("p1_06.j2k", 99)):
        urllib.request.urlretrieve(CONFORMANCE + name, os.path.join(OUT, name))
        references(name, last=last)

    for file in sorted(os.listdir(OUT)):
        print(f"{os.path.getsize(os.path.join(OUT, file)):8d}  {file}")


if __name__ == "__main__":
    main()
