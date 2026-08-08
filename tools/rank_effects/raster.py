from __future__ import annotations

from dataclasses import dataclass
from math import exp, hypot
from pathlib import Path
import struct
import zlib


Color = tuple[float, float, float]


@dataclass
class Canvas:
    width: int = 64
    height: int = 64

    def __post_init__(self) -> None:
        self._pixels = [0.0] * (self.width * self.height * 3)

    def add(self, x: int, y: int, color: Color, amount: float) -> None:
        if x < 0 or y < 0 or x >= self.width or y >= self.height:
            return
        offset = (y * self.width + x) * 3
        for channel in range(3):
            self._pixels[offset + channel] += color[channel] * amount

    def glow(
        self,
        x: float,
        y: float,
        radius: float,
        color: Color,
        strength: float = 1.0,
    ) -> None:
        reach = max(1, round(radius * 3.0))
        left = max(0, int(x) - reach)
        right = min(self.width - 1, int(x) + reach)
        top = max(0, int(y) - reach)
        bottom = min(self.height - 1, int(y) + reach)
        divisor = max(0.01, 2.0 * radius * radius)
        for py in range(top, bottom + 1):
            for px in range(left, right + 1):
                distance_squared = (px - x) ** 2 + (py - y) ** 2
                amount = strength * exp(-distance_squared / divisor)
                if amount > 0.004:
                    self.add(px, py, color, amount)

    def line(
        self,
        start: tuple[float, float],
        end: tuple[float, float],
        width: float,
        color: Color,
        strength: float = 1.0,
    ) -> None:
        x1, y1 = start
        x2, y2 = end
        dx = x2 - x1
        dy = y2 - y1
        length_squared = dx * dx + dy * dy
        reach = max(2, round(width * 3.0))
        left = max(0, int(min(x1, x2)) - reach)
        right = min(self.width - 1, int(max(x1, x2)) + reach)
        top = max(0, int(min(y1, y2)) - reach)
        bottom = min(self.height - 1, int(max(y1, y2)) + reach)
        divisor = max(0.01, 2.0 * width * width)
        for py in range(top, bottom + 1):
            for px in range(left, right + 1):
                if length_squared == 0:
                    distance = hypot(px - x1, py - y1)
                else:
                    projection = ((px - x1) * dx + (py - y1) * dy) / length_squared
                    projection = max(0.0, min(1.0, projection))
                    nearest_x = x1 + projection * dx
                    nearest_y = y1 + projection * dy
                    distance = hypot(px - nearest_x, py - nearest_y)
                amount = strength * exp(-(distance * distance) / divisor)
                if amount > 0.004:
                    self.add(px, py, color, amount)

    def polyline(
        self,
        points: list[tuple[float, float]],
        width: float,
        color: Color,
        strength: float = 1.0,
    ) -> None:
        for start, end in zip(points, points[1:]):
            self.line(start, end, width, color, strength)

    def ring(
        self,
        center: tuple[float, float],
        radii: tuple[float, float],
        width: float,
        color: Color,
        strength: float = 1.0,
        gaps: int = 0,
        gap_fraction: float = 0.0,
    ) -> None:
        from math import atan2, pi

        cx, cy = center
        rx, ry = radii
        divisor = max(0.01, 2.0 * width * width)
        for py in range(self.height):
            for px in range(self.width):
                nx = (px - cx) / max(0.01, rx)
                ny = (py - cy) / max(0.01, ry)
                radial = hypot(nx, ny)
                distance = abs(radial - 1.0) * min(rx, ry)
                if distance > width * 3.0:
                    continue
                if gaps:
                    angle = (atan2(ny, nx) + pi) / (2.0 * pi)
                    phase = (angle * gaps) % 1.0
                    if phase < gap_fraction:
                        continue
                amount = strength * exp(-(distance * distance) / divisor)
                self.add(px, py, color, amount)

    def rgba(self) -> bytes:
        output = bytearray()
        for offset in range(0, len(self._pixels), 3):
            channels = [min(255, round(255 * value)) for value in self._pixels[offset : offset + 3]]
            alpha = min(255, max(channels) * 2)
            output.extend((*channels, alpha))
        return bytes(output)


def write_tga(path: Path, canvas: Canvas, bits_per_pixel: int) -> None:
    if bits_per_pixel not in (24, 32):
        raise ValueError("TGA output must be 24-bit or 32-bit")
    rgba = canvas.rgba()
    header = bytearray(18)
    header[2] = 2
    struct.pack_into("<HH", header, 12, canvas.width, canvas.height)
    header[16] = bits_per_pixel
    header[17] = 8 if bits_per_pixel == 32 else 0
    payload = bytearray(header)
    stride = canvas.width * 4
    for y in range(canvas.height - 1, -1, -1):
        row = rgba[y * stride : (y + 1) * stride]
        for offset in range(0, len(row), 4):
            red, green, blue, alpha = row[offset : offset + 4]
            payload.extend((blue, green, red))
            if bits_per_pixel == 32:
                payload.append(alpha)
    payload.extend(b"\x00" * 8)
    payload.extend(b"TRUEVISION-XFILE.\x00")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)


def write_preview(path: Path, canvas: Canvas, scale: int = 4) -> None:
    rgba = canvas.rgba()
    width = canvas.width * scale
    height = canvas.height * scale
    rows = bytearray()
    source_stride = canvas.width * 4
    for y in range(canvas.height):
        source_row = rgba[y * source_stride : (y + 1) * source_stride]
        expanded = bytearray()
        for offset in range(0, len(source_row), 4):
            pixel = source_row[offset : offset + 4]
            for _ in range(scale):
                expanded.extend(pixel)
        for _ in range(scale):
            rows.append(0)
            rows.extend(expanded)

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
    png += chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png)
