from __future__ import annotations

import argparse
from hashlib import sha256
import json
from pathlib import Path

from rank_effects.designs import ARMOR_DESIGNS, WEAPON_DESIGNS
from rank_effects.raster import write_preview, write_tga


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = REPOSITORY_ROOT / "assets" / "rank-effects" / "generated"


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate deterministic armor and weapon rank textures.")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    return parser.parse_args()


def record(path: Path, kind: str, rank: int, identity: str, bits_per_pixel: int) -> dict[str, object]:
    payload = path.read_bytes()
    return {
        "file": path.name,
        "kind": kind,
        "rank": rank,
        "identity": identity,
        "width": 64,
        "height": 64,
        "bits_per_pixel": bits_per_pixel,
        "bytes": len(payload),
        "sha256": sha256(payload).hexdigest(),
    }


def main() -> None:
    arguments = parse_arguments()
    output = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    manifest: list[dict[str, object]] = []

    for rank, (identity, factory, bits_per_pixel) in ARMOR_DESIGNS.items():
        canvas = factory()
        texture = output / f"armor-rank-{rank:02d}-{identity}.gwo"
        preview = output / f"armor-rank-{rank:02d}-{identity}-preview.png"
        write_tga(texture, canvas, bits_per_pixel)
        write_preview(preview, canvas)
        manifest.append(record(texture, "armor", rank, identity, bits_per_pixel))

    for class_name, (identity, factory) in WEAPON_DESIGNS.items():
        canvas = factory()
        texture = output / f"weapon-rank-10-{class_name}-{identity}.gwo"
        preview = output / f"weapon-rank-10-{class_name}-{identity}-preview.png"
        write_tga(texture, canvas, 24)
        write_preview(preview, canvas)
        manifest.append(record(texture, f"weapon-{class_name}", 10, identity, 24))

    manifest_path = output / "textures.manifest.json"
    manifest_path.write_text(
        json.dumps({"schema_version": 1, "textures": manifest}, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Generated {len(manifest)} rank-effect textures in {output}")


if __name__ == "__main__":
    main()
