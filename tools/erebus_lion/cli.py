"""Install the custom Erebus Lion mount family into the GodsWar client.

The new mount derives from the shipped ``Ride_Lion_002.jcs`` model.  A parent
transform around both the mesh and animated skeleton scales every axis by 40%
while leaving skin weights, animation payload, and external rider actions
unchanged.  This makes Erebus visibly larger without stretching its original
proportions.  Only the cloned texture is recoloured.

The installer is deterministic, backs up every existing file it changes, and
can verify an installed client with ``--check``.  ``--preview-only`` exports
PNG previews without mutating the client.
"""

from __future__ import annotations

import argparse
import sys

from .common import InstallError
from .installer import install


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--client-root",
        default=r"C:\Godswar Origin",
    )
    parser.add_argument(
        "--backup-root",
        default=r"C:\Reborn\backups",
    )
    parser.add_argument("--preview-dir")
    parser.add_argument("--preview-only", action="store_true")
    parser.add_argument("--check", action="store_true")
    return parser


def main() -> int:
    try:
        return install(build_parser().parse_args())
    except (
        InstallError,
        OSError,
        UnicodeError,
        ValueError,
    ) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
