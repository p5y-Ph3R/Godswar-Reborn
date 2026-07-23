from __future__ import annotations

import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import tempfile

from .client_patching import build_outputs
from .common import InstallError, sha256_bytes
from .constants import (
    EXPECTED_TARGET_MODEL_SHA256,
    ITEM_BASE_ID,
    ITEM_COUNT,
    MODEL_UNIFORM_SCALE,
    MOUNT_NAME,
    RIDE_SECTION_ID,
    RIDE_STATUS_ID,
)


def write_atomic(path: Path, data: bytes) -> None:
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
            raise InstallError(
                f"Temporary write validation failed: {temporary_path}"
            )
        os.replace(temporary_path, path)
    finally:
        temporary_path.unlink(missing_ok=True)


def write_previews(
    preview_root: Path,
    previews: dict[str, bytes],
) -> None:
    preview_root.mkdir(parents=True, exist_ok=True)
    for name, data in previews.items():
        path = preview_root / name
        write_atomic(path, data)
        print(f"Preview: {path}")


def ensure_within(root: Path, path: Path, label: str) -> None:
    try:
        path.resolve().relative_to(root.resolve())
    except ValueError as error:
        raise InstallError(
            f"{label} escapes its allowed root: {path}"
        ) from error


def install(args: argparse.Namespace) -> int:
    client_root = Path(args.client_root).resolve()
    backup_root = Path(args.backup_root).resolve()
    preview_root = (
        Path(args.preview_dir).resolve()
        if args.preview_dir
        else None
    )
    if not client_root.is_dir():
        raise InstallError(
            f"Client root does not exist: {client_root}"
        )

    outputs, previews = build_outputs(client_root)
    if preview_root is not None:
        write_previews(preview_root, previews)
    if args.preview_only:
        print(
            "Preview-only validation completed; "
            "client files were not changed."
        )
        return 0

    changed = [
        path
        for path, expected in outputs.items()
        if not path.exists() or path.read_bytes() != expected
    ]
    if args.check:
        if changed:
            raise InstallError(
                "Erebus Lion client patch is not installed: "
                + ", ".join(str(path) for path in changed)
            )
        print(
            f"Verified {MOUNT_NAME}: items "
            f"{ITEM_BASE_ID}-{ITEM_BASE_ID + ITEM_COUNT - 1}, "
            f"status {RIDE_STATUS_ID}, ride section {RIDE_SECTION_ID}."
        )
        print(
            f"Proportional {MODEL_UNIFORM_SCALE:.2f}x model SHA256: "
            f"{EXPECTED_TARGET_MODEL_SHA256}"
        )
        return 0

    backup_path: Path | None = None
    if changed:
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        backup_path = (
            backup_root / f"erebus-lion-mount-{timestamp}"
        )
        backup_path.mkdir(parents=True, exist_ok=False)
        manifest: dict[str, str | None] = {}
        for path in changed:
            ensure_within(client_root, path, "Client output")
            relative = path.resolve().relative_to(client_root)
            if path.exists():
                destination = backup_path / relative
                destination.parent.mkdir(
                    parents=True,
                    exist_ok=True,
                )
                shutil.copy2(path, destination)
                manifest[str(relative)] = sha256_bytes(
                    path.read_bytes()
                )
            else:
                manifest[str(relative)] = None
        write_atomic(
            backup_path / "manifest.json",
            json.dumps(
                manifest,
                indent=2,
                sort_keys=True,
            ).encode("utf-8")
            + b"\n",
        )

        for path in changed:
            write_atomic(path, outputs[path])

    # Rebuild expected content from the installed state to prove the transform
    # is idempotent and all encodings/assets remain parseable.
    verified_outputs, _ = build_outputs(client_root)
    mismatches = [
        path
        for path, expected in verified_outputs.items()
        if not path.exists() or path.read_bytes() != expected
    ]
    if mismatches:
        raise InstallError(
            "Post-install validation failed: "
            + ", ".join(str(path) for path in mismatches)
        )

    state = "Installed" if changed else "Already installed"
    print(
        f"{state} {MOUNT_NAME}: items "
        f"{ITEM_BASE_ID}-{ITEM_BASE_ID + ITEM_COUNT - 1}, "
        f"status {RIDE_STATUS_ID}, ride section {RIDE_SECTION_ID}."
    )
    print(
        f"Proportional model scale: {MODEL_UNIFORM_SCALE:.2f}x "
        f"(SHA256 {EXPECTED_TARGET_MODEL_SHA256})"
    )
    print(f"Changed files: {len(changed)}")
    if backup_path is not None:
        print(f"Backup: {backup_path}")
    return 0
