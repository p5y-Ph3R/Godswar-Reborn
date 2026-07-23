from __future__ import annotations

import argparse
from pathlib import Path
import sys

from .common import InstallError
from .installer import install

def build_parser() -> argparse.ArgumentParser:
    script_root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--client-root",
        default=r"C:\Godswar Origin",
        help="Game client root (default: C:\\Godswar Origin)",
    )
    parser.add_argument(
        "--asset-root",
        default=str(script_root / "assets" / "forging" / "level5"),
        help="Directory containing the six fixed 36x36 PNG sprites",
    )
    parser.add_argument(
        "--backup-root",
        default=str(script_root / "backups"),
        help="Directory in which timestamped backups and manifests are created",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Validate and report required changes without writing anything",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify that both installed locale atlases exactly match the expected output",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Replace an unexpected existing Icon4.gwo after backing it up",
    )
    return parser


def main() -> int:
    try:
        return install(build_parser().parse_args())
    except (InstallError, OSError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
