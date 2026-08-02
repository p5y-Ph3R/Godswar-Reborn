from __future__ import annotations

from dataclasses import dataclass

from erebus_lion.image_assets import TgaImage

from .constants import Palette


@dataclass(frozen=True)
class TransformStats:
    total_pixels: int
    visible_pixels: int
    changed_visible_pixels: int
    alpha_changes: int


def clamp_unit(value: float) -> float:
    return max(0.0, min(1.0, value))


def clamp_byte(value: float) -> int:
    return max(0, min(255, round(value)))


def mix(left: float, right: float, weight: float) -> float:
    return left + (right - left) * clamp_unit(weight)


def gradient(
    dark: tuple[int, int, int],
    mid: tuple[int, int, int],
    bright: tuple[int, int, int],
    light: float,
) -> tuple[float, float, float]:
    if light < 0.52:
        weight = light / 0.52
        return tuple(mix(dark[i], mid[i], weight) for i in range(3))
    weight = (light - 0.52) / 0.48
    return tuple(mix(mid[i], bright[i], weight) for i in range(3))


def recolor_pixel(pixel: bytes, palette: Palette) -> bytes:
    blue, green, red, alpha = pixel
    if alpha == 0:
        return pixel

    maximum = max(red, green, blue)
    minimum = min(red, green, blue)
    chroma = (maximum - minimum) / 255.0
    luminance = (54 * red + 183 * green + 19 * blue) / 256.0
    light = clamp_unit(luminance / 224.0)

    # Gold remains a shared Class Suit cue, but becomes a brighter, cleaner
    # Tier-IV metal. Other saturated regions carry the class palette.
    yellow = clamp_unit((min(red, green) - blue - 9.0) / 100.0)
    red_green_balance = clamp_unit(1.0 - abs(red - green) / 170.0)
    metal_weight = yellow * (0.40 + 0.60 * red_green_balance)
    accent_weight = clamp_unit(chroma * 1.55) * (1.0 - metal_weight * 0.72)
    neutral_weight = clamp_unit((luminance - 38.0) / 175.0) * 0.16

    accent = gradient(palette.dark, palette.mid, palette.bright, light)
    metal = tuple(
        mix(palette.metal[index], palette.highlight[index], light**1.7)
        for index in range(3)
    )
    result = [float(red), float(green), float(blue)]
    for index in range(3):
        result[index] = mix(result[index], accent[index], 0.78 * accent_weight)
        result[index] = mix(result[index], metal[index], 0.84 * metal_weight)
        result[index] = mix(
            result[index],
            palette.highlight[index],
            neutral_weight,
        )

    # Crisp near-white glints read as a higher tier at the original 32px
    # resolution without painting new geometry or touching transparency.
    glint = clamp_unit((maximum - 205.0) / 50.0) * 0.27
    for index in range(3):
        result[index] = mix(result[index], palette.highlight[index], glint)

    return bytes(
        (
            clamp_byte(result[2]),
            clamp_byte(result[1]),
            clamp_byte(result[0]),
            alpha,
        )
    )


def recolor_texture(
    source: TgaImage,
    palette: Palette,
) -> tuple[bytes, TransformStats]:
    output = bytearray(len(source.pixels))
    visible = 0
    changed_visible = 0
    alpha_changes = 0
    for position in range(0, len(source.pixels), 4):
        original = source.pixels[position : position + 4]
        recolored = recolor_pixel(original, palette)
        output[position : position + 4] = recolored
        if original[3] > 0:
            visible += 1
            if recolored != original:
                changed_visible += 1
        if recolored[3] != original[3]:
            alpha_changes += 1
    return bytes(output), TransformStats(
        total_pixels=len(source.pixels) // 4,
        visible_pixels=visible,
        changed_visible_pixels=changed_visible,
        alpha_changes=alpha_changes,
    )

