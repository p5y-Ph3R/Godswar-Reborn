from __future__ import annotations

import hashlib

from InstallLevel5ForgeIcons import InstallError


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


__all__ = ["InstallError", "sha256_bytes"]
