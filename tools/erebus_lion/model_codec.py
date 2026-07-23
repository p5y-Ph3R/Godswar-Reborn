from __future__ import annotations

import re
import struct
import zlib

from .common import InstallError, sha256_bytes
from .constants import (
    EXPECTED_TARGET_MODEL_SHA256,
    MODEL_UNIFORM_SCALE,
    SOURCE_MODEL_NAME,
    TARGET_MODEL_NAME,
)


XOF_MSZIP_HEADER = b"xof 0303bzip0032"
XOF_CHUNK_SIZE = 32768


def expand_xof_mszip(data: bytes, label: str) -> bytes:
    if len(data) < 20 or data[:16] != XOF_MSZIP_HEADER:
        raise InstallError(f"Unsupported compressed X model: {label}")

    declared_length = struct.unpack_from("<I", data, 16)[0]
    cursor = 20
    expanded = bytearray()
    chunk_index = 0
    while cursor < len(data):
        if cursor + 6 > len(data):
            raise InstallError(
                f"Truncated MSZIP chunk header in {label} at {cursor}"
            )

        uncompressed_length, compressed_length = struct.unpack_from(
            "<HH", data, cursor
        )
        chunk_end = cursor + 4 + compressed_length
        if (
            uncompressed_length == 0
            or uncompressed_length > XOF_CHUNK_SIZE
            or compressed_length < 2
            or chunk_end > len(data)
            or data[cursor + 4 : cursor + 6] != b"CK"
        ):
            raise InstallError(
                f"Invalid MSZIP chunk {chunk_index} in {label}"
            )

        options: dict[str, object] = {"wbits": -15}
        if expanded:
            options["zdict"] = bytes(expanded[-XOF_CHUNK_SIZE:])
        try:
            decompressor = zlib.decompressobj(**options)
            chunk = (
                decompressor.decompress(data[cursor + 6 : chunk_end])
                + decompressor.flush()
            )
        except zlib.error as error:
            raise InstallError(
                f"Could not decompress MSZIP chunk {chunk_index} in {label}"
            ) from error
        if (
            len(chunk) != uncompressed_length
            or not decompressor.eof
            or decompressor.unused_data
            or decompressor.unconsumed_tail
        ):
            raise InstallError(
                f"MSZIP chunk {chunk_index} failed validation in {label}"
            )

        expanded.extend(chunk)
        cursor = chunk_end
        chunk_index += 1

    if chunk_index == 0 or declared_length != len(expanded) + 16:
        raise InstallError(
            f"Compressed X length mismatch in {label}: "
            f"declared {declared_length}, decoded {len(expanded) + 16}"
        )
    return bytes(expanded)


def compress_xof_mszip(expanded: bytes, label: str) -> bytes:
    output = bytearray(XOF_MSZIP_HEADER)
    output.extend(struct.pack("<I", len(expanded) + 16))
    for offset in range(0, len(expanded), XOF_CHUNK_SIZE):
        chunk = expanded[offset : offset + XOF_CHUNK_SIZE]
        options: dict[str, object] = {
            "level": 9,
            "method": zlib.DEFLATED,
            "wbits": -15,
            "memLevel": 8,
            "strategy": zlib.Z_DEFAULT_STRATEGY,
        }
        if offset:
            options["zdict"] = expanded[
                max(0, offset - XOF_CHUNK_SIZE) : offset
            ]
        compressor = zlib.compressobj(**options)
        compressed = compressor.compress(chunk) + compressor.flush()
        compressed_length = len(compressed) + 2
        if compressed_length > 0xFFFF:
            raise InstallError(f"MSZIP chunk is too large while encoding {label}")
        output.extend(
            struct.pack("<HH", len(chunk), compressed_length)
            + b"CK"
            + compressed
        )

    encoded = bytes(output)
    if expand_xof_mszip(encoded, label) != expanded:
        raise InstallError(f"Compressed X round trip failed for {label}")
    return encoded


def binary_x_name(value: bytes) -> bytes:
    return struct.pack("<HI", 1, len(value)) + value


def enlarge_erebus_model(source: bytes) -> bytes:
    expanded = expand_xof_mszip(source, SOURCE_MODEL_NAME)
    model_marker = (
        binary_x_name(b"Frame")
        + binary_x_name(b"qichong_6004_01")
        + struct.pack("<H", 10)
    )
    animation_marker = (
        binary_x_name(b"AnimationSet")
        + binary_x_name(b"nomal_ride_stand")
        + struct.pack("<H", 10)
    )
    model_locations = [
        match.start() for match in re.finditer(re.escape(model_marker), expanded)
    ]
    animation_locations = [
        match.start()
        for match in re.finditer(re.escape(animation_marker), expanded)
    ]
    if len(model_locations) != 1 or len(animation_locations) != 1:
        raise InstallError(
            "Could not isolate the African Lion model hierarchy: "
            f"model roots={len(model_locations)}, "
            f"animation sets={len(animation_locations)}"
        )

    model_start = model_locations[0]
    animation_start = animation_locations[0]
    if model_start >= animation_start:
        raise InstallError("African Lion model hierarchy has an invalid layout")

    scale_matrix = (
        MODEL_UNIFORM_SCALE, 0.0, 0.0, 0.0,
        0.0, MODEL_UNIFORM_SCALE, 0.0, 0.0,
        0.0, 0.0, MODEL_UNIFORM_SCALE, 0.0,
        0.0, 0.0, 0.0, 1.0,
    )
    parent_prefix = (
        binary_x_name(b"Frame")
        + binary_x_name(b"ErebusScaleRoot")
        + struct.pack("<H", 10)
        + binary_x_name(b"FrameTransformMatrix")
        + struct.pack("<H", 10)
        + struct.pack("<HI", 7, 16)
        + struct.pack("<16f", *scale_matrix)
        + struct.pack("<H", 11)
    )
    parent_suffix = struct.pack("<H", 11)
    enlarged = (
        expanded[:model_start]
        + parent_prefix
        + expanded[model_start:animation_start]
        + parent_suffix
        + expanded[animation_start:]
    )
    generated_animation_start = (
        animation_start + len(parent_prefix) + len(parent_suffix)
    )
    if (
        enlarged[:model_start] != expanded[:model_start]
        or enlarged[
            model_start + len(parent_prefix) :
            animation_start + len(parent_prefix)
        ] != expanded[model_start:animation_start]
        or enlarged[generated_animation_start:] != expanded[animation_start:]
    ):
        raise InstallError("Erebus hierarchy wrapper changed source model data")

    generated = compress_xof_mszip(enlarged, TARGET_MODEL_NAME)
    if compress_xof_mszip(enlarged, TARGET_MODEL_NAME) != generated:
        raise InstallError("Erebus model compression is not deterministic")
    if sha256_bytes(generated) != EXPECTED_TARGET_MODEL_SHA256:
        raise InstallError("Generated Erebus model hash does not match the validated asset")
    return generated
