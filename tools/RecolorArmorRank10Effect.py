from __future__ import annotations

from datetime import datetime
from pathlib import Path
import shutil


ROOT = Path(r"C:\Godswar Origin")
BACKUP_ROOT = Path(r"C:\Reborn\backups")

TARGETS = [
    ROOT / "Characters" / "effect" / "male_body_effect_0010.gwo",
    ROOT / "Characters" / "effect" / "female_body_effect_0010.gwo",
    ROOT / "Characters_New" / "effect" / "male_body_effect_0010.gwo",
    ROOT / "Characters_New" / "effect" / "female_body_effect_0010.gwo",
]


def decode_tga_rle(data: bytes) -> tuple[bytearray, int, int, list[bytes]]:
    if len(data) < 18:
        raise ValueError("file is too small to be a TGA texture")
    if data[1] != 0 or data[2] != 10 or data[16] != 32:
        raise ValueError("expected 32-bit RLE true-color TGA payload")

    id_length = data[0]
    header = bytearray(data[: 18 + id_length])
    width = data[12] | (data[13] << 8)
    height = data[14] | (data[15] << 8)
    expected = width * height
    pos = 18 + id_length
    pixels: list[bytes] = []

    while len(pixels) < expected:
        packet = data[pos]
        pos += 1
        count = (packet & 0x7F) + 1
        if packet & 0x80:
            pixel = data[pos : pos + 4]
            pos += 4
            pixels.extend([pixel] * count)
        else:
            for _ in range(count):
                pixels.append(data[pos : pos + 4])
                pos += 4

    if len(pixels) != expected:
        raise ValueError(f"decoded {len(pixels)} pixels, expected {expected}")
    return header, width, height, pixels


def encode_tga_rle(header: bytearray, pixels: list[bytes]) -> bytes:
    out = bytearray(header)
    i = 0
    total = len(pixels)

    while i < total:
        run = 1
        while i + run < total and run < 128 and pixels[i + run] == pixels[i]:
            run += 1

        if run >= 2:
            out.append(0x80 | (run - 1))
            out.extend(pixels[i])
            i += run
            continue

        start = i
        i += 1
        while i < total and (i - start) < 128:
            next_run = 1
            while i + next_run < total and next_run < 128 and pixels[i + next_run] == pixels[i]:
                next_run += 1
            if next_run >= 2:
                break
            i += 1

        raw = pixels[start:i]
        out.append(len(raw) - 1)
        for pixel in raw:
            out.extend(pixel)

    return bytes(out)


def recolor_tga_rle_preserving_packets(data: bytes) -> bytes:
    if len(data) < 18:
        raise ValueError("file is too small to be a TGA texture")
    if data[1] != 0 or data[2] != 10 or data[16] != 32:
        raise ValueError("expected 32-bit RLE true-color TGA payload")

    id_length = data[0]
    width = data[12] | (data[13] << 8)
    height = data[14] | (data[15] << 8)
    expected = width * height
    pos = 18 + id_length
    pixels_seen = 0
    out = bytearray(data[:pos])

    while pixels_seen < expected:
        packet = data[pos]
        pos += 1
        count = (packet & 0x7F) + 1
        out.append(packet)

        if packet & 0x80:
            out.extend(recolor_pixel(data[pos : pos + 4]))
            pos += 4
        else:
            for _ in range(count):
                out.extend(recolor_pixel(data[pos : pos + 4]))
                pos += 4

        pixels_seen += count

    if pixels_seen != expected:
        raise ValueError(f"decoded {pixels_seen} pixels, expected {expected}")
    out.extend(data[pos:])
    return bytes(out)


def recolor_pixel(pixel: bytes) -> bytes:
    blue, green, red, alpha = pixel
    if alpha == 0:
        return pixel

    intensity = max(red, green, blue) / 255.0
    glow = intensity ** 0.45

    # White/silver is the dominant color. Gold is limited to the brightest
    # accent bands, similar to how AR4 keeps blue as the non-dominant color.
    gold_accent = max(0.0, min(1.0, (intensity - 0.78) / 0.22)) ** 1.7
    white = (255.0, 252.0, 238.0)
    gold = (255.0, 205.0, 72.0)
    target = tuple(white[i] + (gold[i] - white[i]) * gold_accent for i in range(3))

    new_red = round(target[0] * glow)
    new_green = round(target[1] * glow)
    new_blue = round(target[2] * glow)

    return bytes((
        max(0, min(255, new_blue)),
        max(0, min(255, new_green)),
        max(0, min(255, new_red)),
        alpha,
    ))


def recolor_file(path: Path, backup_dir: Path) -> None:
    original = path.read_bytes()
    # The client is picky about these effect payloads. Preserve the original
    # RLE packet layout and byte count; only mutate the BGRA pixels.
    encoded = recolor_tga_rle_preserving_packets(original)

    backup_path = backup_dir / str(path).replace(":\\", "__").replace("\\", "_")
    backup_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(path, backup_path)
    path.write_bytes(encoded)

    print(f"{path} -> {len(original)} bytes to {len(encoded)} bytes")


def main() -> None:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_dir = BACKUP_ROOT / f"armor-rank10-angelic-white-gold-{timestamp}"
    backup_dir.mkdir(parents=True, exist_ok=True)

    for target in TARGETS:
        if not target.exists():
            raise FileNotFoundError(target)
        recolor_file(target, backup_dir)

    print(f"Backups: {backup_dir}")


if __name__ == "__main__":
    main()
