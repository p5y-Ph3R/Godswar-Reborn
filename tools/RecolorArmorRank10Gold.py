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


def recolor_pixel(pixel: bytes) -> bytes:
    blue, green, red, alpha = pixel
    if alpha == 0:
        return pixel

    intensity = max(red, green, blue) / 255.0
    glow = intensity ** 0.95

    # Near-black gold: muted amber highlights, no bright yellow wash.
    new_red = round(145.0 * glow)
    new_green = round(92.0 * glow)
    new_blue = round(10.0 * glow)

    return bytes((
        max(0, min(255, new_blue)),
        max(0, min(255, new_green)),
        max(0, min(255, new_red)),
        alpha,
    ))


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


def recolor_file(path: Path, backup_dir: Path) -> None:
    original = path.read_bytes()
    recolored = recolor_tga_rle_preserving_packets(original)
    backup_path = backup_dir / str(path).replace(":\\", "__").replace("\\", "_")
    shutil.copy2(path, backup_path)
    path.write_bytes(recolored)
    print(f"{path} -> {len(original)} bytes to {len(recolored)} bytes")


def main() -> None:
    backup_dir = BACKUP_ROOT / f"armor-rank10-near-black-gold-{datetime.now():%Y%m%d-%H%M%S}"
    backup_dir.mkdir(parents=True, exist_ok=True)

    for target in TARGETS:
        if not target.exists():
            raise FileNotFoundError(target)
        recolor_file(target, backup_dir)

    print(f"Backups: {backup_dir}")


if __name__ == "__main__":
    main()
