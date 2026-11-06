"""Verify the repository's reviewed 8-bit RGBA cleanup examples."""

import struct
import sys
import zlib
from pathlib import Path


EXAMPLES = Path(__file__).resolve().parents[1] / "examples"
WIDTH = HEIGHT = 128
STRIDE = WIDTH * 4
CLEAR = bytes(4)
REMOVE = b"\xff" * 4


def paeth(left, up, upper_left):
    estimate = left + up - upper_left
    return min((left, up, upper_left), key=lambda value: abs(estimate - value))


def read_rgba(path):
    data = path.read_bytes()
    header = struct.pack(">I4sIIBBBBB", 13, b"IHDR", WIDTH, HEIGHT, 8, 6, 0, 0, 0)
    if not data.startswith(b"\x89PNG\r\n\x1a\n" + header):
        raise ValueError(f"{path.name}: expected a 128 × 128 non-interlaced 8-bit RGBA PNG")

    offset = 8
    compressed = bytearray()
    while offset + 12 <= len(data):
        length, kind = struct.unpack_from(">I4s", data, offset)
        end = offset + 8 + length
        if end + 4 > len(data):
            raise ValueError(f"{path.name}: truncated PNG chunk")
        checksum = struct.unpack_from(">I", data, end)[0]
        if zlib.crc32(data[offset + 4:end]) != checksum:
            raise ValueError(f"{path.name}: invalid PNG chunk checksum")
        offset = end + 4
        if kind == b"IDAT":
            compressed.extend(data[end - length:end])
        if kind == b"IEND" and length == 0 and offset == len(data):
            break
    else:
        raise ValueError(f"{path.name}: missing or invalid IEND chunk")

    expected_length = HEIGHT * (STRIDE + 1)
    decoder = zlib.decompressobj()
    rows = decoder.decompress(compressed, expected_length + 1)
    if len(rows) != expected_length or not decoder.eof or decoder.unused_data:
        raise ValueError(f"{path.name}: invalid PNG image data")

    pixels = bytearray()
    prior = bytearray(STRIDE)
    for y in range(HEIGHT):
        start = y * (STRIDE + 1)
        filter_type = rows[start]
        row = bytearray(rows[start + 1:start + 1 + STRIDE])
        if filter_type > 4:
            raise ValueError(f"{path.name}: invalid filter on row {y}")
        if filter_type:
            for i in range(STRIDE):
                left = row[i - 4] if i >= 4 else 0
                up = prior[i]
                upper_left = prior[i - 4] if i >= 4 else 0
                if filter_type == 1:
                    predictor = left
                elif filter_type == 2:
                    predictor = up
                elif filter_type == 3:
                    predictor = (left + up) // 2
                else:
                    predictor = paeth(left, up, upper_left)
                row[i] = (row[i] + predictor) & 255
        pixels.extend(row)
        prior = row
    return pixels


def verify(name, expected_removals):
    folder = EXAMPLES / name
    before = read_rgba(folder / "before.png")
    after = read_rgba(folder / "after.png")
    mask = read_rgba(folder / "removed-mask.png")
    removed = 0

    for i in range(WIDTH * HEIGHT):
        start = i * 4
        old, new = before[start:start + 4], after[start:start + 4]
        marker = mask[start:start + 4]
        location = f"({i % WIDTH}, {i // WIDTH})"
        if marker == REMOVE:
            if old[3] == 0 or new != CLEAR:
                raise ValueError(f"{location}: expected visible pixel to become transparent black")
            removed += 1
        elif marker != CLEAR:
            raise ValueError(f"{location}: mask pixel must be opaque white or transparent black")
        elif old != new:
            raise ValueError(f"{location}: RGBA outside mask changed from #{old.hex().upper()} to #{new.hex().upper()}")

    if removed != expected_removals:
        raise ValueError(f"expected {expected_removals} removals, found {removed}")

    unchanged = WIDTH * HEIGHT - removed
    print(f"PASS {name}.png: {removed} removed, {unchanged:,} RGBA pixels unchanged outside the mask")


def main():
    failed = False
    for name, expected in (("active", 439), ("cancelled", 326)):
        try:
            verify(name, expected)
        except (OSError, ValueError, zlib.error) as error:
            print(f"FAIL {name}.png: {error}", file=sys.stderr)
            failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
