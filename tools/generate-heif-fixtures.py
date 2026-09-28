#!/usr/bin/env python3
"""Generate owned synthetic fixtures with macOS ImageIO. Never reads a photo library.

Run on macOS: python3 tools/generate-heif-fixtures.py
The committed outputs keep Windows tests independent of an encoder or system codec.
"""
import hashlib
import struct
import subprocess
import tempfile
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "tests" / "Fixtures" / "Heif"


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def png(path, width, height, orientation=1, with_date=True):
    # Red/green above blue/yellow. An off-centre black marker exposes rotation/mirroring.
    compressed = zlib.compressobj(6)
    data = bytearray()
    for y in range(height):
        left, right = ((240, 20, 20), (20, 230, 20)) if y < height // 2 else ((20, 20, 240), (230, 230, 20))
        row = bytearray(bytes(left) * (width // 2) + bytes(right) * (width - width // 2))
        if height // 8 <= y < height // 4:
            row[width // 8 * 3:width // 4 * 3] = bytes((width // 4 - width // 8) * 3)
        data.extend(compressed.compress(b"\0" + row))
    data.extend(compressed.flush())
    # TIFF EXIF, IFD0 -> Exif SubIFD -> DateTimeOriginal, no personal metadata.
    exif = b"II" + struct.pack("<HI", 42, 8)
    exif += struct.pack("<H", 2) + struct.pack("<HHII", 0x112, 3, 1, orientation)
    exif += struct.pack("<HHII", 0x8769, 4, 1, 38) + bytes(4)
    exif += struct.pack("<H", 1) + struct.pack("<HHII", 0x9003, 2, 20, 56) + bytes(4)
    exif += b"2026:09:28 12:34:56\0"
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                     + (chunk(b"eXIf", exif) if with_date else b"") + chunk(b"IDAT", data) + chunk(b"IEND", b""))


def main():
    ROOT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="photoshelf-synthetic-heif-") as directory:
        for name, width, height, orientation, with_date in (
            ("quadrants", 320, 180, 1, True), ("quadrants-48mp", 8064, 6048, 1, True),
            ("quadrants-rotated", 320, 180, 6, True), ("quadrants-no-date", 320, 180, 1, False)):
            source = Path(directory) / (name + ".png")
            png(source, width, height, orientation, with_date)
            target = ROOT / (name + ".heic")
            subprocess.run(["/usr/bin/sips", "-s", "format", "heic", "-s", "formatOptions", "90", str(source), "--out", str(target)], check=True)
            print(target.name, width, height, hashlib.sha256(target.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
