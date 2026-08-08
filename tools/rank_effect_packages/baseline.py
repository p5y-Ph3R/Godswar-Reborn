"""Protected-stock fingerprints for transactional effect installation."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from .catalog import (
    ASSET_ROOTS,
    GENDERS,
    PROTECTED_ARMOR_RANK,
    WEAPON_EFFECTS,
    safe_protected_path,
)
from .errors import RankEffectError
from .formats import extract_texture_references, structural_fingerprint
from .safety import require_plain_path


BASELINE_FORMAT = "reborn-rank-effect-protected-baseline-v1"
BASELINE_SHARD_FORMAT = "reborn-rank-effect-protected-files-v1"
BASELINE_SHARD_SIZE = 24


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _json_object(path: Path) -> dict[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise RankEffectError(f"Could not read JSON {path}: {error}") from error
    if not isinstance(value, dict):
        raise RankEffectError(f"JSON root must be an object: {path}")
    return value


def _coverage(value: object) -> tuple[tuple[int, ...], tuple[str, ...]]:
    if not isinstance(value, dict):
        raise RankEffectError("Protected baseline has no coverage object")
    armor = value.get("armor_ranks", [])
    weapon = value.get("weapon_classes", [])
    if (
        not isinstance(armor, list)
        or not all(isinstance(rank, int) for rank in armor)
        or not isinstance(weapon, list)
        or not all(isinstance(name, str) for name in weapon)
    ):
        raise RankEffectError("Protected baseline coverage is invalid")
    unknown = set(weapon).difference(WEAPON_EFFECTS)
    if unknown:
        raise RankEffectError(f"Unknown classes in protected baseline: {sorted(unknown)}")
    return tuple(sorted(set(armor))), tuple(sorted(set(weapon)))


def _effect_files(client_root: Path, root: str, pattern: str) -> list[Path]:
    directory = client_root / root / "effect"
    if not directory.is_dir():
        raise RankEffectError(f"Client effect directory is missing: {directory}")
    return sorted(directory.glob(pattern), key=lambda value: value.name.lower())


def _add_file(
    files: dict[Path, tuple[str, str]],
    client_root: Path,
    path: Path,
    reason: str,
) -> None:
    resolved = path.resolve()
    try:
        relative = resolved.relative_to(client_root.resolve())
    except ValueError as error:
        raise RankEffectError(f"Protected path escapes the client: {path}") from error
    safe_protected_path(relative.as_posix())
    if not resolved.is_file() or resolved.is_symlink():
        raise RankEffectError(f"Protected client asset is not a regular file: {path}")
    digest = sha256_bytes(resolved.read_bytes())
    previous = files.get(relative)
    if previous is not None and previous[0] != digest:
        raise RankEffectError(f"Protected asset changed during snapshot: {relative}")
    files[relative] = (digest, previous[1] if previous else reason)


def _add_jcs_dependencies(
    files: dict[Path, tuple[str, str]],
    client_root: Path,
    jcs_paths: list[Path],
    unresolved: set[str],
) -> None:
    for jcs in jcs_paths:
        for reference in extract_texture_references(jcs.read_bytes(), str(jcs)):
            try:
                name = reference.decode("ascii")
            except UnicodeDecodeError:
                unresolved.add(f"hex:{reference.hex()}")
                continue
            if Path(name).name != name or not name:
                unresolved.add(name)
                continue
            candidates = [jcs.parent / name, jcs.parent.parent / name]
            if name.lower().endswith(".tga"):
                candidates.extend(
                    (
                        jcs.parent / f"{name[:-4]}.gwo",
                        jcs.parent.parent / f"{name[:-4]}.gwo",
                    )
                )
            existing = next((candidate for candidate in candidates if candidate.is_file()), None)
            if existing is None:
                unresolved.add(name)
                continue
            _add_file(files, client_root, existing, "protected JCS dependency")


def create_baseline(
    client_root: Path,
    armor_ranks: tuple[int, ...],
    weapon_classes: tuple[str, ...],
) -> dict[str, object]:
    """Fingerprint stock ranks that a package promises not to change."""

    if not client_root.is_dir():
        raise RankEffectError(f"Client root does not exist: {client_root}")
    require_plain_path(client_root, client_root, "client source")
    for root in ASSET_ROOTS:
        require_plain_path(
            client_root,
            client_root / root / "effect",
            "client effect source",
        )
    client_root = client_root.resolve()
    if armor_ranks and any(rank <= PROTECTED_ARMOR_RANK for rank in armor_ranks):
        raise RankEffectError("Authored armor coverage must start above protected AR9")
    unknown = set(weapon_classes).difference(WEAPON_EFFECTS)
    if unknown:
        raise RankEffectError(f"Unknown weapon classes: {sorted(unknown)}")

    files: dict[Path, tuple[str, str]] = {}
    unresolved: set[str] = set()
    if armor_ranks:
        for root in ASSET_ROOTS:
            for gender in GENDERS:
                pattern = f"{gender}_body_effect_{PROTECTED_ARMOR_RANK:04d}*"
                protected = _effect_files(client_root, root, pattern)
                if not protected:
                    raise RankEffectError(f"Protected AR9 files are missing: {root}/{pattern}")
                for path in protected:
                    _add_file(files, client_root, path, "protected AR9 asset")
                _add_jcs_dependencies(
                    files,
                    client_root,
                    [path for path in protected if path.suffix.lower() == ".jcs"],
                    unresolved,
                )

    for class_name in sorted(set(weapon_classes)):
        spec = WEAPON_EFFECTS[class_name]
        for root in ASSET_ROOTS:
            for gender in GENDERS:
                protected_jcs: list[Path] = []
                for effect_id in spec.protected_effect_ids:
                    pattern = f"{gender}_{spec.family}_effect_{effect_id:04d}*"
                    protected = _effect_files(client_root, root, pattern)
                    if not protected:
                        raise RankEffectError(
                            f"Protected WR asset family is missing: {root}/{pattern}"
                        )
                    for path in protected:
                        _add_file(files, client_root, path, f"protected {class_name} WR1-9")
                    protected_jcs.extend(
                        path for path in protected if path.suffix.lower() == ".jcs"
                    )
                _add_jcs_dependencies(
                    files, client_root, protected_jcs, unresolved
                )

    entries = [
        {
            "path": path.as_posix(),
            "sha256": digest,
            **(
                {
                    "structural_sha256": structural_fingerprint(
                        (client_root / path).read_bytes(), path.as_posix()
                    )
                }
                if path.suffix.lower() == ".jcs"
                else {}
            ),
        }
        for path, (digest, reason) in sorted(
            files.items(), key=lambda item: item[0].as_posix().lower()
        )
    ]
    return {
        "format": BASELINE_FORMAT,
        "coverage": {
            "armor_ranks": sorted(set(armor_ranks)),
            "weapon_classes": sorted(set(weapon_classes)),
        },
        "files": entries,
        "unresolved_texture_references": sorted(unresolved),
    }


def shard_baseline(
    baseline: dict[str, object],
) -> tuple[dict[str, object], dict[str, dict[str, object]]]:
    """Split a baseline into reviewable files below the repository size limit."""

    entries = baseline.get("files")
    if not isinstance(entries, list) or not entries:
        raise RankEffectError("Cannot shard an empty protected baseline")
    shards: dict[str, dict[str, object]] = {}
    names: list[str] = []
    for offset in range(0, len(entries), BASELINE_SHARD_SIZE):
        number = offset // BASELINE_SHARD_SIZE + 1
        name = f"protected-stock-files-{number:02d}.json"
        names.append(name)
        shards[name] = {
            "format": BASELINE_SHARD_FORMAT,
            "files": entries[offset : offset + BASELINE_SHARD_SIZE],
        }
    main = dict(baseline)
    main.pop("files", None)
    main["file_manifests"] = names
    return main, shards


def load_baseline(path: Path) -> dict[str, object]:
    baseline = _json_object(path)
    if baseline.get("format") != BASELINE_FORMAT:
        raise RankEffectError("Unexpected protected-stock baseline format")
    _coverage(baseline.get("coverage"))
    entries = baseline.get("files")
    shard_names = baseline.get("file_manifests")
    if entries is not None and shard_names is not None:
        raise RankEffectError("Protected baseline cannot mix inline files and shards")
    if shard_names is not None:
        if (
            not isinstance(shard_names, list)
            or not 1 <= len(shard_names) <= 32
            or not all(isinstance(name, str) for name in shard_names)
            or len(set(shard_names)) != len(shard_names)
        ):
            raise RankEffectError("Protected baseline shard list is invalid")
        entries = []
        for name in shard_names:
            relative = Path(name.replace("\\", "/"))
            if relative.is_absolute() or ".." in relative.parts:
                raise RankEffectError(f"Protected baseline shard escapes: {name}")
            shard_path = (path.parent / relative).resolve()
            try:
                shard_path.relative_to(path.parent.resolve())
            except ValueError as error:
                raise RankEffectError(f"Protected baseline shard escapes: {name}") from error
            if not shard_path.is_file() or shard_path.is_symlink():
                raise RankEffectError(f"Protected baseline shard is unsafe: {shard_path}")
            shard = _json_object(shard_path)
            if shard.get("format") != BASELINE_SHARD_FORMAT:
                raise RankEffectError(f"Unexpected baseline shard format: {shard_path}")
            shard_entries = shard.get("files")
            if not isinstance(shard_entries, list) or not shard_entries:
                raise RankEffectError(f"Protected baseline shard is empty: {shard_path}")
            entries.extend(shard_entries)
        baseline = dict(baseline)
        baseline["files"] = entries
    if not isinstance(entries, list) or not entries:
        raise RankEffectError("Protected-stock baseline must contain files")
    seen: set[Path] = set()
    for entry in entries:
        if not isinstance(entry, dict):
            raise RankEffectError("Protected-stock baseline contains a bad entry")
        relative = safe_protected_path(entry.get("path"))
        digest = entry.get("sha256")
        if (
            relative in seen
            or not isinstance(digest, str)
            or len(digest) != 64
            or any(character not in "0123456789abcdef" for character in digest)
        ):
            raise RankEffectError(f"Invalid protected-stock entry: {relative}")
        structural = entry.get("structural_sha256")
        if relative.suffix.lower() == ".jcs" and (
            not isinstance(structural, str)
            or len(structural) != 64
            or any(character not in "0123456789abcdef" for character in structural)
        ):
            raise RankEffectError(f"Protected JCS has no structural hash: {relative}")
        seen.add(relative)
    unresolved = baseline.get("unresolved_texture_references")
    if not isinstance(unresolved, list) or not all(isinstance(x, str) for x in unresolved):
        raise RankEffectError("Protected-stock unresolved-reference list is invalid")
    return baseline


def verify_baseline(
    client_root: Path,
    baseline: dict[str, object],
) -> None:
    mismatches: list[str] = []
    entries = baseline["files"]
    assert isinstance(entries, list)
    for entry in entries:
        assert isinstance(entry, dict)
        relative = safe_protected_path(entry["path"])
        target = (client_root / relative).resolve()
        try:
            target.relative_to(client_root.resolve())
        except ValueError as error:
            raise RankEffectError(f"Protected path escapes client: {relative}") from error
        actual = sha256_bytes(target.read_bytes()) if target.is_file() else None
        if actual != entry["sha256"]:
            mismatches.append(relative.as_posix())
    if mismatches:
        raise RankEffectError(
            "Protected AR9/WR1-9 assets differ from the package baseline: "
            + ", ".join(sorted(mismatches))
        )


def baseline_coverage(baseline: dict[str, object]) -> tuple[tuple[int, ...], tuple[str, ...]]:
    return _coverage(baseline.get("coverage"))
