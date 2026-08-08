"""Focused checks for role-aware AR10--AR14 armor mantle designs."""

from __future__ import annotations

import argparse
from pathlib import Path

from rank_effect_packages.catalog import ASSET_ROOTS, GENDERS
from rank_effect_packages.formats import structural_fingerprint
from rank_effect_v2.armor_ranks import (
    ARMOR_RANKS,
    ARMOR_RANK_DESIGNS,
    ARMOR_SOURCE_RANK,
    EXPECTED_CARDS,
    EXPECTED_FACES,
    EXPECTED_VERTICES,
    SLOT_ROLES,
    ArmorMantleTransform,
    design_for_rank,
    validate_design_catalogue,
    validate_silhouette,
)
from rank_effect_v2.models import audit_model
from erebus_lion.model_codec import expand_xof_mszip
from xmodel_sculpt.binary_x import parse_tokens
from xmodel_sculpt.mesh import discover_meshes
from xmodel_sculpt.sculpt import sculpt_xof_mszip


ROOT = Path(__file__).resolve().parents[1]


def _source_mesh(data: bytes, label: str):
    expanded = expand_xof_mszip(data, label)
    meshes = discover_meshes(expanded, parse_tokens(expanded))
    assert len(meshes) == 1
    return meshes[0]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-root", type=Path, required=True)
    client = parser.parse_args().client_root.resolve()
    assert client.is_dir()

    validate_design_catalogue()
    assert ARMOR_SOURCE_RANK == 12
    assert tuple(ARMOR_RANK_DESIGNS) == ARMOR_RANKS == (10, 11, 12, 13, 14)
    assert SLOT_ROLES == ("animated-core", "outer-mantle", "animated-rune")
    assert EXPECTED_CARDS == 40
    assert len({design.name for design in ARMOR_RANK_DESIGNS.values()}) == 5
    assert len(
        {
            (design.palette.shadow, design.palette.middle, design.palette.highlight)
            for design in ARMOR_RANK_DESIGNS.values()
        }
    ) == 5

    reviewed = 0
    for asset_root in ASSET_ROOTS:
        for gender in GENDERS:
            path = client / asset_root / "effect" / (
                f"{gender}_body_effect_{ARMOR_SOURCE_RANK:04d}_1.jcs"
            )
            assert path.is_file() and not path.is_symlink(), path
            source = path.read_bytes()
            source_audit = audit_model(source, str(path))
            source_mesh = _source_mesh(source, str(path))
            assert (source_audit.vertices, source_audit.faces) == (
                EXPECTED_VERTICES,
                EXPECTED_FACES,
            )
            assert source_audit.material_face_counts == (0, EXPECTED_FACES)
            assert source_audit.animation_keys == 0

            fingerprints: set[str] = set()
            for rank in ARMOR_RANKS:
                design = design_for_rank(rank)
                sculpt = sculpt_xof_mszip(
                    source,
                    ArmorMantleTransform(design),
                    label=f"{path}:AR{rank}",
                )
                assert sculpt.changed_vertices == EXPECTED_VERTICES
                output_audit = audit_model(sculpt.encoded, f"{path}:AR{rank}")
                assert output_audit.vertices == source_audit.vertices
                assert output_audit.faces == source_audit.faces
                assert output_audit.material_face_counts == source_audit.material_face_counts
                assert output_audit.uv_bounds == source_audit.uv_bounds
                assert output_audit.animation_keys == source_audit.animation_keys
                assert output_audit.texture_references == source_audit.texture_references

                output_mesh = _source_mesh(sculpt.encoded, f"{path}:AR{rank}")
                metrics = validate_silhouette(
                    source_mesh, tuple(output_mesh.vertices), design
                )
                assert metrics.anchor_drift <= design.invariant.maximum_anchor_drift
                fingerprint = structural_fingerprint(
                    sculpt.encoded, f"{path}:AR{rank}"
                )
                assert fingerprint not in fingerprints
                fingerprints.add(fingerprint)
            assert len(fingerprints) == len(ARMOR_RANKS)
            reviewed += 1

    assert reviewed == len(ASSET_ROOTS) * len(GENDERS)
    for path in (
        ROOT / "tools" / "rank_effect_v2" / "armor_ranks.py",
        Path(__file__),
    ):
        assert path.stat().st_size < 20 * 1024, f"oversized source: {path}"
    print("PASS role-aware AR10-AR14 armor designs across four native AR12 sources")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
