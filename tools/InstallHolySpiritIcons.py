from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile

from level5_forge_icons.common import (
    InstallError,
    Sprite,
    SpriteSpec,
)
from level5_forge_icons.png_assets import read_png_rgba
from level5_forge_icons.tga_atlas import (
    make_desired_pixels,
    parse_tga,
    patch_atlas,
    target_differs_only_in_owned_cells,
    validate_generated_atlas,
)


LOCALES = ("en_us", "zh_cn")
ATLAS_RELATIVE_DIRECTORY = Path("UI") / "Texture"
EXPECTED_SOURCE_ATLAS_SHA256 = (
    "3c27e65ddc369728137050006f84b535f"
    "a68686ad1cdff7b3f495d24197c7d29"
)


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def ensure_within(root: Path, path: Path, label: str) -> None:
    try:
        path.resolve().relative_to(root.resolve())
    except ValueError as error:
        raise InstallError(f"{label} is outside the client root: {path}") from error


def load_manifest(asset_root: Path) -> dict[str, object]:
    manifest_path = asset_root / "manifest.json"
    if not manifest_path.is_file():
        raise InstallError(f"Holy Spirit icon manifest is missing: {manifest_path}")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as error:
        raise InstallError(f"Cannot read {manifest_path}: {error}") from error

    if manifest.get("schema_version") != 1:
        raise InstallError("Unsupported Holy Spirit icon manifest version")
    if manifest.get("source_atlas") != "Icon3.gwo":
        raise InstallError("Holy Spirit icons must use verified Icon3.gwo as source")
    if manifest.get("target_atlas") != "Icon5.gwo":
        raise InstallError("Holy Spirit icons must use dedicated Icon5.gwo")
    if manifest.get("sprite_width") != 36 or manifest.get("sprite_height") != 36:
        raise InstallError("Holy Spirit client sprites must be 36x36")
    entries = manifest.get("entries")
    if not isinstance(entries, list) or len(entries) != 17:
        raise InstallError("The Holy Spirit icon manifest must contain 17 entries")
    return manifest


def load_sprites(
    asset_root: Path,
    manifest: dict[str, object],
) -> tuple[tuple[Sprite, ...], list[dict[str, object]]]:
    source_root = asset_root / "source"
    icon_root = asset_root / "icons"
    sprites: list[Sprite] = []
    normalized_entries: list[dict[str, object]] = []
    slugs: set[str] = set()
    coordinates: set[tuple[int, int]] = set()

    for raw_entry in manifest["entries"]:  # type: ignore[index]
        if not isinstance(raw_entry, dict):
            raise InstallError("Holy Spirit icon entry is not an object")
        entry = dict(raw_entry)
        slug = entry.get("slug")
        x = entry.get("x")
        y = entry.get("y")
        source_hash = entry.get("source_sha256")
        if not isinstance(slug, str) or not slug:
            raise InstallError("Holy Spirit icon entry has no slug")
        if slug in slugs:
            raise InstallError(f"Duplicate Holy Spirit icon slug: {slug}")
        slugs.add(slug)
        if not isinstance(x, int) or not isinstance(y, int):
            raise InstallError(f"Invalid atlas coordinate for {slug}")
        if x < 0 or y < 0 or x % 36 or y % 36:
            raise InstallError(f"Unaligned atlas coordinate for {slug}: {x},{y}")
        if x + 36 > 1024 or y + 36 > 1024:
            raise InstallError(f"Atlas coordinate is outside Icon5.gwo: {slug}")
        if (x, y) in coordinates:
            raise InstallError(f"Duplicate atlas coordinate: {x},{y}")
        coordinates.add((x, y))

        source_path = source_root / f"{slug}.png"
        if not source_path.is_file():
            raise InstallError(f"Holy Spirit source image is missing: {source_path}")
        if not isinstance(source_hash, str) or sha256_file(source_path) != source_hash:
            raise InstallError(f"Holy Spirit source SHA256 mismatch: {source_path}")

        icon_path = icon_root / f"{slug}-36.png"
        if not icon_path.is_file():
            raise InstallError(f"Prepared Holy Spirit icon is missing: {icon_path}")
        width, height, rgba = read_png_rgba(icon_path)
        if width != 36 or height != 36:
            raise InstallError(f"Prepared Holy Spirit icon is not 36x36: {icon_path}")

        bgra = bytearray(len(rgba))
        for position in range(0, len(rgba), 4):
            red, green, blue, alpha = rgba[position : position + 4]
            bgra[position : position + 4] = bytes((blue, green, red, alpha))

        spec = SpriteSpec(icon_path.name, x, y)
        sprites.append(Sprite(spec, bytes(bgra), sha256_file(icon_path)))
        entry["prepared_icon"] = str(icon_path.relative_to(asset_root))
        entry["prepared_sha256"] = sprites[-1].sha256
        normalized_entries.append(entry)

    return tuple(sprites), normalized_entries


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


def restore_target(path: Path, original: bytes | None) -> None:
    if original is None:
        path.unlink(missing_ok=True)
        return
    prepared = write_prepared_file(path, original)
    os.replace(prepared, path)


def create_backup_directory(backup_root: Path) -> Path:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    backup_directory = backup_root / f"client-holy-spirit-icons-{timestamp}"
    backup_directory.mkdir(parents=True, exist_ok=False)
    return backup_directory


def install(args: argparse.Namespace) -> int:
    repository_root = Path(__file__).resolve().parents[1]
    client_root = Path(args.client_root).resolve()
    asset_root = Path(
        args.asset_root or repository_root / "assets" / "holy-spirits"
    ).resolve()
    backup_root = Path(args.backup_root or repository_root / "backups").resolve()

    if args.check and args.dry_run:
        raise InstallError("--check and --dry-run cannot be used together")
    if not client_root.is_dir():
        raise InstallError(f"Game client root does not exist: {client_root}")
    if not asset_root.is_dir():
        raise InstallError(f"Holy Spirit asset root does not exist: {asset_root}")

    manifest = load_manifest(asset_root)
    sprites, normalized_entries = load_sprites(asset_root, manifest)
    source_name = str(manifest["source_atlas"])
    target_name = str(manifest["target_atlas"])

    source_paths: dict[str, Path] = {}
    target_paths: dict[str, Path] = {}
    source_data: dict[str, bytes] = {}
    for locale in LOCALES:
        texture_root = client_root / "Localization" / locale / ATLAS_RELATIVE_DIRECTORY
        source_path = texture_root / source_name
        target_path = texture_root / target_name
        ensure_within(client_root, source_path, f"{locale} source atlas")
        ensure_within(client_root, target_path, f"{locale} target atlas")
        if not source_path.is_file():
            raise InstallError(f"Verified source atlas is missing: {source_path}")
        source_paths[locale] = source_path
        target_paths[locale] = target_path
        source_data[locale] = source_path.read_bytes()

    if source_data[LOCALES[0]] != source_data[LOCALES[1]]:
        raise InstallError("Locale copies of Icon3.gwo are not byte-identical")
    source_hash = sha256_bytes(source_data[LOCALES[0]])
    if source_hash != EXPECTED_SOURCE_ATLAS_SHA256:
        raise InstallError(
            f"Unexpected Icon3.gwo SHA256: {source_hash}; "
            f"expected {EXPECTED_SOURCE_ATLAS_SHA256}"
        )

    base = parse_tga(source_data[LOCALES[0]], str(source_paths[LOCALES[0]]))
    desired, target_indices = make_desired_pixels(base, sprites)
    expected_data = patch_atlas(base, desired)
    validate_generated_atlas(base, expected_data, desired, "prepared Icon5.gwo")
    expected_hash = sha256_bytes(expected_data)

    original_data: dict[str, bytes | None] = {}
    changed_locales: list[str] = []
    for locale in LOCALES:
        target_path = target_paths[locale]
        current = target_path.read_bytes() if target_path.exists() else None
        original_data[locale] = current
        if current == expected_data:
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
                    f"Refusing to replace unexpected {target_path}: {reason}. "
                    "Use --force only after inspecting that atlas."
                )

    if args.check:
        if changed_locales:
            raise InstallError(
                "Icon5.gwo is not installed exactly in locale(s): "
                + ", ".join(changed_locales)
            )
        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon5.gwo locale files are not byte-identical")
        print(f"Verified Icon5.gwo: {expected_hash}")
        return 0

    print("Locale atlas changes required: " + (", ".join(changed_locales) or "none"))
    print(f"Prepared Icon5.gwo SHA256: {expected_hash}")
    if args.dry_run or not changed_locales:
        print("Dry run complete; no files were written." if args.dry_run else "No changes needed.")
        return 0

    prepared_paths: dict[str, Path] = {}
    replaced_locales: list[str] = []
    backup_directory: Path | None = None
    try:
        for locale in changed_locales:
            prepared = write_prepared_file(target_paths[locale], expected_data)
            validate_generated_atlas(
                base,
                prepared.read_bytes(),
                desired,
                f"prepared {locale} Icon5.gwo",
            )
            prepared_paths[locale] = prepared

        backup_directory = create_backup_directory(backup_root)
        backup_manifest: dict[str, object] = {
            "created_at": datetime.now().astimezone().isoformat(),
            "client_root": str(client_root),
            "source_atlas": source_name,
            "source_sha256": source_hash,
            "target_atlas": target_name,
            "target_sha256": expected_hash,
            "entries": normalized_entries,
            "locales": {},
        }
        locale_records: dict[str, object] = {}
        for locale in LOCALES:
            current = original_data[locale]
            record: dict[str, object] = {
                "changed": locale in changed_locales,
                "original_sha256": sha256_bytes(current) if current is not None else None,
                "installed_sha256": expected_hash,
                "backup": None,
            }
            if locale in changed_locales and current is not None:
                relative_target = target_paths[locale].relative_to(client_root)
                backup_path = backup_directory / relative_target
                backup_path.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(target_paths[locale], backup_path)
                if backup_path.read_bytes() != current:
                    raise InstallError(f"Atlas backup verification failed: {backup_path}")
                record["backup"] = str(backup_path.relative_to(backup_directory))
            locale_records[locale] = record
        backup_manifest["locales"] = locale_records
        (backup_directory / "manifest.json").write_text(
            json.dumps(backup_manifest, indent=2) + "\n",
            encoding="utf-8",
        )

        for locale in changed_locales:
            os.replace(prepared_paths[locale], target_paths[locale])
            replaced_locales.append(locale)

        for locale in LOCALES:
            installed = target_paths[locale].read_bytes()
            if installed != expected_data:
                raise InstallError(f"Post-install validation failed for {locale}")
            validate_generated_atlas(
                base,
                installed,
                desired,
                f"installed {locale} Icon5.gwo",
            )
        if target_paths[LOCALES[0]].read_bytes() != target_paths[LOCALES[1]].read_bytes():
            raise InstallError("Installed Icon5.gwo locale files are not byte-identical")
    except Exception:
        for locale in reversed(replaced_locales):
            restore_target(target_paths[locale], original_data[locale])
        raise
    finally:
        for prepared in prepared_paths.values():
            prepared.unlink(missing_ok=True)

    print(f"Installed Icon5.gwo in: {', '.join(changed_locales)}")
    print(f"Backup manifest: {backup_directory / 'manifest.json'}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="Prepare or verify the dedicated Holy Spirit Icon5.gwo atlas."
    )
    parser.add_argument(
        "--client-root",
        default=r"C:\Godswar Origin",
        help=r"Game client root (default: C:\Godswar Origin)",
    )
    parser.add_argument(
        "--asset-root",
        default=str(repository_root / "assets" / "holy-spirits"),
    )
    parser.add_argument(
        "--backup-root",
        default=str(repository_root / "backups"),
    )
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--check", action="store_true")
    parser.add_argument(
        "--force",
        action="store_true",
        help="Replace an unexpected existing Icon5.gwo after manual inspection.",
    )
    return parser


def main() -> int:
    try:
        return install(build_parser().parse_args())
    except (InstallError, OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
