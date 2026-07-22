from __future__ import annotations

"""Generate the native-style Level-5 gem-piece sprites.

The piece silhouettes come from the shipped GodsWar atlases, while their
colour, glow, and restrained gold accents are derived from this project's
existing Level-5 full-gem sprites.  The process is deterministic and uses
only Python's standard library.

Run without flags to create/update the three 36x36 PNG assets, or use
``--check`` to prove that the committed assets match the generator exactly.
"""

import argparse
import binascii
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import struct
import sys
import tempfile
import zlib

from InstallLevel5ForgeIcons import (
    InstallError,
    TgaAtlas,
    display_pixel_index,
    parse_tga,
    read_png_rgba,
)


LOCALES = ("en_us", "zh_cn")
ATLAS_RELATIVE_DIRECTORY = Path("UI") / "Texture"
SPRITE_WIDTH = 36
SPRITE_HEIGHT = 36

EXPECTED_ATLAS_SHA256 = {
    "Icon2.gwo": "cec386236e973302a82ca61fa2ad62304cb09759b1dd18a8b71d5923a3427ca7",
    "Icon3.gwo": "3c27e65ddc369728137050006f84b535fa68686ad1cdff7b3f495d24197c7d29",
}


@dataclass(frozen=True)
class PieceSpec:
    kind: str
    source_atlas: str
    source_x: int
    source_y: int
    full_sprite: str
    full_sprite_sha256: str
    output_sprite: str
    accent_rgb: tuple[int, int, int]


PIECE_SPECS = (
    PieceSpec(
        "crystal",
        "Icon3.gwo",
        144,
        0,
        "crystal5-36.png",
        "782af228f9a0370196b6bfecd6f02c421dd0da9124b69890b7bc2f6f23c931ac",
        "crystal5-pieces-36.png",
        (150, 220, 255),
    ),
    PieceSpec(
        "sapphire",
        "Icon2.gwo",
        936,
        648,
        "sapphire5-36.png",
        "87a46cce339af670b5ce8619605c3649c1946509fa9b05fb03e7064d4cd1a8ca",
        "sapphire5-pieces-36.png",
        (85, 115, 255),
    ),
    PieceSpec(
        "emerald",
        "Icon2.gwo",
        900,
        648,
        "emerald5-36.png",
        "09ffb108a5ec0f82ba1e51faa306245b88a3b15e059a151a42d9783aa4d6b3df",
        "emerald5-pieces-36.png",
        (70, 245, 135),
    ),
)


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def clamp_unit(value: float) -> float:
    return max(0.0, min(1.0, value))


def blend_channel(source: int, target: float, weight: float) -> int:
    return max(0, min(255, round(source + (target - source) * weight)))


def extract_sprite(atlas: TgaAtlas, x: int, y: int) -> bytes:
    if x < 0 or y < 0 or x + SPRITE_WIDTH > atlas.width or y + SPRITE_HEIGHT > atlas.height:
        raise InstallError(f"Source cell is outside the atlas: {x},{y}")

    rgba = bytearray(SPRITE_WIDTH * SPRITE_HEIGHT * 4)
    destination = 0
    for row in range(SPRITE_HEIGHT):
        for column in range(SPRITE_WIDTH):
            source_index = display_pixel_index(atlas, x + column, y + row) * 4
            blue, green, red, alpha = atlas.pixels[source_index : source_index + 4]
            rgba[destination : destination + 4] = bytes((red, green, blue, alpha))
            destination += 4
    return bytes(rgba)


def foreground_weight(kind: str, red: int, green: int, blue: int) -> float:
    luminance = (54 * red + 183 * green + 19 * blue) / 256
    brightness = clamp_unit((luminance - 38) / 110)

    if kind == "sapphire":
        chroma = clamp_unit((blue - (red + green) / 2 + 15) / 95)
    elif kind == "emerald":
        chroma = clamp_unit((green - (red + blue) / 2 + 12) / 90)
    else:
        chroma = clamp_unit((min(red, green) - blue * 0.65 - 10) / 120) * 0.25

    return max(brightness, chroma * 0.72)


def render_piece_sprite(
    spec: PieceSpec,
    source_rgba: bytes,
    full_gem_rgba: bytes,
) -> bytes:
    pixels = [
        tuple(source_rgba[position : position + 4])
        for position in range(0, len(source_rgba), 4)
    ]
    foreground = [
        foreground_weight(spec.kind, red, green, blue)
        for red, green, blue, _ in pixels
    ]

    dilated: list[float] = []
    for y in range(SPRITE_HEIGHT):
        for x in range(SPRITE_WIDTH):
            dilated.append(
                max(
                    foreground[near_y * SPRITE_WIDTH + near_x]
                    for near_y in range(max(0, y - 2), min(SPRITE_HEIGHT, y + 3))
                    for near_x in range(max(0, x - 2), min(SPRITE_WIDTH, x + 3))
                )
            )

    accent_red, accent_green, accent_blue = spec.accent_rgb
    output = bytearray()
    for index, ((red, green, blue, alpha), weight, expanded_weight) in enumerate(
        zip(pixels, foreground, dilated)
    ):
        luminance = (54 * red + 183 * green + 19 * blue) / 256
        scale = 0.32 + 0.88 * (luminance / 255)
        target_red = min(255, accent_red * scale + luminance * 0.35)
        target_green = min(255, accent_green * scale + luminance * 0.35)
        target_blue = min(255, accent_blue * scale + luminance * 0.35)
        tint_weight = 0.14 + 0.18 * weight

        result_red = blend_channel(red, target_red, tint_weight)
        result_green = blend_channel(green, target_green, tint_weight)
        result_blue = blend_channel(blue, target_blue, tint_weight)

        glow_weight = max(0.0, expanded_weight - weight) * 0.22
        result_red = blend_channel(result_red, accent_red, glow_weight)
        result_green = blend_channel(result_green, accent_green, glow_weight)
        result_blue = blend_channel(result_blue, accent_blue, glow_weight)

        full_offset = index * 4
        full_red, full_green, full_blue, full_alpha = full_gem_rgba[
            full_offset : full_offset + 4
        ]
        gold_weight = (
            clamp_unit((min(full_red, full_green) - 75) / 105)
            * clamp_unit(((full_red + full_green) / 2 - full_blue - 12) / 70)
            * 0.62
        )
        result_red = blend_channel(result_red, full_red, gold_weight)
        result_green = blend_channel(result_green, full_green, gold_weight)
        result_blue = blend_channel(result_blue, full_blue, gold_weight)
        result_alpha = max(alpha, full_alpha if gold_weight > 0.12 else 0)
        output.extend((result_red, result_green, result_blue, result_alpha))

    # A small gold sparkle is a shared, readable Level-5 cue at native size.
    sparkle = {
        (5, 2): 0.70,
        (5, 3): 0.85,
        (5, 4): 1.00,
        (5, 5): 1.00,
        (5, 6): 1.00,
        (5, 7): 0.85,
        (5, 8): 0.70,
        (2, 5): 0.70,
        (3, 5): 0.85,
        (4, 5): 1.00,
        (6, 5): 1.00,
        (7, 5): 0.85,
        (8, 5): 0.70,
        (4, 4): 0.75,
        (6, 4): 0.75,
        (4, 6): 0.75,
        (6, 6): 0.75,
    }
    for (x, y), weight in sparkle.items():
        offset = (y * SPRITE_WIDTH + x) * 4
        current = tuple(output[offset : offset + 4])
        target = (255, 224, 122, 255) if weight >= 0.95 else (215, 162, 62, 255)
        output[offset : offset + 4] = bytes(
            blend_channel(current[channel], target[channel], weight)
            for channel in range(4)
        )

    return bytes(output)


def png_chunk(chunk_type: bytes, data: bytes) -> bytes:
    checksum = binascii.crc32(chunk_type + data) & 0xFFFFFFFF
    return struct.pack(">I", len(data)) + chunk_type + data + struct.pack(">I", checksum)


def encode_png_rgba(rgba: bytes) -> bytes:
    expected_length = SPRITE_WIDTH * SPRITE_HEIGHT * 4
    if len(rgba) != expected_length:
        raise InstallError(
            f"Unexpected rendered RGBA length: {len(rgba)} instead of {expected_length}"
        )

    scanlines = b"".join(
        b"\x00" + rgba[row * SPRITE_WIDTH * 4 : (row + 1) * SPRITE_WIDTH * 4]
        for row in range(SPRITE_HEIGHT)
    )
    header = struct.pack(">IIBBBBB", SPRITE_WIDTH, SPRITE_HEIGHT, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", header)
        + png_chunk(b"IDAT", zlib.compress(scanlines, 9))
        + png_chunk(b"IEND", b"")
    )


def load_source_atlases(client_root: Path) -> dict[str, TgaAtlas]:
    atlases: dict[str, TgaAtlas] = {}
    for atlas_name, expected_sha256 in EXPECTED_ATLAS_SHA256.items():
        locale_data: dict[str, bytes] = {}
        for locale in LOCALES:
            path = (
                client_root
                / "Localization"
                / locale
                / ATLAS_RELATIVE_DIRECTORY
                / atlas_name
            )
            if not path.is_file():
                raise InstallError(f"Required source atlas is missing: {path}")
            locale_data[locale] = path.read_bytes()

        if locale_data[LOCALES[0]] != locale_data[LOCALES[1]]:
            raise InstallError(f"Locale copies of {atlas_name} are not byte-identical")
        actual_sha256 = sha256_bytes(locale_data[LOCALES[0]])
        if actual_sha256 != expected_sha256:
            raise InstallError(
                f"Unexpected {atlas_name} SHA256: {actual_sha256}; expected {expected_sha256}"
            )
        atlases[atlas_name] = parse_tga(locale_data[LOCALES[0]], atlas_name)
    return atlases


def prepare_outputs(client_root: Path, asset_root: Path) -> dict[Path, bytes]:
    atlases = load_source_atlases(client_root)
    outputs: dict[Path, bytes] = {}
    for spec in PIECE_SPECS:
        full_path = asset_root / spec.full_sprite
        if not full_path.is_file():
            raise InstallError(f"Required Level-5 full-gem sprite is missing: {full_path}")
        full_data = full_path.read_bytes()
        if sha256_bytes(full_data) != spec.full_sprite_sha256:
            raise InstallError(f"Unexpected source sprite content: {full_path}")
        width, height, full_rgba = read_png_rgba(full_path)
        if width != SPRITE_WIDTH or height != SPRITE_HEIGHT:
            raise InstallError(f"Source sprite must be 36x36: {full_path}")

        source_rgba = extract_sprite(
            atlases[spec.source_atlas],
            spec.source_x,
            spec.source_y,
        )
        rendered_rgba = render_piece_sprite(spec, source_rgba, full_rgba)
        output_data = encode_png_rgba(rendered_rgba)
        output_path = asset_root / spec.output_sprite
        outputs[output_path] = output_data

    return outputs


def write_atomic(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.",
        suffix=".tmp",
        dir=path.parent,
    )
    temporary_path = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        if temporary_path.read_bytes() != data:
            raise InstallError(f"Prepared asset validation failed: {temporary_path}")
        os.replace(temporary_path, path)
    finally:
        temporary_path.unlink(missing_ok=True)


def run(args: argparse.Namespace) -> int:
    script_root = Path(__file__).resolve().parents[1]
    client_root = Path(args.client_root).resolve()
    asset_root = Path(
        args.asset_root or script_root / "assets" / "forging" / "level5"
    ).resolve()
    if not client_root.is_dir():
        raise InstallError(f"Client root does not exist: {client_root}")
    if not asset_root.is_dir():
        raise InstallError(f"Asset root does not exist: {asset_root}")

    outputs = prepare_outputs(client_root, asset_root)
    changed: list[Path] = []
    for path, expected_data in outputs.items():
        current_data = path.read_bytes() if path.exists() else None
        if current_data != expected_data:
            changed.append(path)

    if args.check:
        if changed:
            raise InstallError(
                "Generated Level-5 piece sprites differ: "
                + ", ".join(path.name for path in changed)
            )
        for path, data in outputs.items():
            print(f"Verified {path.name}: {sha256_bytes(data)}")
        return 0

    for path in changed:
        write_atomic(path, outputs[path])
        width, height, _ = read_png_rgba(path)
        if width != SPRITE_WIDTH or height != SPRITE_HEIGHT:
            raise InstallError(f"Written sprite validation failed: {path}")

    for path, data in outputs.items():
        status = "Generated" if path in changed else "Unchanged"
        print(f"{status} {path.name}: {sha256_bytes(data)}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    script_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--client-root",
        default=r"C:\Godswar Origin",
        help="Game client root (default: C:\\Godswar Origin)",
    )
    parser.add_argument(
        "--asset-root",
        default=str(script_root / "assets" / "forging" / "level5"),
        help="Level-5 forging asset directory",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify committed output sprites without writing anything",
    )
    return parser


def main() -> int:
    try:
        return run(build_parser().parse_args())
    except (InstallError, OSError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
