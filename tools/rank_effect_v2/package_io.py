"""Small, atomic package-writing helpers shared by v2 authoring modules."""

from __future__ import annotations

import json
import os
from pathlib import Path
import tempfile

from rank_effect_packages.baseline import sha256_bytes
from rank_effect_packages.errors import RankEffectError
from rank_effect_packages.package import PACKAGE_SHARD_FORMAT


MAX_JSON_BYTES = 20 * 1024


def json_bytes(value: object) -> bytes:
    return json.dumps(value, indent=2, sort_keys=True).encode("utf-8") + b"\n"


def write(path: Path, value: bytes) -> None:
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


def write_json(path: Path, value: object) -> None:
    data = json_bytes(value)
    if len(data) > MAX_JSON_BYTES:
        raise RankEffectError(f"Generated v2 JSON exceeds 20 KiB: {path}")
    write(path, data)


def regular(path: Path, label: str) -> bytes:
    if not path.is_file() or path.is_symlink():
        raise RankEffectError(f"{label} is not a regular file: {path}")
    return path.read_bytes()


class PackageShard:
    """One bounded effect manifest and its transaction-owned assets."""

    def __init__(self, stage: Path, name: str) -> None:
        self.stage = stage
        self.name = name
        self.files: dict[Path, dict[str, object]] = {}
        self.effects: list[dict[str, object]] = []

    def add(self, target: Path, value: bytes) -> None:
        existing = self.files.get(target)
        if existing is not None:
            if existing["sha256"] != sha256_bytes(value):
                raise RankEffectError(f"Conflicting v2 asset: {target}")
            return
        source = Path("package") / target
        write(self.stage / source, value)
        self.files[target] = {
            "source": source.as_posix(),
            "target": target.as_posix(),
            "sha256": sha256_bytes(value),
        }

    def write_manifest(self) -> str:
        relative = Path("generated") / "manifests" / f"{self.name}.json"
        write_json(
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
