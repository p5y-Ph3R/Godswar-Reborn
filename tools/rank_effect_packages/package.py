"""Manifest validation for self-contained AR10-14 and WR10 packages."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re

from .baseline import baseline_coverage, load_baseline, sha256_bytes
from .catalog import (
    ARMOR_RANKS,
    ASSET_ROOTS,
    GENDERS,
    WEAPON_EFFECTS,
    WEAPON_RANK,
    armor_stem,
    expected_model_pattern,
    private_texture_pattern,
    safe_asset_path,
    safe_protected_path,
    weapon_stem,
)
from .errors import RankEffectError
from .formats import (
    extract_texture_references,
    structural_fingerprint,
    validate_tga_texture,
)
from .safety import require_plain_path


PACKAGE_FORMAT = "reborn-rank-effect-package-v1"
PACKAGE_SHARD_FORMAT = "reborn-rank-effect-package-shard-v1"
MAX_INSTALL_FILES = 256
MAX_ASSET_BYTES = 64 * 1024 * 1024
_HASH = re.compile(r"^[0-9a-f]{64}$")
_PACKAGE_ID = re.compile(r"^[a-z0-9][a-z0-9._-]{2,63}$")


@dataclass(frozen=True, slots=True)
class EffectRecord:
    key: str
    kind: str
    rank: int
    asset_root: str
    gender: str
    class_name: str | None
    models: tuple[Path, ...]
    canonical_texture: Path
    private_textures: tuple[Path, ...]
    structural_sha256: str


@dataclass(frozen=True, slots=True)
class LoadedPackage:
    root: Path
    manifest: dict[str, object]
    assets: dict[Path, bytes]
    effects: tuple[EffectRecord, ...]
    baseline: dict[str, object]


def _read_json(path: Path) -> dict[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise RankEffectError(f"Could not read JSON {path}: {error}") from error
    if not isinstance(value, dict):
        raise RankEffectError(f"JSON root must be an object: {path}")
    return value


def _expand_manifest(root: Path, manifest: dict[str, object]) -> dict[str, object]:
    shard_names = manifest.get("effect_manifests")
    if shard_names is None:
        return manifest
    if (
        "files" in manifest
        or "effects" in manifest
        or not isinstance(shard_names, list)
        or not 1 <= len(shard_names) <= 32
        or not all(isinstance(name, str) for name in shard_names)
        or len(set(shard_names)) != len(shard_names)
    ):
        raise RankEffectError("Package shard list is invalid")
    files: list[object] = []
    effects: list[object] = []
    for name in shard_names:
        path = _inside(root, name, "effect manifest")
        if not path.is_file() or path.is_symlink():
            raise RankEffectError(f"Effect manifest is not a regular file: {path}")
        shard = _read_json(path)
        if shard.get("format") != PACKAGE_SHARD_FORMAT:
            raise RankEffectError(f"Unexpected effect-manifest format: {path}")
        shard_files, shard_effects = shard.get("files"), shard.get("effects")
        if not isinstance(shard_files, list) or not isinstance(shard_effects, list):
            raise RankEffectError(f"Effect manifest is incomplete: {path}")
        files.extend(shard_files)
        effects.extend(shard_effects)
    expanded = dict(manifest)
    expanded["files"] = files
    expanded["effects"] = effects
    return expanded


def _inside(root: Path, value: object, label: str) -> Path:
    if not isinstance(value, str) or not value or "\x00" in value:
        raise RankEffectError(f"Invalid {label} path")
    relative = Path(value.replace("\\", "/"))
    if relative.is_absolute() or ".." in relative.parts:
        raise RankEffectError(f"{label} path escapes the package: {value}")
    resolved = (root / relative).resolve()
    try:
        resolved.relative_to(root.resolve())
    except ValueError as error:
        raise RankEffectError(f"{label} path escapes the package: {value}") from error
    require_plain_path(root, resolved, label)
    return resolved


def _coverage(manifest: dict[str, object]) -> tuple[tuple[int, ...], tuple[str, ...]]:
    value = manifest.get("coverage")
    if not isinstance(value, dict):
        raise RankEffectError("Package has no coverage object")
    armor = value.get("armor_ranks", [])
    weapon = value.get("weapon_classes", [])
    if (
        not isinstance(armor, list)
        or not all(isinstance(rank, int) for rank in armor)
        or not isinstance(weapon, list)
        or not all(isinstance(name, str) for name in weapon)
    ):
        raise RankEffectError("Package coverage is invalid")
    armor_set = tuple(sorted(set(armor)))
    weapon_set = tuple(sorted(set(weapon)))
    if any(rank not in ARMOR_RANKS for rank in armor_set):
        raise RankEffectError("Armor coverage may contain only AR10..AR14")
    unknown = set(weapon_set).difference(WEAPON_EFFECTS)
    if unknown:
        raise RankEffectError(f"Unknown weapon classes: {sorted(unknown)}")
    if not armor_set and not weapon_set:
        raise RankEffectError("Package coverage cannot be empty")
    return armor_set, weapon_set


def _load_assets(root: Path, manifest: dict[str, object]) -> dict[Path, bytes]:
    entries = manifest.get("files")
    if not isinstance(entries, list) or not 1 <= len(entries) <= MAX_INSTALL_FILES:
        raise RankEffectError(f"Package must contain 1..{MAX_INSTALL_FILES} install files")
    assets: dict[Path, bytes] = {}
    sources: set[Path] = set()
    for entry in entries:
        if not isinstance(entry, dict):
            raise RankEffectError("Package files contains an invalid entry")
        target = safe_asset_path(entry.get("target"))
        source = _inside(root, entry.get("source"), "asset source")
        digest = entry.get("sha256")
        if target in assets or source in sources:
            raise RankEffectError(f"Duplicate package source or target: {target}")
        if not isinstance(digest, str) or not _HASH.fullmatch(digest):
            raise RankEffectError(f"Invalid asset hash: {target}")
        if not source.is_file() or source.is_symlink():
            raise RankEffectError(f"Package asset is not a regular file: {source}")
        size = source.stat().st_size
        if size <= 0 or size > MAX_ASSET_BYTES:
            raise RankEffectError(f"Package asset has unsafe size: {source}")
        data = source.read_bytes()
        if sha256_bytes(data) != digest:
            raise RankEffectError(f"Package asset hash mismatch: {target}")
        assets[target] = data
        sources.add(source)
    return assets


def _effect_key(
    kind: str,
    rank: int,
    asset_root: str,
    gender: str,
    class_name: str | None,
) -> str:
    suffix = class_name if class_name is not None else "body"
    return f"{kind}:{rank}:{suffix}:{asset_root}:{gender}"


def _path_list(value: object, label: str, maximum: int) -> tuple[Path, ...]:
    if not isinstance(value, list) or not 1 <= len(value) <= maximum:
        raise RankEffectError(f"{label} must contain 1..{maximum} paths")
    paths = tuple(safe_asset_path(item) for item in value)
    if len(set(paths)) != len(paths):
        raise RankEffectError(f"{label} contains duplicate paths")
    return paths


def _load_effect(
    entry: object,
    assets: dict[Path, bytes],
) -> EffectRecord:
    if not isinstance(entry, dict):
        raise RankEffectError("Package effects contains an invalid entry")
    kind = entry.get("kind")
    rank = entry.get("rank")
    asset_root = entry.get("asset_root")
    gender = entry.get("gender")
    class_name = entry.get("class")
    if kind not in {"armor", "weapon"} or not isinstance(rank, int):
        raise RankEffectError("Effect kind or rank is invalid")
    if asset_root not in ASSET_ROOTS or gender not in GENDERS:
        raise RankEffectError("Effect root or gender is invalid")
    if kind == "armor":
        if class_name is not None:
            raise RankEffectError("Armor effects cannot declare a class")
        stem = armor_stem(gender, rank)
        expected_models = 3
    else:
        if rank != WEAPON_RANK or class_name not in WEAPON_EFFECTS:
            raise RankEffectError("Weapon effects must be mapped WR10 class effects")
        assert isinstance(class_name, str)
        stem = weapon_stem(gender, class_name)
        expected_models = 2

    models = _path_list(entry.get("models"), "effect models", expected_models)
    if len(models) != expected_models:
        raise RankEffectError(f"{stem} must contain exactly {expected_models} JCS models")
    expected_directory = Path(asset_root) / "effect"
    pattern = expected_model_pattern(kind, stem)
    indexes: list[int] = []
    for model in models:
        match = pattern.fullmatch(model.name)
        if model.parent != expected_directory or match is None:
            raise RankEffectError(f"Non-canonical model target: {model}")
        indexes.append(int(match.group(1)))
    if sorted(indexes) != list(range(expected_models)):
        raise RankEffectError(f"Model indexes must be contiguous for {stem}")

    canonical = safe_asset_path(entry.get("canonical_texture"))
    if canonical != expected_directory / f"{stem}.gwo":
        raise RankEffectError(f"Non-canonical GWO target: {canonical}")
    private = _path_list(entry.get("private_textures"), "private textures", 8)
    private_pattern = private_texture_pattern(kind, rank, gender, class_name)
    if any(
        path.parent != expected_directory or private_pattern.fullmatch(path.name) is None
        for path in private
    ):
        raise RankEffectError(f"Effect has a non-private texture name: {stem}")

    required = set(models) | {canonical} | set(private)
    missing = required.difference(assets)
    if missing:
        raise RankEffectError(f"Effect assets are not in files: {sorted(map(str, missing))}")
    validate_tga_texture(assets[canonical], canonical.as_posix())
    for path in private:
        validate_tga_texture(assets[path], path.as_posix())

    private_names = {path.name.encode("ascii") for path in private}
    referenced: set[bytes] = set()
    structures: list[str] = []
    for model in sorted(models, key=lambda value: value.name):
        references = extract_texture_references(assets[model], model.as_posix())
        if any(reference not in private_names for reference in references):
            raise RankEffectError(
                f"JCS is not self-contained in private textures: {model}"
            )
        referenced.update(references)
        structures.append(structural_fingerprint(assets[model], model.as_posix()))
    if not referenced or referenced != private_names:
        raise RankEffectError(f"Effect contains an unused private texture: {stem}")
    structure = hashlib.sha256("\n".join(structures).encode("ascii")).hexdigest()
    key = _effect_key(kind, rank, asset_root, gender, class_name)
    return EffectRecord(
        key, kind, rank, asset_root, gender, class_name,
        models, canonical, private, structure,
    )


def _expected_keys(
    armor_ranks: tuple[int, ...],
    weapon_classes: tuple[str, ...],
) -> set[str]:
    result = {
        _effect_key("armor", rank, root, gender, None)
        for rank in armor_ranks
        for root in ASSET_ROOTS
        for gender in GENDERS
    }
    result.update(
        _effect_key("weapon", WEAPON_RANK, root, gender, class_name)
        for class_name in weapon_classes
        for root in ASSET_ROOTS
        for gender in GENDERS
    )
    return result


def _validate_compatibility(manifest: dict[str, object], has_armor: bool) -> None:
    value = manifest.get("armor_rank_9_compatibility")
    if not has_armor:
        if value not in (None, False):
            raise RankEffectError("WR-only packages cannot rewrite AR9 dependencies")
        return
    expected = {
        "female_body_effect_0010.tga": "legacy_body_effect_0010.tga",
        "female_body_effect_0011.tga": "legacy_body_effect_0011.tga",
    }
    if not isinstance(value, dict) or value.get("mode") != "runtime_token_remap":
        raise RankEffectError("Armor packages require the AR9 runtime token remap")
    mappings = value.get("mappings")
    if not isinstance(mappings, list):
        raise RankEffectError("AR9 compatibility mappings are invalid")
    actual = {}
    for mapping in mappings:
        if not isinstance(mapping, dict):
            raise RankEffectError("AR9 compatibility contains an invalid mapping")
        source, target = mapping.get("from"), mapping.get("to")
        if not isinstance(source, str) or not isinstance(target, str):
            raise RankEffectError("AR9 compatibility names must be strings")
        actual[source] = target
    if actual != expected:
        raise RankEffectError("AR9 compatibility must use the reviewed legacy remaps")


def _validate_uniqueness(
    effects: tuple[EffectRecord, ...],
    assets: dict[Path, bytes],
) -> None:
    groups: dict[tuple[str, str, str], list[EffectRecord]] = {}
    for effect in effects:
        group = (effect.kind, effect.asset_root, effect.gender)
        groups.setdefault(group, []).append(effect)
    for group, members in groups.items():
        structures = [member.structural_sha256 for member in members]
        if group[0] == "armor" and len(structures) != len(set(structures)):
            raise RankEffectError(f"Effect silhouettes are duplicated in {group}")
        texture_hashes = [
            sha256_bytes(assets[member.canonical_texture]) for member in members
        ]
        if len(texture_hashes) != len(set(texture_hashes)):
            raise RankEffectError(f"Effect palettes are duplicated in {group}")


def load_package(root: Path) -> LoadedPackage:
    if not root.is_dir():
        raise RankEffectError(f"Package root does not exist: {root}")
    require_plain_path(root, root, "package")
    root = root.resolve()
    manifest = _read_json(root / "rank-effect-manifest.json")
    if manifest.get("format") != PACKAGE_FORMAT:
        raise RankEffectError("Unexpected rank-effect package format")
    package_id = manifest.get("package_id")
    if not isinstance(package_id, str) or not _PACKAGE_ID.fullmatch(package_id):
        raise RankEffectError("Package ID is invalid")
    armor_ranks, weapon_classes = _coverage(manifest)
    _validate_compatibility(manifest, bool(armor_ranks))
    expanded = _expand_manifest(root, manifest)
    assets = _load_assets(root, expanded)
    entries = expanded.get("effects")
    if not isinstance(entries, list):
        raise RankEffectError("Package has no effects list")
    effects = tuple(_load_effect(entry, assets) for entry in entries)
    keys = [effect.key for effect in effects]
    if len(keys) != len(set(keys)) or set(keys) != _expected_keys(armor_ranks, weapon_classes):
        raise RankEffectError("Effect records do not exactly match declared coverage")
    used = {
        path
        for effect in effects
        for path in (*effect.models, effect.canonical_texture, *effect.private_textures)
    }
    if used != set(assets):
        raise RankEffectError("Package contains install files not owned by an effect")
    _validate_uniqueness(effects, assets)

    baseline_path = _inside(root, manifest.get("protected_baseline"), "baseline")
    baseline = load_baseline(baseline_path)
    if baseline_coverage(baseline) != (armor_ranks, weapon_classes):
        raise RankEffectError("Protected baseline coverage differs from package coverage")
    protected_paths = {
        safe_protected_path(entry["path"])
        for entry in baseline["files"]
        if isinstance(entry, dict)
    }
    collisions = protected_paths.intersection(assets)
    reviewed_canonical_replacements = {
        effect.canonical_texture
        for effect in effects
        if effect.kind == "armor"
    }
    unexpected_collisions = collisions.difference(reviewed_canonical_replacements)
    if unexpected_collisions:
        raise RankEffectError(
            "Package targets protected stock files: "
            + ", ".join(
                sorted(path.as_posix() for path in unexpected_collisions)
            )
        )
    return LoadedPackage(root, manifest, assets, effects, baseline)
