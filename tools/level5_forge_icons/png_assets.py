from __future__ import annotations

import binascii
from pathlib import Path
import struct
import zlib

from .common import (
    InstallError,
    SPRITE_HEIGHT,
    SPRITE_SPECS,
    SPRITE_WIDTH,
    Sprite,
    sha256_bytes,
)

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
