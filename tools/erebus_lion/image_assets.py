from __future__ import annotations

import binascii
from dataclasses import dataclass
import struct
import zlib

from InstallLevel5ForgeIcons import display_pixel_index

from .common import InstallError
from .constants import ICON_SIZE


@dataclass(frozen=True)
class TgaImage:
    raw: bytes
    prefix: bytes
    width: int
    height: int
    image_type: int
    descriptor: int
    pixels: bytes
    suffix: bytes


def clamp_byte(value: float) -> int:
    return max(0, min(255, round(value)))


def clamp_unit(value: float) -> float:
    return max(0.0, min(1.0, value))


def parse_tga_image(data: bytes, label: str) -> TgaImage:
    if len(data) < 18:
        raise InstallError(f"{label} is too small to be a TGA texture")

    (
        id_length,
        color_map_type,
        image_type,
        color_map_first,
        color_map_length,
        color_map_depth,
        _x_origin,
        _y_origin,
        width,
        height,
        pixel_depth,
        descriptor,
    ) = struct.unpack_from("<BBBHHBHHHHBB", data, 0)

    if (
        color_map_type != 0
        or color_map_first != 0
        or color_map_length != 0
        or color_map_depth != 0
        or image_type not in (2, 10)
        or pixel_depth != 32
        or width <= 0
        or height <= 0
    ):
        raise InstallError(
            f"Unexpected TGA format for {label}: type={image_type}, "
            f"size={width}x{height}, depth={pixel_depth}"
        )

    prefix_end = 18 + id_length
    total_pixels = width * height
    position = prefix_end
    pixels = bytearray(total_pixels * 4)

    if image_type == 2:
        stream_end = position + total_pixels * 4
        if stream_end > len(data):
            raise InstallError(f"Truncated raw TGA pixel stream in {label}")
        pixels[:] = data[position:stream_end]
        position = stream_end
    else:
        decoded_pixels = 0
        while decoded_pixels < total_pixels:
            if position >= len(data):
                raise InstallError(f"Truncated RLE TGA packet stream in {label}")
            packet_header = data[position]
            position += 1
            pixel_count = (packet_header & 0x7F) + 1
            if decoded_pixels + pixel_count > total_pixels:
                raise InstallError(f"RLE TGA packet overruns pixels in {label}")

            destination = decoded_pixels * 4
            if packet_header & 0x80:
                if position + 4 > len(data):
                    raise InstallError(f"Truncated RLE pixel in {label}")
                pixel = data[position : position + 4]
                position += 4
                pixels[destination : destination + pixel_count * 4] = (
                    pixel * pixel_count
                )
            else:
                byte_count = pixel_count * 4
                if position + byte_count > len(data):
                    raise InstallError(f"Truncated raw packet in {label}")
                pixels[destination : destination + byte_count] = data[
                    position : position + byte_count
                ]
                position += byte_count
            decoded_pixels += pixel_count

    return TgaImage(
        raw=data,
        prefix=data[:prefix_end],
        width=width,
        height=height,
        image_type=image_type,
        descriptor=descriptor,
        pixels=bytes(pixels),
        suffix=data[position:],
    )


def encode_pixel_sequence(pixels: bytes) -> bytes:
    pixel_list = [
        pixels[offset : offset + 4]
        for offset in range(0, len(pixels), 4)
    ]
    encoded = bytearray()
    position = 0
    while position < len(pixel_list):
        run_length = 1
        while (
            position + run_length < len(pixel_list)
            and run_length < 128
            and pixel_list[position + run_length] == pixel_list[position]
        ):
            run_length += 1
        if run_length >= 2:
            encoded.append(0x80 | (run_length - 1))
            encoded.extend(pixel_list[position])
            position += run_length
            continue

        raw_start = position
        position += 1
        while position < len(pixel_list) and position - raw_start < 128:
            next_run = 1
            while (
                position + next_run < len(pixel_list)
                and next_run < 128
                and pixel_list[position + next_run] == pixel_list[position]
            ):
                next_run += 1
            if next_run >= 2:
                break
            position += 1
        raw_pixels = pixel_list[raw_start:position]
        encoded.append(len(raw_pixels) - 1)
        for pixel in raw_pixels:
            encoded.extend(pixel)
    return bytes(encoded)


def encode_tga_image(source: TgaImage, pixels: bytes) -> bytes:
    if len(pixels) != source.width * source.height * 4:
        raise InstallError("TGA output pixel count changed")
    encoded = (
        pixels
        if source.image_type == 2
        else encode_pixel_sequence(pixels)
    )
    return source.prefix + encoded + source.suffix


def recolor_fur_pixel(pixel: bytes) -> bytes:
    blue, green, red, alpha = pixel
    if alpha == 0:
        return pixel

    luminance = (54 * red + 183 * green + 19 * blue) / 256.0
    warmth = clamp_unit((red - blue - 4.0) / 72.0)
    brown_bias = clamp_unit((red - green + 34.0) / 72.0)
    visible = clamp_unit((luminance - 18.0) / 70.0)
    fur_weight = (warmth ** 0.72) * (0.48 + 0.52 * brown_bias) * visible
    if fur_weight <= 0.015:
        return pixel

    # Cool charcoal retains the original luminance structure, making muscle,
    # mane, and fur detail readable without leaving the coat brown.
    charcoal = 7.0 + luminance * 0.42
    target_red = charcoal * 0.82
    target_green = charcoal * 0.89
    target_blue = charcoal * 1.02
    blend = min(0.97, 0.40 + fur_weight * 0.72)

    new_red = red + (target_red - red) * blend
    new_green = green + (target_green - green) * blend
    new_blue = blue + (target_blue - blue) * blend
    return bytes(
        (
            clamp_byte(new_blue),
            clamp_byte(new_green),
            clamp_byte(new_red),
            alpha,
        )
    )


def recolor_pixels(pixels: bytes) -> tuple[bytes, int]:
    output = bytearray(len(pixels))
    changed = 0
    for position in range(0, len(pixels), 4):
        original = pixels[position : position + 4]
        recolored = recolor_fur_pixel(original)
        output[position : position + 4] = recolored
        if recolored != original:
            changed += 1
    return bytes(output), changed


def display_pixel_index_generic(image: TgaImage, x: int, y: int) -> int:
    if not (0 <= x < image.width and 0 <= y < image.height):
        raise InstallError(f"Texture coordinate is out of range: {x},{y}")
    stream_x = image.width - 1 - x if image.descriptor & 0x10 else x
    stream_y = y if image.descriptor & 0x20 else image.height - 1 - y
    return stream_y * image.width + stream_x


def image_to_display_rgba(
    image: TgaImage,
    pixels: bytes | None = None,
) -> bytes:
    source = image.pixels if pixels is None else pixels
    rgba = bytearray(image.width * image.height * 4)
    destination = 0
    for y in range(image.height):
        for x in range(image.width):
            index = display_pixel_index_generic(image, x, y) * 4
            blue, green, red, alpha = source[index : index + 4]
            rgba[destination : destination + 4] = bytes(
                (red, green, blue, alpha)
            )
            destination += 4
    return bytes(rgba)


def atlas_cell_bgra(atlas, x: int, y: int, recolor: bool) -> bytes:
    output = bytearray(ICON_SIZE * ICON_SIZE * 4)
    destination = 0
    for row in range(ICON_SIZE):
        for column in range(ICON_SIZE):
            index = display_pixel_index(atlas, x + column, y + row) * 4
            pixel = atlas.pixels[index : index + 4]
            if recolor:
                pixel = recolor_fur_pixel(pixel)
            output[destination : destination + 4] = pixel
            destination += 4
    return bytes(output)


def sprite_bgra_to_rgba(sprite: bytes) -> bytes:
    rgba = bytearray(len(sprite))
    for position in range(0, len(sprite), 4):
        blue, green, red, alpha = sprite[position : position + 4]
        rgba[position : position + 4] = bytes((red, green, blue, alpha))
    return bytes(rgba)


def png_chunk(chunk_type: bytes, data: bytes) -> bytes:
    checksum = binascii.crc32(chunk_type + data) & 0xFFFFFFFF
    return (
        struct.pack(">I", len(data))
        + chunk_type
        + data
        + struct.pack(">I", checksum)
    )


def encode_png_rgba(
    width: int,
    height: int,
    rgba: bytes,
) -> bytes:
    if len(rgba) != width * height * 4:
        raise InstallError("PNG pixel count does not match its dimensions")
    stride = width * 4
    scanlines = b"".join(
        b"\x00" + rgba[row * stride : (row + 1) * stride]
        for row in range(height)
    )
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", header)
        + png_chunk(b"IDAT", zlib.compress(scanlines, 9))
        + png_chunk(b"IEND", b"")
    )
