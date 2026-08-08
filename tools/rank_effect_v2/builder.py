"""Build an isolated, validated AR14 plus Warrior WR10 role-aware package."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import tempfile
import uuid

from rank_effect_packages.baseline import create_baseline, sha256_bytes, shard_baseline
from rank_effect_packages.catalog import ASSET_ROOTS, GENDERS, WEAPON_EFFECTS
from rank_effect_packages.errors import RankEffectError
from rank_effect_packages.formats import (
    extract_texture_references,
    rewrite_texture_references,
    structural_fingerprint,
    validate_tga_texture,
)
from rank_effect_packages.installer import installation_assets, verify_new_silhouettes
from rank_effect_packages.package import (
    PACKAGE_FORMAT,
    PACKAGE_SHARD_FORMAT,
    load_package,
)
from rank_effect_packages.safety import require_plain_path
from xmodel_sculpt.sculpt import sculpt_xof_mszip

from .atlas import Region, recolour_luminance
from .models import MantleTransform, audit_model


PACKAGE_ID = "reborn-role-aware-rank-effects-v2-prototype"
ARMOR_SOURCE_RANK = 12
ARMOR_TARGET_RANK = 14
WEAPON_CLASS = "warrior"
MAX_JSON_BYTES = 20 * 1024
CONTRACT_SHARD_SIZE = 8


def _json_bytes(value: object) -> bytes:
    return json.dumps(value, indent=2, sort_keys=True).encode("utf-8") + b"\n"


def _write(path: Path, value: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _write_json(path: Path, value: object) -> None:
    data = _json_bytes(value)
    if len(data) > MAX_JSON_BYTES:
        raise RankEffectError(f"Prototype JSON exceeds 20 KiB: {path}")
    _write(path, data)


def _regular(path: Path, label: str) -> bytes:
    if not path.is_file() or path.is_symlink():
        raise RankEffectError(f"{label} is not a regular file: {path}")
    return path.read_bytes()


class _Shard:
    def __init__(self, stage: Path, name: str) -> None:
        self.stage = stage
        self.name = name
        self.files: dict[Path, dict[str, object]] = {}
        self.effects: list[dict[str, object]] = []

    def add(self, target: Path, value: bytes) -> None:
        existing = self.files.get(target)
        if existing is not None:
            if existing["sha256"] != sha256_bytes(value):
                raise RankEffectError(f"Conflicting prototype asset: {target}")
            return
        source = Path("package") / target
        _write(self.stage / source, value)
        self.files[target] = {
            "source": source.as_posix(),
            "target": target.as_posix(),
            "sha256": sha256_bytes(value),
        }

    def write(self) -> str:
        relative = Path("generated") / "manifests" / f"{self.name}.json"
        _write_json(
            self.stage / relative,
            {
                "format": PACKAGE_SHARD_FORMAT,
                "files": [
                    self.files[path]
                    for path in sorted(self.files, key=lambda value: value.as_posix())
                ],
                "effects": self.effects,
            },
        )
        return relative.as_posix()


def _rewrite(data: bytes, mapping: dict[bytes, bytes], label: str) -> bytes:
    references = set(extract_texture_references(data, label))
    selected = {source: target for source, target in mapping.items() if source in references}
    if not selected or references != set(selected):
        raise RankEffectError(f"Prototype texture map is incomplete: {label}")
    rewritten, _ = rewrite_texture_references(
        data, selected, label, require_all_references=True
    )
    return rewritten


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


def _armor_shard(client: Path, stage: Path, contracts: list[dict[str, object]]) -> _Shard:
    shard = _Shard(stage, "armor-14-prototype")
    for asset_root in ASSET_ROOTS:
        directory = client / asset_root / "effect"
        body_source = _regular(directory / "female_body_effect_0010.tga", "AR12 body atlas")
        declared_source = _regular(directory / "11.tga", "AR12 declared atlas")
        body = recolour_luminance(
            body_source,
            Region(0.0, 1.0, 0.0, 0.52),
            (0.025, 0.08, 0.22),
            (0.28, 0.72, 0.92),
            (1.0, 0.94, 0.72),
        )
        if body.changed_pixels <= 0 or body.alpha_changes != 0:
            raise RankEffectError("AR14 role-aware atlas did not preserve alpha/detail")
        main = Path(asset_root) / "effect" / "reborn_body_effect_0014_v2_main.tga"
        declared = Path(asset_root) / "effect" / "reborn_body_effect_0014_v2_declared.tga"
        shard.add(main, body.encoded)
        shard.add(declared, declared_source)
        mapping = {
            b"female_body_effect_0010.tga": main.name.encode("ascii"),
            b"11.tga": declared.name.encode("ascii"),
        }
        for gender in GENDERS:
            source_stem = f"{gender}_body_effect_{ARMOR_SOURCE_RANK:04d}"
            target_stem = f"{gender}_body_effect_{ARMOR_TARGET_RANK:04d}"
            canonical = Path(asset_root) / "effect" / f"{target_stem}.gwo"
            shard.add(canonical, body.encoded)
            models: list[Path] = []
            for index in range(3):
                source_path = directory / f"{source_stem}_{index}.jcs"
                source = _regular(source_path, "coherent stock AR12 JCS")
                authored = source
                changed_vertices = 0
                if index == 1:
                    sculpt = sculpt_xof_mszip(
                        source,
                        MantleTransform(),
                        label=str(source_path),
                    )
                    authored = sculpt.encoded
                    changed_vertices = sculpt.changed_vertices
                    if changed_vertices <= 0:
                        raise RankEffectError("AR14 mantle sculpt changed no vertices")
                output = _rewrite(authored, mapping, str(source_path))
                target = Path(asset_root) / "effect" / f"{target_stem}_{index}.jcs"
                shard.add(target, output)
                models.append(target)
                contracts.append(
                    {
                        "effect": "armor-ar14",
                        "asset_root": asset_root,
                        "gender": gender,
                        "slot": index,
                        "role": ("animated-core", "outer-mantle", "animated-rune")[index],
                        "source_rank": ARMOR_SOURCE_RANK,
                        "sculpted": index == 1,
                        "changed_vertices": changed_vertices,
                        "source_sha256": sha256_bytes(source),
                        "output_sha256": sha256_bytes(output),
                        "source_structural_sha256": structural_fingerprint(source, str(source_path)),
                        "output_structural_sha256": structural_fingerprint(output, str(target)),
                        "source": _audit_dict(audit_model(source, str(source_path))),
                        "output": _audit_dict(audit_model(output, str(target))),
                    }
                )
            shard.effects.append(
                {
                    "kind": "armor",
                    "rank": ARMOR_TARGET_RANK,
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
                "effect": "armor-ar14-atlas",
                "asset_root": asset_root,
                "source_sha256": sha256_bytes(body_source),
                "output_sha256": sha256_bytes(body.encoded),
                "changed_pixels": body.changed_pixels,
                "alpha_changes": body.alpha_changes,
                "region": [0.0, 1.0, 0.0, 0.52],
            }
        )
    return shard


def _resolve_texture(directory: Path, canonical: Path, reference: bytes) -> tuple[bytes, str]:
    try:
        name = reference.decode("ascii")
    except UnicodeDecodeError:
        name = None
    candidates: list[Path] = []
    if name and Path(name).name == name:
        candidate = directory / name
        candidates.append(candidate)
        if candidate.suffix.lower() == ".tga":
            candidates.append(candidate.with_suffix(".gwo"))
    candidates.append(canonical)
    for candidate in candidates:
        if candidate.is_file() and not candidate.is_symlink():
            value = candidate.read_bytes()
            try:
                validate_tga_texture(value, str(candidate))
            except RankEffectError:
                continue
            return value, str(candidate)
    raise RankEffectError(f"No stock texture for {reference!r}")


def _weapon_shard(client: Path, stage: Path, contracts: list[dict[str, object]]) -> _Shard:
    shard = _Shard(stage, "weapon-warrior-prototype")
    spec = WEAPON_EFFECTS[WEAPON_CLASS]
    for asset_root in ASSET_ROOTS:
        directory = client / asset_root / "effect"
        for gender in GENDERS:
            stem = f"{gender}_{spec.family}_effect_{spec.effect_id:04d}"
            canonical_source = directory / f"{stem}.gwo"
            hand = "_right"
            source_paths = [directory / f"{stem}{hand}_{index}.jcs" for index in range(2)]
            source_models = [_regular(path, "native Warrior WR10 JCS") for path in source_paths]
            references = tuple(
                dict.fromkeys(
                    reference
                    for path, data in zip(source_paths, source_models)
                    for reference in extract_texture_references(data, str(path))
                )
            )
            if len(references) != 3:
                raise RankEffectError("Native Warrior WR10 must retain three texture bindings")
            private: list[Path] = []
            mapping: dict[bytes, bytes] = {}
            authored_by_reference: dict[bytes, bytes] = {}
            for index, reference in enumerate(references):
                source, source_name = _resolve_texture(directory, canonical_source, reference)
                if index == 0:
                    role, authored = "declared-unused", source
                    changed_pixels = alpha_changes = 0
                elif index == 1:
                    role = "static-corona"
                    recolour = recolour_luminance(
                        source,
                        Region(0.50, 0.85, 0.0, 0.50),
                        (0.30, 0.01, 0.01),
                        (0.92, 0.12, 0.02),
                        (1.0, 0.76, 0.20),
                    )
                    authored = recolour.encoded
                    changed_pixels = recolour.changed_pixels
                    alpha_changes = recolour.alpha_changes
                else:
                    role = "travelling-spark"
                    recolour = recolour_luminance(
                        source,
                        Region(0.81, 0.97, 0.03, 0.20),
                        (0.28, 0.01, 0.01),
                        (0.95, 0.25, 0.03),
                        (1.0, 0.92, 0.48),
                    )
                    authored = recolour.encoded
                    changed_pixels = recolour.changed_pixels
                    alpha_changes = recolour.alpha_changes
                target = Path(asset_root) / "effect" / (
                    f"reborn_wr10_warrior_{gender}_v2_{role.replace('-', '_')}.tga"
                )
                shard.add(target, authored)
                private.append(target)
                mapping[reference] = target.name.encode("ascii")
                authored_by_reference[reference] = authored
                contracts.append(
                    {
                        "effect": "weapon-warrior-wr10-atlas",
                        "asset_root": asset_root,
                        "gender": gender,
                        "binding": index,
                        "role": role,
                        "source_reference": (
                            "ascii:" + reference.decode("ascii")
                            if reference.isascii()
                            else "hex:" + reference.hex()
                        ),
                        "resolved_source": source_name,
                        "source_sha256": sha256_bytes(source),
                        "output_sha256": sha256_bytes(authored),
                        "changed_pixels": changed_pixels,
                        "alpha_changes": alpha_changes,
                    }
                )
            visible = authored_by_reference[references[1]]
            canonical = Path(asset_root) / "effect" / f"{stem}.gwo"
            shard.add(canonical, visible)
            models: list[Path] = []
            for index, (source_path, source) in enumerate(zip(source_paths, source_models)):
                output = _rewrite(source, mapping, str(source_path))
                target = Path(asset_root) / "effect" / f"{stem}{hand}_{index}.jcs"
                shard.add(target, output)
                models.append(target)
                contracts.append(
                    {
                        "effect": "weapon-warrior-wr10",
                        "asset_root": asset_root,
                        "gender": gender,
                        "slot": index,
                        "role": ("static-corona", "travelling-spark")[index],
                        "source_effect_id": spec.effect_id,
                        "sculpted": False,
                        "source_sha256": sha256_bytes(source),
                        "output_sha256": sha256_bytes(output),
                        "source_structural_sha256": structural_fingerprint(source, str(source_path)),
                        "output_structural_sha256": structural_fingerprint(output, str(target)),
                        "source": _audit_dict(audit_model(source, str(source_path))),
                        "output": _audit_dict(audit_model(output, str(target))),
                    }
                )
            shard.effects.append(
                {
                    "kind": "weapon",
                    "rank": 10,
                    "asset_root": asset_root,
                    "gender": gender,
                    "class": WEAPON_CLASS,
                    "models": [path.as_posix() for path in models],
                    "canonical_texture": canonical.as_posix(),
                    "private_textures": [path.as_posix() for path in private],
                }
            )
    return shard


def _baseline(stage: Path, client: Path) -> None:
    value = create_baseline(client, (ARMOR_TARGET_RANK,), (WEAPON_CLASS,))
    main, shards = shard_baseline(value)
    root = stage / "generated"
    _write_json(root / "protected-stock.json", main)
    for name, shard in shards.items():
        _write_json(root / name, shard)


def _contracts(stage: Path, contracts: list[dict[str, object]]) -> str:
    directory = Path("generated") / "role-contracts"
    names: list[str] = []
    for offset in range(0, len(contracts), CONTRACT_SHARD_SIZE):
        number = offset // CONTRACT_SHARD_SIZE + 1
        relative = directory / f"contracts-{number:02d}.json"
        names.append(relative.as_posix())
        _write_json(
            stage / relative,
            {
                "format": "reborn-rank-effect-role-contract-shard-v2",
                "contracts": contracts[offset : offset + CONTRACT_SHARD_SIZE],
            },
        )
    main = Path("generated") / "role-contract.json"
    _write_json(
        stage / main,
        {
            "format": "reborn-rank-effect-role-contract-v2",
            "prototype": True,
            "contract_manifests": names,
        },
    )
    return main.as_posix()


def _promote(stage: Path, output: Path) -> None:
    if output.exists():
        raise RankEffectError(f"Prototype output already exists: {output}")
    os.replace(stage, output)


def build_prototype(client_root: Path, output_root: Path) -> tuple[int, int]:
    client = client_root.resolve()
    output = output_root.resolve()
    if not client.is_dir():
        raise RankEffectError(f"Client root does not exist: {client}")
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise RankEffectError(f"Prototype output already exists: {output}")
    require_plain_path(client, client, "prototype client source")
    require_plain_path(output.parent, output.parent, "prototype output parent")
    stage = output.parent / f".{output.name}.build-{uuid.uuid4().hex}"
    stage.mkdir()
    try:
        contracts: list[dict[str, object]] = []
        _baseline(stage, client)
        shards = [
            _armor_shard(client, stage, contracts),
            _weapon_shard(client, stage, contracts),
        ]
        shard_names = [shard.write() for shard in shards]
        contract_path = _contracts(stage, contracts)
        _write_json(
            stage / "rank-effect-manifest.json",
            {
                "format": PACKAGE_FORMAT,
                "package_id": PACKAGE_ID,
                "coverage": {
                    "armor_ranks": [ARMOR_TARGET_RANK],
                    "weapon_classes": [WEAPON_CLASS],
                },
                "protected_baseline": "generated/protected-stock.json",
                "effect_manifests": shard_names,
                "role_contract": contract_path,
                "armor_rank_9_compatibility": {
                    "mode": "runtime_token_remap",
                    "mappings": [
                        {
                            "from": "female_body_effect_0010.tga",
                            "to": "legacy_body_effect_0010.tga",
                        },
                        {
                            "from": "female_body_effect_0011.tga",
                            "to": "legacy_body_effect_0011.tga",
                        },
                    ],
                },
            },
        )
        package = load_package(stage)
        verify_new_silhouettes(package)
        installation_assets(client, package)
        result = len(package.effects), len(package.assets)
        _promote(stage, output)
        final = load_package(output)
        verify_new_silhouettes(final)
        installation_assets(client, final)
        return result
    finally:
        if stage.exists():
            shutil.rmtree(stage, ignore_errors=True)
