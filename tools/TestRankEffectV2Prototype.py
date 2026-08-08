"""Focused offline checks for the complete role-aware v2 rank package."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from rank_effect_packages.formats import validate_tga_texture
from rank_effect_packages.installer import verify_new_silhouettes
from rank_effect_packages.package import load_package
from rank_effect_v2.armor_ranks import ARMOR_RANKS, ARMOR_RANK_DESIGNS


ROOT = Path(__file__).resolve().parents[1]


def _contracts(root: Path, manifest: dict[str, object]) -> list[dict[str, object]]:
    relative = manifest.get("role_contract")
    assert isinstance(relative, str)
    main = json.loads((root / relative).read_text(encoding="utf-8"))
    assert main["format"] == "reborn-rank-effect-role-contract-v2"
    assert main["prototype"] is False
    result: list[dict[str, object]] = []
    for name in main["contract_manifests"]:
        shard = json.loads((root / name).read_text(encoding="utf-8"))
        assert shard["format"] == "reborn-rank-effect-role-contract-shard-v2"
        result.extend(shard["contracts"])
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", type=Path, required=True)
    root = parser.parse_args().package_root.resolve()
    package = load_package(root)
    verify_new_silhouettes(package)
    assert package.manifest["package_id"] == "reborn-role-aware-rank-effects-v2"
    assert package.manifest["coverage"] == {
        "armor_ranks": list(ARMOR_RANKS),
        "weapon_classes": ["warrior"],
    }
    assert len(package.effects) == 24
    assert len(package.assets) == 124

    contracts = _contracts(root, package.manifest)
    assert len(contracts) == 90
    armor = [entry for entry in contracts if str(entry["effect"]).startswith("armor-ar") and not str(entry["effect"]).endswith("-atlas")]
    armor_atlas = [entry for entry in contracts if str(entry["effect"]).endswith("-atlas") and str(entry["effect"]).startswith("armor-ar")]
    weapon = [entry for entry in contracts if entry["effect"] == "weapon-warrior-wr10"]
    weapon_atlas = [
        entry for entry in contracts if entry["effect"] == "weapon-warrior-wr10-atlas"
    ]
    assert (len(armor), len(armor_atlas), len(weapon), len(weapon_atlas)) == (
        60,
        10,
        8,
        12,
    )

    expected_armor = {
        0: ("animated-core", 116, 100, [100], 1),
        1: ("outer-mantle", 848, 768, [0, 768], 0),
        2: ("animated-rune", 136, 128, [128], 1),
    }
    structures: dict[tuple[str, str], dict[int, str]] = {}
    for entry in armor:
        rank = int(str(entry["effect"]).removeprefix("armor-ar"))
        assert rank in ARMOR_RANKS
        assert entry["design"] == ARMOR_RANK_DESIGNS[rank].name
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
            key = (str(entry["asset_root"]), str(entry["gender"]))
            structures.setdefault(key, {})[rank] = str(entry["output_structural_sha256"])
        else:
            assert entry["sculpted"] is False and entry["changed_vertices"] == 0
            assert entry["source_structural_sha256"] == entry["output_structural_sha256"]
    assert len(structures) == 4
    assert all(
        set(values) == set(ARMOR_RANKS) and len(set(values.values())) == 5
        for values in structures.values()
    )

    for entry in armor_atlas:
        rank = int(str(entry["effect"]).removeprefix("armor-ar").removesuffix("-atlas"))
        assert entry["design"] == ARMOR_RANK_DESIGNS[rank].name
        assert entry["alpha_changes"] == 0
        assert entry["outside_region_changes"] == 0
        assert int(entry["changed_pixels"]) > 0
        assert entry["source_sha256"] != entry["output_sha256"]

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

    for path, data in package.assets.items():
        if path.suffix.lower() in {".gwo", ".tga"}:
            info = validate_tga_texture(data, path.as_posix())
            assert (info.width, info.height) == (64, 64)
    for path in root.rglob("*.json"):
        assert path.stat().st_size < 20 * 1024, f"oversized JSON: {path}"
    for path in (ROOT / "tools" / "rank_effect_v2").glob("*.py"):
        assert path.stat().st_size < 20 * 1024, f"oversized source: {path}"
    print("PASS complete role-aware AR10-AR14/Warrior-WR10 v2 package")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
