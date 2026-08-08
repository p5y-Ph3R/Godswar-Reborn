"""Author all five role-aware armor ranks from one coherent native family."""

from __future__ import annotations

from pathlib import Path

from rank_effect_packages.baseline import sha256_bytes
from rank_effect_packages.catalog import ASSET_ROOTS, GENDERS
from rank_effect_packages.errors import RankEffectError
from rank_effect_packages.formats import structural_fingerprint
from erebus_lion.model_codec import expand_xof_mszip
from xmodel_sculpt.binary_x import parse_tokens
from xmodel_sculpt.mesh import discover_meshes
from xmodel_sculpt.sculpt import sculpt_xof_mszip

from .armor_ranks import (
    ARMOR_RANK_DESIGNS,
    ARMOR_SOURCE_RANK,
    SLOT_ROLES,
    ArmorMantleTransform,
    validate_silhouette,
)
from .atlas import Region, recolour_luminance
from .models import audit_model
from .package_io import PackageShard, regular


def _audit_dict(value) -> dict[str, object]:
    return {
        "vertices": value.vertices,
        "faces": value.faces,
        "material_face_counts": list(value.material_face_counts),
        "uv_bounds": [round(number, 6) for number in value.uv_bounds],
        "animation_keys": value.animation_keys,
        "texture_references": list(value.texture_references),
        "bounds": [[round(number, 7) for number in vector] for vector in value.bounds],
        "centroid": [round(number, 7) for number in value.centroid],
    }


def _single_mesh(encoded: bytes, label: str):
    expanded = expand_xof_mszip(encoded, label)
    meshes = discover_meshes(expanded, parse_tokens(expanded))
    if len(meshes) != 1:
        raise RankEffectError(f"Role-aware armor JCS must contain one mesh: {label}")
    return meshes[0]


def build_armor_shards(
    client: Path,
    stage: Path,
    contracts: list[dict[str, object]],
    rewrite,
) -> list[PackageShard]:
    """Create one bounded shard per rank while sharing no visible assets."""

    shards = {
        rank: PackageShard(stage, f"armor-{rank:02d}-role-aware")
        for rank in ARMOR_RANK_DESIGNS
    }
    for asset_root in ASSET_ROOTS:
        directory = client / asset_root / "effect"
        body_source = regular(
            directory / "female_body_effect_0010.tga", "native body-effect atlas"
        )
        declared_source = regular(directory / "11.tga", "native declared atlas")
        for rank, design in ARMOR_RANK_DESIGNS.items():
            shard = shards[rank]
            palette = design.palette
            body = recolour_luminance(
                body_source,
                Region(*palette.region),
                palette.shadow,
                palette.middle,
                palette.highlight,
                strength=palette.strength,
            )
            if body.changed_pixels <= 0 or body.alpha_changes != 0:
                raise RankEffectError(
                    f"AR{rank} role-aware atlas did not preserve alpha/detail"
                )
            main = Path(asset_root) / "effect" / (
                f"reborn_body_effect_{rank:04d}_v2_main.tga"
            )
            declared = Path(asset_root) / "effect" / (
                f"reborn_body_effect_{rank:04d}_v2_declared.tga"
            )
            shard.add(main, body.encoded)
            shard.add(declared, declared_source)
            mapping = {
                b"female_body_effect_0010.tga": main.name.encode("ascii"),
                b"11.tga": declared.name.encode("ascii"),
            }
            for gender in GENDERS:
                source_stem = f"{gender}_body_effect_{ARMOR_SOURCE_RANK:04d}"
                target_stem = f"{gender}_body_effect_{rank:04d}"
                canonical = Path(asset_root) / "effect" / f"{target_stem}.gwo"
                shard.add(canonical, body.encoded)
                models: list[Path] = []
                for slot, role in enumerate(SLOT_ROLES):
                    source_path = directory / f"{source_stem}_{slot}.jcs"
                    source = regular(source_path, "coherent native AR12 JCS")
                    authored = source
                    changed_vertices = 0
                    if slot == 1:
                        sculpt = sculpt_xof_mszip(
                            source,
                            ArmorMantleTransform(design),
                            label=str(source_path),
                        )
                        authored = sculpt.encoded
                        changed_vertices = sculpt.changed_vertices
                        if changed_vertices <= 0:
                            raise RankEffectError(
                                f"AR{rank} mantle sculpt changed no vertices"
                            )
                        validate_silhouette(
                            _single_mesh(source, f"{source_path}:source"),
                            tuple(
                                _single_mesh(
                                    authored, f"{source_path}:AR{rank}"
                                ).vertices
                            ),
                            design,
                        )
                    output = rewrite(authored, mapping, str(source_path))
                    target = Path(asset_root) / "effect" / f"{target_stem}_{slot}.jcs"
                    shard.add(target, output)
                    models.append(target)
                    contracts.append(
                        {
                            "effect": f"armor-ar{rank}",
                            "design": design.name,
                            "intent": design.intent,
                            "asset_root": asset_root,
                            "gender": gender,
                            "slot": slot,
                            "role": role,
                            "source_rank": ARMOR_SOURCE_RANK,
                            "sculpted": slot == 1,
                            "changed_vertices": changed_vertices,
                            "source_sha256": sha256_bytes(source),
                            "output_sha256": sha256_bytes(output),
                            "source_structural_sha256": structural_fingerprint(
                                source, str(source_path)
                            ),
                            "output_structural_sha256": structural_fingerprint(
                                output, str(target)
                            ),
                            "source": _audit_dict(audit_model(source, str(source_path))),
                            "output": _audit_dict(audit_model(output, str(target))),
                        }
                    )
                shard.effects.append(
                    {
                        "kind": "armor",
                        "rank": rank,
                        "asset_root": asset_root,
                        "gender": gender,
                        "class": None,
                        "models": [path.as_posix() for path in models],
                        "canonical_texture": canonical.as_posix(),
                        "private_textures": [main.as_posix(), declared.as_posix()],
                    }
                )
            contracts.append(
                {
                    "effect": f"armor-ar{rank}-atlas",
                    "design": design.name,
                    "asset_root": asset_root,
                    "source_sha256": sha256_bytes(body_source),
                    "output_sha256": sha256_bytes(body.encoded),
                    "changed_pixels": body.changed_pixels,
                    "outside_region_changes": body.outside_region_changes,
                    "alpha_changes": body.alpha_changes,
                    "region": list(palette.region),
                    "palette": {
                        "shadow": list(palette.shadow),
                        "middle": list(palette.middle),
                        "highlight": list(palette.highlight),
                        "strength": palette.strength,
                    },
                }
            )
    return list(shards.values())
