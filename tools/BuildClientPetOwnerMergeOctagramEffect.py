"""Build or verify the canonical owner-Merge effect-0004 GWM asset."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import uuid


TOOLS = Path(__file__).resolve().parent
REPOSITORY = TOOLS.parent
sys.path.insert(0, str(TOOLS))

from pet_owner_merge_effect import build_octagram_gwm  # noqa: E402
from pet_owner_merge_effect.octagram_gwm import TARGET_GWM_SHA256  # noqa: E402


DEFAULT_SOURCE = Path(
    r"C:\Godswar Origin\Characters_New\PetUniteEffect\e_he_0003_all.gwm"
)
DEFAULT_PNG = (
    REPOSITORY / "assets" / "pet-owner-merge" / "e_he_0004_a-black-octagram.png"
)
DEFAULT_OUTPUT = (
    REPOSITORY / "assets" / "pet-owner-merge" / "e_he_0004_all.gwm"
)
INSTALLED_CLIENT = Path(r"C:\Godswar Origin")


def _is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-gwm", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--source-png", type=Path, default=DEFAULT_PNG)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--check", action="store_true", help="verify output without writing it"
    )
    arguments = parser.parse_args()

    output = arguments.output.resolve()
    if _is_within(output, INSTALLED_CLIENT.resolve()):
        raise ValueError("Builder refuses installed-client output; use the installer patcher")
    source_gwm = arguments.source_gwm.resolve().read_bytes()
    source_png = arguments.source_png.resolve().read_bytes()
    result = build_octagram_gwm(source_gwm, source_png)
    if result.report["targetGwmSha256"] != TARGET_GWM_SHA256:
        raise ValueError("Builder returned an unexpected target hash")

    if arguments.check:
        if not output.is_file() or output.read_bytes() != result.gwm:
            raise ValueError(f"Canonical effect-0004 output is missing or changed: {output}")
        status = "Verified"
    elif output.exists():
        if not output.is_file() or output.read_bytes() != result.gwm:
            raise ValueError(f"Refusing to overwrite changed output: {output}")
        status = "AlreadyBuilt"
    else:
        output.parent.mkdir(parents=True, exist_ok=True)
        stage = output.with_name(f"{output.name}.{uuid.uuid4().hex}.stage")
        try:
            with stage.open("xb") as stream:
                stream.write(result.gwm)
                stream.flush()
                os.fsync(stream.fileno())
            if stage.read_bytes() != result.gwm:
                raise ValueError("Staged effect-0004 output failed byte validation")
            if output.exists():
                raise ValueError(f"Output appeared while staging: {output}")
            os.rename(stage, output)
            if output.read_bytes() != result.gwm:
                raise ValueError("Installed repository asset failed byte validation")
        finally:
            if stage.exists():
                stage.unlink()
        status = "Built"

    print(
        json.dumps(
            {
                "status": status,
                "output": str(output),
                **result.report,
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
