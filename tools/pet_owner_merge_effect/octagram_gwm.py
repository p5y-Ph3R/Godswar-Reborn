"""Deterministic effect-0004 GWM construction from the reviewed PNG atlas."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import struct
import zlib

from erebus_lion.model_codec import expand_xof_mszip
from rank_effect_packages.formats import (
    extract_texture_references,
    rewrite_texture_references,
    structural_fingerprint,
    validate_tga_texture,
)


SOURCE_GWM_SHA256 = (
    "D46D3741FBFCBB0E393B758F0B8674782032672CAB3CB49C8E671DFF974937D2"
)
SOURCE_PNG_SHA256 = (
    "395002D7A9239FBC823972227B0FB3445C849074B732F665D56E8132B2401E01"
)
LEGACY_CROSS_SCANLINE_GWM_SHA256 = (
    "97E14E301888C41E774F8C4312312F96E3DAD2FC8B88D3836369D60F4A0BAC59"
)
TARGET_GWM_SHA256 = (
    "0CF3D009356726F9A0A4691E2B03AD01557FDB8C7AAAF860E15170D66C0C1B4D"
)
SOURCE_TEXTURE_REFERENCE = b"e_he_0003_a.tga"
TARGET_TEXTURE_REFERENCE = b"e_he_0004_a.tga"
STRUCTURAL_FINGERPRINT = (
    "4b16a3ed82eab1b058f7a9eda6976fb9de1cadeace8bea924b67a5417b8e8ac1"
)


@dataclass(frozen=True, slots=True)
class BuildResult:
    gwm: bytes
    texture: bytes
    report: dict[str, object]


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _replace_once(data: bytes, source: bytes, target: bytes, label: str) -> bytes:
    if len(source) != len(target) or data.count(source) != 1:
        raise ValueError(f"Unexpected {label} occurrence count")
    return data.replace(source, target)


def _decode_png(data: bytes) -> tuple[int, int, list[bytes]]:
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Octagram atlas is not a PNG")
    cursor = 8
    idat = bytearray()
    width = height = 0
    while cursor < len(data):
        if cursor + 12 > len(data):
            raise ValueError("Truncated PNG chunk")
        length = struct.unpack_from(">I", data, cursor)[0]
        end = cursor + 12 + length
        if end > len(data):
            raise ValueError("Truncated PNG payload")
        kind = data[cursor + 4 : cursor + 8]
        payload = data[cursor + 8 : cursor + 8 + length]
        cursor = end
        if kind == b"IHDR":
            width, height, depth, color_type, _, _, interlace = struct.unpack(
                ">IIBBBBB", payload
            )
            if (depth, color_type, interlace) != (8, 6, 0):
                raise ValueError("Octagram PNG must be non-interlaced 8-bit RGBA")
        elif kind == b"IDAT":
            idat.extend(payload)
        elif kind == b"IEND":
            break
    if width <= 0 or height <= 0 or not idat:
        raise ValueError("PNG is missing its image payload")

    stride = width * 4
    encoded = zlib.decompress(bytes(idat))
    if len(encoded) != height * (stride + 1):
        raise ValueError("PNG scanline length is not exact")
    position = 0
    previous = bytearray(stride)
    rows: list[bytes] = []
    for _ in range(height):
        filter_type = encoded[position]
        position += 1
        row = bytearray(encoded[position : position + stride])
        position += stride
        for index in range(stride):
            left = row[index - 4] if index >= 4 else 0
            above = previous[index]
            upper_left = previous[index - 4] if index >= 4 else 0
            if filter_type == 0:
                predictor = 0
            elif filter_type == 1:
                predictor = left
            elif filter_type == 2:
                predictor = above
            elif filter_type == 3:
                predictor = (left + above) // 2
            elif filter_type == 4:
                estimate = left + above - upper_left
                distances = (
                    abs(estimate - left),
                    abs(estimate - above),
                    abs(estimate - upper_left),
                )
                predictor = (left, above, upper_left)[distances.index(min(distances))]
            else:
                raise ValueError(f"Unsupported PNG filter {filter_type}")
            row[index] = (row[index] + predictor) & 0xFF
        rows.append(bytes(row))
        previous = row
    pixels = [
        row[index : index + 4]
        for row in rows
        for index in range(0, len(row), 4)
    ]
    return width, height, pixels


def _bake_additive_transparency(pixels: list[bytes]) -> list[bytes]:
    """Premultiply straight RGBA, convert to BGRA, and force stock alpha 255."""

    return [
        bytes(
            (
                round(pixel[2] * pixel[3] / 255.0),
                round(pixel[1] * pixel[3] / 255.0),
                round(pixel[0] * pixel[3] / 255.0),
                255,
            )
        )
        for pixel in pixels
    ]


def _encode_rle_tga(header: bytes, pixels: list[bytes], footer: bytes) -> bytes:
    if len(header) != 18 or len(pixels) != 128 * 128 or len(footer) != 26:
        raise ValueError("Unexpected source texture framing")
    payload = bytearray()
    for y in range(127, -1, -1):
        row = pixels[y * 128 : (y + 1) * 128]
        cursor = 0
        while cursor < len(row):
            run = 1
            while (
                cursor + run < len(row)
                and run < 128
                and row[cursor + run] == row[cursor]
            ):
                run += 1
            if run >= 2:
                payload.append(0x80 | (run - 1))
                payload.extend(row[cursor])
                cursor += run
                continue

            start = cursor
            cursor += 1
            while cursor < len(row) and cursor - start < 128:
                next_run = 1
                while (
                    cursor + next_run < len(row)
                    and next_run < 128
                    and row[cursor + next_run] == row[cursor]
                ):
                    next_run += 1
                if next_run >= 2:
                    break
                cursor += 1
            payload.append(cursor - start - 1)
            payload.extend(b"".join(row[start:cursor]))
    return header + bytes(payload) + footer


def _decode_tga(data: bytes) -> tuple[list[bytes], int]:
    cursor = 18 + data[0]
    ordered: list[bytes] = []
    footer_offset = len(data) - 26
    while len(ordered) < 128 * 128:
        if cursor >= footer_offset:
            raise ValueError("Truncated target TGA RLE stream")
        packet = data[cursor]
        cursor += 1
        count = (packet & 0x7F) + 1
        if len(ordered) + count > 128 * 128:
            raise ValueError("Target TGA RLE packet overruns the atlas")
        if packet & 0x80:
            if cursor + 4 > footer_offset:
                raise ValueError("Truncated target TGA RLE sample")
            sample = data[cursor : cursor + 4]
            cursor += 4
            ordered.extend([sample] * count)
        else:
            length = count * 4
            if cursor + length > footer_offset:
                raise ValueError("Truncated target TGA raw packet")
            ordered.extend(
                data[cursor + index * 4 : cursor + (index + 1) * 4]
                for index in range(count)
            )
            cursor += length
    rows = [ordered[y * 128 : (y + 1) * 128] for y in range(128)]
    rows.reverse()
    return [pixel for row in rows for pixel in row], cursor


def build_octagram_gwm(source_gwm: bytes, source_png: bytes) -> BuildResult:
    """Return the exact reviewed effect-0004 package without writing files."""

    if _sha256(source_gwm) != SOURCE_GWM_SHA256:
        raise ValueError("Stock effect-0003 GWM hash changed")
    if _sha256(source_png) != SOURCE_PNG_SHA256:
        raise ValueError("Reviewed effect-0004 PNG hash changed")
    if len(source_gwm) != 31091 or struct.unpack_from("<II", source_gwm) != (1, 0):
        raise ValueError("Stock effect-0003 GWM framing changed")

    model_length, texture_length = struct.unpack_from("<II", source_gwm, 8)
    model = source_gwm[16 : 16 + model_length]
    texture_offset = 16 + model_length
    texture = source_gwm[texture_offset : texture_offset + texture_length]
    metadata_offset = texture_offset + texture_length
    metadata = source_gwm[metadata_offset : metadata_offset + 428]
    trailer = source_gwm[metadata_offset + 428 :]
    if (model_length, texture_length, len(metadata), trailer) != (
        1637,
        29002,
        428,
        bytes(8),
    ):
        raise ValueError("Stock effect-0003 record boundaries changed")

    target_model, counts = rewrite_texture_references(
        model,
        {SOURCE_TEXTURE_REFERENCE: TARGET_TEXTURE_REFERENCE},
        "e_he_0004_all.gwm",
    )
    target_metadata = _replace_once(
        metadata, b"e_he_0003_all", b"e_he_0004_all", "effect identity"
    )
    target_metadata = _replace_once(
        target_metadata,
        SOURCE_TEXTURE_REFERENCE,
        TARGET_TEXTURE_REFERENCE,
        "texture identity",
    )
    width, height, png_pixels = _decode_png(source_png)
    if (width, height) != (128, 128):
        raise ValueError("Reviewed octagram atlas must be 128x128")
    source_nonzero = [
        (index % 128, index // 128)
        for index, pixel in enumerate(png_pixels)
        if any(pixel[:3]) and pixel[3] > 0
    ]
    source_bounds = (
        min(x for x, _ in source_nonzero),
        min(y for _, y in source_nonzero),
        max(x for x, _ in source_nonzero),
        max(y for _, y in source_nonzero),
    )
    if source_bounds[2] > 94 or source_bounds[3] > 91:
        raise ValueError(f"Octagram atlas exceeds stock effect-0003 UVs: {source_bounds}")
    pixels = _bake_additive_transparency(png_pixels)
    target_texture = _encode_rle_tga(texture[:18], pixels, texture[-26:])
    info = validate_tga_texture(target_texture, "e_he_0004_a.tga")
    decoded, pixel_end = _decode_tga(target_texture)
    if decoded != pixels or pixel_end != len(target_texture) - 26:
        raise ValueError("Generated effect-0004 TGA did not round-trip")

    target = (
        struct.pack("<IIII", 1, 0, len(target_model), len(target_texture))
        + target_model
        + target_texture
        + target_metadata
        + trailer
    )
    source_expanded = expand_xof_mszip(model, "effect-0003 source")
    target_expanded = expand_xof_mszip(target_model, "effect-0004 target")
    differences = [
        index
        for index, pair in enumerate(zip(source_expanded, target_expanded))
        if pair[0] != pair[1]
    ]
    if (
        len(source_expanded) != len(target_expanded)
        or len(differences) != 1
        or (source_expanded[differences[0]], target_expanded[differences[0]])
        != (ord("3"), ord("4"))
    ):
        raise ValueError("Expanded model changed outside its texture identity")
    fingerprint = structural_fingerprint(target_model, "effect-0004 target")
    if fingerprint != STRUCTURAL_FINGERPRINT:
        raise ValueError("Effect geometry, animation, topology, or UVs changed")
    if extract_texture_references(target_model, "effect-0004 target") != (
        TARGET_TEXTURE_REFERENCE,
    ):
        raise ValueError("Effect-0004 texture reference did not round-trip")
    if target.count(b"e_he_0003") != 0 or _sha256(target) != TARGET_GWM_SHA256:
        raise ValueError("Generated effect-0004 package is not the reviewed target")

    nonzero = [
        (index % 128, index // 128)
        for index, pixel in enumerate(pixels)
        if any(pixel[:3])
    ]
    target_bounds = (
        min(x for x, _ in nonzero),
        min(y for _, y in nonzero),
        max(x for x, _ in nonzero),
        max(y for _, y in nonzero),
    )
    report: dict[str, object] = {
        "sourceGwmSha256": SOURCE_GWM_SHA256,
        "sourcePngSha256": SOURCE_PNG_SHA256,
        "targetGwmSha256": TARGET_GWM_SHA256,
        "targetGwmBytes": len(target),
        "targetModelBytes": len(target_model),
        "targetTextureBytes": len(target_texture),
        "textureReference": TARGET_TEXTURE_REFERENCE.decode("ascii"),
        "textureReferenceRewriteCount": counts[SOURCE_TEXTURE_REFERENCE],
        "textureFormat": (
            info.width,
            info.height,
            info.bits_per_pixel,
            info.image_type,
            info.descriptor,
            info.suffix_bytes,
        ),
        "sourcePngNonzeroBounds": source_bounds,
        "targetNonzeroRgbBounds": target_bounds,
        "targetNonzeroRgbPixels": len(nonzero),
        "targetRgbEnergy": sum(sum(pixel[:3]) for pixel in pixels),
        "alphaValues": tuple(sorted(set(pixel[3] for pixel in pixels))),
        "expandedModelBytes": len(target_expanded),
        "expandedModelDifferentBytes": len(differences),
        "structuralFingerprint": fingerprint,
        "metadataBytes": len(target_metadata),
        "trailerHex": trailer.hex().upper(),
    }
    return BuildResult(gwm=target, texture=target_texture, report=report)
