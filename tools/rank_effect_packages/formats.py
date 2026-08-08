"""Bounded readers for the compressed-X and TGA payloads used by effects."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import struct

from erebus_lion.model_codec import expand_xof_mszip, compress_xof_mszip
from xmodel_sculpt.binary_x import TOKEN_STRING, XModelError, parse_tokens

from .errors import RankEffectError


_TEXTURE_EXTENSIONS = (b".tga", b".gwo", b".dds", b".bmp")
_TGA_FOOTER = b"TRUEVISION-XFILE.\x00"
_MAX_TEXTURE_DIMENSION = 4096
_MAX_TEXTURE_PIXELS = 16_777_216


@dataclass(frozen=True, slots=True)
class TextureInfo:
    width: int
    height: int
    bits_per_pixel: int
    image_type: int
    descriptor: int
    suffix_bytes: int


def _is_texture_reference(value: bytes) -> bool:
    lowered = value.lower()
    return any(lowered.endswith(extension) for extension in _TEXTURE_EXTENSIONS)


def _expanded_jcs(data: bytes, label: str) -> bytes:
    try:
        return expand_xof_mszip(data, label)
    except (ValueError, OSError) as error:
        raise RankEffectError(f"Invalid compressed-X model {label}: {error}") from error


def extract_texture_references(data: bytes, label: str) -> tuple[bytes, ...]:
    """Read image references from validated binary-X string tokens only."""

    expanded = _expanded_jcs(data, label)
    try:
        tokens = parse_tokens(expanded)
    except XModelError as error:
        raise RankEffectError(f"Invalid binary-X token stream {label}: {error}") from error
    return tuple(
        token.value
        for token in tokens
        if token.kind == TOKEN_STRING
        and isinstance(token.value, bytes)
        and _is_texture_reference(token.value)
    )


def structural_fingerprint(data: bytes, label: str) -> str:
    """Hash XOF structure while ignoring all authored names and texture names."""

    expanded = _expanded_jcs(data, label)
    try:
        tokens = parse_tokens(expanded)
    except XModelError as error:
        raise RankEffectError(f"Invalid binary-X token stream {label}: {error}") from error
    normalized = bytearray()
    cursor = 0
    for token in tokens:
        normalized.extend(expanded[cursor : token.start])
        if token.kind in (1, TOKEN_STRING):
            normalized.extend(struct.pack("<HI", token.kind, 0))
            if token.kind == TOKEN_STRING:
                normalized.extend(struct.pack("<H", 20))
        else:
            normalized.extend(expanded[token.start : token.end])
        cursor = token.end
    normalized.extend(expanded[cursor:])
    try:
        parse_tokens(bytes(normalized))
    except XModelError as error:
        raise RankEffectError(f"Normalized XOF failed validation {label}: {error}") from error
    return hashlib.sha256(normalized).hexdigest()


def rewrite_texture_references(
    data: bytes,
    replacements: dict[bytes, bytes],
    label: str,
    *,
    require_all_references: bool = True,
) -> tuple[bytes, dict[bytes, int]]:
    """Rewrite complete binary-X string tokens and validate the new stream.

    Token rebuilding supports differently sized names; raw byte searching does not.
    """

    if not replacements:
        raise RankEffectError("At least one texture-reference replacement is required")
    for source, target in replacements.items():
        if not source or not target or not _is_texture_reference(source):
            raise RankEffectError("Replacement sources must be complete texture names")
        if not _is_texture_reference(target) or b"\x00" in target:
            raise RankEffectError("Replacement targets must be NUL-free texture names")

    expanded = _expanded_jcs(data, label)
    try:
        tokens = parse_tokens(expanded)
    except XModelError as error:
        raise RankEffectError(f"Invalid binary-X token stream {label}: {error}") from error

    output = bytearray()
    cursor = 0
    counts = {source: 0 for source in replacements}
    unresolved: list[bytes] = []
    for token in tokens:
        output.extend(expanded[cursor : token.start])
        if (
            token.kind == TOKEN_STRING
            and isinstance(token.value, bytes)
            and _is_texture_reference(token.value)
        ):
            replacement = replacements.get(token.value)
            if replacement is None:
                unresolved.append(token.value)
                output.extend(expanded[token.start : token.end])
            else:
                output.extend(struct.pack("<HI", TOKEN_STRING, len(replacement)))
                output.extend(replacement)
                output.extend(struct.pack("<H", 20))
                counts[token.value] += 1
        else:
            output.extend(expanded[token.start : token.end])
        cursor = token.end
    output.extend(expanded[cursor:])

    missing = [source for source, count in counts.items() if count == 0]
    if missing:
        rendered = ", ".join(repr(value) for value in missing)
        raise RankEffectError(f"Texture references were not found in {label}: {rendered}")
    if require_all_references and unresolved:
        rendered = ", ".join(repr(value) for value in sorted(set(unresolved)))
        raise RankEffectError(f"Unmapped texture references remain in {label}: {rendered}")

    rebuilt = bytes(output)
    try:
        parse_tokens(rebuilt)
        encoded = compress_xof_mszip(rebuilt, label)
    except (XModelError, ValueError) as error:
        raise RankEffectError(f"Rewritten model failed validation {label}: {error}") from error
    expected = tuple(replacements.get(value, value) for value in extract_texture_references(data, label))
    if extract_texture_references(encoded, label) != expected:
        raise RankEffectError(f"Rewritten texture references failed round trip: {label}")
    return encoded, counts


def validate_tga_texture(data: bytes, label: str) -> TextureInfo:
    """Validate the 24/32-bit raw or RLE TGA payload stored as GWO/TGA."""

    if len(data) < 18:
        raise RankEffectError(f"Texture is too small: {label}")
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
        bits_per_pixel,
        descriptor,
    ) = struct.unpack_from("<BBBHHBHHHHBB", data)
    if (
        color_map_type != 0
        or color_map_first != 0
        or color_map_length != 0
        or color_map_depth != 0
        or image_type not in (2, 10)
        or bits_per_pixel not in (24, 32)
        or width <= 0
        or height <= 0
        or width > _MAX_TEXTURE_DIMENSION
        or height > _MAX_TEXTURE_DIMENSION
        or width * height > _MAX_TEXTURE_PIXELS
    ):
        raise RankEffectError(
            f"Unsupported effect texture {label}: type={image_type}, "
            f"size={width}x{height}, depth={bits_per_pixel}"
        )

    bytes_per_pixel = bits_per_pixel // 8
    position = 18 + id_length
    if position > len(data):
        raise RankEffectError(f"Truncated TGA identifier in {label}")
    total_pixels = width * height
    if image_type == 2:
        position += total_pixels * bytes_per_pixel
        if position > len(data):
            raise RankEffectError(f"Truncated raw TGA pixels in {label}")
    else:
        decoded = 0
        while decoded < total_pixels:
            if position >= len(data):
                raise RankEffectError(f"Truncated RLE packet in {label}")
            header = data[position]
            position += 1
            count = (header & 0x7F) + 1
            if decoded + count > total_pixels:
                raise RankEffectError(f"RLE packet overruns texture {label}")
            payload = bytes_per_pixel if header & 0x80 else count * bytes_per_pixel
            position += payload
            if position > len(data):
                raise RankEffectError(f"Truncated RLE payload in {label}")
            decoded += count

    suffix = data[position:]
    if suffix and (len(suffix) != 26 or not suffix.endswith(_TGA_FOOTER)):
        raise RankEffectError(f"Unexpected trailing TGA data in {label}")
    return TextureInfo(
        width=width,
        height=height,
        bits_per_pixel=bits_per_pixel,
        image_type=image_type,
        descriptor=descriptor,
        suffix_bytes=len(suffix),
    )
