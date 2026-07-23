from __future__ import annotations

from dataclasses import dataclass
import hashlib
from pathlib import Path

LOCALES = ("en_us", "zh_cn")
SOURCE_ATLAS_NAME = "Icon3.gwo"
TARGET_ATLAS_NAME = "Icon4.gwo"
ATLAS_RELATIVE_DIRECTORY = Path("UI") / "Texture"
TGA_FOOTER_SIGNATURE = b"TRUEVISION-XFILE.\x00"
EXPECTED_TGA_WIDTH = 1024
EXPECTED_TGA_HEIGHT = 1024
EXPECTED_TGA_DESCRIPTOR = 0x08
EXPECTED_EXTENSION_SIZE = 495


@dataclass(frozen=True)
class SpriteSpec:
    filename: str
    x: int
    y: int


SPRITE_SPECS = (
    SpriteSpec("crystal5-36.png", 0, 0),
    SpriteSpec("sapphire5-36.png", 36, 0),
    SpriteSpec("emerald5-36.png", 72, 0),
    SpriteSpec("crystal5-pieces-36.png", 108, 0),
    SpriteSpec("sapphire5-pieces-36.png", 144, 0),
    SpriteSpec("emerald5-pieces-36.png", 180, 0),
)
SPRITE_WIDTH = 36
SPRITE_HEIGHT = 36


class InstallError(RuntimeError):
    pass


@dataclass(frozen=True)
class RlePacket:
    pixel_start: int
    pixel_count: int
    encoded_start: int
    encoded_end: int
    is_rle: bool


@dataclass(frozen=True)
class TgaAtlas:
    raw: bytes
    prefix: bytes
    width: int
    height: int
    descriptor: int
    pixels: bytes
    packets: tuple[RlePacket, ...]
    stream_end: int
    extension: bytes
    footer: bytes


@dataclass(frozen=True)
class Sprite:
    spec: SpriteSpec
    bgra: bytes
    sha256: str


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()
