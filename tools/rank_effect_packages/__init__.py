"""Safe authoring and installation primitives for rank-effect packages."""

from .errors import RankEffectError
from .formats import (
    TextureInfo,
    extract_texture_references,
    rewrite_texture_references,
    structural_fingerprint,
    validate_tga_texture,
)
from .package import LoadedPackage, load_package

__all__ = [
    "LoadedPackage",
    "RankEffectError",
    "TextureInfo",
    "extract_texture_references",
    "load_package",
    "rewrite_texture_references",
    "structural_fingerprint",
    "validate_tga_texture",
]
