"""Focused offline checks for the isolated role-aware v2 prototype package."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from rank_effect_packages.formats import validate_tga_texture
from rank_effect_packages.installer import verify_new_silhouettes
from rank_effect_packages.package import load_package


ROOT = Path(__file__).resolve().parents[1]


def _contracts(root: Path, manifest: dict[str, object]) -> list[dict[str, object]]:
    relative = manifest.get("role_contract")
    assert isinstance(relative, str)
    main = json.loads((root / relative).read_text(encoding="utf-8"))
    assert main["format"] == "reborn-rank-effect-role-contract-v2"
    assert main["prototype"] is True
    result: list[dict[str, object]] = []
    for name in main["contract_manifests"]:
        shard = json.loads((root / name).read_text(encoding="utf-8"))
        assert shard["format"] == "reborn-rank-effect-role-contract-shard-v2"
        result.extend(shard["contracts"])
    return result


def _span(audit: dict[str, object], axis: int) -> float:
    bounds = audit["bounds"]
    assert isinstance(bounds, list)
    return float(bounds[1][axis]) - float(bounds[0][axis])


def _near(actual: float, expected: float, tolerance: float = 0.00001) -> None:
    assert math.isclose(actual, expected, rel_tol=tolerance, abs_tol=tolerance), (
        actual,
        expected,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", type=Path, required=True)
    root = parser.parse_args().package_root.resolve()
    package = load_package(root)
    verify_new_silhouettes(package)
    assert package.manifest["package_id"] == "reborn-role-aware-rank-effects-v2-prototype"
    assert package.manifest["coverage"] == {
        "armor_ranks": [14],
        "weapon_classes": ["warrior"],
    }
    assert len(package.effects) == 8
    assert len(package.assets) == 44

    contracts = _contracts(root, package.manifest)
    assert len(contracts) == 34
    armor = [entry for entry in contracts if entry["effect"] == "armor-ar14"]
    weapon = [entry for entry in contracts if entry["effect"] == "weapon-warrior-wr10"]
    armor_atlas = [entry for entry in contracts if entry["effect"] == "armor-ar14-atlas"]
    weapon_atlas = [
        entry for entry in contracts if entry["effect"] == "weapon-warrior-wr10-atlas"
    ]
    assert (len(armor), len(weapon), len(armor_atlas), len(weapon_atlas)) == (12, 8, 2, 12)

    expected_armor = {
        0: ("animated-core", 116, 100, [100], 1),
        1: ("outer-mantle", 848, 768, [0, 768], 0),
        2: ("animated-rune", 136, 128, [128], 1),
    }
    for entry in armor:
        slot = int(entry["slot"])
        role, vertices, faces, materials, animations = expected_armor[slot]
        assert entry["source_rank"] == 12 and entry["role"] == role
        source, output = entry["source"], entry["output"]
        assert isinstance(source, dict) and isinstance(output, dict)
        for audit in (source, output):
            assert audit["vertices"] == vertices and audit["faces"] == faces
            assert audit["material_face_counts"] == materials
            assert audit["animation_keys"] == animations
        if slot == 1:
            assert entry["sculpted"] is True and int(entry["changed_vertices"]) == 848
            assert entry["source_structural_sha256"] != entry["output_structural_sha256"]
            assert _span(output, 1) > _span(source, 1) * 1.01
            assert _span(output, 1) < _span(source, 1) * 1.10
            assert _span(output, 2) <= _span(source, 2) * 1.05
            assert float(output["bounds"][1][0]) > float(source["bounds"][1][0]) + 0.25
            assert abs(
                float(output["bounds"][0][0]) - float(source["bounds"][0][0])
            ) < 0.05
            x_shift = float(output["centroid"][0]) - float(source["centroid"][0])
            assert 0.25 < x_shift < 0.40
            _near(
                float(source["centroid"][1]),
                float(output["centroid"][1]),
                tolerance=0.0001,
            )
            _near(float(source["centroid"][2]), float(output["centroid"][2]))
        else:
            assert entry["sculpted"] is False and entry["changed_vertices"] == 0
            assert entry["source_structural_sha256"] == entry["output_structural_sha256"]

    expected_weapon = {
        0: ("static-corona", 78, 26, [0, 26], 0),
        1: ("travelling-spark", 240, 80, [80], 2),
    }
    for entry in weapon:
        slot = int(entry["slot"])
        role, vertices, faces, materials, animations = expected_weapon[slot]
        assert entry["source_effect_id"] == 9
        assert entry["sculpted"] is False and entry["role"] == role
        assert entry["source_structural_sha256"] == entry["output_structural_sha256"]
        source, output = entry["source"], entry["output"]
        assert isinstance(source, dict) and isinstance(output, dict)
        for audit in (source, output):
            assert audit["vertices"] == vertices and audit["faces"] == faces
            assert audit["material_face_counts"] == materials
            assert audit["animation_keys"] == animations
            assert audit["bounds"] == source["bounds"]

    roles = {str(entry["role"]) for entry in weapon_atlas}
    assert roles == {"declared-unused", "static-corona", "travelling-spark"}
    for entry in weapon_atlas:
        assert entry["alpha_changes"] == 0
        if entry["role"] == "declared-unused":
            assert entry["source_sha256"] == entry["output_sha256"]
            assert entry["changed_pixels"] == 0
        else:
            assert entry["source_sha256"] != entry["output_sha256"]
            assert int(entry["changed_pixels"]) > 0
    for entry in armor_atlas:
        assert entry["alpha_changes"] == 0
        assert int(entry["changed_pixels"]) > 0
        assert entry["source_sha256"] != entry["output_sha256"]

    for path, data in package.assets.items():
        if path.suffix.lower() in {".gwo", ".tga"}:
            info = validate_tga_texture(data, path.as_posix())
            assert (info.width, info.height) == (64, 64)
    for path in root.rglob("*.json"):
        assert path.stat().st_size < 20 * 1024, f"oversized JSON: {path}"
    for path in (ROOT / "tools" / "rank_effect_v2").glob("*.py"):
        assert path.stat().st_size < 20 * 1024, f"oversized source: {path}"
    print("PASS isolated role-aware AR14/Warrior-WR10 v2 prototype")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
