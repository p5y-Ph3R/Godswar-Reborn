"""Build and preflight the reviewed rank-effect package without installing it."""

from __future__ import annotations

import argparse
from pathlib import Path

from rank_effect_packages.builder import build_package
from rank_effect_packages.errors import RankEffectError


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-root", type=Path, default=Path(r"C:\Godswar Origin"))
    parser.add_argument(
        "--package-root",
        type=Path,
        default=REPOSITORY_ROOT / "assets" / "rank-effects",
    )
    arguments = parser.parse_args()
    try:
        effects, assets = build_package(arguments.client_root, arguments.package_root)
        print(f"Built and preflighted {effects} effects and {assets} install assets")
        print("The client was not modified")
        return 0
    except (RankEffectError, OSError, UnicodeError) as error:
        print(f"ERROR: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
