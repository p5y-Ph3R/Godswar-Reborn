"""Install or restore reviewed Class Suit IV weapon models and textures."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import tempfile


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CLIENT_ROOT = Path(r"C:\Godswar Origin")
DEFAULT_MODEL_STAGE = (
    REPOSITORY_ROOT / "artifacts" / "class-suit-iv-weapon-models"
)
DEFAULT_TEXTURE_STAGE = (
    REPOSITORY_ROOT / "artifacts" / "class-suit-iv-weapons" / "staged"
)
DEFAULT_BACKUP_ROOT = REPOSITORY_ROOT / "backups"


class AssetInstallError(ValueError):
    """Raised when staged or installed assets fail validation."""


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def read_json(path: Path) -> dict[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise AssetInstallError(f"Could not read manifest {path}: {error}") from error
    if not isinstance(value, dict):
        raise AssetInstallError(f"Manifest must contain an object: {path}")
    return value


def safe_relative(value: object) -> Path:
    if not isinstance(value, str) or not value:
        raise AssetInstallError("Manifest contains an invalid target path")
    path = Path(value)
    if path.is_absolute() or ".." in path.parts:
        raise AssetInstallError(f"Manifest target escapes its root: {value}")
    if path.parts[0] not in {"Characters", "Characters_New"}:
        raise AssetInstallError(f"Unexpected client asset root: {value}")
    return path


def ensure_inside(path: Path, root: Path) -> Path:
    resolved = path.resolve()
    try:
        resolved.relative_to(root.resolve())
    except ValueError as error:
        raise AssetInstallError(f"Path escapes expected root: {resolved}") from error
    return resolved


def atomic_write(path: Path, value: bytes) -> None:
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
            raise AssetInstallError(f"Temporary write verification failed: {path}")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def json_bytes(value: object) -> bytes:
    return json.dumps(value, indent=2, sort_keys=True).encode("utf-8") + b"\n"


def load_model_assets(
    stage: Path,
    item_id: int | None = None,
) -> dict[Path, bytes]:
    manifest = read_json(stage / "manifest.json")
    if manifest.get("format") != "reborn-class-suit-iv-weapon-models-v1":
        raise AssetInstallError("Unexpected Class Suit IV model manifest format")
    models = manifest.get("models")
    if not isinstance(models, list) or len(models) != 16:
        raise AssetInstallError("Model manifest must contain exactly 16 models")
    assets: dict[Path, bytes] = {}
    for entry in models:
        if not isinstance(entry, dict):
            raise AssetInstallError("Model manifest contains an invalid entry")
        entry_item_id = entry.get("item_id")
        if not isinstance(entry_item_id, int):
            raise AssetInstallError("Model manifest entry has no numeric item ID")
        if item_id is not None and entry_item_id != item_id:
            continue
        relative = safe_relative(entry.get("target"))
        expected_hash = entry.get("target_sha256")
        source = ensure_inside(stage / relative, stage)
        if not source.is_file():
            raise AssetInstallError(f"Staged model is missing: {source}")
        value = source.read_bytes()
        if not isinstance(expected_hash, str) or sha256_bytes(value) != expected_hash:
            raise AssetInstallError(f"Staged model hash mismatch: {relative}")
        if relative in assets:
            raise AssetInstallError(f"Duplicate staged target: {relative}")
        assets[relative] = value
    expected_count = 4 if item_id is not None else 16
    if len(assets) != expected_count:
        raise AssetInstallError(
            f"Expected {expected_count} staged model targets, found {len(assets)}"
        )
    return assets


def load_texture_assets(stage: Path) -> dict[Path, bytes]:
    manifest = read_json(stage / "manifest.json")
    if manifest.get("schema_version") != 1:
        raise AssetInstallError("Unexpected Class Suit IV texture manifest format")
    textures = manifest.get("textures")
    if not isinstance(textures, list) or len(textures) != 8:
        raise AssetInstallError("Texture manifest must contain exactly 8 variants")
    assets: dict[Path, bytes] = {}
    for entry in textures:
        if not isinstance(entry, dict):
            raise AssetInstallError("Texture manifest contains an invalid entry")
        expected_hash = entry.get("output_sha256")
        targets = entry.get("targets")
        if not isinstance(expected_hash, str) or not isinstance(targets, list):
            raise AssetInstallError("Texture manifest entry is incomplete")
        for target in targets:
            relative = safe_relative(target)
            source = ensure_inside(stage / relative, stage)
            if not source.is_file():
                raise AssetInstallError(f"Staged texture is missing: {source}")
            value = source.read_bytes()
            if sha256_bytes(value) != expected_hash:
                raise AssetInstallError(f"Staged texture hash mismatch: {relative}")
            if relative in assets:
                raise AssetInstallError(f"Duplicate staged target: {relative}")
            assets[relative] = value
    if len(assets) != 16:
        raise AssetInstallError("Texture manifest must resolve to exactly 16 files")
    return assets


def load_all_assets(model_stage: Path, texture_stage: Path) -> dict[Path, bytes]:
    assets = load_model_assets(model_stage.resolve())
    for relative, value in load_texture_assets(texture_stage.resolve()).items():
        if relative in assets:
            raise AssetInstallError(f"Model and texture target collide: {relative}")
        assets[relative] = value
    if len(assets) != 32:
        raise AssetInstallError("Expected exactly 32 Tier IV client assets")
    return assets


def verify_installed(client_root: Path, assets: dict[Path, bytes]) -> None:
    mismatches: list[str] = []
    for relative, expected in assets.items():
        target = ensure_inside(client_root / relative, client_root)
        if not target.is_file() or target.read_bytes() != expected:
            mismatches.append(relative.as_posix())
    if mismatches:
        raise AssetInstallError(
            "Installed assets differ: " + ", ".join(sorted(mismatches))
        )


def create_backup(
    client_root: Path,
    backup_root: Path,
    assets: dict[Path, bytes],
) -> tuple[Path, dict[str, object]]:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup = backup_root.resolve() / f"class-suit-iv-weapons-{stamp}"
    if backup.exists():
        raise AssetInstallError(f"Backup directory already exists: {backup}")
    entries: list[dict[str, object]] = []
    for relative in sorted(assets, key=lambda value: value.as_posix()):
        target = ensure_inside(client_root / relative, client_root)
        existed = target.is_file()
        original = target.read_bytes() if existed else None
        if original is not None:
            atomic_write(backup / "files" / relative, original)
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
    manifest: dict[str, object] = {
        "format": "reborn-class-suit-iv-weapon-backup-v1",
        "client_root": str(client_root),
        "created_at_utc": datetime.now(timezone.utc).isoformat(),
        "files": entries,
    }
    atomic_write(backup / "manifest.json", json_bytes(manifest))
    return backup, manifest


def restore_backup(client_root: Path, backup: Path) -> None:
    manifest = read_json(backup / "manifest.json")
    if manifest.get("format") != "reborn-class-suit-iv-weapon-backup-v1":
        raise AssetInstallError("Unexpected backup manifest format")
    recorded_root = manifest.get("client_root")
    if not isinstance(recorded_root, str) or Path(recorded_root).resolve() != client_root:
        raise AssetInstallError("Backup belongs to a different game-client root")
    entries = manifest.get("files")
    if not isinstance(entries, list) or len(entries) not in {4, 32}:
        raise AssetInstallError("Backup manifest must contain 4 or 32 files")
    for entry in entries:
        if not isinstance(entry, dict):
            raise AssetInstallError("Backup manifest contains an invalid entry")
        relative = safe_relative(entry.get("path"))
        target = ensure_inside(client_root / relative, client_root)
        if entry.get("existed") is True:
            source = ensure_inside(backup / "files" / relative, backup)
            if not source.is_file():
                raise AssetInstallError(f"Backup file is missing: {source}")
            value = source.read_bytes()
            if sha256_bytes(value) != entry.get("original_sha256"):
                raise AssetInstallError(f"Backup hash mismatch: {relative}")
            atomic_write(target, value)
        elif entry.get("existed") is False:
            target.unlink(missing_ok=True)
        else:
            raise AssetInstallError(f"Backup entry has invalid state: {relative}")
    print(f"Restored {len(entries)} Class Suit IV asset targets from {backup}")


def install(
    client_root: Path,
    backup_root: Path,
    assets: dict[Path, bytes],
) -> Path:
    backup, _ = create_backup(client_root, backup_root, assets)
    try:
        for relative, value in assets.items():
            atomic_write(ensure_inside(client_root / relative, client_root), value)
        verify_installed(client_root, assets)
    except Exception:
        restore_backup(client_root, backup)
        raise
    print(f"Installed and verified {len(assets)} Class Suit IV client assets")
    print(f"Rollback backup: {backup}")
    return backup


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-root", type=Path, default=DEFAULT_CLIENT_ROOT)
    parser.add_argument("--model-stage", type=Path, default=DEFAULT_MODEL_STAGE)
    parser.add_argument("--texture-stage", type=Path, default=DEFAULT_TEXTURE_STAGE)
    parser.add_argument("--backup-root", type=Path, default=DEFAULT_BACKUP_ROOT)
    parser.add_argument(
        "--model-item-id",
        type=int,
        choices=(1035, 1435, 1735, 1835),
        help="Install/check only the four model variants for one Tier IV item",
    )
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--install", action="store_true")
    action.add_argument("--check", action="store_true")
    action.add_argument("--restore", type=Path)
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    try:
        client_root = arguments.client_root.resolve()
        if not client_root.is_dir():
            raise AssetInstallError(f"Client root does not exist: {client_root}")
        if arguments.restore is not None:
            restore_backup(client_root, arguments.restore.resolve())
            return 0
        assets = (
            load_model_assets(arguments.model_stage.resolve(), arguments.model_item_id)
            if arguments.model_item_id is not None
            else load_all_assets(arguments.model_stage, arguments.texture_stage)
        )
        if arguments.check:
            verify_installed(client_root, assets)
            print("Verified 32 installed Class Suit IV client assets")
            return 0
        install(client_root, arguments.backup_root, assets)
        return 0
    except (AssetInstallError, OSError, UnicodeError) as error:
        print(f"ERROR: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
