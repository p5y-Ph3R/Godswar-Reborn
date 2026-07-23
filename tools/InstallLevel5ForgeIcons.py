from __future__ import annotations

"""Install the dedicated Level-5 forging-material icon atlas.

The GodsWar ``.gwo`` icon atlases are 32-bit RLE TGA files.  This installer
clones the proven ``Icon3.gwo`` payload into ``Icon4.gwo`` for both locales,
then replaces six complete 36x36 atlas cells.  It intentionally has no
third-party dependencies.

Run with ``--dry-run`` to validate and preview, or ``--check`` to verify an
already-installed client without writing anything.
"""

from level5_forge_icons import *  # noqa: F401,F403


if __name__ == "__main__":
    raise SystemExit(main())
