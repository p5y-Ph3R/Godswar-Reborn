"""Deterministic authoring for the reviewed AR10-14 and class WR10 package."""

from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
import shutil
import tempfile
import time
import uuid

from .baseline import (
    BASELINE_FORMAT,
    BASELINE_SHARD_FORMAT,
    create_baseline,
    sha256_bytes,
)
from .catalog import (
    ARMOR_RANKS,
    ASSET_ROOTS,
    GENDERS,
    WEAPON_EFFECTS,
    WEAPON_RANK,
    armor_stem,
    weapon_stem,
)
from .errors import RankEffectError
from .formats import (
    extract_texture_references,
    rewrite_texture_references,
    validate_tga_texture,
)
from .installer import installation_assets, verify_new_silhouettes
from .package import PACKAGE_FORMAT, PACKAGE_SHARD_FORMAT, load_package
from .safety import require_plain_path


PACKAGE_ID = "reborn-greek-rank-effects-v1"
MAX_MANIFEST_BYTES = 20 * 1024


@dataclass(frozen=True, slots=True)
class ArmorDesign:
    identity: str
    master: str
    # Each tuple is (legacy rank, component index), in target slot order.
    source_slots: tuple[tuple[int, int], tuple[int, int], tuple[int, int]]


ARMOR_DESIGNS = {
    10: ArmorDesign(
        "helios-aegis",
        "armor-rank-10-helios-aegis.gwo",
        ((10, 0), (10, 1), (10, 2)),
    ),
    11: ArmorDesign(
        "hecates-veil",
        "armor-rank-11-hecates-veil.gwo",
        ((7, 0), (7, 1), (5, 2)),
    ),
    12: ArmorDesign(
        "gaias-laurel",
        "armor-rank-12-gaias-laurel.gwo",
        ((6, 0), (6, 1), (2, 2)),
    ),
    13: ArmorDesign(
        "ares-eclipse",
        "armor-rank-13-ares-eclipse.gwo",
        ((5, 0), (5, 1), (5, 2)),
    ),
    14: ArmorDesign(
        "olympian-apotheosis",
        "armor-rank-14-olympian-apotheosis.gwo",
        ((2, 0), (4, 0), (2, 2)),
    ),
}


WEAPON_MASTERS = {
    "warrior": "weapon-rank-10-warrior-ares-emberblade.gwo",
    "champion": "weapon-rank-10-champion-zeus-stormlance.gwo",
    "priest": "weapon-rank-10-priest-apollo-radiance.gwo",
    "mage": "weapon-rank-10-mage-hecates-aether.gwo",
}


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
        for attempt in range(6):
            try:
                os.replace(temporary, path)
                break
            except PermissionError:
                if attempt == 5:
                    raise
                time.sleep(0.05 * (attempt + 1))
    finally:
        temporary.unlink(missing_ok=True)


def _regular(path: Path, label: str) -> bytes:
    if not path.is_file() or path.is_symlink():
        raise RankEffectError(f"{label} is not a regular file: {path}")
    return path.read_bytes()


def _manifest(path: Path, value: object) -> None:
    data = _json_bytes(value)
    if len(data) > MAX_MANIFEST_BYTES:
        raise RankEffectError(f"Generated manifest exceeds 20 KiB: {path}")
    _write(path, data)


class _Shard:
    def __init__(self, stage: Path, name: str) -> None:
        self.stage = stage
        self.name = name
        self.files: dict[Path, dict[str, object]] = {}
        self.effects: list[dict[str, object]] = []

    def add_asset(self, target: Path, value: bytes) -> None:
        source = Path("package") / target
        existing = self.files.get(target)
        if existing is not None:
            if existing["sha256"] != sha256_bytes(value):
                raise RankEffectError(f"Conflicting generated asset: {target}")
            return
        _write(self.stage / source, value)
        self.files[target] = {
            "source": source.as_posix(),
            "target": target.as_posix(),
            "sha256": sha256_bytes(value),
        }

    def write(self) -> str:
        relative = Path("generated") / "manifests" / f"{self.name}.json"
        value = {
            "format": PACKAGE_SHARD_FORMAT,
            "files": [
                self.files[path]
                for path in sorted(self.files, key=lambda item: item.as_posix())
            ],
            "effects": self.effects,
        }
        _manifest(self.stage / relative, value)
        return relative.as_posix()


def _master(package_root: Path, filename: str) -> bytes:
    path = package_root / "generated" / filename
    value = _regular(path, "Generated texture master")
    validate_tga_texture(value, str(path))
    return value


def _rewrite(data: bytes, target_name: bytes, label: str) -> bytes:
    references = tuple(dict.fromkeys(extract_texture_references(data, label)))
    if not references:
        return data
    rewritten, _ = rewrite_texture_references(
        data,
        {reference: target_name for reference in references},
        label,
        require_all_references=True,
    )
    return rewritten


def _build_armor(
    client_root: Path,
    package_root: Path,
    stage: Path,
    rank: int,
) -> _Shard:
    design = ARMOR_DESIGNS[rank]
    palette = _master(package_root, design.master)
    shard = _Shard(stage, f"armor-{rank}")
    for asset_root in ASSET_ROOTS:
        effect_dir = client_root / asset_root / "effect"
        private = Path(asset_root) / "effect" / (
            f"reborn_body_effect_{rank:04d}_palette.tga"
        )
        shard.add_asset(private, palette)
        for gender in GENDERS:
            stem = armor_stem(gender, rank)
            canonical = Path(asset_root) / "effect" / f"{stem}.gwo"
            shard.add_asset(canonical, palette)
            models: list[Path] = []
            for target_index, (source_rank, source_index) in enumerate(
                design.source_slots
            ):
                source = effect_dir / (
                    f"{gender}_body_effect_{source_rank:04d}_{source_index}.jcs"
                )
                target = Path(asset_root) / "effect" / (
                    f"{stem}_{target_index}.jcs"
                )
                data = _rewrite(
                    _regular(source, "Armor geometry source"),
                    private.name.encode("ascii"),
                    str(source),
                )
                shard.add_asset(target, data)
                models.append(target)
            shard.effects.append(
                {
                    "kind": "armor",
                    "rank": rank,
                    "asset_root": asset_root,
                    "gender": gender,
                    "class": None,
                    "models": [path.as_posix() for path in models],
                    "canonical_texture": canonical.as_posix(),
                    "private_textures": [private.as_posix()],
                }
            )
    return shard


def _reference_name(reference: bytes) -> str | None:
    for encoding in ("ascii", "gbk"):
        try:
            value = reference.decode(encoding)
        except UnicodeDecodeError:
            continue
        if value and "\x00" not in value and Path(value).name == value:
            return value
    return None


def _base_texture(
    client_root: Path,
    asset_root: str,
    source_dir: Path,
    source_canonical: Path,
    reference: bytes,
) -> bytes:
    name = _reference_name(reference)
    candidates: list[Path] = []
    if name is not None:
        for parent in (source_dir, client_root / asset_root):
            candidate = parent / name
            candidates.append(candidate)
            if candidate.suffix.lower() == ".tga":
                candidates.append(candidate.with_suffix(".gwo"))
    candidates.append(source_canonical)
    for candidate in candidates:
        if candidate.is_file() and not candidate.is_symlink():
            data = candidate.read_bytes()
            try:
                validate_tga_texture(data, str(candidate))
            except RankEffectError:
                continue
            return data
    raise RankEffectError(
        f"No valid private texture source for {reference!r} in {source_dir}"
    )


def _build_weapon(
    client_root: Path,
    package_root: Path,
    stage: Path,
    class_name: str,
) -> _Shard:
    spec = WEAPON_EFFECTS[class_name]
    master = _master(package_root, WEAPON_MASTERS[class_name])
    source_id = spec.effect_id - 2
    shard = _Shard(stage, f"weapon-{class_name}")
    for asset_root in ASSET_ROOTS:
        effect_dir = client_root / asset_root / "effect"
        for gender in GENDERS:
            source_stem = f"{gender}_{spec.family}_effect_{source_id:04d}"
            source_canonical = effect_dir / f"{source_stem}.gwo"
            target_stem = weapon_stem(gender, class_name)
            canonical = Path(asset_root) / "effect" / f"{target_stem}.gwo"
            shard.add_asset(canonical, master)
            hand = "_right" if spec.family == "weapononehand" else ""
            sources = [
                effect_dir / f"{source_stem}{hand}_{index}.jcs"
                for index in range(2)
            ]
            source_data = [
                _regular(path, "WR7 geometry source") for path in sources
            ]
            references = tuple(
                dict.fromkeys(
                    reference
                    for path, data in zip(sources, source_data)
                    for reference in extract_texture_references(data, str(path))
                )
            )
            mapping: dict[bytes, bytes] = {}
            private: list[Path] = []
            for index, reference in enumerate(references):
                target = Path(asset_root) / "effect" / (
                    f"reborn_wr10_{class_name}_{gender}_base_{index:02d}.tga"
                )
                shard.add_asset(
                    target,
                    _base_texture(
                        client_root,
                        asset_root,
                        effect_dir,
                        source_canonical,
                        reference,
                    ),
                )
                mapping[reference] = target.name.encode("ascii")
                private.append(target)
            models: list[Path] = []
            for index, (source, data) in enumerate(zip(sources, source_data)):
                target = Path(asset_root) / "effect" / (
                    f"{target_stem}{hand}_{index}.jcs"
                )
                model_references = set(
                    extract_texture_references(data, str(source))
                )
                model_mapping = {
                    reference: target_name
                    for reference, target_name in mapping.items()
                    if reference in model_references
                }
                if model_mapping:
                    data, _ = rewrite_texture_references(
                        data,
                        model_mapping,
                        str(source),
                        require_all_references=True,
                    )
                shard.add_asset(target, data)
                models.append(target)
            shard.effects.append(
                {
                    "kind": "weapon",
                    "rank": WEAPON_RANK,
                    "asset_root": asset_root,
                    "gender": gender,
                    "class": class_name,
                    "models": [path.as_posix() for path in models],
                    "canonical_texture": canonical.as_posix(),
                    "private_textures": [path.as_posix() for path in private],
                }
            )
    return shard


def _write_baseline(stage: Path, baseline: dict[str, object]) -> None:
    entries = baseline.pop("files")
    assert isinstance(entries, list)
    shards: list[str] = []
    for index in range(0, len(entries), 24):
        relative = Path("baselines") / f"protected-files-{index // 24 + 1:02d}.json"
        _manifest(
            stage / "generated" / relative,
            {"format": BASELINE_SHARD_FORMAT, "files": entries[index : index + 24]},
        )
        shards.append(relative.as_posix())
    baseline["file_manifests"] = shards
    _manifest(stage / "generated" / "protected-stock.json", baseline)


def _promote(stage: Path, package_root: Path) -> None:
    generated = package_root / "generated"
    generated.mkdir(parents=True, exist_ok=True)
    targets = (
        (stage / "package", package_root / "package"),
        (stage / "generated" / "manifests", generated / "manifests"),
        (stage / "generated" / "baselines", generated / "baselines"),
        (
            stage / "generated" / "protected-stock.json",
            generated / "protected-stock.json",
        ),
        (stage / "rank-effect-manifest.json", package_root / "rank-effect-manifest.json"),
    )
    backups: list[tuple[Path, Path]] = []
    promoted: list[Path] = []
    try:
        for source, target in targets:
            backup = stage / f"old-{target.name}-{uuid.uuid4().hex}"
            if target.exists():
                require_plain_path(package_root, target, "generated output")
                os.replace(target, backup)
                backups.append((target, backup))
            os.replace(source, target)
            promoted.append(target)
    except Exception:
        for target in reversed(promoted):
            if target.is_dir():
                shutil.rmtree(target)
            else:
                target.unlink(missing_ok=True)
        for target, backup in reversed(backups):
            if backup.exists():
                os.replace(backup, target)
        raise
    for _, backup in backups:
        if backup.is_dir():
            shutil.rmtree(backup, ignore_errors=True)
        else:
            backup.unlink(missing_ok=True)


def build_package(client_root: Path, package_root: Path) -> tuple[int, int]:
    client_root = client_root.resolve()
    package_root = package_root.resolve()
    if not client_root.is_dir() or not package_root.is_dir():
        raise RankEffectError("Client and rank-effect package roots must exist")
    require_plain_path(client_root, client_root, "client root")
    require_plain_path(package_root, package_root, "package root")
    stage = package_root / f".build-{uuid.uuid4().hex}"
    stage.mkdir()
    try:
        baseline = create_baseline(
            client_root, tuple(ARMOR_RANKS), tuple(WEAPON_EFFECTS)
        )
        _write_baseline(stage, baseline)
        shards = [
            _build_armor(client_root, package_root, stage, rank)
            for rank in ARMOR_RANKS
        ]
        shards.extend(
            _build_weapon(client_root, package_root, stage, class_name)
            for class_name in WEAPON_EFFECTS
        )
        shard_names = [shard.write() for shard in shards]
        manifest = {
            "format": PACKAGE_FORMAT,
            "package_id": PACKAGE_ID,
            "coverage": {
                "armor_ranks": list(ARMOR_RANKS),
                "weapon_classes": list(WEAPON_EFFECTS),
            },
            "protected_baseline": "generated/protected-stock.json",
            "effect_manifests": shard_names,
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
        }
        _manifest(stage / "rank-effect-manifest.json", manifest)
        package = load_package(stage)
        verify_new_silhouettes(package)
        installation_assets(client_root, package)
        counts = len(package.effects), len(package.assets)
        _promote(stage, package_root)
        final = load_package(package_root)
        verify_new_silhouettes(final)
        installation_assets(client_root, final)
        return counts
    finally:
        shutil.rmtree(stage, ignore_errors=True)
