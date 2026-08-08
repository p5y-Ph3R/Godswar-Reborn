"""Transactional installer for a validated rank-effect package."""

from __future__ import annotations

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import tempfile

from .baseline import sha256_bytes, verify_baseline
from .catalog import WEAPON_EFFECTS, safe_asset_path, safe_protected_path
from .errors import RankEffectError
from .formats import (
    extract_texture_references,
    rewrite_texture_references,
    structural_fingerprint,
)
from .package import LoadedPackage
from .safety import require_origin_closed, require_plain_path


BACKUP_FORMAT = "reborn-rank-effect-backup-v1"
_LEGACY_MAPPINGS = {
    b"female_body_effect_0010.tga": b"legacy_body_effect_0010.tga",
    b"female_body_effect_0011.tga": b"legacy_body_effect_0011.tga",
}


def _inside(root: Path, relative: Path) -> Path:
    target = (root / relative).resolve()
    try:
        target.relative_to(root.resolve())
    except ValueError as error:
        raise RankEffectError(f"Target escapes client root: {relative}") from error
    return target


def _atomic_write(path: Path, value: bytes) -> None:
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
        if temporary.read_bytes() != value:
            raise RankEffectError(f"Temporary write verification failed: {path}")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _json_bytes(value: object) -> bytes:
    return json.dumps(value, indent=2, sort_keys=True).encode("utf-8") + b"\n"


def _baseline_entries(package: LoadedPackage) -> dict[Path, dict[str, object]]:
    entries = package.baseline["files"]
    assert isinstance(entries, list)
    return {
        safe_protected_path(entry["path"]): entry
        for entry in entries
        if isinstance(entry, dict)
    }


def _legacy_assets(client_root: Path, package: LoadedPackage) -> dict[Path, bytes]:
    coverage = package.manifest["coverage"]
    assert isinstance(coverage, dict)
    if not coverage.get("armor_ranks"):
        return {}
    assets: dict[Path, bytes] = {}
    baseline = _baseline_entries(package)
    for root in ("Characters", "Characters_New"):
        for source_name, target_name in _LEGACY_MAPPINGS.items():
            source_text = source_name.decode("ascii")
            source_relative = Path(root) / "effect" / source_text
            source = _inside(client_root, source_relative)
            if not source.is_file():
                fallback = source.with_suffix(".gwo")
                if not fallback.is_file():
                    raise RankEffectError(f"AR9 dependency is missing: {source}")
                source = fallback
                source_relative = source_relative.with_suffix(".gwo")
            baseline_entry = baseline.get(source_relative)
            data = source.read_bytes()
            if baseline_entry is None or sha256_bytes(data) != baseline_entry["sha256"]:
                raise RankEffectError(f"AR9 dependency is not baseline-pinned: {source}")
            target_relative = Path(root) / "effect" / target_name.decode("ascii")
            assets[target_relative] = data

        for gender in ("female", "male"):
            for index in range(3):
                relative = (
                    Path(root)
                    / "effect"
                    / f"{gender}_body_effect_0009_{index}.jcs"
                )
                source = _inside(client_root, relative)
                if not source.is_file():
                    raise RankEffectError(f"AR9 JCS is missing: {source}")
                original = source.read_bytes()
                baseline_entry = baseline.get(relative)
                if baseline_entry is None or sha256_bytes(original) != baseline_entry["sha256"]:
                    raise RankEffectError(f"AR9 JCS is not baseline-pinned: {source}")
                references = set(extract_texture_references(original, str(source)))
                replacements = {
                    old: new for old, new in _LEGACY_MAPPINGS.items() if old in references
                }
                if not replacements:
                    raise RankEffectError(
                        f"AR9 JCS has unexpected shared dependencies: {source}"
                    )
                patched, _ = rewrite_texture_references(
                    original, replacements, str(source), require_all_references=False
                )
                if structural_fingerprint(patched, str(source)) != baseline_entry.get(
                    "structural_sha256"
                ):
                    raise RankEffectError(f"AR9 compatibility changed structure: {source}")
                assets[relative] = patched
    return assets


def installation_assets(
    client_root: Path,
    package: LoadedPackage,
) -> dict[Path, bytes]:
    require_plain_path(client_root, client_root, "client")
    for relative in (
        Path("Characters") / "effect",
        Path("Characters_New") / "effect",
    ):
        require_plain_path(client_root, client_root / relative, "client effect path")
    verify_baseline(client_root, package.baseline)
    assets = dict(package.assets)
    for relative, value in _legacy_assets(client_root, package).items():
        if relative in assets:
            raise RankEffectError(f"Compatibility target collides with package: {relative}")
        assets[relative] = value
    return assets


def _combined_structure(entries: list[dict[str, object]]) -> str:
    import hashlib

    values = [entry["structural_sha256"] for entry in entries]
    return hashlib.sha256("\n".join(values).encode("ascii")).hexdigest()


def verify_new_silhouettes(package: LoadedPackage) -> None:
    """Reject exact structural reuse of the immediately preceding rank."""

    baseline = _baseline_entries(package)
    for effect in package.effects:
        prior_texture: Path | None = None
        if effect.kind == "armor":
            prior_stem = f"{effect.gender}_body_effect_0009_"
        else:
            assert effect.class_name is not None
            spec = WEAPON_EFFECTS[effect.class_name]
            hand = "_right" if spec.family == "weapononehand" else ""
            prior_stem = (
                f"{effect.gender}_{spec.family}_effect_"
                f"{spec.effect_id - 1:04d}{hand}_"
            )
            prior_texture = Path(effect.asset_root) / "effect" / (
                f"{effect.gender}_{spec.family}_effect_"
                f"{spec.effect_id - 1:04d}.gwo"
            )
        prefix = f"{effect.asset_root}/effect/{prior_stem}"
        prior = [
            entry
            for path, entry in sorted(
                baseline.items(), key=lambda item: item[0].as_posix()
            )
            if path.as_posix().startswith(prefix) and path.suffix.lower() == ".jcs"
        ]
        if not prior:
            raise RankEffectError(f"No predecessor silhouette baseline for {effect.key}")
        if _combined_structure(prior) == effect.structural_sha256:
            if effect.kind == "armor":
                raise RankEffectError(
                    f"Effect reuses the preceding-rank silhouette: {effect.key}"
                )
            assert prior_texture is not None
            prior_entry = baseline.get(prior_texture)
            if (
                prior_entry is None
                or prior_entry["sha256"]
                == sha256_bytes(package.assets[effect.canonical_texture])
            ):
                raise RankEffectError(
                    f"WR10 has no new geometry or palette: {effect.key}"
                )


def verify_installed(
    client_root: Path,
    package: LoadedPackage,
) -> None:
    require_plain_path(client_root, client_root, "client")
    mismatches: list[str] = []
    for relative, expected in package.assets.items():
        target = _inside(client_root, relative)
        if not target.is_file() or target.read_bytes() != expected:
            mismatches.append(relative.as_posix())
    coverage = package.manifest["coverage"]
    assert isinstance(coverage, dict)
    if coverage.get("armor_ranks"):
        baseline = _baseline_entries(package)
        for root in ("Characters", "Characters_New"):
            for old, new in _LEGACY_MAPPINGS.items():
                old_path = Path(root) / "effect" / old.decode("ascii")
                if old_path not in baseline:
                    old_path = old_path.with_suffix(".gwo")
                legacy = _inside(
                    client_root, Path(root) / "effect" / new.decode("ascii")
                )
                if (
                    not legacy.is_file()
                    or sha256_bytes(legacy.read_bytes()) != baseline[old_path]["sha256"]
                ):
                    mismatches.append(legacy.relative_to(client_root).as_posix())
            for gender in ("female", "male"):
                for index in range(3):
                    relative = Path(root) / "effect" / (
                        f"{gender}_body_effect_0009_{index}.jcs"
                    )
                    target = _inside(client_root, relative)
                    expected_structure = baseline[relative]["structural_sha256"]
                    references = set(extract_texture_references(target.read_bytes(), str(target)))
                    if (
                        structural_fingerprint(target.read_bytes(), str(target))
                        != expected_structure
                        or not references
                        or any(old in references for old in _LEGACY_MAPPINGS)
                        or not any(
                            new in references for new in _LEGACY_MAPPINGS.values()
                        )
                    ):
                        mismatches.append(relative.as_posix())
    compatibility_targets = {
        Path(root) / "effect" / f"{gender}_body_effect_0009_{index}.jcs"
        for root in ("Characters", "Characters_New")
        for gender in ("female", "male")
        for index in range(3)
    }
    for relative, entry in _baseline_entries(package).items():
        canonical_armor_targets = {
            effect.canonical_texture
            for effect in package.effects
            if effect.kind == "armor"
        }
        if relative in compatibility_targets or relative in canonical_armor_targets:
            continue
        target = _inside(client_root, relative)
        if not target.is_file() or sha256_bytes(target.read_bytes()) != entry["sha256"]:
            mismatches.append(relative.as_posix())
    if mismatches:
        raise RankEffectError(
            "Installed rank-effect assets differ: " + ", ".join(sorted(set(mismatches)))
        )


def create_backup(
    client_root: Path,
    backup_root: Path,
    assets: dict[Path, bytes],
    package_id: str,
) -> Path:
    safety_root = backup_root if backup_root.exists() else backup_root.parent
    if not safety_root.is_dir():
        raise RankEffectError(f"Backup parent does not exist: {safety_root}")
    require_plain_path(safety_root, safety_root, "backup")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup = backup_root.resolve() / f"rank-effects-{stamp}"
    if backup.exists():
        raise RankEffectError(f"Backup directory already exists: {backup}")
    entries: list[dict[str, object]] = []
    for relative in sorted(assets, key=lambda path: path.as_posix()):
        target = _inside(client_root, relative)
        existed = target.is_file()
        original = target.read_bytes() if existed else None
        if original is not None:
            _atomic_write(backup / "files" / relative, original)
        entries.append(
            {
                "path": relative.as_posix(),
                "existed": existed,
                "original_sha256": (
                    sha256_bytes(original) if original is not None else None
                ),
                "installed_sha256": sha256_bytes(assets[relative]),
            }
        )
    manifest = {
        "format": BACKUP_FORMAT,
        "client_root": str(client_root.resolve()),
        "package_id": package_id,
        "files": entries,
    }
    _atomic_write(backup / "manifest.json", _json_bytes(manifest))
    return backup


def restore_backup(client_root: Path, backup: Path) -> None:
    require_plain_path(client_root, client_root, "client")
    require_plain_path(backup, backup, "backup")
    try:
        manifest = json.loads((backup / "manifest.json").read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise RankEffectError(f"Could not read backup manifest: {error}") from error
    if (
        not isinstance(manifest, dict)
        or manifest.get("format") != BACKUP_FORMAT
        or Path(str(manifest.get("client_root"))).resolve() != client_root.resolve()
    ):
        raise RankEffectError("Backup manifest does not belong to this client")
    entries = manifest.get("files")
    if not isinstance(entries, list) or not entries:
        raise RankEffectError("Backup has no file entries")
    for entry in entries:
        if not isinstance(entry, dict):
            raise RankEffectError("Backup contains an invalid entry")
        relative = safe_asset_path(entry.get("path"))
        target = _inside(client_root, relative)
        if entry.get("existed") is True:
            source = _inside(backup, Path("files") / relative)
            value = source.read_bytes()
            if sha256_bytes(value) != entry.get("original_sha256"):
                raise RankEffectError(f"Backup hash mismatch: {relative}")
            _atomic_write(target, value)
        elif entry.get("existed") is False:
            target.unlink(missing_ok=True)
        else:
            raise RankEffectError(f"Backup state is invalid: {relative}")


def install(
    client_root: Path,
    backup_root: Path,
    package: LoadedPackage,
) -> Path:
    require_origin_closed(client_root)
    verify_new_silhouettes(package)
    assets = installation_assets(client_root, package)
    package_id = str(package.manifest["package_id"])
    backup = create_backup(client_root, backup_root, assets, package_id)
    try:
        for relative, value in assets.items():
            _atomic_write(_inside(client_root, relative), value)
        verify_installed(client_root, package)
    except Exception:
        restore_backup(client_root, backup)
        raise
    return backup
