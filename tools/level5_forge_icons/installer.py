from __future__ import annotations

import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import tempfile

from .common import (
    ATLAS_RELATIVE_DIRECTORY,
    InstallError,
    LOCALES,
    SOURCE_ATLAS_NAME,
    SPRITE_HEIGHT,
    SPRITE_WIDTH,
    TARGET_ATLAS_NAME,
    sha256_bytes,
)
from .png_assets import load_sprites
from .tga_atlas import (
    make_desired_pixels,
    parse_tga,
    patch_atlas,
    target_differs_only_in_owned_cells,
    validate_generated_atlas,
)

def ensure_within(root: Path, path: Path, label: str) -> None:
    try:
        path.resolve().relative_to(root.resolve())
    except ValueError as error:
        raise InstallError(f"{label} is outside the client root: {path}") from error


def write_prepared_file(path: Path, data: bytes) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.",
        suffix=".tmp",
        dir=path.parent,
    )
    temporary_path = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        if temporary_path.read_bytes() != data:
            raise InstallError(f"Prepared file verification failed: {temporary_path}")
        return temporary_path
    except Exception:
        temporary_path.unlink(missing_ok=True)
        raise


def create_backup_directory(backup_root: Path) -> Path:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    backup_directory = backup_root / f"client-level5-forge-icons-{timestamp}"
    backup_directory.mkdir(parents=True, exist_ok=False)
    return backup_directory


def install(args: argparse.Namespace) -> int:
    script_root = Path(__file__).resolve().parents[2]
    client_root = Path(args.client_root).resolve()
    asset_root = Path(args.asset_root or script_root / "assets" / "forging" / "level5").resolve()
    backup_root = Path(args.backup_root or script_root / "backups").resolve()

    if args.check and args.dry_run:
        raise InstallError("--check and --dry-run cannot be used together")
    if not client_root.is_dir():
        raise InstallError(f"Client root does not exist: {client_root}")

    sprites = load_sprites(asset_root)
    source_paths: dict[str, Path] = {}
    target_paths: dict[str, Path] = {}
    source_data: dict[str, bytes] = {}

    for locale in LOCALES:
        texture_root = client_root / "Localization" / locale / ATLAS_RELATIVE_DIRECTORY
        source_path = texture_root / SOURCE_ATLAS_NAME
        target_path = texture_root / TARGET_ATLAS_NAME
        ensure_within(client_root, source_path, f"{locale} source atlas")
        ensure_within(client_root, target_path, f"{locale} target atlas")
        if not source_path.is_file():
            raise InstallError(f"Source atlas is missing: {source_path}")
        source_paths[locale] = source_path
        target_paths[locale] = target_path
        source_data[locale] = source_path.read_bytes()

    if source_data[LOCALES[0]] != source_data[LOCALES[1]]:
        raise InstallError("The en_us and zh_cn Icon3.gwo source atlases are not byte-identical")

    base = parse_tga(source_data[LOCALES[0]], str(source_paths[LOCALES[0]]))
    desired, target_indices = make_desired_pixels(base, sprites)
    expected_data = patch_atlas(base, desired)
    validate_generated_atlas(base, expected_data, desired, "prepared Icon4.gwo")

    expected_by_locale = {locale: expected_data for locale in LOCALES}
    if expected_by_locale[LOCALES[0]] != expected_by_locale[LOCALES[1]]:
        raise InstallError("Prepared locale atlases are not byte-identical")

    original_data: dict[str, bytes | None] = {}
    changed_locales: list[str] = []
    for locale in LOCALES:
        target_path = target_paths[locale]
        current = target_path.read_bytes() if target_path.exists() else None
        original_data[locale] = current
        if current == expected_by_locale[locale]:
            continue
        changed_locales.append(locale)
        if current is not None and not args.force:
            safe, reason = target_differs_only_in_owned_cells(
                base,
                current,
                target_indices,
                str(target_path),
            )
            if not safe:
                raise InstallError(
                    f"Refusing to replace unexpected existing atlas {target_path}: {reason}. "
                    "Use --force only after inspecting and backing up that file."
                )

    if args.check:
        if changed_locales:
            raise InstallError(
                "Level-5 icon atlas is not installed exactly in locale(s): "
                + ", ".join(changed_locales)
            )
        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon4.gwo locale files are not byte-identical")
        print(f"Verified {TARGET_ATLAS_NAME}: {sha256_bytes(expected_data)}")
        return 0

    if not changed_locales:
        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon4.gwo locale files are not byte-identical")
        print(f"No changes needed; {TARGET_ATLAS_NAME} is already installed and verified.")
        return 0

    print("Locale atlas changes required: " + ", ".join(changed_locales))
    print(f"Prepared SHA256: {sha256_bytes(expected_data)}")
    if args.dry_run:
        print("Dry run complete; no files or backups were written.")
        return 0

    prepared_paths: dict[str, Path] = {}
    backup_directory: Path | None = None
    replaced_locales: list[str] = []
    try:
        for locale in changed_locales:
            prepared = write_prepared_file(target_paths[locale], expected_by_locale[locale])
            validate_generated_atlas(
                base,
                prepared.read_bytes(),
                desired,
                f"prepared {locale} {TARGET_ATLAS_NAME}",
            )
            prepared_paths[locale] = prepared

        backup_directory = create_backup_directory(backup_root)
        manifest: dict[str, object] = {
            "created_at": datetime.now().astimezone().isoformat(),
            "client_root": str(client_root),
            "source_atlas": SOURCE_ATLAS_NAME,
            "target_atlas": TARGET_ATLAS_NAME,
            "source_sha256": sha256_bytes(source_data[LOCALES[0]]),
            "target_sha256": sha256_bytes(expected_data),
            "sprites": [
                {
                    "file": sprite.spec.filename,
                    "sha256": sprite.sha256,
                    "x": sprite.spec.x,
                    "y": sprite.spec.y,
                    "width": SPRITE_WIDTH,
                    "height": SPRITE_HEIGHT,
                }
                for sprite in sprites
            ],
            "locales": {},
        }

        locale_manifest: dict[str, object] = {}
        for locale in LOCALES:
            current = original_data[locale]
            entry: dict[str, object] = {
                "changed": locale in changed_locales,
                "original_sha256": sha256_bytes(current) if current is not None else None,
                "installed_sha256": sha256_bytes(expected_by_locale[locale]),
            }
            if locale in changed_locales and current is not None:
                relative_target = target_paths[locale].relative_to(client_root)
                backup_path = backup_directory / relative_target
                backup_path.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(target_paths[locale], backup_path)
                if backup_path.read_bytes() != current:
                    raise InstallError(f"Backup verification failed: {backup_path}")
                entry["backup"] = str(backup_path.relative_to(backup_directory))
            else:
                entry["backup"] = None
            locale_manifest[locale] = entry
        manifest["locales"] = locale_manifest

        manifest_path = backup_directory / "manifest.json"
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

        for locale in changed_locales:
            os.replace(prepared_paths[locale], target_paths[locale])
            replaced_locales.append(locale)

        for locale in LOCALES:
            installed_data = target_paths[locale].read_bytes()
            if installed_data != expected_by_locale[locale]:
                raise InstallError(f"Post-install byte validation failed for {locale}")
            validate_generated_atlas(
                base,
                installed_data,
                desired,
                f"installed {locale} {TARGET_ATLAS_NAME}",
            )

        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon4.gwo locale files are not byte-identical")

    except Exception:
        for locale in reversed(replaced_locales):
            target_path = target_paths[locale]
            original = original_data[locale]
            if original is None:
                target_path.unlink(missing_ok=True)
            else:
                rollback_path = write_prepared_file(target_path, original)
                os.replace(rollback_path, target_path)
        raise
    finally:
        for prepared_path in prepared_paths.values():
            prepared_path.unlink(missing_ok=True)

    print(f"Installed byte-identical locale atlases: {sha256_bytes(expected_data)}")
    print(f"Backup manifest: {backup_directory / 'manifest.json'}")
    return 0
