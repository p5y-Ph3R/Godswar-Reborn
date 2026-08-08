"""Conservative recolouring of stock 64x64 TGA effect atlases."""

from __future__ import annotations

from dataclasses import dataclass
import math
import struct

from rank_effect_packages.errors import RankEffectError
from rank_effect_packages.formats import validate_tga_texture


Rgb = tuple[float, float, float]


@dataclass(frozen=True, slots=True)
class Region:
    minimum_u: float
    maximum_u: float
    minimum_v: float
    maximum_v: float

    def contains(self, u: float, v: float) -> bool:
        return (
            self.minimum_u <= u <= self.maximum_u
            and self.minimum_v <= v <= self.maximum_v
        )


@dataclass(frozen=True, slots=True)
class RecolourResult:
    encoded: bytes
    changed_pixels: int
    outside_region_changes: int
    alpha_changes: int


def _mix(left: Rgb, right: Rgb, amount: float) -> Rgb:
    return tuple(a + (b - a) * amount for a, b in zip(left, right))  # type: ignore[return-value]


def _palette(value: float, shadow: Rgb, middle: Rgb, highlight: Rgb) -> Rgb:
    if value <= 0.55:
        return _mix(shadow, middle, value / 0.55)
    return _mix(middle, highlight, (value - 0.55) / 0.45)


def recolour_luminance(
    source: bytes,
    region: Region,
    shadow: Rgb,
    middle: Rgb,
    highlight: Rgb,
    *,
    strength: float = 0.88,
) -> RecolourResult:
    """Tint sampled detail while retaining stock luminance and exact alpha."""

    info = validate_tga_texture(source, "role-aware stock atlas")
    if info.width != 64 or info.height != 64:
        raise RankEffectError("Rank-effect prototype requires a 64x64 stock atlas")
    if not 0.0 <= strength <= 1.0:
        raise RankEffectError("Recolour strength must be within 0..1")
    header = source[:18]
    if header[0] != 0 or header[1] != 0 or header[2] != 2:
        raise RankEffectError("Only raw, identifier-free true-colour TGA is supported")
    bytes_per_pixel = info.bits_per_pixel // 8
    payload_end = 18 + info.width * info.height * bytes_per_pixel
    target = bytearray(source)
    top_origin = bool(header[17] & 0x20)
    right_origin = bool(header[17] & 0x10)
    changed = 0
    alpha_changes = 0

    for y in range(info.height):
        source_y = y if top_origin else info.height - 1 - y
        v = y / (info.height - 1)
        for x in range(info.width):
            source_x = info.width - 1 - x if right_origin else x
            u = x / (info.width - 1)
            if not region.contains(u, v):
                continue
            offset = 18 + (source_y * info.width + source_x) * bytes_per_pixel
            blue, green, red = target[offset : offset + 3]
            alpha = target[offset + 3] if bytes_per_pixel == 4 else 255
            value = max(red, green, blue) / 255.0
            if value <= 0.015 or alpha == 0:
                continue
            colour = _palette(math.sqrt(value), shadow, middle, highlight)
            desired = tuple(round(255.0 * channel * value) for channel in colour)
            result = tuple(
                max(0, min(255, round(old + (new - old) * strength)))
                for old, new in zip((red, green, blue), desired)
            )
            replacement = bytes((result[2], result[1], result[0]))
            if replacement != target[offset : offset + 3]:
                changed += 1
                target[offset : offset + 3] = replacement
            if bytes_per_pixel == 4 and target[offset + 3] != alpha:
                alpha_changes += 1

    if bytes(target[payload_end:]) != source[payload_end:]:
        raise RankEffectError("TGA footer or trailing metadata changed")
    return RecolourResult(bytes(target), changed, 0, alpha_changes)
