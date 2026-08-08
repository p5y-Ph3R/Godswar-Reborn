"""Snapshot, validate, preflight, install, or restore rank-effect packages."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import tempfile

from rank_effect_packages.baseline import create_baseline, shard_baseline
from rank_effect_packages.catalog import ARMOR_RANKS, WEAPON_EFFECTS
from rank_effect_packages.errors import RankEffectError
from rank_effect_packages.installer import (
    install,
    installation_assets,
    restore_backup,
    verify_installed,
    verify_new_silhouettes,
)
from rank_effect_packages.package import load_package
from rank_effect_packages.safety import require_origin_closed


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PACKAGE_ROOT = REPOSITORY_ROOT / "assets" / "rank-effects"
DEFAULT_CLIENT_ROOT = Path(r"C:\Godswar Origin")
DEFAULT_BACKUP_ROOT = REPOSITORY_ROOT / "backups"


def _atomic_json(path: Path, value: object) -> None:
    data = json.dumps(value, indent=2, sort_keys=True).encode("utf-8") + b"\n"
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", type=Path, default=DEFAULT_PACKAGE_ROOT)
    parser.add_argument("--client-root", type=Path, default=DEFAULT_CLIENT_ROOT)
    parser.add_argument("--backup-root", type=Path, default=DEFAULT_BACKUP_ROOT)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--snapshot-protected", action="store_true")
    action.add_argument("--validate", action="store_true")
    action.add_argument("--preflight", action="store_true")
    action.add_argument("--install", action="store_true")
    action.add_argument("--verify-installed", action="store_true")
    action.add_argument("--restore", type=Path)
    parser.add_argument(
        "--armor-ranks",
        type=int,
        nargs="*",
        choices=ARMOR_RANKS,
        default=list(ARMOR_RANKS),
        help="Snapshot coverage; defaults to AR10..AR14",
    )
    parser.add_argument(
        "--weapon-classes",
        nargs="*",
        choices=tuple(WEAPON_EFFECTS),
        default=list(WEAPON_EFFECTS),
        help="Snapshot coverage; defaults to all four WR10 classes",
    )
    return parser


def main() -> int:
    arguments = _parser().parse_args()
    try:
        package_root = arguments.package_root.resolve()
        client_root = Path(os.path.abspath(arguments.client_root))
        if arguments.snapshot_protected:
            baseline = create_baseline(
                client_root,
                tuple(arguments.armor_ranks),
                tuple(arguments.weapon_classes),
            )
            target = package_root / "generated" / "protected-stock.json"
            main, shards = shard_baseline(baseline)
            _atomic_json(target, main)
            for name, shard in shards.items():
                _atomic_json(target.parent / name, shard)
            print(f"Wrote protected-stock baseline: {target}")
            print(f"Pinned files: {len(baseline['files'])}")
            return 0
        if arguments.restore is not None:
            require_origin_closed(client_root)
            restore_backup(client_root, arguments.restore.resolve())
            print(f"Restored rank-effect backup: {arguments.restore.resolve()}")
            return 0

        package = load_package(package_root)
        if arguments.validate:
            print(
                f"Validated {len(package.effects)} effects and "
                f"{len(package.assets)} install assets"
            )
            return 0
        if arguments.preflight:
            verify_new_silhouettes(package)
            assets = installation_assets(client_root, package)
            print(
                f"Preflight passed for {len(assets)} transactional targets; "
                "the client was not modified"
            )
            return 0
        if arguments.verify_installed:
            verify_installed(client_root, package)
            print("Installed rank-effect package is valid")
            return 0
        backup = install(client_root, arguments.backup_root, package)
        print(f"Installed rank-effect package; rollback backup: {backup}")
        return 0
    except (RankEffectError, OSError, UnicodeError) as error:
        print(f"ERROR: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
