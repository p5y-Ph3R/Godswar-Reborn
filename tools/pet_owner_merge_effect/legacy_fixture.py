"""Build the rejected cross-scanline package for disposable migration tests only."""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import struct
import tempfile
import uuid


FIXED_SHA256 = "0CF3D009356726F9A0A4691E2B03AD01557FDB8C7AAAF860E15170D66C0C1B4D"
LEGACY_SHA256 = "97E14E301888C41E774F8C4312312F96E3DAD2FC8B88D3836369D60F4A0BAC59"


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _decode_texture(texture: bytes) -> list[bytes]:
    cursor = 18 + texture[0]
    footer = len(texture) - 26
    ordered: list[bytes] = []
    while len(ordered) < 128 * 128:
        if cursor >= footer:
            raise ValueError("Truncated fixed TGA RLE stream")
        packet = texture[cursor]
        cursor += 1
        count = (packet & 0x7F) + 1
        if packet & 0x80:
            sample = texture[cursor : cursor + 4]
            if len(sample) != 4:
                raise ValueError("Truncated fixed TGA sample")
            cursor += 4
            ordered.extend([sample] * count)
        else:
            length = count * 4
            if cursor + length > footer:
                raise ValueError("Truncated fixed TGA raw packet")
            ordered.extend(
                texture[cursor + index * 4 : cursor + (index + 1) * 4]
                for index in range(count)
            )
            cursor += length
    if len(ordered) != 128 * 128 or cursor != footer:
        raise ValueError("Fixed TGA did not decode exactly to its footer")
    rows = [ordered[index : index + 128] for index in range(0, len(ordered), 128)]
    rows.reverse()
    return [pixel for row in rows for pixel in row]


def _encode_legacy_texture(header: bytes, pixels: list[bytes], footer: bytes) -> bytes:
    # This deliberately reproduces the old bug: packets continue across row ends.
    ordered = [
        pixel
        for y in range(127, -1, -1)
        for pixel in pixels[y * 128 : (y + 1) * 128]
    ]
    payload = bytearray()
    cursor = 0
    while cursor < len(ordered):
        run = 1
        while (
            cursor + run < len(ordered)
            and run < 128
            and ordered[cursor + run] == ordered[cursor]
        ):
            run += 1
        if run >= 2:
            payload.append(0x80 | (run - 1))
            payload.extend(ordered[cursor])
            cursor += run
            continue
        start = cursor
        cursor += 1
        while cursor < len(ordered) and cursor - start < 128:
            next_run = 1
            while (
                cursor + next_run < len(ordered)
                and next_run < 128
                and ordered[cursor + next_run] == ordered[cursor]
            ):
                next_run += 1
            if next_run >= 2:
                break
            cursor += 1
        payload.append(cursor - start - 1)
        payload.extend(b"".join(ordered[start:cursor]))
    return header + bytes(payload) + footer


def build_legacy_fixture(fixed: bytes) -> bytes:
    """Return exact legacy bytes in memory; never use these bytes as an asset."""

    if len(fixed) != 20816 or _sha256(fixed) != FIXED_SHA256:
        raise ValueError("Fixed octagram package is not the exact canonical input")
    count, unknown, model_length, texture_length = struct.unpack_from("<IIII", fixed)
    if (count, unknown, model_length, texture_length) != (1, 0, 1637, 18727):
        raise ValueError("Fixed octagram package framing changed")
    texture_offset = 16 + model_length
    texture = fixed[texture_offset : texture_offset + texture_length]
    pixels = _decode_texture(texture)
    legacy_texture = _encode_legacy_texture(texture[:18], pixels, texture[-26:])
    legacy = (
        struct.pack("<IIII", 1, 0, model_length, len(legacy_texture))
        + fixed[16:texture_offset]
        + legacy_texture
        + fixed[texture_offset + texture_length :]
    )
    if (
        len(legacy_texture) != 18277
        or len(legacy) != 20366
        or _sha256(legacy) != LEGACY_SHA256
    ):
        raise ValueError("Legacy migration fixture is not exact")
    return legacy


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--canonical", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    output = arguments.output.resolve()
    temporary_root = Path(tempfile.gettempdir()).resolve()
    try:
        output.relative_to(temporary_root)
    except ValueError as error:
        raise ValueError("Legacy fixture output must remain under the temp directory") from error
    if output.exists():
        raise ValueError(f"Refusing to overwrite legacy fixture output: {output}")
    data = build_legacy_fixture(arguments.canonical.resolve().read_bytes())
    output.parent.mkdir(parents=True, exist_ok=True)
    stage = output.with_name(f"{output.name}.{uuid.uuid4().hex}.stage")
    try:
        with stage.open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        if stage.read_bytes() != data or output.exists():
            raise ValueError("Legacy fixture stage did not remain exact and exclusive")
        os.rename(stage, output)
    finally:
        if stage.exists():
            stage.unlink()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
