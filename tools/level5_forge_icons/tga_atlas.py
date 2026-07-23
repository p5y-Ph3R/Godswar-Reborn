from __future__ import annotations

import struct

from .common import (
    EXPECTED_EXTENSION_SIZE,
    EXPECTED_TGA_DESCRIPTOR,
    EXPECTED_TGA_HEIGHT,
    EXPECTED_TGA_WIDTH,
    InstallError,
    RlePacket,
    SPRITE_HEIGHT,
    SPRITE_WIDTH,
    Sprite,
    TGA_FOOTER_SIGNATURE,
    TgaAtlas,
)

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
