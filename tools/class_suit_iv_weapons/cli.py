from __future__ import annotations

import argparse
from pathlib import Path
import sys

from erebus_lion.common import InstallError

from .staging import stage


def build_parser() -> argparse.ArgumentParser:
    repository_root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(
        description=(
            "Stage deterministic Tier IV weapon textures and previews. "
            "This command never writes into the game client."
        )
    )
    parser.add_argument(
        "--client-root",
        default=r"C:\Godswar Origin",
        help="Read-only original client root",
    )
    parser.add_argument(
        "--output-root",
        default=str(
            repository_root / "artifacts" / "class-suit-iv-weapons" / "staged"
        ),
        help="Staging directory outside the client",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Recompute and verify staged files without writing",
    )
    parser.add_argument(
        "--audit-stock",
        action="store_true",
        help="Also write/check pre-install Tier III/IV duplication evidence",
    )
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        return stage(
            Path(args.client_root),
            Path(args.output_root),
            args.check,
            args.audit_stock,
        )
    except (InstallError, OSError, UnicodeError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

