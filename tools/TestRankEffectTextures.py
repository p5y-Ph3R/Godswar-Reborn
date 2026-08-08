from __future__ import annotations

import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[1]
GENERATOR = ROOT / "tools" / "GenerateRankEffectTextures.py"
CHECKED_OUTPUT = ROOT / "assets" / "rank-effects" / "generated"


def fail(message: str) -> None:
    raise AssertionError(message)


def validate_texture(path: Path, expected_depth: int) -> None:
    data = path.read_bytes()
    if len(data) < 44 or data[2] != 2:
        fail(f"{path.name} is not a raw true-color TGA")
    width, height = struct.unpack_from("<HH", data, 12)
    if (width, height, data[16]) != (64, 64, expected_depth):
        fail(f"{path.name} has unexpected dimensions or depth")
    if not data.endswith(b"TRUEVISION-XFILE.\x00"):
        fail(f"{path.name} has no TGA 2.0 footer")
    expected_size = 18 + 64 * 64 * (expected_depth // 8) + 26
    if len(data) != expected_size:
        fail(f"{path.name} has unexpected byte length")


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="reborn-rank-effects-") as temporary:
        generated = Path(temporary)
        subprocess.run(
            [sys.executable, str(GENERATOR), "--output", str(generated)],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        expected = json.loads((CHECKED_OUTPUT / "textures.manifest.json").read_text("utf-8"))
        actual = json.loads((generated / "textures.manifest.json").read_text("utf-8"))
        if actual != expected:
            fail("checked rank-effect textures are not deterministic generator output")

        hashes: set[str] = set()
        for entry in actual["textures"]:
            texture = generated / entry["file"]
            validate_texture(texture, entry["bits_per_pixel"])
            if entry["sha256"] in hashes:
                fail(f"rank-effect texture is duplicated: {entry['file']}")
            hashes.add(entry["sha256"])
            preview = generated / f"{texture.stem}-preview.png"
            if not preview.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
                fail(f"preview is not PNG: {preview.name}")

    concept_root = ROOT / "assets" / "rank-effects" / "concepts"
    required_concepts = {
        "armor-weapon-rank-redesign-reference.png",
        "armor-rank-redesign-v2-role-aware.png",
        "weapon-rank10-redesign-v2-role-aware.png",
        "weapon-rank10-class-reference.png",
    }
    if {path.name for path in concept_root.glob("*.png")} != required_concepts:
        fail("rank-effect concept sheet set is incomplete or contains an unreviewed file")
    print("PASS deterministic armor/weapon rank-effect textures")


if __name__ == "__main__":
    main()
