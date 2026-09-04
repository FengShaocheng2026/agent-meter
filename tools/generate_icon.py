from __future__ import annotations

import math
import struct
import zlib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SUPERSAMPLE = 4


def blend(samples: list[tuple[int, int, int, int]]) -> tuple[int, int, int, int]:
    count = len(samples)
    alpha = sum(sample[3] for sample in samples) / count
    if alpha == 0:
        return 0, 0, 0, 0

    red = sum(sample[0] * sample[3] for sample in samples) / count / alpha
    green = sum(sample[1] * sample[3] for sample in samples) / count / alpha
    blue = sum(sample[2] * sample[3] for sample in samples) / count / alpha
    return round(red), round(green), round(blue), round(alpha)


def sample_pixel(x: float, y: float) -> tuple[int, int, int, int]:
    dx = x - 0.5
    dy = y - 0.5
    radius = math.hypot(dx, dy)
    if radius > 0.47:
        return 0, 0, 0, 0

    background = (34, 37, 42, 255)
    if radius < 0.075:
        return 248, 250, 252, 255

    ring_radius = 0.315
    ring_width = 0.105
    if abs(radius - ring_radius) <= ring_width / 2:
        angle = math.degrees(math.atan2(dy, dx)) % 360
        sweep = (angle - 135) % 360
        if sweep <= 270:
            return (83, 211, 139, 255) if sweep <= 194 else (99, 109, 122, 255)

    return background


def render(size: int) -> bytes:
    scale = size * SUPERSAMPLE
    high_res: list[tuple[int, int, int, int]] = []
    for y in range(scale):
        for x in range(scale):
            high_res.append(sample_pixel((x + 0.5) / scale, (y + 0.5) / scale))

    pixels = bytearray()
    for y in range(size):
        pixels.append(0)
        for x in range(size):
            samples = [
                high_res[(y * SUPERSAMPLE + sy) * scale + x * SUPERSAMPLE + sx]
                for sy in range(SUPERSAMPLE)
                for sx in range(SUPERSAMPLE)
            ]
            pixels.extend(blend(samples))

    def chunk(name: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + name
            + data
            + struct.pack(">I", zlib.crc32(name + data) & 0xFFFFFFFF)
        )

    header = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return header + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(pixels), 9)) + chunk(b"IEND", b"")


def write_assets() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    images = [(size, render(size)) for size in ICON_SIZES]

    header_size = 6 + len(images) * 16
    offset = header_size
    entries = bytearray()
    payload = bytearray()
    for size, image in images:
        dimension = 0 if size == 256 else size
        entries.extend(struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(image), offset))
        payload.extend(image)
        offset += len(image)

    (ASSETS / "AgentMeter.ico").write_bytes(
        struct.pack("<HHH", 0, 1, len(images)) + entries + payload
    )
    (ASSETS / "AgentMeter.png").write_bytes(render(512))


if __name__ == "__main__":
    write_assets()
