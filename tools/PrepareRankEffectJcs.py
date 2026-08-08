"""Rewrite authored JCS texture strings into private rank-effect names."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import tempfile

from rank_effect_packages.errors import RankEffectError
from rank_effect_packages.formats import (
    extract_texture_references,
    rewrite_texture_references,
)


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT_ROOT = REPOSITORY_ROOT / "assets" / "rank-effects" / "generated"


def _mapping(value: str) -> tuple[bytes, bytes]:
    source, separator, target = value.partition("=")
    if not separator or not source or not target:
        raise argparse.ArgumentTypeError("mapping must be OLD=NEW")
    try:
        return source.encode("ascii"), target.encode("ascii")
    except UnicodeEncodeError as error:
        raise argparse.ArgumentTypeError("mapping names must be ASCII") from error


def _inside_output(path: Path, output_root: Path) -> Path:
    resolved = path.resolve()
    try:
        resolved.relative_to(output_root.resolve())
    except ValueError as error:
        raise RankEffectError(
            f"Output must stay under the repository staging root: {output_root}"
        ) from error
    return resolved


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
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, default=DEFAULT_OUTPUT_ROOT)
    parser.add_argument("--map", type=_mapping, action="append", required=True)
    arguments = parser.parse_args()
    try:
        source = arguments.input.resolve()
        output = _inside_output(arguments.output, arguments.output_root)
        if not source.is_file() or source.is_symlink():
            raise RankEffectError(f"Input JCS is not a regular file: {source}")
        if source == output:
            raise RankEffectError("Input and output JCS paths must differ")
        replacements = dict(arguments.map)
        if len(replacements) != len(arguments.map):
            raise RankEffectError("Duplicate source mappings are not allowed")
        for target in replacements.values():
            name = target.decode("ascii")
            if Path(name).name != name or not name.startswith(("reborn_", "legacy_")):
                raise RankEffectError(
                    "Output references must be private reborn_* or legacy_* filenames"
                )
        encoded, counts = rewrite_texture_references(
            source.read_bytes(), replacements, str(source), require_all_references=True
        )
        final_refs = extract_texture_references(encoded, str(output))
        if any(reference not in set(replacements.values()) for reference in final_refs):
            raise RankEffectError("Rewritten JCS retains a non-private texture reference")
        _atomic_write(output, encoded)
        print(f"Prepared JCS: {output}")
        print(f"Replacements: {sum(counts.values())}; references: {len(final_refs)}")
        return 0
    except (RankEffectError, OSError, UnicodeError) as error:
        print(f"ERROR: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
