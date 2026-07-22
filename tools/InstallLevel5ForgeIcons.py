from __future__ import annotations

"""Install the dedicated Level-5 forging-material icon atlas.

The GodsWar ``.gwo`` icon atlases are 32-bit RLE TGA files.  This installer
clones the proven ``Icon3.gwo`` payload into ``Icon4.gwo`` for both locales,
then replaces six complete 36x36 atlas cells.  It intentionally has no
third-party dependencies.

Run with ``--dry-run`` to validate and preview, or ``--check`` to verify an
already-installed client without writing anything.
"""

import argparse
import binascii
from dataclasses import dataclass
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import zlib


LOCALES = ("en_us", "zh_cn")
SOURCE_ATLAS_NAME = "Icon3.gwo"
TARGET_ATLAS_NAME = "Icon4.gwo"
ATLAS_RELATIVE_DIRECTORY = Path("UI") / "Texture"
TGA_FOOTER_SIGNATURE = b"TRUEVISION-XFILE.\x00"
EXPECTED_TGA_WIDTH = 1024
EXPECTED_TGA_HEIGHT = 1024
EXPECTED_TGA_DESCRIPTOR = 0x08
EXPECTED_EXTENSION_SIZE = 495


@dataclass(frozen=True)
class SpriteSpec:
    filename: str
    x: int
    y: int


SPRITE_SPECS = (
    SpriteSpec("crystal5-36.png", 0, 0),
    SpriteSpec("sapphire5-36.png", 36, 0),
    SpriteSpec("emerald5-36.png", 72, 0),
    SpriteSpec("crystal5-pieces-36.png", 108, 0),
    SpriteSpec("sapphire5-pieces-36.png", 144, 0),
    SpriteSpec("emerald5-pieces-36.png", 180, 0),
)
SPRITE_WIDTH = 36
SPRITE_HEIGHT = 36


class InstallError(RuntimeError):
    pass


@dataclass(frozen=True)
class RlePacket:
    pixel_start: int
    pixel_count: int
    encoded_start: int
    encoded_end: int
    is_rle: bool


@dataclass(frozen=True)
class TgaAtlas:
    raw: bytes
    prefix: bytes
    width: int
    height: int
    descriptor: int
    pixels: bytes
    packets: tuple[RlePacket, ...]
    stream_end: int
    extension: bytes
    footer: bytes


@dataclass(frozen=True)
class Sprite:
    spec: SpriteSpec
    bgra: bytes
    sha256: str


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def read_png_rgba(path: Path) -> tuple[int, int, bytes]:
    data = path.read_bytes()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise InstallError(f"Sprite is not a PNG file: {path}")

    position = 8
    ihdr: tuple[int, int, int, int, int, int, int] | None = None
    idat = bytearray()
    saw_iend = False

    while position < len(data):
        if position + 12 > len(data):
            raise InstallError(f"Truncated PNG chunk in {path}")
        chunk_length = struct.unpack_from(">I", data, position)[0]
        chunk_type = data[position + 4 : position + 8]
        chunk_start = position + 8
        chunk_end = chunk_start + chunk_length
        crc_end = chunk_end + 4
        if crc_end > len(data):
            raise InstallError(f"Truncated PNG payload in {path}")

        chunk_data = data[chunk_start:chunk_end]
        stored_crc = struct.unpack_from(">I", data, chunk_end)[0]
        actual_crc = binascii.crc32(chunk_type + chunk_data) & 0xFFFFFFFF
        if stored_crc != actual_crc:
            raise InstallError(f"PNG CRC mismatch in {path} ({chunk_type!r})")

        if chunk_type == b"IHDR":
            if ihdr is not None or chunk_length != 13:
                raise InstallError(f"Invalid PNG IHDR in {path}")
            ihdr = struct.unpack(">IIBBBBB", chunk_data)
        elif chunk_type == b"IDAT":
            if ihdr is None:
                raise InstallError(f"PNG IDAT precedes IHDR in {path}")
            idat.extend(chunk_data)
        elif chunk_type == b"IEND":
            if chunk_length != 0:
                raise InstallError(f"Invalid PNG IEND in {path}")
            saw_iend = True
            position = crc_end
            break

        position = crc_end

    if ihdr is None or not saw_iend or position != len(data):
        raise InstallError(f"Incomplete or trailing PNG data in {path}")

    width, height, bit_depth, color_type, compression, filtering, interlace = ihdr
    if (
        bit_depth != 8
        or color_type != 6
        or compression != 0
        or filtering != 0
        or interlace != 0
    ):
        raise InstallError(
            f"Expected non-interlaced 8-bit RGBA PNG at {path}; "
            f"got depth={bit_depth}, color={color_type}, interlace={interlace}"
        )

    try:
        filtered = zlib.decompress(bytes(idat))
    except zlib.error as error:
        raise InstallError(f"Cannot decompress PNG data in {path}: {error}") from error

    bytes_per_pixel = 4
    stride = width * bytes_per_pixel
    expected_length = height * (stride + 1)
    if len(filtered) != expected_length:
        raise InstallError(
            f"Unexpected decompressed PNG size in {path}: "
            f"{len(filtered)} instead of {expected_length}"
        )

    rgba = bytearray(height * stride)
    source_position = 0
    previous = bytearray(stride)

    for row in range(height):
        filter_type = filtered[source_position]
        source_position += 1
        encoded_row = filtered[source_position : source_position + stride]
        source_position += stride
        decoded_row = bytearray(stride)

        for index, encoded_byte in enumerate(encoded_row):
            left = decoded_row[index - bytes_per_pixel] if index >= bytes_per_pixel else 0
            above = previous[index]
            upper_left = previous[index - bytes_per_pixel] if index >= bytes_per_pixel else 0

            if filter_type == 0:
                value = encoded_byte
            elif filter_type == 1:
                value = (encoded_byte + left) & 0xFF
            elif filter_type == 2:
                value = (encoded_byte + above) & 0xFF
            elif filter_type == 3:
                value = (encoded_byte + ((left + above) // 2)) & 0xFF
            elif filter_type == 4:
                predictor = left + above - upper_left
                distance_left = abs(predictor - left)
                distance_above = abs(predictor - above)
                distance_upper_left = abs(predictor - upper_left)
                if distance_left <= distance_above and distance_left <= distance_upper_left:
                    paeth = left
                elif distance_above <= distance_upper_left:
                    paeth = above
                else:
                    paeth = upper_left
                value = (encoded_byte + paeth) & 0xFF
            else:
                raise InstallError(f"Unsupported PNG filter {filter_type} in {path}")

            decoded_row[index] = value

        row_start = row * stride
        rgba[row_start : row_start + stride] = decoded_row
        previous = decoded_row

    return width, height, bytes(rgba)


def load_sprites(asset_root: Path) -> tuple[Sprite, ...]:
    sprites: list[Sprite] = []
    for spec in SPRITE_SPECS:
        path = asset_root / spec.filename
        if not path.is_file():
            raise InstallError(f"Required sprite is missing: {path}")
        width, height, rgba = read_png_rgba(path)
        if width != SPRITE_WIDTH or height != SPRITE_HEIGHT:
            raise InstallError(
                f"Sprite {path} must be {SPRITE_WIDTH}x{SPRITE_HEIGHT}; "
                f"got {width}x{height}"
            )

        bgra = bytearray(len(rgba))
        for position in range(0, len(rgba), 4):
            red, green, blue, alpha = rgba[position : position + 4]
            bgra[position : position + 4] = bytes((blue, green, red, alpha))

        sprites.append(Sprite(spec, bytes(bgra), sha256_bytes(path.read_bytes())))

    return tuple(sprites)


def parse_tga(data: bytes, label: str) -> TgaAtlas:
    if len(data) < 18 + 26:
        raise InstallError(f"{label} is too small to be a TGA atlas")

    (
        id_length,
        color_map_type,
        image_type,
        color_map_first,
        color_map_length,
        color_map_depth,
        x_origin,
        y_origin,
        width,
        height,
        pixel_depth,
        descriptor,
    ) = struct.unpack_from("<BBBHHBHHHHBB", data, 0)

    if (
        id_length != 0
        or color_map_type != 0
        or image_type != 10
        or color_map_first != 0
        or color_map_length != 0
        or color_map_depth != 0
        or x_origin != 0
        or y_origin != 0
        or width != EXPECTED_TGA_WIDTH
        or height != EXPECTED_TGA_HEIGHT
        or pixel_depth != 32
        or descriptor != EXPECTED_TGA_DESCRIPTOR
    ):
        raise InstallError(
            f"Unexpected TGA format for {label}: type={image_type}, "
            f"size={width}x{height}, depth={pixel_depth}, descriptor=0x{descriptor:02x}"
        )

    prefix_end = 18 + id_length
    total_pixels = width * height
    pixels = bytearray(total_pixels * 4)
    packets: list[RlePacket] = []
    position = prefix_end
    decoded_pixels = 0

    while decoded_pixels < total_pixels:
        if position >= len(data):
            raise InstallError(f"Truncated TGA packet stream in {label}")
        packet_start = position
        packet_header = data[position]
        position += 1
        pixel_count = (packet_header & 0x7F) + 1
        is_rle = bool(packet_header & 0x80)
        if decoded_pixels + pixel_count > total_pixels:
            raise InstallError(f"TGA packet overruns the pixel count in {label}")

        destination_start = decoded_pixels * 4
        if is_rle:
            if position + 4 > len(data):
                raise InstallError(f"Truncated TGA RLE pixel in {label}")
            pixel = data[position : position + 4]
            position += 4
            pixels[destination_start : destination_start + pixel_count * 4] = pixel * pixel_count
        else:
            byte_count = pixel_count * 4
            if position + byte_count > len(data):
                raise InstallError(f"Truncated TGA raw packet in {label}")
            pixels[destination_start : destination_start + byte_count] = data[
                position : position + byte_count
            ]
            position += byte_count

        packets.append(
            RlePacket(
                decoded_pixels,
                pixel_count,
                packet_start,
                position,
                is_rle,
            )
        )
        decoded_pixels += pixel_count

    stream_end = position
    footer_start = len(data) - 26
    if footer_start < stream_end:
        raise InstallError(f"TGA footer overlaps the packet stream in {label}")
    extension_offset, developer_offset = struct.unpack_from("<II", data, footer_start)
    signature = data[footer_start + 8 :]
    if signature != TGA_FOOTER_SIGNATURE:
        raise InstallError(f"Missing TGA 2.0 footer signature in {label}")
    if extension_offset != stream_end:
        raise InstallError(
            f"TGA extension offset mismatch in {label}: "
            f"footer={extension_offset}, stream={stream_end}"
        )
    if developer_offset != 0:
        raise InstallError(f"Unexpected TGA developer directory in {label}")

    extension = data[extension_offset:footer_start]
    if len(extension) != EXPECTED_EXTENSION_SIZE:
        raise InstallError(
            f"Unexpected TGA extension length in {label}: {len(extension)}"
        )
    if struct.unpack_from("<H", extension, 0)[0] != EXPECTED_EXTENSION_SIZE:
        raise InstallError(f"Invalid TGA extension header in {label}")
    if struct.unpack_from("<III", extension, 482) != (0, 0, 0):
        raise InstallError(f"Unexpected auxiliary TGA extension offsets in {label}")

    return TgaAtlas(
        raw=data,
        prefix=data[:prefix_end],
        width=width,
        height=height,
        descriptor=descriptor,
        pixels=bytes(pixels),
        packets=tuple(packets),
        stream_end=stream_end,
        extension=extension,
        footer=data[footer_start:],
    )


def display_pixel_index(atlas: TgaAtlas, x: int, y: int) -> int:
    if not (0 <= x < atlas.width and 0 <= y < atlas.height):
        raise InstallError(f"Atlas coordinate is out of range: {x},{y}")
    stream_x = atlas.width - 1 - x if atlas.descriptor & 0x10 else x
    stream_y = y if atlas.descriptor & 0x20 else atlas.height - 1 - y
    return stream_y * atlas.width + stream_x


def make_desired_pixels(
    atlas: TgaAtlas, sprites: tuple[Sprite, ...]
) -> tuple[dict[int, bytes], frozenset[int]]:
    desired: dict[int, bytes] = {}
    target_indices: set[int] = set()

    for sprite in sprites:
        spec = sprite.spec
        if spec.x + SPRITE_WIDTH > atlas.width or spec.y + SPRITE_HEIGHT > atlas.height:
            raise InstallError(f"Sprite {spec.filename} does not fit in the atlas")
        for row in range(SPRITE_HEIGHT):
            for column in range(SPRITE_WIDTH):
                atlas_index = display_pixel_index(atlas, spec.x + column, spec.y + row)
                if atlas_index in target_indices:
                    raise InstallError(f"Sprite cells overlap at atlas pixel {atlas_index}")
                sprite_offset = (row * SPRITE_WIDTH + column) * 4
                desired[atlas_index] = sprite.bgra[sprite_offset : sprite_offset + 4]
                target_indices.add(atlas_index)

    return desired, frozenset(target_indices)


def encode_pixel_sequence(pixels: list[bytes]) -> bytes:
    encoded = bytearray()
    position = 0

    while position < len(pixels):
        run_length = 1
        while (
            position + run_length < len(pixels)
            and run_length < 128
            and pixels[position + run_length] == pixels[position]
        ):
            run_length += 1

        if run_length >= 2:
            encoded.append(0x80 | (run_length - 1))
            encoded.extend(pixels[position])
            position += run_length
            continue

        raw_start = position
        position += 1
        while position < len(pixels) and position - raw_start < 128:
            next_run = 1
            while (
                position + next_run < len(pixels)
                and next_run < 128
                and pixels[position + next_run] == pixels[position]
            ):
                next_run += 1
            if next_run >= 2:
                break
            position += 1

        raw_pixels = pixels[raw_start:position]
        encoded.append(len(raw_pixels) - 1)
        for pixel in raw_pixels:
            encoded.extend(pixel)

    return bytes(encoded)


def patch_atlas(base: TgaAtlas, desired: dict[int, bytes]) -> bytes:
    actual_changes = {
        index: pixel
        for index, pixel in desired.items()
        if base.pixels[index * 4 : index * 4 + 4] != pixel
    }
    output = bytearray(base.prefix)

    for packet in base.packets:
        changed_offsets = [
            offset
            for offset in range(packet.pixel_count)
            if packet.pixel_start + offset in actual_changes
        ]
        original_packet = base.raw[packet.encoded_start : packet.encoded_end]
        if not changed_offsets:
            output.extend(original_packet)
            continue

        if not packet.is_rle:
            patched_packet = bytearray(original_packet)
            for offset in changed_offsets:
                atlas_index = packet.pixel_start + offset
                byte_start = 1 + offset * 4
                patched_packet[byte_start : byte_start + 4] = actual_changes[atlas_index]
            output.extend(patched_packet)
            continue

        packet_pixels = []
        for offset in range(packet.pixel_count):
            atlas_index = packet.pixel_start + offset
            packet_pixels.append(
                actual_changes.get(
                    atlas_index,
                    base.pixels[atlas_index * 4 : atlas_index * 4 + 4],
                )
            )

        if all(pixel == packet_pixels[0] for pixel in packet_pixels):
            output.append(0x80 | (packet.pixel_count - 1))
            output.extend(packet_pixels[0])
        else:
            output.extend(encode_pixel_sequence(packet_pixels))

    extension_offset = len(output)
    output.extend(base.extension)
    footer = bytearray(base.footer)
    struct.pack_into("<I", footer, 0, extension_offset)
    output.extend(footer)
    return bytes(output)


def validate_generated_atlas(
    base: TgaAtlas,
    generated_data: bytes,
    desired: dict[int, bytes],
    label: str,
) -> TgaAtlas:
    generated = parse_tga(generated_data, label)
    if generated.prefix != base.prefix:
        raise InstallError(f"Generated TGA header changed unexpectedly in {label}")
    if generated.extension != base.extension:
        raise InstallError(f"Generated TGA extension changed unexpectedly in {label}")
    if generated.footer[4:] != base.footer[4:]:
        raise InstallError(f"Generated TGA footer changed unexpectedly in {label}")

    expected_pixels = bytearray(base.pixels)
    for index, pixel in desired.items():
        expected_pixels[index * 4 : index * 4 + 4] = pixel
    if generated.pixels != bytes(expected_pixels):
        raise InstallError(f"Generated atlas pixel validation failed in {label}")

    return generated


def target_differs_only_in_owned_cells(
    base: TgaAtlas,
    target_data: bytes,
    target_indices: frozenset[int],
    label: str,
) -> tuple[bool, str]:
    try:
        target = parse_tga(target_data, label)
    except InstallError as error:
        return False, str(error)

    if target.prefix != base.prefix:
        return False, "TGA header differs from Icon3.gwo"
    if target.extension != base.extension:
        return False, "TGA extension differs from Icon3.gwo"
    if target.footer[4:] != base.footer[4:]:
        return False, "TGA footer differs from Icon3.gwo"

    normalized = bytearray(target.pixels)
    for index in target_indices:
        normalized[index * 4 : index * 4 + 4] = base.pixels[index * 4 : index * 4 + 4]
    if bytes(normalized) != base.pixels:
        return False, "pixels outside the six Level-5 cells differ from Icon3.gwo"

    return True, "only the owned Level-5 cells differ"


def ensure_within(root: Path, path: Path, label: str) -> None:
    try:
        path.resolve().relative_to(root.resolve())
    except ValueError as error:
        raise InstallError(f"{label} is outside the client root: {path}") from error


def write_prepared_file(path: Path, data: bytes) -> Path:
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
            raise InstallError(f"Prepared file verification failed: {temporary_path}")
        return temporary_path
    except Exception:
        temporary_path.unlink(missing_ok=True)
        raise


def create_backup_directory(backup_root: Path) -> Path:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    backup_directory = backup_root / f"client-level5-forge-icons-{timestamp}"
    backup_directory.mkdir(parents=True, exist_ok=False)
    return backup_directory


def install(args: argparse.Namespace) -> int:
    script_root = Path(__file__).resolve().parents[1]
    client_root = Path(args.client_root).resolve()
    asset_root = Path(args.asset_root or script_root / "assets" / "forging" / "level5").resolve()
    backup_root = Path(args.backup_root or script_root / "backups").resolve()

    if args.check and args.dry_run:
        raise InstallError("--check and --dry-run cannot be used together")
    if not client_root.is_dir():
        raise InstallError(f"Client root does not exist: {client_root}")

    sprites = load_sprites(asset_root)
    source_paths: dict[str, Path] = {}
    target_paths: dict[str, Path] = {}
    source_data: dict[str, bytes] = {}

    for locale in LOCALES:
        texture_root = client_root / "Localization" / locale / ATLAS_RELATIVE_DIRECTORY
        source_path = texture_root / SOURCE_ATLAS_NAME
        target_path = texture_root / TARGET_ATLAS_NAME
        ensure_within(client_root, source_path, f"{locale} source atlas")
        ensure_within(client_root, target_path, f"{locale} target atlas")
        if not source_path.is_file():
            raise InstallError(f"Source atlas is missing: {source_path}")
        source_paths[locale] = source_path
        target_paths[locale] = target_path
        source_data[locale] = source_path.read_bytes()

    if source_data[LOCALES[0]] != source_data[LOCALES[1]]:
        raise InstallError("The en_us and zh_cn Icon3.gwo source atlases are not byte-identical")

    base = parse_tga(source_data[LOCALES[0]], str(source_paths[LOCALES[0]]))
    desired, target_indices = make_desired_pixels(base, sprites)
    expected_data = patch_atlas(base, desired)
    validate_generated_atlas(base, expected_data, desired, "prepared Icon4.gwo")

    expected_by_locale = {locale: expected_data for locale in LOCALES}
    if expected_by_locale[LOCALES[0]] != expected_by_locale[LOCALES[1]]:
        raise InstallError("Prepared locale atlases are not byte-identical")

    original_data: dict[str, bytes | None] = {}
    changed_locales: list[str] = []
    for locale in LOCALES:
        target_path = target_paths[locale]
        current = target_path.read_bytes() if target_path.exists() else None
        original_data[locale] = current
        if current == expected_by_locale[locale]:
            continue
        changed_locales.append(locale)
        if current is not None and not args.force:
            safe, reason = target_differs_only_in_owned_cells(
                base,
                current,
                target_indices,
                str(target_path),
            )
            if not safe:
                raise InstallError(
                    f"Refusing to replace unexpected existing atlas {target_path}: {reason}. "
                    "Use --force only after inspecting and backing up that file."
                )

    if args.check:
        if changed_locales:
            raise InstallError(
                "Level-5 icon atlas is not installed exactly in locale(s): "
                + ", ".join(changed_locales)
            )
        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon4.gwo locale files are not byte-identical")
        print(f"Verified {TARGET_ATLAS_NAME}: {sha256_bytes(expected_data)}")
        return 0

    if not changed_locales:
        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon4.gwo locale files are not byte-identical")
        print(f"No changes needed; {TARGET_ATLAS_NAME} is already installed and verified.")
        return 0

    print("Locale atlas changes required: " + ", ".join(changed_locales))
    print(f"Prepared SHA256: {sha256_bytes(expected_data)}")
    if args.dry_run:
        print("Dry run complete; no files or backups were written.")
        return 0

    prepared_paths: dict[str, Path] = {}
    backup_directory: Path | None = None
    replaced_locales: list[str] = []
    try:
        for locale in changed_locales:
            prepared = write_prepared_file(target_paths[locale], expected_by_locale[locale])
            validate_generated_atlas(
                base,
                prepared.read_bytes(),
                desired,
                f"prepared {locale} {TARGET_ATLAS_NAME}",
            )
            prepared_paths[locale] = prepared

        backup_directory = create_backup_directory(backup_root)
        manifest: dict[str, object] = {
            "created_at": datetime.now().astimezone().isoformat(),
            "client_root": str(client_root),
            "source_atlas": SOURCE_ATLAS_NAME,
            "target_atlas": TARGET_ATLAS_NAME,
            "source_sha256": sha256_bytes(source_data[LOCALES[0]]),
            "target_sha256": sha256_bytes(expected_data),
            "sprites": [
                {
                    "file": sprite.spec.filename,
                    "sha256": sprite.sha256,
                    "x": sprite.spec.x,
                    "y": sprite.spec.y,
                    "width": SPRITE_WIDTH,
                    "height": SPRITE_HEIGHT,
                }
                for sprite in sprites
            ],
            "locales": {},
        }

        locale_manifest: dict[str, object] = {}
        for locale in LOCALES:
            current = original_data[locale]
            entry: dict[str, object] = {
                "changed": locale in changed_locales,
                "original_sha256": sha256_bytes(current) if current is not None else None,
                "installed_sha256": sha256_bytes(expected_by_locale[locale]),
            }
            if locale in changed_locales and current is not None:
                relative_target = target_paths[locale].relative_to(client_root)
                backup_path = backup_directory / relative_target
                backup_path.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(target_paths[locale], backup_path)
                if backup_path.read_bytes() != current:
                    raise InstallError(f"Backup verification failed: {backup_path}")
                entry["backup"] = str(backup_path.relative_to(backup_directory))
            else:
                entry["backup"] = None
            locale_manifest[locale] = entry
        manifest["locales"] = locale_manifest

        manifest_path = backup_directory / "manifest.json"
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

        for locale in changed_locales:
            os.replace(prepared_paths[locale], target_paths[locale])
            replaced_locales.append(locale)

        for locale in LOCALES:
            installed_data = target_paths[locale].read_bytes()
            if installed_data != expected_by_locale[locale]:
                raise InstallError(f"Post-install byte validation failed for {locale}")
            validate_generated_atlas(
                base,
                installed_data,
                desired,
                f"installed {locale} {TARGET_ATLAS_NAME}",
            )

        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon4.gwo locale files are not byte-identical")

    except Exception:
        for locale in reversed(replaced_locales):
            target_path = target_paths[locale]
            original = original_data[locale]
            if original is None:
                target_path.unlink(missing_ok=True)
            else:
                rollback_path = write_prepared_file(target_path, original)
                os.replace(rollback_path, target_path)
        raise
    finally:
        for prepared_path in prepared_paths.values():
            prepared_path.unlink(missing_ok=True)

    print(f"Installed byte-identical locale atlases: {sha256_bytes(expected_data)}")
    print(f"Backup manifest: {backup_directory / 'manifest.json'}")
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
        help="Directory containing the six fixed 36x36 PNG sprites",
    )
    parser.add_argument(
        "--backup-root",
        default=str(script_root / "backups"),
        help="Directory in which timestamped backups and manifests are created",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Validate and report required changes without writing anything",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify that both installed locale atlases exactly match the expected output",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Replace an unexpected existing Icon4.gwo after backing it up",
    )
    return parser


def main() -> int:
    try:
        return install(build_parser().parse_args())
    except (InstallError, OSError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
