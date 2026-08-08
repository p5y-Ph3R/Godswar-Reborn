"""Build and validate the isolated AR14/Warrior-WR10 v2 prototype."""

from __future__ import annotations

import argparse
from pathlib import Path

from rank_effect_packages.errors import RankEffectError
from rank_effect_v2 import build_prototype


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-root", type=Path, default=Path(r"C:\Godswar Origin"))
    parser.add_argument(
        "--output-root",
        type=Path,
        required=True,
        help="A new, non-existing package directory (normally under artifacts)",
    )
    arguments = parser.parse_args()
    try:
        effects, assets = build_prototype(arguments.client_root, arguments.output_root)
        print(f"Built and validated {effects} effects and {assets} assets")
        print("Prototype was not installed into any client")
        return 0
    except (RankEffectError, OSError, UnicodeError, ValueError) as error:
        print(f"ERROR: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
