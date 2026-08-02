"""Generate topology-safe Class Suit IV weapon models into a staging folder.

This tool never writes into the game client.  It reads the Tier III weapon
models, applies the reviewed Tier IV PCA silhouette profiles, validates that
only positions and normals changed, and emits inspectable SVG comparisons.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import sys

from xmodel_sculpt.binary_x import XModelError
from xmodel_sculpt.preview import comparison_svg
from xmodel_sculpt.pinned_hashes import PINNED_MODEL_HASHES
from xmodel_sculpt.profiles import PROFILES, profile_transform
from xmodel_sculpt.sculpt import immutable_sha256, sculpt_xof_mszip


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = REPOSITORY_ROOT / "artifacts" / "class-suit-iv-weapon-models"
RENDER_TREES = ("Characters", "Characters_New")
SEXES = ("female", "male")


@dataclass(frozen=True, slots=True)
class WeaponSpec:
    item_id: int
    class_name: str
    source_stem: str
    target_stem: str


WEAPONS = (
    WeaponSpec(1035, "Warrior", "weapononehand_1034_right", "weapononehand_1035_right"),
    WeaponSpec(1435, "Champion", "weapontwohand_1434", "weapontwohand_1435"),
    WeaponSpec(1735, "Priest", "weapononehand_1734_right", "weapononehand_1735_right"),
    WeaponSpec(1835, "Mage", "weapontwohand_1834", "weapontwohand_1835"),
)


def _inside(path: Path, parent: Path) -> bool:
    try:
        path.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _atomic_write(path: Path, value: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(value)
    temporary.replace(path)


def _build(client_root: Path) -> tuple[list[tuple[Path, bytes]], dict[str, object]]:
    files: list[tuple[Path, bytes]] = []
    entries: list[dict[str, object]] = []
    for tree in RENDER_TREES:
        for sex in SEXES:
            for spec in WEAPONS:
                source_relative = Path(tree) / f"{sex}_{spec.source_stem}.jcs"
                target_relative = Path(tree) / f"{sex}_{spec.target_stem}.jcs"
                source_path = client_root / source_relative
                if not source_path.is_file():
                    raise XModelError(f"Required Tier III source is missing: {source_path}")
                source = source_path.read_bytes()
                source_key = source_relative.as_posix()
                try:
                    expected_source_hash, expected_target_hash = PINNED_MODEL_HASHES[
                        source_key
                    ]
                except KeyError as error:
                    raise XModelError(f"No reviewed hash pair for {source_key}") from error
                actual_source_hash = _sha256(source)
                if actual_source_hash != expected_source_hash:
                    raise XModelError(
                        f"Tier III source hash is not reviewed: {source_key} "
                        f"({actual_source_hash})"
                    )
                result = sculpt_xof_mszip(
                    source,
                    profile_transform(spec.item_id),
                    label=source_path.name,
                )
                repeated = sculpt_xof_mszip(
                    source,
                    profile_transform(spec.item_id),
                    label=source_path.name,
                )
                if result.encoded != repeated.encoded:
                    raise XModelError(f"Sculpt is not deterministic: {source_relative}")
                if result.changed_vertices == 0:
                    raise XModelError(f"Sculpt did not change any vertices: {source_relative}")
                if len(result.before) != 1 or len(result.after) != 1:
                    raise XModelError(f"Expected exactly one Mesh: {source_relative}")
                if result.result_sha256 != expected_target_hash:
                    raise XModelError(
                        f"Tier IV output hash changed unexpectedly: {target_relative} "
                        f"({result.result_sha256})"
                    )
                if immutable_sha256(
                    result.expanded,
                    result.after,
                ) != immutable_sha256(repeated.expanded, repeated.after):
                    raise XModelError(f"Immutable model digest is unstable: {source_relative}")

                preview_relative = (
                    Path("previews")
                    / f"{tree}_{sex}_{spec.item_id}_{PROFILES[spec.item_id].name}.svg"
                )
                preview = comparison_svg(
                    result.before,
                    result.after,
                    f"{spec.class_name} Class Suit IV — item {spec.item_id} — {tree}/{sex}",
                ).encode("utf-8")
                files.extend(((target_relative, result.encoded), (preview_relative, preview)))
                entries.append(
                    {
                        "class": spec.class_name,
                        "item_id": spec.item_id,
                        "profile": PROFILES[spec.item_id].name,
                        "source": source_relative.as_posix(),
                        "target": target_relative.as_posix(),
                        "preview": preview_relative.as_posix(),
                        "source_sha256": actual_source_hash,
                        "target_sha256": result.result_sha256,
                        "preview_sha256": _sha256(preview),
                        "changed_vertices": result.changed_vertices,
                        "mesh_count": len(result.before),
                        "vertex_count": sum(len(mesh.vertices) for mesh in result.before),
                        "face_count": sum(len(mesh.faces) for mesh in result.before),
                    }
                )
    manifest: dict[str, object] = {
        "format": "reborn-class-suit-iv-weapon-models-v1",
        "installation_performed": False,
        "models": entries,
    }
    return files, manifest


def _manifest_bytes(manifest: dict[str, object]) -> bytes:
    return (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode("utf-8")


def run(client_root: Path, output_dir: Path, check: bool) -> int:
    client_root = client_root.resolve()
    output_dir = output_dir.resolve()
    if not client_root.is_dir():
        raise XModelError(f"Client root does not exist: {client_root}")
    if _inside(output_dir, client_root):
        raise XModelError("Output directory must not be inside the game client")
    files, manifest = _build(client_root)
    files.append((Path("manifest.json"), _manifest_bytes(manifest)))

    if check:
        errors: list[str] = []
        for relative, expected in files:
            candidate = output_dir / relative
            if not candidate.is_file():
                errors.append(f"missing {relative.as_posix()}")
            elif candidate.read_bytes() != expected:
                errors.append(f"does not match {relative.as_posix()}")
        if errors:
            raise XModelError("Staging verification failed: " + "; ".join(errors))
        print(f"Verified {len(files) - 1} staged models/previews and manifest")
        return 0

    for relative, value in files:
        _atomic_write(output_dir / relative, value)
    model_count = len(manifest["models"])  # type: ignore[arg-type]
    print(f"Generated {model_count} validated Tier IV models in {output_dir}")
    print("No game-client files were changed.")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-root", type=Path, default=Path(r"C:\Godswar Origin"))
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true")
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    try:
        return run(arguments.client_root, arguments.output_dir, arguments.check)
    except (OSError, ValueError, XModelError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
